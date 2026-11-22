using System.Security.Claims;
using Microsoft.EntityFrameworkCore;

namespace SlotGuard;

internal static class ApprovalEndpoints
{
    public static void MapApprovalEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/approvals", List);
        api.MapPost("/approvals/{id:guid}/decision", Decide);
    }

    private static async Task<IResult> List(BookingDb db, OrganizationScope scope, TimeProvider clock,
        HttpContext context, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        if (!await OrganizationStructure.CanApprove(db, scope, now, ct))
            return Results.StatusCode(403);
        context.Response.Headers.CacheControl = "no-store";

        var pending =
            from booking in OrganizationStructure.Reviewable(db, scope).AsNoTracking()
            join resource in scope.Resources(db) on booking.ResourceId equals resource.Id
            join account in db.Users.AsNoTracking() on booking.OwnerId equals account.Id into owners
            from owner in owners.DefaultIfEmpty()
            where booking.ApprovalStatus == BookingApproval.Pending && booking.CancelledAt == null && booking.StartUtc > now
            orderby booking.StartUtc, resource.Name, booking.Id
            select new
            {
                booking.Id, booking.OwnerId, booking.StartUtc, booking.EndUtc, ResourceName = resource.Name,
                resource.Location, booking.AnswersJson, booking.BundleId, booking.RoutingContext, booking.RoutingReviewerId,
                Name = owner == null ? null : owner.DisplayName
            };
        await using var snapshot = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        var requests = await pending.Take(100).ToListAsync(ct);
        var bundles = requests.Where(item => item.BundleId.HasValue).Select(item => item.BundleId).Distinct().ToArray();
        var bundleRows = bundles.Length == 0 ? [] : await pending.Where(item => bundles.Contains(item.BundleId)).ToListAsync(ct);
        var reviewerNames = await BookingRouting.ReviewerNames(db, scope.Current.Id,
            requests.Where(item => item.RoutingReviewerId.HasValue).Select(item => item.RoutingReviewerId).Distinct().ToArray(), ct);
        await snapshot.CommitAsync(ct);

        var members = bundleRows.ToLookup(item => (item.BundleId, item.OwnerId));
        return Results.Ok(requests.GroupBy(item => (Key: item.BundleId ?? item.Id, item.OwnerId)).Select(group =>
        {
            var item = group.First();
            var bundle = item.BundleId is null ? null : members[(item.BundleId, item.OwnerId)].Select(row =>
                new ApprovalResource(row.ResourceName, row.Location, BookingForm.ReadAnswers(row.AnswersJson))).ToArray();
            return new ApprovalRequest(item.Id, item.ResourceName, item.Location,
                item.StartUtc, item.Name ?? (item.OwnerId == ExternalApiAccess.OwnerOf(scope.Current.Id)
                    ? "Интеграция API" : DemoAccounts.LabelFor(item.OwnerId)), item.EndUtc,
                bundle is null ? BookingForm.ReadAnswers(item.AnswersJson) : null, bundle, RoutedContext.Read(item.RoutingContext),
                item.RoutingReviewerId is { } reviewer ? reviewerNames.GetValueOrDefault(reviewer) : null);
        }));
    }

    private static async Task<IResult> Decide(Guid id, Decision input, OrganizationScope scope, SlotBookings bookings,
        BookingDb db, TimeProvider clock, ClaimsPrincipal user, CancellationToken ct)
    {
        if (!await OrganizationStructure.CanApprove(db, scope, clock.GetUtcNow(), ct))
            return Results.StatusCode(403);
        if (input.Status is not (BookingApproval.Approved or BookingApproval.Rejected))
            return Results.Problem(statusCode: 400, title: "Выберите одобрение или отклонение заявки.");
        var reason = input.Reason?.Trim();
        if (reason is null || reason.Length is 0 or > 240)
            return Results.Problem(statusCode: 400, title: "Укажите причину длиной от 1 до 240 символов.");

        return await bookings.Decide(id, scope.Resources(db), OrganizationStructure.Reviewable(db, scope),
            input.Status, reason, WorkspaceEndpoints.Actor(user), ct);
    }

    internal sealed record Decision(string? Status, string? Reason);
    internal sealed record ApprovalRequest(Guid Id, string ResourceName, string Location, DateTimeOffset StartsAt, string Requester,
        DateTimeOffset EndsAt, BookingAnswer[]? Answers = null, ApprovalResource[]? Bundle = null, RoutedContext? Routing = null,
        string? Reviewer = null);
    internal sealed record ApprovalResource(string ResourceName, string Location, BookingAnswer[] Answers);
}
