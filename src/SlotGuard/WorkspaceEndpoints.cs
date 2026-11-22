using System.Data;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;

namespace SlotGuard;

internal static class WorkspaceEndpoints
{
    public static void MapWorkspaceEndpoints(this RouteGroupBuilder api)
    {
        var workspace = api.MapGroup("/workspace");
        workspace.MapGet("/overview", Overview);
        workspace.MapGet("/me", MySchedule);
        workspace.MapGet("/analytics", AnalyticsEndpoints.Read);
        workspace.MapGet("/resources", ListResources);
        workspace.MapPost("/resources", CreateResource);
        workspace.MapPut("/resources/{id}", EditResource);
        workspace.MapPut("/resources/{id}/public-link", SetPublicLink);
        workspace.MapResourceSchedules();
        workspace.MapResourceTemplates();
        workspace.MapIntegrationSettings();
        workspace.MapEmbedSettings();
        workspace.MapGoogleCalendarSettings();
        workspace.MapPrivateLinks();
        workspace.MapGet("/activity", ListActivity);
    }

    private static async Task<IResult> Overview(DateOnly? week, string? timeZone, BookingDb db, OrganizationScope scope,
        ClaimsPrincipal user, TimeProvider clock, CancellationToken ct)
    {
        if (!Schedule.TryZone(timeZone, out var viewer))
            return BookingEndpoints.InvalidTimeZone();
        var now = clock.GetUtcNow();
        var firstWeek = Schedule.Monday(Schedule.LocalDate(now, viewer));
        var lastWeek = Schedule.Monday(Schedule.DisplayLastDay(now, viewer));
        var monday = week ?? firstWeek;
        if (monday < firstWeek || monday > lastWeek || monday.DayOfWeek != DayOfWeek.Monday)
            return Results.Problem(statusCode: 400, title: "Выберите понедельник в пределах календаря.");

        await using var snapshot = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var resources = await scope.Resources(db).AsNoTracking().Where(resource => !resource.IsArchived)
            .OrderBy(resource => resource.SortOrder).ThenBy(resource => resource.Name).ThenBy(resource => resource.Id)
            .ToListAsync(ct);
        var ids = resources.Select(resource => resource.Id).ToArray();
        var (start, end) = Schedule.WeekEnvelope(monday);
        var until = monday.AddDays(7);
        bool InWeek(DateTimeOffset time)
        {
            var date = Schedule.LocalDate(time, viewer);
            return date >= monday && date < until;
        }
        var busyEnd = end.AddHours(3);
        var rows = await db.Bookings.AsNoTracking().Where(item => ids.Contains(item.ResourceId)
            && item.OccupiedUntilUtc > now && item.OccupiedFromUtc < busyEnd && item.CancelledAt == null)
            .Select(item => new Occupancy(item.ResourceId, item.StartUtc, item.OccupiedFromUtc, item.OccupiedUntilUtc,
                item.MaintenanceReason != null ? "maintenance" : item.HoldExpiresAt != null ? "held"
                : item.ApprovalStatus == BookingApproval.Pending ? "pending" : "booked"))
            .ToListAsync(ct);
        var waiting = await db.WaitlistEntries.AsNoTracking().Where(item => ids.Contains(item.ResourceId)
            && item.StartUtc >= start && item.StartUtc < end && item.StartUtc > now && item.RemovedAt == null)
            .Select(item => item.StartUtc).ToListAsync(ct);
        var external = await ExternalAvailability.Read(db, ids, now, ct);
        WorkspaceSetup? setup = null;
        if (scope.IsAdmin && AccountSessions.IsAccount(user))
        {
            var owner = DemoAccounts.OwnerId(user);
            var organization = scope.Current.Id;
            var hasTeam = await db.Memberships.AnyAsync(item => item.OrganizationId == organization && item.OwnerId != owner, ct)
                || await db.Invitations.AnyAsync(item => item.OrganizationId == organization && item.AcceptedAt == null
                    && item.ExpiresAt > now, ct);
            var hasBooking = ids.Length > 0 && await db.Bookings.AnyAsync(item => ids.Contains(item.ResourceId)
                && item.CancelledAt == null && item.HoldExpiresAt == null && item.MaintenanceReason == null
                && item.SeatCapacity == null && (item.ApprovalStatus == null || item.ApprovalStatus == BookingApproval.Approved), ct);
            setup = new WorkspaceSetup(ids.Length > 0, hasTeam, hasBooking);
        }
        await snapshot.CommitAsync(ct);

        var inWeek = rows.Where(item => item.StartsAt > now && InWeek(item.StartsAt)).ToArray();
        var byDay = inWeek.ToLookup(item => Schedule.LocalDate(item.StartsAt, viewer));
        var byResource = inWeek.ToLookup(item => item.ResourceId);
        var dates = Enumerable.Range(0, 7).Select(monday.AddDays).ToArray();
        var occupied = rows.ToLookup(item => item.ResourceId);
        var free = resources.ToDictionary(resource => resource.Id, resource =>
        {
            var schedule = Schedule.Read(resource);
            var slots = Schedule.WeekStarts(monday, viewer, schedule, now)
                .Select(time => schedule.Rules.Window(time, Schedule.DefaultDuration))
                .Where(window => window.OccupiedFrom > now && schedule.Policies.TimeRestriction(window.StartsAt, now) is null
                    && external.GetValueOrDefault(resource.Id)?.Conflict(window) is null
                    && !occupied[resource.Id].Any(item =>
                    item.From < window.OccupiedUntil && window.OccupiedFrom < item.Until))
                .ToLookup(window => Schedule.LocalDate(window.StartsAt, viewer));
            return dates.Select(day => slots[day].Count()).ToArray();
        });
        var days = dates.Select((day, index) =>
        {
            var open = free.Values.Sum(values => values[index]);
            return new { Date = day, Counts = Count(byDay[day], open), Capacity = open + byDay[day].Count() };
        }).ToArray();
        return Results.Ok(new
        {
            Week = monday, FirstWeek = firstWeek, LastWeek = lastWeek, Days = days, Setup = setup,
            Totals = Count(inWeek, days.Sum(day => day.Counts.Free)), Waiting = waiting.Count(InWeek),
            Resources = resources.Select(resource => new
            {
                resource.Id, resource.Name, resource.Location,
                Counts = Count(byResource[resource.Id], free[resource.Id].Sum())
            })
        });
    }

