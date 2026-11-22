using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace SlotGuard;

internal sealed class GoogleCalendarWorker(IServiceScopeFactory scopes, TimeProvider clock, IDataProtectionProvider protection,
    IOptions<GoogleCalendarOptions> options, IMemoryCache cache, CalendarUpdates updates,
    ILogger<GoogleCalendarWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Configured)
            return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(8));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await RunDue(stoppingToken);
            }
            catch (Exception error) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning("Google Calendar worker will retry ({ErrorType}).", error.GetType().Name);
            }
        }
    }

    private async Task RunDue(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BookingDb>();
        var client = scope.ServiceProvider.GetRequiredService<GoogleCalendarClient>();
        var now = clock.GetUtcNow();
        var due = await db.GoogleCalendars.AsNoTracking().Where(link => link.ProtectedRefreshToken != null && link.NextSyncAt <= now
            && db.Resources.Any(resource => resource.Id == link.ResourceId && !resource.IsArchived))
            .OrderBy(link => link.NextSyncAt).Take(12).ToArrayAsync(ct);
        foreach (var link in due)
        {
            var current = db.GoogleCalendars.Where(item => item.ResourceId == link.ResourceId && item.Revision == link.Revision);
            var started = clock.GetUtcNow();
            var lease = started.AddMinutes(2);
            if (await current.Where(item => item.NextSyncAt <= started)
                    .ExecuteUpdateAsync(set => set.SetProperty(item => item.NextSyncAt, lease), ct) == 0)
                continue;
            current = current.Where(item => item.NextSyncAt == lease);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(35));
            try
            {
                await Synchronize(db, client, scope.ServiceProvider.GetRequiredService<SlotBookings>(), link, current,
                    started, timeout.Token);
                await current.ExecuteUpdateAsync(set => set.SetProperty(item => item.ExportedAt, clock.GetUtcNow())
                    .SetProperty(item => item.Error, (string?)null).SetProperty(item => item.NextSyncAt, started.AddMinutes(1)), ct);
            }
            catch (Exception error) when (!ct.IsCancellationRequested)
            {
                cache.Remove(link.Revision);
                var message = error is GoogleCalendarException google ? google.Message : "Связь с Google прервана. Повторим синхронизацию.";
                await current.ExecuteUpdateAsync(set => set.SetProperty(item => item.Error, message)
                    .SetProperty(item => item.NextSyncAt, started.AddMinutes(1)), ct);
            }
        }
    }

    private async Task Synchronize(BookingDb db, GoogleCalendarClient client, SlotBookings bookings, GoogleCalendarLink link,
        IQueryable<GoogleCalendarLink> current, DateTimeOffset started, CancellationToken ct)
    {
        var bearer = await cache.GetOrCreateAsync(link.Revision, async entry =>
        {
            var refresh = protection.CreateProtector(GoogleCalendarEndpoints.TokenPurpose).Unprotect(link.ProtectedRefreshToken!);
            var token = await client.Refresh(refresh, ct);
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(Math.Max(1, token.ExpiresIn - 90));
            return token.AccessToken;
        }) ?? throw new GoogleCalendarException("Google не вернул ключ доступа.");
        var events = (await client.Events(bearer, link.CalendarId, started, ct))
            .Where(item => item.Status != "cancelled").ToArray();
        var owned = events.Where(item => GoogleCalendarClient.ManagedBy(item, link.Key)).ToArray();
        var zone = TimeZoneInfo.FindSystemTimeZoneById(link.TimeZone);
        var busy = Merge(events.Where(item => item.Transparency != "transparent" && !GoogleCalendarClient.ManagedBy(item, link.Key)
                && item.Attendees?.Any(attendee => attendee is { Self: true, ResponseStatus: "declined" }) != true)
            .Select(item => new CalendarBusyPeriod(Instant(item.Start, zone), Instant(item.End, zone))));
        var json = JsonSerializer.Serialize(busy, JsonSerializerOptions.Web);
        Resource resource;
        await using (var transaction = await db.Database.BeginTransactionAsync(ct))
        {
            resource = await ResourceBookingLock.Acquire(db, link.ResourceId, ct);
            if (resource.IsArchived || await current.ExecuteUpdateAsync(set => set.SetProperty(item => item.BusyJson, json)
                    .SetProperty(item => item.ImportedAt, started), ct) == 0)
                return;
            await transaction.CommitAsync(ct);
        }
        var previous = ExternalAvailability.From(link, started);
        if (previous.Stale || !previous.Periods.SequenceEqual(busy))
        {
            await updates.Publish(resource.OrganizationId);
            await bookings.ReevaluateWaitlist(resource.Id, ct);
        }

        var confirmed = await db.Bookings.AsNoTracking().Where(item => item.ResourceId == link.ResourceId
            && item.StartUtc < Schedule.MaximumHorizon(started) && item.EndUtc > started && item.CancelledAt == null
            && item.HoldExpiresAt == null && item.MaintenanceReason == null
            && (item.ApprovalStatus == null || item.ApprovalStatus == BookingApproval.Approved)).ToArrayAsync(ct);
        var existing = owned.ToLookup(GoogleCalendarClient.BookingId);
        var desired = confirmed.Select(item => item.Id).ToHashSet();
        if (!await current.AnyAsync(ct))
            return;
        foreach (var booking in confirmed)
        {
            var matching = existing[booking.Id].OrderBy(item => item.Id).ToArray();
            if (matching.Length == 0)
                await client.Export(bearer, link, resource, booking, ct);
            else
            {
                var kept = matching[0];
                if (kept.Start?.DateTime != booking.StartUtc || kept.End?.DateTime != booking.EndUtc)
                    await client.UpdateTime(bearer, link.CalendarId, kept, booking, ct);
                foreach (var duplicate in matching.Skip(1))
                    await client.Delete(bearer, link.CalendarId, duplicate, ct);
            }
        }
        foreach (var old in owned.Where(item => GoogleCalendarClient.BookingId(item) is { } id && !desired.Contains(id)))
            await client.Delete(bearer, link.CalendarId, old, ct);
    }

    private static DateTimeOffset Instant(GoogleEventTime? value, TimeZoneInfo zone)
    {
        if (value?.DateTime is { } time)
            return time.ToUniversalTime();
        if (!DateOnly.TryParseExact(value?.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            throw new GoogleCalendarException("У события Google нет корректного времени.");
        for (var minute = 0; minute <= 1440; minute++)
        {
            var local = date.ToDateTime(TimeOnly.MinValue).AddMinutes(minute);
            var possible = Schedule.Instants(DateOnly.FromDateTime(local), TimeOnly.FromDateTime(local), zone);
            if (possible.Length > 0)
                return possible[0];
        }
        throw new GoogleCalendarException("Не удалось определить начало дня в часовом поясе Google.");
    }

    private static CalendarBusyPeriod[] Merge(IEnumerable<CalendarBusyPeriod> events)
    {
        var result = new List<CalendarBusyPeriod>();
        foreach (var item in events.Where(item => item.Start < item.End).OrderBy(item => item.Start))
        {
            if (result.Count == 0 || result[^1].End < item.Start)
                result.Add(item);
            else if (result[^1].End < item.End)
                result[^1] = result[^1] with { End = item.End };
        }
        return result.ToArray();
    }
}
