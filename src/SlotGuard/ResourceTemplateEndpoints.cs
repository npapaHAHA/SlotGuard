using System.Data;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace SlotGuard;

internal static class ResourceTemplateEndpoints
{
    internal const string NameIndex = "ux_resource_templates_org_name";

    private sealed record TemplateInput(string? Name, TemplateSettings? Settings, Guid? Revision);
    private sealed record ScheduleConflict(string ResourceName, string TimeZone, DateTimeOffset[] Starts);
    private sealed record ResourceUpdate(Resource Resource, string Schedule, bool RequiresApproval);

    public static void MapResourceTemplates(this RouteGroupBuilder workspace)
    {
        var templates = workspace.MapGroup("/resource-templates");
        templates.MapGet("", List);
        templates.MapPost("", Create);
        templates.MapPut("/{id:guid}", Update);
        templates.MapDelete("/{id:guid}", Delete);
        workspace.MapDelete("/resources/{id}/template", Detach);
    }

    private static async Task<IResult> List(BookingDb db, OrganizationScope scope, HttpContext context, CancellationToken ct)
    {
        if (!scope.IsAdmin)
            return Results.StatusCode(403);
        context.Response.Headers.CacheControl = "no-store";
        await using var snapshot = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var templates = await InOrganization(db, scope).AsNoTracking().OrderBy(item => item.Name).ThenBy(item => item.Id)
            .ToArrayAsync(ct);
        var counts = await scope.Resources(db).Where(item => item.TemplateId != null)
            .GroupBy(item => item.TemplateId!.Value)
            .Select(group => new { Id = group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.Id, item => item.Count, ct);
        await snapshot.CommitAsync(ct);
        return Results.Ok(TemplatePresets.All.Concat(templates.Select(item => View(item, counts.GetValueOrDefault(item.Id)))));
    }

    private static async Task<IResult> Create(TemplateInput input, BookingDb db, OrganizationScope scope, CancellationToken ct)
    {
        if (!scope.IsAdmin)
            return Results.StatusCode(403);
        if (Validate(input, out var name, out var settings) is { } invalid)
            return invalid;

        var template = new ResourceTemplate
        {
            Id = Guid.NewGuid(), Revision = Guid.NewGuid(), OrganizationId = scope.Current.Id,
            Name = name!, NormalizedName = name!.ToUpperInvariant(), Definition = settings!.Write()
        };
        db.ResourceTemplates.Add(template);
        return await Save(db, ct) ?? Results.Json(View(template), statusCode: 201);
    }

    private static async Task<IResult> Update(Guid id, TemplateInput input, BookingDb db, OrganizationScope scope,
        ClaimsPrincipal user, TimeProvider clock, CalendarUpdates updates, CancellationToken ct)
    {
        if (!scope.IsAdmin)
            return Results.StatusCode(403);
        if (Validate(input, out var name, out var settings) is { } invalid)
            return invalid;

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var template = await Lock(db, scope.Current.Id, id, ct);
        if (template is null)
            return Results.NotFound();
        if (template.Revision != input.Revision)
            return Results.Problem(statusCode: 409, title: "Шаблон уже изменили. Обновите список и откройте редактор снова.");

        var ids = await scope.Resources(db).Where(item => item.TemplateId == id).Select(item => item.Id).ToArrayAsync(ct);
        var changed = new List<ResourceUpdate>();
        var conflicts = new List<ScheduleConflict>();
        var now = clock.GetUtcNow();
        foreach (var resourceId in ids.Order(StringComparer.Ordinal))
        {
            // Use the same resource locking order as multi-resource reservations.
            var resource = await WorkspaceEndpoints.LockEditableResource(db, scope, resourceId, ct);
            if (resource?.TemplateId != id)
                continue;

            var prior = ResourceScheduleEndpoints.Canonical(Schedule.Read(resource));
            var next = settings!.Manage(prior);
            var json = Schedule.Write(next);
            var approval = settings.LockRules ? settings.RequiresApproval : resource.RequiresApproval;
            if (json == Schedule.Write(prior) && approval == resource.RequiresApproval)
                continue;

            if (!ResourceScheduleEndpoints.SameWeek(prior, next) || prior.Rules != next.Rules)
            {
                var blocked = await ResourceScheduleEndpoints.Conflicts(db, resourceId, next, now, ct);
                if (blocked.Length > 0)
                    conflicts.Add(new ScheduleConflict(resource.Name, prior.ZoneId, blocked));
            }
            changed.Add(new ResourceUpdate(resource, json, approval));
        }
        if (conflicts.Count > 0)
            return Results.Problem(statusCode: 409,
                title: "Новый график закрывает время с действующими записями. Изменения шаблона не применены.",
                extensions: new Dictionary<string, object?> { ["templateConflicts"] = conflicts });

        template.Name = name!;
        template.NormalizedName = name!.ToUpperInvariant();
        template.Definition = settings!.Write();
        template.Revision = Guid.NewGuid();
        foreach (var entry in changed)
        {
            await scope.Resources(db).Where(item => item.Id == entry.Resource.Id).ExecuteUpdateAsync(set => set
                .SetProperty(item => item.ScheduleDefinition, entry.Schedule)
                .SetProperty(item => item.RequiresApproval, entry.RequiresApproval), ct);
            Audit.Add(db, entry.Resource, WorkspaceEndpoints.Actor(user), "resource.template-updated", template.Name, now);
        }
        if (await Save(db, ct) is { } duplicate)
            return duplicate;
        await transaction.CommitAsync(ct);
        if (changed.Count > 0)
            await updates.Publish(scope.Current.Id);
        var linked = await scope.Resources(db).CountAsync(item => item.TemplateId == id, ct);
        return Results.Ok(View(template, linked));
    }

    private static async Task<IResult> Delete(Guid id, BookingDb db, OrganizationScope scope, CancellationToken ct)
    {
        if (!scope.IsAdmin)
            return Results.StatusCode(403);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var template = await Lock(db, scope.Current.Id, id, ct);
        if (template is null)
            return Results.NotFound();
        if (await scope.Resources(db).AnyAsync(item => item.TemplateId == id, ct))
            return Results.Problem(statusCode: 409, title: "Сначала отвяжите от шаблона все ресурсы.");
        db.ResourceTemplates.Remove(template);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> Detach(string id, BookingDb db, OrganizationScope scope, ClaimsPrincipal user,
        TimeProvider clock, CancellationToken ct)
    {
        if (!scope.IsAdmin)
            return Results.StatusCode(403);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var resource = await WorkspaceEndpoints.LockEditableResource(db, scope, id, ct);
        if (resource is null)
            return Results.NotFound();
        if (resource.TemplateId is not null)
        {
            var template = await LockInfo(db, resource, ct);
            await scope.Resources(db).Where(item => item.Id == id)
                .ExecuteUpdateAsync(set => set.SetProperty(item => item.TemplateId, (Guid?)null), ct);
            Audit.Add(db, resource, WorkspaceEndpoints.Actor(user), "resource.template-detached", template?.Name ?? "", clock.GetUtcNow());
            await db.SaveChangesAsync(ct);
        }
        await transaction.CommitAsync(ct);
        return Results.NoContent();
    }

    internal static Task<ResourceTemplateLock?> LockInfo(BookingDb db, Resource resource, CancellationToken ct) =>
        resource.TemplateId is { } id ? ReadLock(db, id, resource.OrganizationId, ct) : Task.FromResult<ResourceTemplateLock?>(null);

    private static async Task<ResourceTemplateLock?> ReadLock(BookingDb db, Guid id, string organizationId, CancellationToken ct)
    {
        var template = await db.ResourceTemplates.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == id && item.OrganizationId == organizationId, ct);
        if (template is null)
            return null;
        var settings = template.Read();
        return new ResourceTemplateLock(template.Name, settings.LockSchedule, settings.LockRules);
    }

    internal static IResult Locked(string name, string setting) =>
        Results.Problem(statusCode: 409, title: $"Шаблон «{name}» управляет {setting}. Настройте шаблон или отвяжите ресурс.");

    internal static async Task<ResourceTemplate?> Lock(BookingDb db, string organizationId, Guid id, CancellationToken ct) =>
        (await db.ResourceTemplates.FromSqlInterpolated($"""
            SELECT * FROM "ResourceTemplates" WHERE "Id" = {id} AND "OrganizationId" = {organizationId} FOR UPDATE
            """).ToListAsync(ct)).SingleOrDefault();

    private static IQueryable<ResourceTemplate> InOrganization(BookingDb db, OrganizationScope scope) =>
        db.ResourceTemplates.Where(item => item.OrganizationId == scope.Current.Id);

    private static TemplateView View(ResourceTemplate template, int count = 0) =>
        new(template.Id.ToString(), template.Name, template.Read(), template.Revision, count);

    private static IResult? Validate(TemplateInput input, out string? name, out TemplateSettings? settings)
    {
        name = input.Name?.Trim();
        settings = null;
        if (name is null || name.Length is 0 or > 100 || name.Any(char.IsControl)
            || input.Settings is not { Details: { } details, Schedule: { } schedule } || !details.IsValid())
            return Results.Problem(statusCode: 400, title: "Укажите название и проверьте тип, вместимость и оснащение шаблона.");
        if (ResourceScheduleEndpoints.Validate(schedule, out var normalized) is { } error)
            return error;
        if (normalized!.Exceptions.Length > 0)
            return Results.Problem(statusCode: 400, title: "Исключения по датам задаются отдельно для каждого ресурса.");

        settings = input.Settings with
        {
            Details = details with { Equipment = ResourceDetails.NormalizeEquipment(details.Equipment) },
            Schedule = ResourceScheduleEndpoints.Canonical(normalized)
        };
        return null;
    }

    private static async Task<IResult?> Save(BookingDb db, CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException error) when (error.InnerException is PostgresException { ConstraintName: NameIndex })
        {
            return Results.Problem(statusCode: 409, title: "Шаблон с таким названием уже есть в организации.");
        }
        return null;
    }
}
