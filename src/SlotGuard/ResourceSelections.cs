using System.Data;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;

namespace SlotGuard;

internal sealed record ResourceSelection(string? Mode, string?[]? ResourceIds, DateTimeOffset StartsAt,
    int DurationMinutes, Guid? RequestId = null, string? PreferredId = null, bool? ExpectedApproval = null,
    Dictionary<string, BookingFormSubmission>? Forms = null);

internal sealed record SelectionResource(string Id, string Name, string Location, BookingForm Form);
internal sealed record SelectionPreview(SelectionResource[] Resources, bool RequiresApproval);
internal sealed record SelectionReceipt(Guid? BundleId, SelectionBooking[] Bookings);
internal sealed record SelectionBooking(CreatedBooking Booking, string ResourceName);

internal static class ResourceSelectionEndpoints
{
    public static void MapResourceSelections(this RouteGroupBuilder api)
    {
        var selection = api.MapGroup("/booking-selections");
        selection.MapPost("/preview", Preview);
        selection.MapPost("", Create);
    }

    private static Task<IResult> Preview(ResourceSelection input, SlotBookings bookings, BookingDb db,
        OrganizationScope scope, ClaimsPrincipal user, CancellationToken ct) =>
        bookings.PreviewSelection(scope.Resources(db), DemoAccounts.OwnerId(user), input, ct);

    private static Task<IResult> Create(ResourceSelection input, SlotBookings bookings, BookingDb db,
        OrganizationScope scope, ClaimsPrincipal user, CancellationToken ct) =>
        bookings.BookSelection(scope.Resources(db), DemoAccounts.OwnerId(user), input, ct);
}

internal sealed partial class SlotBookings
{
    private const int MaxSelectionCandidates = 32;
    internal const int MaxBundleResources = 6;

    public async Task<IResult> PreviewSelection(IQueryable<Resource> allowed, Guid owner, ResourceSelection input,
        CancellationToken ct)
    {
        if (ValidateSelection(input) is { } invalid)
            return invalid;

        await using var snapshot = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var resources = await SelectionResources(allowed, input, ct);
        if (resources.Length != input.ResourceIds!.Length)
            return Results.NotFound();
        if (resources.Any(item => item.PriceMinor.HasValue))
            return BookingPayments.OnlyPublic();
        var now = clock.GetUtcNow();
        if (!Schedule.InBookableHorizon(input.StartsAt, now))
            return InvalidSlot();

        var candidates = await FreeCandidates(LockSchedules(resources), owner, input, now, ct);
        if (input.Mode == "auto")
            candidates = candidates.Take(1).ToArray();
        if (candidates.Length < (input.Mode == "auto" ? 1 : resources.Length))
            return SelectionConflict();

        var preview = new SelectionPreview(candidates.Select(item =>
        {
            var resource = item.Resource;
            return new SelectionResource(resource.Id, resource.Name, resource.Location, BookingForm.Read(resource));
        }).ToArray(), candidates.Any(item => item.Resource.RequiresApproval));
        await snapshot.CommitAsync(ct);
        return Results.Ok(preview);
    }

