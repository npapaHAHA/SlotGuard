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
        CancellationToken ct)
    {
        if (!scope.IsAdmin)
            return Results.StatusCode(403);

        var now = clock.GetUtcNow();
        var requests = await (
            from booking in db.Bookings.AsNoTracking()
            join resource in scope.Resources(db) on booking.ResourceId equals resource.Id
            join account in db.Users.AsNoTracking() on booking.OwnerId equals account.Id into owners
            from owner in owners.DefaultIfEmpty()
            where booking.ApprovalStatus == BookingApproval.Pending && booking.CancelledAt == null
                && booking.StartUtc > now
            orderby booking.StartUtc, resource.Name, booking.Id
            select new
            {
                booking.Id, booking.OwnerId, booking.StartUtc, ResourceName = resource.Name,
                resource.Location, Name = owner == null ? null : owner.DisplayName
            }).Take(100).ToListAsync(ct);

        return Results.Ok(requests.Select(item => new ApprovalRequest(item.Id, item.ResourceName, item.Location,
            item.StartUtc, item.Name ?? DemoAccounts.LabelFor(item.OwnerId))));
    }

    private static Task<IResult> Decide(Guid id, Decision input, OrganizationScope scope, SlotBookings bookings,
        ClaimsPrincipal user, CancellationToken ct)
    {
        if (!scope.IsAdmin)
            return Task.FromResult(Results.StatusCode(403));
        if (input.Status is not (BookingApproval.Approved or BookingApproval.Rejected))
            return Task.FromResult(Results.Problem(statusCode: 400, title: "Выберите одобрение или отклонение заявки."));
        var reason = input.Reason?.Trim();
        if (reason is null || reason.Length is 0 or > 240)
            return Task.FromResult(Results.Problem(statusCode: 400, title: "Укажите причину длиной от 1 до 240 символов."));

        return bookings.Decide(id, scope.Current.Id, input.Status, reason, WorkspaceEndpoints.Actor(user), ct);
    }

    internal sealed record Decision(string? Status, string? Reason);
    internal sealed record ApprovalRequest(Guid Id, string ResourceName, string Location, DateTimeOffset StartsAt, string Requester);
}
