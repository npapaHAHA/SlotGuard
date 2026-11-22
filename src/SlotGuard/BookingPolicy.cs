using Microsoft.EntityFrameworkCore;

namespace SlotGuard;

internal sealed record BookingPolicy(int MinimumNoticeMinutes = 0, int? BookingWindowDays = null,
    int? MaxActiveBookings = null, int CancellationNoticeMinutes = 0, int RescheduleNoticeMinutes = 0)
{
    public const int MaximumDays = Schedule.WeekCount * 7;
    public const int MaximumNoticeMinutes = MaximumDays * 24 * 60;
    public static readonly BookingPolicy Standard = new();

    public bool IsValid() => BookingWindowDays is null or >= 1 and <= MaximumDays
        && MaxActiveBookings is null or >= 1 and <= 1000
        && ValidNotice(MinimumNoticeMinutes) && ValidNotice(CancellationNoticeMinutes) && ValidNotice(RescheduleNoticeMinutes)
        && (BookingWindowDays is null || MinimumNoticeMinutes <= BookingWindowDays * 24 * 60);

    public DateTimeOffset Horizon(DateTimeOffset now) => BookingWindowDays is { } days
        ? now.AddDays(days).AddTicks(1) : Schedule.Horizon(now);

    public string? TimeRestriction(DateTimeOffset start, DateTimeOffset now)
    {
        if (start >= Horizon(now))
            return BookingWindowDays is { } days ? $"На этот ресурс можно бронировать максимум на {days} дн. вперёд."
                : "Это время за пределами окна бронирования.";
        return start < now.AddMinutes(MinimumNoticeMinutes)
            ? $"Бронируйте этот ресурс минимум за {MinimumNoticeMinutes} мин. до начала." : null;
    }

    public BookingChangeTimes Changes(DateTimeOffset start, DateTimeOffset now)
    {
        var cancelUntil = start.AddMinutes(-CancellationNoticeMinutes);
        var rescheduleUntil = start.AddMinutes(-RescheduleNoticeMinutes);
        return new BookingChangeTimes(start > now && now <= cancelUntil, start > now && now <= rescheduleUntil,
            cancelUntil, rescheduleUntil);
    }

    [System.Text.Json.Serialization.JsonIgnore]
    public string LimitMessage => $"Достигнут лимит активных броней на этот ресурс ({MaxActiveBookings}).";

    internal static IQueryable<BookingUse> ActiveForLimit(BookingDb db, DateTimeOffset now)
    {
        var active = db.Bookings.AsNoTracking().Where(item => item.CancelledAt == null && item.MaintenanceReason == null
            && item.EndUtc > now);
        var personal = active.Where(item => item.SeatCapacity == null && (item.HoldExpiresAt == null || item.HoldExpiresAt > now))
            .Select(item => new BookingUse { ResourceId = item.ResourceId, OwnerId = item.OwnerId });
        var seats = from seat in db.SeatReservations.AsNoTracking().Active()
                    join session in active on seat.SessionId equals session.Id
                    where seat.Status == SeatStatus.Confirmed || session.StartUtc > now
                    select new BookingUse { ResourceId = session.ResourceId, OwnerId = seat.OwnerId };
        return personal.Concat(seats);
    }

    internal sealed class BookingUse
    {
        public required string ResourceId { get; init; }
        public Guid OwnerId { get; init; }
    }

    public async Task<bool> AtLimit(BookingDb db, string resourceId, Guid owner, DateTimeOffset now, CancellationToken ct) =>
        MaxActiveBookings is { } limit && await ActiveForLimit(db, now)
            .CountAsync(item => item.ResourceId == resourceId && item.OwnerId == owner, ct) >= limit;

    private static bool ValidNotice(int minutes) => minutes is >= 0 and <= MaximumNoticeMinutes;
}

internal sealed record BookingChangeTimes(bool CanCancel, bool CanReschedule, DateTimeOffset CancelUntil, DateTimeOffset RescheduleUntil);

internal static class GuestBookingLimits
{
    public const int Maximum = 12;
    public const string Message = "У гостя уже слишком много активных записей на этот ресурс. Освободите время или дождитесь окончания брони.";

    public static async Task<bool> Reached(BookingDb db, string resourceId, Guid owner, int requested, DateTimeOffset now,
        CancellationToken ct) =>
        await db.Bookings.AsNoTracking().CountAsync(item => item.ResourceId == resourceId && item.OwnerId == owner
            && item.GuestManagement && item.CancelledAt == null && item.EndUtc > now
            && (item.HoldExpiresAt == null || item.HoldExpiresAt > now), ct) + requested > Maximum;
}
