using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace SlotGuard;

internal sealed class GoogleCalendarLink
{
    public required string ResourceId { get; init; }
    public Guid Key { get; set; }
    public Guid Revision { get; set; }
    public required string CalendarId { get; set; }
    public required string Name { get; set; }
    public required string TimeZone { get; set; }
    public string? ProtectedRefreshToken { get; set; }
    public string BusyJson { get; set; } = "[]";
    public DateTimeOffset? ImportedAt { get; set; }
    public DateTimeOffset? ExportedAt { get; set; }
    public DateTimeOffset NextSyncAt { get; set; }
    public string? Error { get; set; }
}

internal sealed record CalendarBusyPeriod(DateTimeOffset Start, DateTimeOffset End)
{
    public bool Intersects(BookingWindow window) => Start < window.OccupiedUntil && window.OccupiedFrom < End;
}

internal sealed record ExternalAvailability(bool Stale, CalendarBusyPeriod[] Periods)
{
    public const string StaleMessage = "Внешний календарь обновляется. Попробуйте немного позже.";
    public const string BusyMessage = "Интервал занят во внешнем календаре.";
    public static readonly ExternalAvailability Open = new(false, []);

    public static ExternalAvailability From(GoogleCalendarLink? link, DateTimeOffset now) =>
        link?.ProtectedRefreshToken is null ? Open : new(
            link.ImportedAt is null || link.ImportedAt < now.AddMinutes(-3),
            JsonSerializer.Deserialize<CalendarBusyPeriod[]>(link.BusyJson, JsonSerializerOptions.Web) ?? []);

    public static async Task<ExternalAvailability> Read(BookingDb db, string resourceId, DateTimeOffset now, CancellationToken ct) =>
        From(await db.GoogleCalendars.AsNoTracking().SingleOrDefaultAsync(link => link.ResourceId == resourceId, ct), now);

    public static async Task<Dictionary<string, ExternalAvailability>> Read(BookingDb db, string[] resourceIds,
        DateTimeOffset now, CancellationToken ct) =>
        (await db.GoogleCalendars.AsNoTracking().Where(link => link.ProtectedRefreshToken != null
            && resourceIds.Contains(link.ResourceId)).ToArrayAsync(ct))
        .ToDictionary(link => link.ResourceId, link => From(link, now));

    public string? Conflict(BookingWindow window) => Stale ? StaleMessage :
        Periods.Any(period => period.Intersects(window)) ? BusyMessage : null;
}