    private sealed record WorkspaceSetup(bool HasResource, bool HasTeam, bool HasBooking);

    private static Totals Count(IEnumerable<Occupancy> items, int free)
    {
        var groups = items.ToLookup(item => item.Status);
        var booked = groups["booked"].Count();
        var held = groups["held"].Count();
        var maintenance = groups["maintenance"].Count();
        var pending = groups["pending"].Count();
        return new Totals(free, booked, held, maintenance, pending);
    }

    private static async Task<IResult> MySchedule(BookingDb db, OrganizationScope scope, ClaimsPrincipal user,
        TimeProvider clock, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var owner = DemoAccounts.OwnerId(user);
        await using var snapshot = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var myBookings =
            from booking in db.Bookings.AsNoTracking()
            join resource in scope.Resources(db) on booking.ResourceId equals resource.Id
            where booking.OwnerId == owner && booking.MaintenanceReason == null && booking.SeatCapacity == null
            select new
            {
                booking.Id, booking.ResourceId, ResourceName = resource.Name, resource.Location,
                StartsAt = booking.StartUtc, EndsAt = booking.EndUtc, booking.HoldExpiresAt, booking.CancelledAt,
                booking.ApprovalStatus, booking.ApprovalReason, booking.SeriesId, booking.BundleId, resource.RequiresApproval,
                booking.RoutingContext, booking.RoutingReviewerId,
                IsRescheduled = db.Bookings.Any(replacement => replacement.RescheduledFromId == booking.Id)
            };
        var future = myBookings.Where(item => item.StartsAt > now && item.CancelledAt == null
            && (item.HoldExpiresAt == null || item.HoldExpiresAt > now));
        var upcoming = await future.OrderBy(item => item.StartsAt).ThenBy(item => item.Id).Take(60).ToListAsync(ct);
        var bundles = upcoming.Where(item => item.BundleId.HasValue).Select(item => item.BundleId).Distinct().ToArray();
        if (bundles.Length > 0)
        {
            var listed = upcoming.Select(item => item.Id).ToArray();
            upcoming.AddRange(await future.Where(item => bundles.Contains(item.BundleId) && !listed.Contains(item.Id))
                .ToListAsync(ct));
        }
        var upcomingResources = upcoming.Select(item => item.ResourceId).Distinct().ToArray();
        var policies = (await scope.Resources(db).AsNoTracking().Where(item => upcomingResources.Contains(item.Id)).ToListAsync(ct))
            .ToDictionary(item => item.Id, item => Schedule.Read(item).Policies);
        var history = await myBookings.Where(item => item.StartsAt <= now || item.CancelledAt != null
                || item.HoldExpiresAt <= now)
            .OrderByDescending(item => item.StartsAt).ThenBy(item => item.Id).Take(30).ToListAsync(ct);
        var pending = db.WaitlistEntries.AsNoTracking().Where(item => item.RemovedAt == null && item.StartUtc > now);
        var waiting = await (
                from entry in pending
                join resource in scope.Resources(db) on entry.ResourceId equals resource.Id
                where entry.OwnerId == owner
                orderby entry.StartUtc, entry.Id
                select new
                {
                    entry.Id, entry.ResourceId, ResourceName = resource.Name, resource.Location, StartsAt = entry.StartUtc, entry.DurationMinutes,
                    Position = pending.Count(other => other.ResourceId == entry.ResourceId && other.StartUtc == entry.StartUtc
                        && other.DurationMinutes == entry.DurationMinutes && other.Id <= entry.Id)
                }).Take(60).ToListAsync(ct);
        var reviewerNames = await BookingRouting.ReviewerNames(db, scope.Current.Id,
            upcoming.Where(item => item.RoutingReviewerId.HasValue).Select(item => item.RoutingReviewerId).Distinct().ToArray(), ct);
        await snapshot.CommitAsync(ct);
        var bundleChanges = upcoming.Where(item => item.BundleId.HasValue).GroupBy(item => item.BundleId!.Value)
            .ToDictionary(group => group.Key, group => SlotBookings.BundleChangeTimes(
                group.Select(item => policies[item.ResourceId].Changes(item.StartsAt, now))));
        return Results.Ok(new
        {
            upcoming = upcoming.OrderBy(item => item.StartsAt).ThenBy(item => item.Id).Select(item => new
            {
                item.Id, item.ResourceId, item.ResourceName, item.Location, item.StartsAt, item.EndsAt, item.HoldExpiresAt,
                item.CancelledAt, item.ApprovalStatus, item.ApprovalReason, item.SeriesId, item.BundleId, item.RequiresApproval, item.IsRescheduled,
                Routing = RoutedContext.Read(item.RoutingContext),
                Reviewer = item.RoutingReviewerId is { } reviewer ? reviewerNames.GetValueOrDefault(reviewer) : null,
                Changes = item.BundleId is { } bundle ? bundleChanges[bundle]
                    : item.HoldExpiresAt is null ? policies[item.ResourceId].Changes(item.StartsAt, now) : null
            }), history = history.Select(item => new
            {
                item.Id, item.ResourceId, item.ResourceName, item.Location, item.StartsAt, item.EndsAt, item.HoldExpiresAt,
                item.CancelledAt, item.ApprovalStatus, item.ApprovalReason, item.SeriesId, item.BundleId, item.RequiresApproval,
                item.IsRescheduled, Routing = RoutedContext.Read(item.RoutingContext)
            }), waiting
        });
    }

