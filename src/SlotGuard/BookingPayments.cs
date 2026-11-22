using System.Security.Claims;
using Microsoft.EntityFrameworkCore;

namespace SlotGuard;

internal sealed record BookingPaymentView(int AmountMinor, string Status, DateTimeOffset? ExpiresAt);

internal static class BookingPayments
{
    public const string Awaiting = "awaiting_payment";
    public const string Paid = "paid";
    public const string Refunded = "refunded";
    public const string Expired = "payment_expired";
    public const string Cancelled = "payment_cancelled";

    public static BookingPaymentView? Describe(Booking booking, DateTimeOffset now) =>
        booking.PriceMinor is not { } amount ? null : new(amount,
            booking.PaymentRefundedAt is not null ? Refunded : booking.PaidAt is not null ? Paid
            : booking.HoldExpiresAt <= (booking.CancelledAt ?? now) ? Expired
            : booking.CancelledAt is not null ? Cancelled : Awaiting, booking.PaidAt is null ? booking.HoldExpiresAt : null);

    public static IResult OnlyPublic() => Results.Problem(statusCode: 422,
        title: "Платную бронь можно оформить на странице ресурса. Оплата доступна для отдельных бронирований.");

    public static IResult QuoteChanged() => Results.Problem(statusCode: 422,
        title: "Цена изменилась. Проверьте новую стоимость перед записью.",
        extensions: new Dictionary<string, object?> { ["priceChanged"] = true });

    public static IResult HoldEnded() => Results.Problem(statusCode: 409, title: "Резерв на оплату больше не действует.");

    public static void MapBookingPrices(this RouteGroupBuilder api) =>
        api.MapPut("/workspace/resources/{id}/price", SavePrice);

    private static async Task<IResult> SavePrice(string id, PriceInput input, BookingDb db, OrganizationScope scope,
        ClaimsPrincipal user, TimeProvider clock, CalendarUpdates updates, CancellationToken ct)
    {
        if (!scope.IsAdmin) return Results.StatusCode(403);
        if (input.PriceMinor is < 100 or > 100_000_000)
            return Results.Problem(statusCode: 400, title: "Укажите цену от 1 до 1 000 000 ₽.");

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var resource = await WorkspaceEndpoints.LockEditableResource(db, scope, id, ct);
        if (resource is null || resource.IsArchived)
            return Results.NotFound();
        if (input.PriceMinor == resource.PriceMinor)
            return Results.NoContent();
        await scope.Resources(db).Where(item => item.Id == id)
            .ExecuteUpdateAsync(set => set.SetProperty(item => item.PriceMinor, input.PriceMinor), ct);
        Audit.Add(db, resource, WorkspaceEndpoints.Actor(user), "payment.price",
            input.PriceMinor is { } amount ? Format(amount) : "Цена снята", clock.GetUtcNow());
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        await updates.Publish(scope.Current.Id);
        return Results.NoContent();
    }

    public static string Format(int amount) => FormattableString.Invariant($"{amount / 100m:0.00} RUB");
    private sealed record PriceInput(int? PriceMinor);
}

internal sealed partial class SlotBookings
{
    public async Task<IResult> Pay(Guid id, Guid owner, IQueryable<Resource> allowed, CancellationToken ct)
    {
        var target = await ScopedBooking(id, owner, allowed).Where(item => item.Booking.PriceMinor != null)
            .Select(item => item.Resource.Id).SingleOrDefaultAsync(ct);
        if (target is null) return Results.NotFound();

        return await InResource(target, async (now, resource, _) =>
        {
            if (!await allowed.AnyAsync(item => item.Id == target, ct))
                return Results.NotFound();
            var booking = await db.Bookings.SingleAsync(item => item.Id == id, ct);
            if (booking.CancelledAt is not null || booking.PaidAt is null && booking.HoldExpiresAt <= now)
                return BookingPayments.HoldEnded();
            if (booking.PaidAt is not null)
                return Results.Ok(PublicBookingEndpoints.Describe(booking, resource, now));
            if (booking.PrivateLinkId is { } link && !await db.PrivateBookingLinks.AnyAsync(item => item.Id == link
                && item.RevokedAt == null && item.ExpiresAt > now, ct))
                return BookingPayments.HoldEnded();
            if (await ExternalConflict(resource.Id, booking.Window, now, ct) is { } unavailable)
                return unavailable;

            booking.PaidAt = now;
            booking.HoldExpiresAt = null;
            if (resource.RequiresApproval)
                booking.ApprovalStatus = BookingApproval.Pending;
            Audit.Add(db, resource, "Демо-касса", "payment.paid", BookingPayments.Format(booking.PriceMinor!.Value), now);
            await QueueConfirmation(booking, now, ct);
            await WebhookEvents.Enqueue(db, resource.OrganizationId, booking, WebhookEvents.Created, now, ct);
            return Results.Ok(PublicBookingEndpoints.Describe(booking, resource, now));
        }, ct);
    }
}
