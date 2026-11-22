using System.Security.Claims;
using Microsoft.EntityFrameworkCore;

namespace SlotGuard;

internal static class OrganizationStructureEndpoints
{
    public static void MapOrganizationStructure(this RouteGroupBuilder api)
    {
        var structure = api.MapGroup("/structure").AddEndpointFilter((context, next) =>
            context.HttpContext.RequestServices.GetRequiredService<OrganizationScope>().IsAdmin
                ? next(context) : ValueTask.FromResult<object?>(Results.StatusCode(403)));
        structure.MapGet("", Read);
        structure.MapPut("/units/{id:guid}", SaveUnit);
        structure.MapPut("/groups/{id:guid}", SaveGroup);
        structure.MapPut("/members/{id:guid}", SaveMember);
        structure.MapPut("/resources/{id}", AssignResource);
    }

    private static async Task<IResult> Read(BookingDb db, OrganizationScope scope, HttpContext context, CancellationToken ct)
    {
        context.Response.Headers.CacheControl = "no-store";
        var organization = scope.Current.Id;
        await using var snapshot = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        var units = await db.OrganizationUnits.AsNoTracking().Where(item => item.OrganizationId == organization)
            .OrderBy(item => item.Name).ToArrayAsync(ct);
        var groups = await db.ResourceGroups.AsNoTracking().Where(item => item.OrganizationId == organization)
            .OrderBy(item => item.Name).ToArrayAsync(ct);
        var resources = await scope.Resources(db).AsNoTracking().OrderBy(item => item.IsArchived).ThenBy(item => item.Name)
            .Select(item => new { item.Id, item.Name, item.GroupId, item.IsArchived }).ToArrayAsync(ct);
        var members = await (
            from member in db.Memberships.AsNoTracking()
            join account in db.Users.AsNoTracking() on member.OwnerId equals account.Id into accounts
            from account in accounts.DefaultIfEmpty()
            where member.OrganizationId == organization
            select new { member.OwnerId, member.Role, member.UnitId, Name = account == null ? null : account.DisplayName,
                Email = account == null ? null : account.Email }
        ).ToArrayAsync(ct);
        await snapshot.CommitAsync(ct);
        return Results.Ok(new { units, groups, resources, members = members
            .Select(item => new { Id = item.OwnerId, Name = item.Name ?? DemoAccounts.LabelFor(item.OwnerId),
                item.Email, item.Role, item.UnitId }).OrderBy(item => item.Name, StringComparer.CurrentCulture) });
    }