    private static async Task<IResult> ListResources(BookingDb db, OrganizationScope scope, CancellationToken ct) =>
        scope.IsAdmin
            ? Results.Ok(await scope.Resources(db).AsNoTracking().OrderBy(item => item.IsArchived)
                .ThenBy(item => item.SortOrder).ThenBy(item => item.Name).ThenBy(item => item.Id).ToListAsync(ct))
            : Results.StatusCode(403);

    private static async Task<IResult> CreateResource(ResourceInput input, BookingDb db, OrganizationScope scope,
        ClaimsPrincipal user, TimeProvider clock, CalendarUpdates updates, CancellationToken ct)
    {
        if (!scope.IsAdmin)
            return Results.StatusCode(403);
        if (!ValidResource(input))
            return InvalidResource();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        TemplateSettings? defaults = null;
        Guid? templateId = null;
        if (input.TemplateKey is { } key)
        {
            var preset = TemplatePresets.All.SingleOrDefault(item => item.Key == key);
            if (preset is not null)
                defaults = preset.Settings;
            else
            {
                if (!Guid.TryParse(key, out var id))
                    return Results.NotFound();
                var template = await ResourceTemplateEndpoints.Lock(db, scope.Current.Id, id, ct);
                if (template is null)
                    return Results.NotFound();
                if (template.Revision != input.TemplateRevision)
                    return Results.Problem(statusCode: 409, title: "Шаблон изменился. Выберите его снова.");
                defaults = template.Read();
                if (defaults.LockRules && input.RequiresApproval is { } approval && approval != defaults.RequiresApproval)
                    return ResourceTemplateEndpoints.Locked(template.Name, "согласованием");
                templateId = id;
            }
        }

        var resource = new Resource(Guid.NewGuid().ToString("N"), input.Name!.Trim(), input.Location!.Trim(), 100, scope.Current.Id);
        if (defaults is not null)
            resource = defaults.Create(resource, templateId);
        if (input.Details is not null)
            resource = input.Details.ApplyTo(resource);
        if (input.RequiresApproval is { } required)
            resource = resource with { RequiresApproval = required };

        db.Resources.Add(resource);
        Audit.Add(db, resource, Actor(user), "resource.created", resource.Location, clock.GetUtcNow());
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        await updates.Publish(scope.Current.Id);
        return Results.Json(resource, statusCode: 201);
    }