    public async Task<IResult> BookSelection(IQueryable<Resource> allowed, Guid owner, ResourceSelection input,
        CancellationToken ct)
    {
        if (ValidateSelection(input) is { } invalid)
            return invalid;
        var auto = input.Mode == "auto";
        if (input.RequestId is not { } requestId || requestId == Guid.Empty || input.ExpectedApproval is null
            || auto && !input.ResourceIds!.Contains(input.PreferredId, StringComparer.Ordinal)
            || input.Forms?.Keys.Any(id => auto ? id != input.PreferredId : !input.ResourceIds!.Contains(id)) == true)
            return Problem(400, "Сначала откройте подтверждение подбора.");

        var resources = await SelectionResources(allowed, input, ct);
        if (resources.Length != input.ResourceIds!.Length)
            return Results.NotFound();
        var resourceIds = resources.Select(item => item.Id).ToArray();
        var start = input.StartsAt.ToUniversalTime();

        return await InResources(resourceIds, async (now, locked) =>
        {
            if (locked.Any(item => item.Resource.PriceMinor.HasValue))
                return BookingPayments.OnlyPublic();
            if (await allowed.CountAsync(item => resourceIds.Contains(item.Id) && !item.IsArchived, ct) != resourceIds.Length)
                return Results.NotFound();
            var receipts = await (from booking in db.Bookings.AsNoTracking()
                join resource in allowed on booking.ResourceId equals resource.Id
                where booking.OwnerId == owner && booking.RescheduledFromId == null
                    && (auto ? booking.Id == requestId && booking.BundleId == null : booking.BundleId == requestId)
                select new BookingAndResource { Booking = booking, Resource = resource }).ToArrayAsync(ct);
            if (receipts.Length != 0)
            {
                if (receipts.Length != (auto ? 1 : resourceIds.Length)
                    || receipts.Any(item => !resourceIds.Contains(item.Resource.Id) || item.Booking.StartUtc != start
                        || BookingDuration(item.Booking) != input.DurationMinutes || item.Booking.CancelledAt != null))
                    return Problem(409, "Эта запись уже изменилась. Обновите поиск.");
                return Results.Ok(new SelectionReceipt(auto ? null : requestId, receipts.Select(item =>
                    new SelectionBooking(Receipt(item.Booking, Schedule.Read(item.Resource), now), item.Resource.Name)).ToArray()));
            }

            if (!Schedule.InBookableHorizon(start, now))
                return InvalidSlot();
            if (auto && await db.Bookings.AnyAsync(item => item.Id == requestId, ct))
                return SelectionConflict();

            var candidates = await FreeCandidates(locked, owner, input, now, ct);
            if (auto)
            {
                var preferred = candidates.FirstOrDefault(item => item.Resource.Id == input.PreferredId);
                if (preferred is not null)
                    candidates = [preferred];
                else
                {
                    var original = locked.Single(item => item.Resource.Id == input.PreferredId);
                    if (BookingForm.Read(original.Resource).Questions.Length > 0 || input.Forms?.Count > 0)
                        return SelectionConflict();
                    candidates = candidates.Where(item => item.Resource.RequiresApproval == input.ExpectedApproval
                        && BookingForm.Read(item.Resource).Questions.Length == 0).Take(1).ToArray();
                }
            }
            if (candidates.Length != (auto ? 1 : resources.Length))
                return SelectionConflict();
            var requiresApproval = candidates.Any(item => item.Resource.RequiresApproval);
            if (requiresApproval != input.ExpectedApproval)
                return Problem(409, "Правила согласования изменились. Проверьте состав брони.");

            var answers = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var item in candidates)
            {
                var id = item.Resource.Id;
                var form = BookingForm.Read(item.Resource);
                var submitted = input.Forms?.GetValueOrDefault(id);
                if (form.Questions.Length > 0 && submitted is null)
                    return Problem(409, "У ресурса появились вопросы. Проверьте форму бронирования.");
                if (form.Check(submitted, out var submission) is { } error)
                    return error;
                answers.Add(id, submission);
            }

            Guid? bundleId = auto ? null : requestId;
            var created = new List<SelectionBooking>(candidates.Length);
            var email = requiresApproval ? null : await ConfirmedEmail(owner, ct);
            foreach (var item in candidates)
            {
                var booking = NewBooking(item.Resource.Id, item.Schedule.Rules.Window(start, input.DurationMinutes), owner,
                    guestEmail: email, bookingId: auto ? requestId : null, bundleId: bundleId);
                booking.AnswersJson = answers[item.Resource.Id];
                if (requiresApproval)
                    booking.ApprovalStatus = BookingApproval.Pending;
                db.Bookings.Add(booking);
                BookingEmailQueue.Enqueue(db, booking, now);
                await WebhookEvents.Enqueue(db, item.Resource.OrganizationId, booking, WebhookEvents.Created, now, ct);
                created.Add(new SelectionBooking(Receipt(booking, item.Schedule, now), item.Resource.Name));
            }
            return Results.Json(new SelectionReceipt(bundleId, created.ToArray()), statusCode: 201);
        }, ct, requestId);
    }

    private static IResult? ValidateSelection(ResourceSelection input)
    {
        var count = input.ResourceIds?.Length ?? 0;
        if (input.Mode is not ("auto" or "bundle")
            || count < (input.Mode == "auto" ? 1 : 2)
            || count > (input.Mode == "auto" ? MaxSelectionCandidates : MaxBundleResources)
            || input.ResourceIds!.Any(id => string.IsNullOrWhiteSpace(id) || id.Length > 64)
            || input.ResourceIds!.Distinct(StringComparer.Ordinal).Count() != count
            || !Schedule.Durations.Contains(input.DurationMinutes))
            return Problem(400, "Выберите подходящие ресурсы и длительность. В комплекте может быть от 2 до 6 ресурсов.");
        return null;
    }

    private static Task<Resource[]> SelectionResources(IQueryable<Resource> allowed, ResourceSelection input,
        CancellationToken ct) => allowed.AsNoTracking().Where(item => input.ResourceIds!.Contains(item.Id) && !item.IsArchived)
            .ToArrayAsync(ct);

    private static LockedResource[] LockSchedules(Resource[] resources) =>
        resources.Select(resource => new LockedResource(resource, Schedule.Read(resource))).ToArray();

    private async Task<LockedResource[]> FreeCandidates(LockedResource[] items, Guid owner, ResourceSelection input,
        DateTimeOffset now, CancellationToken ct)
    {
        var start = input.StartsAt.ToUniversalTime();
        var duration = input.DurationMinutes;
        var eligible = items.Where(item => !item.Resource.IsArchived && Schedule.CanBook(start, now, item.Schedule, duration))
            .ToArray();
        if (eligible.Length == 0)
            return [];
        var ids = eligible.Select(item => item.Resource.Id).ToArray();
        var lowerBound = start.AddMinutes(-60);
        var upperBound = start.AddMinutes(duration + 60);
        var overlapping = (await db.Bookings.AsNoTracking().Where(item => ids.Contains(item.ResourceId)
            && item.CancelledAt == null && item.OccupiedFromUtc < upperBound && item.OccupiedUntilUtc > lowerBound)
            .ToArrayAsync(ct)).ToLookup(item => item.ResourceId);
        var limited = eligible.Where(item => item.Schedule.Policies.MaxActiveBookings.HasValue)
            .Select(item => item.Resource.Id).ToArray();
        var used = limited.Length == 0 ? [] : await BookingPolicy.ActiveForLimit(db, now)
            .Where(item => item.OwnerId == owner && limited.Contains(item.ResourceId))
            .GroupBy(item => item.ResourceId).ToDictionaryAsync(group => group.Key, group => group.Count(), ct);
        var external = await ExternalAvailability.Read(db, ids, now, ct);

        return eligible.Where(item =>
        {
            var id = item.Resource.Id;
            var window = item.Schedule.Rules.Window(start, duration);
            return (item.Schedule.Policies.MaxActiveBookings is not { } max || used.GetValueOrDefault(id) < max)
                && !overlapping[id].Any(booking => booking.Window.Overlaps(window))
                && external.GetValueOrDefault(id)?.Conflict(window) is null;
        }).OrderBy(item => item.Resource.RequiresApproval).ThenBy(item => item.Resource.Capacity ?? int.MaxValue)
            .ThenBy(item => item.Resource.SortOrder).ThenBy(item => item.Resource.Id, StringComparer.Ordinal).ToArray();
    }

    private static IResult SelectionConflict() =>
        Problem(409, "Доступность изменилась. Проверьте ресурсы и выберите свободный вариант.");
}
