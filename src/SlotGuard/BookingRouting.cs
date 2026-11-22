using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace SlotGuard;

internal sealed class BookingRouteRule
{
    public required string OrganizationId { get; init; }
    public required string Purpose { get; init; }
    public required string Type { get; set; }
    public string[] Equipment { get; set; } = [];
    public Guid? ReviewerId { get; set; }
    public Guid Revision { get; set; }
}

internal sealed record RoutePurpose(string Key, string Name, string DefaultType);
internal sealed record RouteAnswers(string? Purpose, int? People, string[]? Equipment);
internal sealed record RoutedContext(string Purpose, int? People, string[] Equipment, bool RequiresReview)
{
    public string Write() => JsonSerializer.Serialize(this, JsonSerializerOptions.Web);
    public static RoutedContext? Read(string? json) =>
        json is null ? null : JsonSerializer.Deserialize<RoutedContext>(json, JsonSerializerOptions.Web);
}

internal sealed record RouteAssignment(RoutePurpose Purpose, string Type, int? People, string[] Equipment,
    Guid? ReviewerId, bool RequiresReview, Guid? Revision)
{
    public bool Matches(Resource resource) => resource.Type == Type
        && (People is null || resource.Capacity >= People)
        && Equipment.All(resource.Equipment.Contains);
    public RoutedContext Context => new(Purpose.Name, People, Equipment, RequiresReview);
}

internal static class BookingRouting
{
    public static readonly RoutePurpose[] Purposes =
    [
        new("meeting", "Встреча", ResourceTypes.Room),
        new("recording", "Запись", ResourceTypes.Studio),
        new("workshop", "Практикум", ResourceTypes.Room),
        new("equipment", "Оборудование", ResourceTypes.Equipment)
    ];

    public static RoutePurpose? FindPurpose(string? key) => Purposes.SingleOrDefault(purpose => purpose.Key == key);

    public static bool ValidAnswers(RouteAnswers? answers) =>
        answers is { } && FindPurpose(answers.Purpose) is not null
        && (answers.Purpose == "equipment" ? answers.People is null : answers.People is >= 1 and <= 10000)
        && ResourceDetails.ValidEquipment(answers.Equipment);

    public static async Task<RouteAssignment> Resolve(BookingDb db, string organizationId, RouteAnswers answers,
        Guid owner, CancellationToken ct)
    {
        var purpose = FindPurpose(answers.Purpose)!;
        var rule = await db.BookingRouteRules.AsNoTracking()
            .SingleOrDefaultAsync(item => item.OrganizationId == organizationId && item.Purpose == purpose.Key, ct);
        var reviewer = rule?.ReviewerId;
        var assigned = reviewer is { } employee && employee != owner
            && await db.Memberships.AsNoTracking().AnyAsync(item => item.OwnerId == employee && item.OrganizationId == organizationId, ct)
            ? reviewer : null;
        var equipment = ResourceDetails.NormalizeEquipment(answers.Equipment)
            .Union(rule?.Equipment ?? [], StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        return new RouteAssignment(purpose, rule?.Type ?? purpose.DefaultType, answers.People, equipment,
            assigned, reviewer.HasValue, rule?.Revision);
    }

    public static IQueryable<string> ReviewingOrganizations(BookingDb db, Guid owner, DateTimeOffset now)
    {
        var routed = db.BookingRouteRules.AsNoTracking().Where(item => item.ReviewerId == owner).Select(item => item.OrganizationId)
            .Union(from booking in db.Bookings.AsNoTracking()
                   join resource in db.Resources.AsNoTracking() on booking.ResourceId equals resource.Id
                   where booking.RoutingReviewerId == owner && booking.OwnerId != owner
                       && booking.ApprovalStatus == BookingApproval.Pending && booking.CancelledAt == null && booking.StartUtc > now
                   select resource.OrganizationId);
        return db.Memberships.AsNoTracking().Where(member => member.OwnerId == owner && routed.Contains(member.OrganizationId))
            .Select(member => member.OrganizationId);
    }

    public static Task<string?> Name(BookingDb db, Guid? id, CancellationToken ct) =>
        id is { } owner ? ReadName(db, owner, ct) : Task.FromResult<string?>(null);

    private static async Task<string?> ReadName(BookingDb db, Guid id, CancellationToken ct) =>
        await db.Users.AsNoTracking().Where(item => item.Id == id).Select(item => item.DisplayName).SingleOrDefaultAsync(ct)
        ?? DemoAccounts.LabelFor(id);

    public static async Task<Dictionary<Guid, string>> ReviewerNames(BookingDb db, string organizationId, Guid?[] ids,
        CancellationToken ct)
    {
        if (ids.Length == 0)
            return [];
        var names = await (from membership in db.Memberships.AsNoTracking()
                           join account in db.Users.AsNoTracking() on membership.OwnerId equals account.Id into accounts
                           from person in accounts.DefaultIfEmpty()
                           where membership.OrganizationId == organizationId && ids.Contains(membership.OwnerId)
                           select new { membership.OwnerId, Name = person == null ? null : person.DisplayName }).ToArrayAsync(ct);
        return names.ToDictionary(item => item.OwnerId, item => item.Name ?? DemoAccounts.LabelFor(item.OwnerId));
    }

    public static bool NeedsReview(Booking booking, Resource resource) =>
        resource.RequiresApproval || RoutedContext.Read(booking.RoutingContext)?.RequiresReview == true;
}