    private static async Task<IResult> EditResource(string id, ResourceInput input, BookingDb db, OrganizationScope scope,
        ClaimsPrincipal user, TimeProvider clock, CalendarUpdates updates, CancellationToken ct)
    {
        if (!scope.IsAdmin)
            return Results.StatusCode(403);
        if (!ValidResource(input))
            return InvalidResource();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var resource = await LockEditableResource(db, scope, id, ct);
        if (resource is null)
            return Results.NotFound();
        if (input.RequiresApproval is { } approval && approval != resource.RequiresApproval
            && await ResourceTemplateEndpoints.LockInfo(db, resource, ct) is { LockRules: true } template)
            return ResourceTemplateEndpoints.Locked(template.Name, "согласованием");

        var now = clock.GetUtcNow();
        if (input.IsArchived && !resource.IsArchived && await db.Bookings.AnyAsync(
                item => item.ResourceId == id && item.CancelledAt == null && item.StartUtc > now, ct))
            return Results.Problem(statusCode: 409, title: "Сначала завершите или отмените будущие записи по этому ресурсу.");

        var updated = resource with
        {
            Name = input.Name!.Trim(), Location = input.Location!.Trim(), IsArchived = input.IsArchived,
            RequiresApproval = input.RequiresApproval ?? resource.RequiresApproval,
            PublicLinkId = input.IsArchived ? null : resource.PublicLinkId
        };
        if (input.Details is not null)
            updated = input.Details.ApplyTo(updated);
        if (updated == resource)
            return Results.Ok(resource);

        await scope.Resources(db).Where(item => item.Id == id).ExecuteUpdateAsync(set => set
            .SetProperty(item => item.Name, updated.Name)
            .SetProperty(item => item.Location, updated.Location)
            .SetProperty(item => item.Type, updated.Type)
            .SetProperty(item => item.Capacity, updated.Capacity)
            .SetProperty(item => item.Equipment, updated.Equipment)
            .SetProperty(item => item.RequiresApproval, updated.RequiresApproval)
            .SetProperty(item => item.PublicLinkId, updated.PublicLinkId)
            .SetProperty(item => item.IsArchived, updated.IsArchived), ct);
        if (!resource.IsArchived && updated.IsArchived)
        {
            await db.PrivateBookingLinks.Where(item => item.ResourceId == id && item.RevokedAt == null)
                .ExecuteUpdateAsync(set => set.SetProperty(item => item.RevokedAt, now), ct);
            await db.GoogleCalendars.Where(item => item.ResourceId == id)
                .ExecuteUpdateAsync(set => set.SetProperty(item => item.ProtectedRefreshToken, (string?)null)
                    .SetProperty(item => item.BusyJson, "[]").SetProperty(item => item.Revision, Guid.NewGuid()), ct);
        }
        var action = resource.IsArchived == updated.IsArchived ? "resource.updated"
            : updated.IsArchived ? "resource.archived" : "resource.restored";
        Audit.Add(db, updated, Actor(user), action, updated.Location, now);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        if (updated.RequiresApproval != resource.RequiresApproval)
            await updates.Publish(scope.Current.Id);
        return Results.Ok(updated);
    }

