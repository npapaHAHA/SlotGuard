using System.Data;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Http.Features;

namespace SlotGuard;

internal sealed class CalendarImportBatch
{
    public Guid Id { get; init; }
    public required string ResourceId { get; init; }
    public required string Revision { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public int Count { get; init; }
    public int Skipped { get; init; }
}

internal sealed record CalendarImportView(string Revision, CalendarImportRow[] Rows, int Ready, int Skipped);
internal sealed record CalendarImportReceipt(Guid Id, int Count, int Skipped, bool AlreadyImported);

internal static class CalendarImportEndpoints
{
    public static void MapCalendarImport(this RouteGroupBuilder api)
    {
        var endpoints = api.MapGroup("/workspace/calendar-import");
        endpoints.MapGet("", History);
        endpoints.MapPost("/preview", (HttpContext http, OrganizationScope scope, BookingDb db, SlotBookings bookings,
            CancellationToken ct) => Upload(http, scope, db, bookings, false, ct));
        endpoints.MapPost("/commit", (HttpContext http, OrganizationScope scope, BookingDb db, SlotBookings bookings,
            CancellationToken ct) => Upload(http, scope, db, bookings, true, ct));
    }

    private static async Task<IResult> Upload(HttpContext http, OrganizationScope scope, BookingDb db,
        SlotBookings bookings, bool commit, CancellationToken ct)
    {
        if (!scope.IsAdmin) return Results.StatusCode(403);
        http.Response.Headers.CacheControl = "no-store";
        // JSON escaping may expand one input byte to six transport bytes.
        const int transportLimit = CalendarImportFile.MaxBytes * 6 + 4096;
        if (http.Request.ContentLength > transportLimit)
            return Results.StatusCode(413);
        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
            limit.MaxRequestBodySize = transportLimit;
        if (!http.Request.HasJsonContentType())
            return Results.StatusCode(415);
        Input? input;
        try
        {
            input = await http.Request.ReadFromJsonAsync<Input>(ct);
        }
        catch (JsonException)
        {
            return Invalid();
        }
        if (input?.ResourceId is not { Length: > 0 and <= 32 } id || input.Content is not { Length: > 0 and <= CalendarImportFile.MaxBytes }
            || Encoding.UTF8.GetByteCount(input.Content) > CalendarImportFile.MaxBytes
            || commit && (input.Revision is not { Length: 97 } stamp || stamp[64] != '.'
                || !Guid.TryParseExact(stamp.AsSpan(65), "N", out _)))
            return Invalid();
        var allowed = scope.Resources(db).Where(resource => resource.Id == id && !resource.IsArchived);
        if (!commit)
            return await bookings.PreviewImport(allowed, input.Content, ct);
        if (!await allowed.AnyAsync(ct)) return Results.NotFound();
        return await bookings.ImportCalendar(id, scope.Current.Id, input.Content, input.Revision!, scope.OwnerId,
            WorkspaceEndpoints.Actor(http.User), ct);
    }

    private static async Task<IResult> History(BookingDb db, OrganizationScope scope, HttpContext http, CancellationToken ct)
    {
        if (!scope.IsAdmin) return Results.StatusCode(403);
        http.Response.Headers.CacheControl = "no-store";
        var resources = await scope.Resources(db).AsNoTracking().Where(resource => !resource.IsArchived)
            .OrderBy(resource => resource.SortOrder).ThenBy(resource => resource.Name)
            .Select(resource => new { resource.Id, resource.Name, resource.TimeZone }).ToArrayAsync(ct);
        var history = await (from batch in db.CalendarImports.AsNoTracking()
                             join resource in scope.Resources(db) on batch.ResourceId equals resource.Id
                             orderby batch.CreatedAt descending, batch.Id
                             select new { batch.Id, ResourceName = resource.Name, batch.CreatedAt, batch.Count, batch.Skipped })
            .Take(20).ToArrayAsync(ct);
        return Results.Ok(new { resources, history });
    }

    internal static IResult Invalid() => Results.Problem(statusCode: 400,
        title: "Выберите ресурс и корректный файл .ics до 256 КБ с 1–250 событиями.");
    private sealed record Input(string? ResourceId, string? Content, string? Revision);
}

internal sealed partial class SlotBookings
{
    private const string CalendarImportReason = "Импорт из календаря";

    public async Task<IResult> PreviewImport(IQueryable<Resource> allowed, string content, CancellationToken ct)
    {
        await using var snapshot = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var resource = await allowed.AsNoTracking().SingleOrDefaultAsync(ct);
        if (resource is null) return Results.NotFound();
        var plan = await PlanImport(resource, Schedule.Read(resource), content, clock.GetUtcNow(), ct);
        await snapshot.CommitAsync(ct);
        return plan is null ? CalendarImportEndpoints.Invalid()
            : Results.Ok(plan with { Revision = $"{plan.Revision}.{Guid.NewGuid():N}" });
    }

