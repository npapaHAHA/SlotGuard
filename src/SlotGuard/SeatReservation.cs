using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SlotGuard;

internal sealed class SeatReservation
{
    public Guid Id { get; init; }
    public Guid SessionId { get; init; }
    public Guid OwnerId { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public string Status { get; set; } = SeatStatus.Pending;
    public string? DecisionReason { get; set; }
    public bool WasApproved { get; set; }
    public string? AnswersJson { get; init; }
    public string? Attendance { get; set; }
    public DateTimeOffset? AttendanceAt { get; set; }
    public string? GuestName { get; init; }
    public string? GuestEmail { get; init; }

    internal static void Configure(EntityTypeBuilder<SeatReservation> seats)
    {
        seats.Property(item => item.Status).HasMaxLength(16);
        seats.Property(item => item.Attendance).HasMaxLength(16);
        seats.Property(item => item.DecisionReason).HasMaxLength(240);
        seats.Property(item => item.WasApproved).HasDefaultValue(false);
        seats.Property(item => item.AnswersJson).HasColumnType("jsonb");
        seats.Property(item => item.GuestName).HasMaxLength(80);
        seats.Property(item => item.GuestEmail).HasMaxLength(254);
        seats.HasOne<Booking>().WithMany().HasForeignKey(item => item.SessionId).OnDelete(DeleteBehavior.Restrict);
        seats.HasIndex(item => new { item.SessionId, item.OwnerId }).IsUnique()
            .HasDatabaseName("ux_group_seats_active_owner").HasFilter(SeatStatus.ActiveFilter);
        seats.HasIndex(item => new { item.OwnerId, item.CreatedAt });
    }
}

internal static class SeatStatus
{
    public const string Pending = "pending";
    public const string Confirmed = "confirmed";
    public const string Waiting = "waiting";
    public const string Rejected = "rejected";
    public const string Cancelled = "cancelled";
    public const string Present = "present";
    public const string NoShow = "no-show";
    public const string ActiveFilter = """
        "Status" IN ('pending', 'confirmed', 'waiting')
        """;

    public static IQueryable<SeatReservation> Active(this IQueryable<SeatReservation> seats) =>
        seats.Where(item => item.Status == Pending || item.Status == Confirmed);

    public static IQueryable<SeatReservation> ActiveOrWaiting(this IQueryable<SeatReservation> seats) =>
        seats.Where(item => item.Status == Pending || item.Status == Confirmed || item.Status == Waiting);
}