    private static async Task<IResult> SetPublicLink(string id, PublicLinkInput input, BookingDb db,
        OrganizationScope scope, ClaimsPrincipal user, TimeProvider clock, CancellationToken ct)
    {
        if (!scope.IsAdmin)
            return Results.StatusCode(403);
        var photoUrl = string.IsNullOrWhiteSpace(input.PhotoUrl) ? null : input.PhotoUrl.Trim();
        if (photoUrl is not null && (photoUrl.Length > 512 || !Uri.TryCreate(photoUrl, UriKind.Absolute, out var uri)
                || uri.Scheme != Uri.UriSchemeHttps || string.IsNullOrEmpty(uri.Host) || uri.UserInfo.Length != 0))
            return Results.Problem(statusCode: 400, title: "Укажите HTTPS-ссылку на фотографию.");

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var resource = await LockEditableResource(db, scope, id, ct);
        if (resource is null)
            return Results.NotFound();
        if (resource.IsArchived && input.Enabled)
            return Results.Problem(statusCode: 409, title: "Восстановите ресурс из архива, чтобы открыть доступ.");
        if (input.Enabled && !await OrganizationStructure.AllowsLinks(db, id, ct))
            return OrganizationStructure.LinksDisabled();

        Guid? linkId = input.Enabled ? resource.PublicLinkId ?? Guid.NewGuid() : null;
        if (linkId != resource.PublicLinkId || photoUrl != resource.PublicPhotoUrl)
        {
            await scope.Resources(db).Where(item => item.Id == id).ExecuteUpdateAsync(set => set
                .SetProperty(item => item.PublicLinkId, linkId).SetProperty(item => item.PublicPhotoUrl, photoUrl), ct);
            var details = linkId == resource.PublicLinkId ? "Обновлено фото"
                : linkId.HasValue ? "Доступ по ссылке открыт" : "Доступ по ссылке закрыт";
            Audit.Add(db, resource, Actor(user), "resource.public-link", details, clock.GetUtcNow());
            await db.SaveChangesAsync(ct);
        }
        await transaction.CommitAsync(ct);
        return Results.Ok(new { PublicLinkId = linkId, PublicPhotoUrl = photoUrl });
    }

    internal static async Task<Resource?> LockEditableResource(BookingDb db, OrganizationScope scope, string id,
        CancellationToken ct) =>
        (await db.Resources.FromSqlInterpolated($"""
            SELECT * FROM "Resources" WHERE "Id" = {id} AND "OrganizationId" = {scope.Current.Id} FOR UPDATE
            """).AsNoTracking().ToListAsync(ct)).SingleOrDefault();

    private static async Task<IResult> ListActivity(BookingDb db, OrganizationScope scope, CancellationToken ct) =>
        scope.IsAdmin
            ? Results.Ok(await db.WorkspaceActivities.AsNoTracking().Where(item => item.OrganizationId == scope.Current.Id)
                .OrderByDescending(item => item.Id).Take(50)
                .Select(item => new { item.ResourceName, item.Actor, item.Action, item.Details, item.OccurredAt })
                .ToListAsync(ct))
            : Results.StatusCode(403);

    private static bool ValidResource(ResourceInput input) =>
        input.Name?.Trim().Length is > 0 and <= 100 && input.Location?.Trim().Length is > 0 and <= 140
        && (input.Details?.IsValid() ?? true);

    private static IResult InvalidResource() =>
        Results.Problem(statusCode: 400,
            title: "Проверьте название, расположение, тип, вместимость и список оснащения ресурса.");

    internal static string Actor(ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.GivenName)
        ?? user.Identity?.Name ?? "Администратор";
    private sealed record ResourceInput(string? Name, string? Location, bool IsArchived = false, ResourceDetails? Details = null,
        bool? RequiresApproval = null, string? TemplateKey = null, Guid? TemplateRevision = null);
    private sealed record PublicLinkInput(bool Enabled, string? PhotoUrl);
    private sealed record Occupancy(string ResourceId, DateTimeOffset StartsAt, DateTimeOffset From, DateTimeOffset Until, string Status);
    private sealed record Totals(int Free, int Booked, int Held, int Maintenance, int Pending);
}

internal static class Audit
{
    public static void Add(BookingDb db, Resource resource, string actor, string action, string details, DateTimeOffset now) =>
        Add(db, resource.OrganizationId, resource.Id, resource.Name, actor, action, details, now);

    public static void Add(BookingDb db, string organizationId, string subjectId, string subjectName, string actor,
        string action, string details, DateTimeOffset now) =>
        db.WorkspaceActivities.Add(new WorkspaceActivity
        {
            OrganizationId = organizationId, ResourceId = subjectId, ResourceName = subjectName,
            Actor = actor, Action = action, Details = details, OccurredAt = now
        });
}