    private static async Task<IResult> SaveUnit(Guid id, UnitInput input, BookingDb db, OrganizationScope scope,
        ClaimsPrincipal user, TimeProvider clock, CancellationToken ct)
    {
        if (id == Guid.Empty || Name(input.Name) is not { } name)
            return Problem(400, "Укажите название длиной от 1 до 100 символов.");

        var organization = scope.Current.Id;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await LockOrganization(db, organization, ct);
        var units = db.OrganizationUnits.Where(item => item.OrganizationId == organization);
        var unit = await units.SingleOrDefaultAsync(item => item.Id == id, ct);
        if (unit is null && await db.OrganizationUnits.AnyAsync(item => item.Id == id, ct))
            return Results.NotFound();
        if (unit is not null && unit.BranchId != input.BranchId)
            return Problem(409, "При переименовании филиал подразделения остаётся прежним.");
        if (input.BranchId is { } branch && !await units.AnyAsync(item => item.Id == branch && item.BranchId == null, ct))
            return Problem(404, "Филиал не найден.");
        if (await units.AnyAsync(item => item.Id != id && item.BranchId == input.BranchId && item.Name.ToUpper() == name.ToUpper(), ct))
            return Problem(409, "Такое название уже используется на этом уровне.");
        if (unit?.Name == name)
            return Results.NoContent();

        var previous = unit?.Name;
        if (unit is null)
            db.OrganizationUnits.Add(new OrganizationUnit { Id = id, OrganizationId = organization, Name = name, BranchId = input.BranchId });
        else
            unit.Name = name;
        var kind = input.BranchId.HasValue ? "Отдел" : "Филиал";
        Audit.Add(db, organization, id.ToString("N"), name, WorkspaceEndpoints.Actor(user), "structure.unit",
            previous is null ? $"{kind} создан" : $"{kind} переименован из «{previous}»", clock.GetUtcNow());
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> SaveGroup(Guid id, GroupInput input, BookingDb db, OrganizationScope scope,
        ClaimsPrincipal user, TimeProvider clock, CalendarUpdates updates, CancellationToken ct)
    {
        if (id == Guid.Empty || Name(input.Name) is not { } name)
            return Problem(400, "Укажите название длиной от 1 до 100 символов.");

        var organization = scope.Current.Id;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await LockOrganization(db, organization, ct);
        var group = await db.ResourceGroups.SingleOrDefaultAsync(item => item.Id == id && item.OrganizationId == organization, ct);
        if (group is null && await db.ResourceGroups.AnyAsync(item => item.Id == id, ct))
            return Results.NotFound();
        if (await db.ResourceGroups.AnyAsync(item => item.OrganizationId == organization
                && item.Id != id && item.Name.ToUpper() == name.ToUpper(), ct))
            return Problem(409, "Группа с таким названием уже существует.");
        var unit = await UnitLabel(db, organization, input.UnitId, ct);
        if (unit is null)
            return Problem(404, "Подразделение не найдено.");
        if (input.ReviewerId is { } reviewer && !await db.Memberships.AnyAsync(item => item.OwnerId == reviewer
                && item.OrganizationId == organization && item.Role == OrganizationMembership.Reviewer, ct))
            return Problem(400, "Сначала назначьте участнику роль согласующего.");
        if (group?.Name == name && group.UnitId == input.UnitId && group.ReviewerId == input.ReviewerId)
            return Results.NoContent();

        var reviewerLabel = input.ReviewerId is { } owner ? await MemberLabel(db, owner, ct) : "не назначен";
        if (group is null)
        {
            group = new ResourceGroup { Id = id, OrganizationId = organization, Name = name };
            db.ResourceGroups.Add(group);
        }
        group.Name = name;
        group.UnitId = input.UnitId;
        group.ReviewerId = input.ReviewerId;
        Audit.Add(db, organization, id.ToString("N"), name, WorkspaceEndpoints.Actor(user), "structure.group",
            $"Доступ — {unit}. Согласующий — {reviewerLabel}.", clock.GetUtcNow());
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        await updates.Publish(organization, "StructureChanged");
        return Results.NoContent();
    }

    private static async Task<IResult> SaveMember(Guid id, MemberInput input, BookingDb db, OrganizationScope scope,
        ClaimsPrincipal user, TimeProvider clock, CalendarUpdates updates, CancellationToken ct)
    {
        var organization = scope.Current.Id;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await LockOrganization(db, organization, ct);
        var membership = db.Memberships.Where(item => item.OwnerId == id && item.OrganizationId == organization);
        var member = await membership.AsNoTracking().SingleOrDefaultAsync(ct);
        if (member is null)
            return Problem(404, "Участник не найден.");
        var role = input.Role;
        if (member.Role == OrganizationMembership.Admin ? role != OrganizationMembership.Admin
            : role is not (OrganizationMembership.Member or OrganizationMembership.Reviewer))
            return Problem(400, "Для участника выберите роль «Участник» или «Согласующий».");
        var unit = await UnitLabel(db, organization, input.UnitId, ct);
        if (unit is null)
            return Problem(404, "Подразделение не найдено.");
        if (member.Role == role && member.UnitId == input.UnitId)
            return Results.NoContent();

        if (role != OrganizationMembership.Reviewer)
            await db.ResourceGroups.Where(item => item.OrganizationId == organization && item.ReviewerId == id)
                .ExecuteUpdateAsync(set => set.SetProperty(item => item.ReviewerId, (Guid?)null), ct);
        await membership.ExecuteUpdateAsync(set => set.SetProperty(item => item.Role, role!).SetProperty(item => item.UnitId, input.UnitId), ct);
        var displayRole = role switch
        {
            OrganizationMembership.Admin => "администратор",
            OrganizationMembership.Reviewer => "согласующий",
            _ => "участник"
        };
        Audit.Add(db, organization, id.ToString("N"), await MemberLabel(db, id, ct), WorkspaceEndpoints.Actor(user),
            "structure.member", $"Роль — {displayRole}. Подразделение — {(input.UnitId is null ? "без назначения" : unit)}.",
            clock.GetUtcNow());
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        await updates.Publish(organization, "StructureChanged");
        return Results.NoContent();
    }

    private static async Task<IResult> AssignResource(string id, ResourceInput input, BookingDb db, OrganizationScope scope,
        ClaimsPrincipal user, TimeProvider clock, CalendarUpdates updates, CancellationToken ct)
    {
        var organization = scope.Current.Id;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await LockOrganization(db, organization, ct);
        var resource = await WorkspaceEndpoints.LockEditableResource(db, scope, id, ct);
        if (resource is null)
            return Problem(404, "Ресурс не найден.");
        var groupName = input.GroupId is { } group
            ? await db.ResourceGroups.Where(item => item.Id == group && item.OrganizationId == organization)
                .Select(item => item.Name).SingleOrDefaultAsync(ct)
            : "без группы";
        if (groupName is null)
            return Problem(404, "Группа ресурсов не найдена.");
        if (resource.GroupId == input.GroupId)
            return Results.NoContent();

        await scope.Resources(db).Where(item => item.Id == id)
            .ExecuteUpdateAsync(set => set.SetProperty(item => item.GroupId, input.GroupId), ct);
        Audit.Add(db, resource, WorkspaceEndpoints.Actor(user), "structure.resource", $"Группа — {groupName}.", clock.GetUtcNow());
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        await updates.Publish(organization, "StructureChanged");
        return Results.NoContent();
    }

    private static Task LockOrganization(BookingDb db, string id, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""SELECT 1 FROM "Organizations" WHERE "Id" = {id} FOR UPDATE""", ct);

    private static Task<string?> UnitLabel(BookingDb db, string organization, Guid? id, CancellationToken ct) =>
        id is null ? Task.FromResult<string?>("вся организация") : (
            from unit in db.OrganizationUnits
            join branch in db.OrganizationUnits on unit.BranchId equals branch.Id into branches
            from branch in branches.DefaultIfEmpty()
            where unit.OrganizationId == organization && unit.Id == id
            select (string?)(branch == null ? unit.Name : branch.Name + " / " + unit.Name)).SingleOrDefaultAsync(ct);

    private static async Task<string> MemberLabel(BookingDb db, Guid id, CancellationToken ct) =>
        await db.Users.Where(item => item.Id == id).Select(item => item.DisplayName).SingleOrDefaultAsync(ct) ?? DemoAccounts.LabelFor(id);

    private static string? Name(string? name) => name?.Trim() is { Length: > 0 and <= 100 } clean ? clean : null;
    private static IResult Problem(int status, string title) => Results.Problem(statusCode: status, title: title);

    private sealed record UnitInput(string? Name, Guid? BranchId);
    private sealed record GroupInput(string? Name, Guid? UnitId, Guid? ReviewerId);
    private sealed record MemberInput(string? Role, Guid? UnitId);
    private sealed record ResourceInput(Guid? GroupId);
}
