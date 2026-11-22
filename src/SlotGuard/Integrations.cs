using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace SlotGuard;

internal sealed class ExternalApiKey
{
    public Guid Id { get; init; }
    public required string OrganizationId { get; init; }
    public required string Name { get; init; }
    public required string TokenHash { get; init; }
    public required string Prefix { get; init; }
    public bool CanWrite { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? RevokedAt { get; set; }
}

internal sealed class WebhookSubscription
{
    public Guid Id { get; init; }
    public required string OrganizationId { get; init; }
    public required string Name { get; init; }
    public required string Url { get; init; }
    public required string ProtectedSecret { get; init; }
    public required string[] Events { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? DisabledAt { get; set; }
}

internal sealed class WebhookDelivery
{
    public Guid Id { get; init; }
    public Guid SubscriptionId { get; init; }
    public Guid EventId { get; init; }
    public required string Type { get; init; }
    public required string Payload { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }
    public DateTimeOffset? FailedAt { get; set; }
    public Guid? Lease { get; set; }
    public int Attempts { get; set; }
    public int? LastStatus { get; set; }
    public string? LastError { get; set; }
}

internal sealed record ExternalBooking(Guid Id, string ResourceId, DateTimeOffset StartsAt, DateTimeOffset EndsAt,
    string Status, Guid? PreviousBookingId, Guid? BundleId, Guid? SeriesId, bool GroupSession)
{
    public static ExternalBooking From(Booking item) => new(item.Id, item.ResourceId, item.StartUtc, item.EndUtc,
        item.ApprovalStatus == BookingApproval.Rejected ? "rejected" : item.CancelledAt is not null ? "cancelled"
            : item.ApprovalStatus == BookingApproval.Pending ? "pending" : "confirmed",
        item.RescheduledFromId, item.BundleId, item.SeriesId, item.SeatCapacity.HasValue);
}

internal static class WebhookEvents
{
    public const string Created = "booking.created";
    public const string Rescheduled = "booking.rescheduled";
    public const string Cancelled = "booking.cancelled";
    public const string Approved = "booking.approved";
    public const string CalendarImported = "calendar.imported";
    public const string Test = "integration.test";
    public static readonly string[] All = [Created, Rescheduled, Cancelled, Approved, CalendarImported];
    public const string ProtectionPurpose = "SlotGuard.WebhookSecret.v1";

    public static Task Enqueue(BookingDb db, string organization, Booking booking, string type,
        DateTimeOffset now, CancellationToken ct)
    {
        if (booking.MaintenanceReason is not null || booking.HoldExpiresAt is not null)
            return Task.CompletedTask;
        return EnqueueSubscribers(db, organization, type, now, id => new
        {
            Id = id, Type = type, Version = 1, OrganizationId = organization, OccurredAt = now, Booking = ExternalBooking.From(booking)
        }, ct);
    }

    public static Task EnqueueImport(BookingDb db, string organization, CalendarImportBatch batch, CancellationToken ct) =>
        EnqueueSubscribers(db, organization, CalendarImported, batch.CreatedAt, id => new
        {
            Id = id, Type = CalendarImported, Version = 1, OrganizationId = organization, OccurredAt = batch.CreatedAt,
            Import = new { batch.Id, batch.ResourceId, batch.Count, batch.Skipped }
        }, ct);

    public static Guid EnqueueTest(BookingDb db, string organization, Guid subscription, DateTimeOffset now)
    {
        var id = Guid.NewGuid();
        var payload = JsonSerializer.Serialize(new
        {
            Id = id, Type = Test, Version = 1, OrganizationId = organization, OccurredAt = now, Test = true
        }, JsonSerializerOptions.Web);
        db.WebhookDeliveries.Add(Delivery(subscription, id, Test, payload, now));
        return id;
    }

    private static async Task EnqueueSubscribers<T>(BookingDb db, string organization, string type, DateTimeOffset now,
        Func<Guid, T> envelope, CancellationToken ct)
    {
        var destinations = await db.WebhookSubscriptions.AsNoTracking()
            .Where(item => item.OrganizationId == organization && item.DisabledAt == null && item.Events.Contains(type))
            .Select(item => item.Id).ToArrayAsync(ct);
        if (destinations.Length == 0)
            return;
        var id = Guid.NewGuid();
        var payload = JsonSerializer.Serialize(envelope(id), JsonSerializerOptions.Web);
        db.WebhookDeliveries.AddRange(destinations.Select(destination => Delivery(destination, id, type, payload, now)));
    }

    private static WebhookDelivery Delivery(Guid subscription, Guid id, string type, string payload, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(), SubscriptionId = subscription, EventId = id, Type = type,
        Payload = payload, CreatedAt = now, NextAttemptAt = now
    };

    public static string RandomSecret(string prefix) =>
        prefix + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    public static string HashKey(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public static string Sign(string secret, string timestamp, string payload) =>
        "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret),
            Encoding.UTF8.GetBytes($"{timestamp}.{payload}")));
}