    public Task<IResult> ImportCalendar(string id, string organization, string content, string revision,
        Guid owner, string actor, CancellationToken ct) =>
        InResource(id, async (now, resource, schedule) =>
        {
            if (resource.OrganizationId != organization || resource.IsArchived)
                return Results.NotFound();
            var existing = await db.CalendarImports.AsNoTracking().SingleOrDefaultAsync(batch =>
                batch.ResourceId == id && batch.Revision == revision, ct);
            if (existing is not null)
                return Results.Ok(new CalendarImportReceipt(existing.Id, existing.Count, existing.Skipped, true));
            var plan = await PlanImport(resource, schedule, content, now, ct);
            if (plan is null) return CalendarImportEndpoints.Invalid();
            if (!revision.AsSpan(0, 64).SequenceEqual(plan.Revision))
                return Results.Problem(statusCode: 409, title: "Расписание изменилось. Обновите предпросмотр и проверьте конфликты.");
            if (plan.Ready == 0)
                return Results.Problem(statusCode: 422, title: "В файле больше нет интервалов, готовых к импорту.");

            var batch = new CalendarImportBatch
            {
                Id = Guid.NewGuid(), ResourceId = id, Revision = revision, CreatedAt = now, Count = plan.Ready, Skipped = plan.Skipped
            };
            db.CalendarImports.Add(batch);
            foreach (var row in plan.Rows.Where(row => row.Status == "ready"))
            {
                var start = row.Start!.Value;
                var end = row.End!.Value;
                var block = NewBooking(id, new BookingWindow(start, end, start, end), owner);
                block.MaintenanceReason = CalendarImportReason;
                block.CalendarImportId = batch.Id;
                db.Bookings.Add(block);
            }
            Audit.Add(db, resource, actor, "calendar.imported",
                $"Добавлено: {batch.Count}. Пропущено: {batch.Skipped}.", now);
            await WebhookEvents.EnqueueImport(db, organization, batch, ct);
            return Results.Json(new CalendarImportReceipt(batch.Id, batch.Count, batch.Skipped, false), statusCode: 201);
        }, ct);

    private async Task<CalendarImportView?> PlanImport(Resource resource, ResourceSchedule schedule, string content,
        DateTimeOffset now, CancellationToken ct)
    {
        if (CalendarImportFile.Read(content, schedule.Zone) is not { Length: > 0 } rows)
            return null;
        var horizon = Schedule.MaximumHorizon(now);
        var candidates = rows.Where(row => row.Status == "candidate" && row.Start > now && row.End <= horizon).ToArray();
        var taken = new List<BookingWindow>();
        var alreadyImported = new HashSet<(DateTimeOffset, DateTimeOffset)>();
        if (candidates.Length > 0)
        {
            var first = candidates.Min(row => row.Start!.Value);
            var last = candidates.Max(row => row.End!.Value);
            var blocks = await Active(resource.Id).AsNoTracking()
                .Where(item => item.OccupiedFromUtc < last && item.OccupiedUntilUtc > first
                    && (item.HoldExpiresAt == null || item.HoldExpiresAt > now))
                .Select(item => new { item.StartUtc, item.EndUtc, item.OccupiedFromUtc, item.OccupiedUntilUtc, item.CalendarImportId })
                .ToArrayAsync(ct);
            foreach (var item in blocks)
            {
                taken.Add(new BookingWindow(item.StartUtc, item.EndUtc, item.OccupiedFromUtc, item.OccupiedUntilUtc));
                if (item.CalendarImportId.HasValue) alreadyImported.Add((item.StartUtc, item.EndUtc));
            }
        }
        var statuses = rows.Select(row =>
        {
            if (row.Status != "candidate") return row;
            if (row.Start <= now || row.End > horizon) return row with { Status = "outside" };
            var start = row.Start!.Value;
            var end = row.End!.Value;
            if (alreadyImported.Contains((start, end))) return row with { Status = "duplicate" };
            var window = new BookingWindow(start, end, start, end);
            if (taken.Any(existing => existing.Overlaps(window))) return row with { Status = "conflict" };
            taken.Add(window);
            alreadyImported.Add((start, end));
            return row with { Status = "ready" };
        }).ToArray();
        var stamp = WebhookEvents.HashKey(JsonSerializer.Serialize(new { resource.Id, Content = WebhookEvents.HashKey(content), Rows = statuses }));
        var ready = statuses.Count(row => row.Status == "ready");
        return new CalendarImportView(stamp, statuses, ready, rows.Length - ready);
    }
}
