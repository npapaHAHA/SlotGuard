using System.Data;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace SlotGuard;

internal sealed record RoutedBookingInput(string ResourceId, DateTimeOffset StartsAt, int DurationMinutes, RouteAnswers? Answers,
    Guid? Revision, BookingFormSubmission? BookingForm);

internal static class RoutingEndpoints
{
    private sealed record SuggestInput(RouteAnswers? Answers, DateOnly? Date, string? Time, string? TimeZone, int DurationMinutes);
    private sealed record RuleInput(string? Type, string[]? Equipment, Guid? ReviewerId, Guid? Revision);

    public static void MapBookingRouting(this RouteGroupBuilder api)
    {
        api.MapPost("/routing/suggest", Suggest);
        var management = api.MapGroup("/workspace/routing");
        management.MapGet("", Get);
        management.MapPut("/{purpose}", Save);
    }

    private static async Task<IResult> Suggest(SuggestInput input, BookingDb db, OrganizationScope scope,
        ClaimsPrincipal user, TimeProvider clock, CancellationToken ct)
    {
        if (!BookingRouting.ValidAnswers(input.Answers))
            return InvalidAnswers();
        if (!Schedule.Durations.Contains(input.DurationMinutes))
            return Results.Problem(statusCode: 400, title: "Выберите 30, 60 или 90 минут.");
        if (!Schedule.TryZone(input.TimeZone, out var zone))
            return BookingEndpoints.InvalidTimeZone();
        var now = clock.GetUtcNow();
        if (AvailabilitySearch.ReadStart(input.Date, null, input.Time, zone, now, out var starts) is { } invalid)
            return invalid;

        var owner = DemoAccounts.OwnerId(user);
        await using var snapshot = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var route = await BookingRouting.Resolve(db, scope.Current.Id, input.Answers!, owner, ct);
        var matches = AvailabilitySearch.Filter(scope.Resources(db), route.Type, route.People, route.Equipment);
        var result = await AvailabilitySearch.Search(db, matches, starts, input.DurationMinutes, owner, now, false, ct);
        var reviewer = await BookingRouting.Name(db, route.ReviewerId, ct);
        await snapshot.CommitAsync(ct);

        return Results.Ok(new
        {
            route.Type, route.Revision, route.RequiresReview, route.People, route.Equipment, Reviewer = reviewer,
            Purpose = route.Purpose.Name, result.Requested, result.DurationMinutes, result.Alternatives, result.BookingMessage,
            Exact = result.Exact.OrderBy(item => !route.RequiresReview && item.Resource.RequiresApproval)
                .ThenBy(item => item.Resource.Capacity ?? int.MaxValue).Take(6)
        });
    }

    private static async Task<IResult> Get(BookingDb db, OrganizationScope scope, HttpContext context, CancellationToken ct)
    {
        if (!scope.IsAdmin)
            return Results.StatusCode(403);
        context.Response.Headers.CacheControl = "no-store";
        var rules = await db.BookingRouteRules.AsNoTracking().Where(item => item.OrganizationId == scope.Current.Id)
            .ToDictionaryAsync(item => item.Purpose, ct);
        var members = await (
            from membership in db.Memberships.AsNoTracking()
            join account in db.Users.AsNoTracking() on membership.OwnerId equals account.Id into names
            from account in names.DefaultIfEmpty()
            where membership.OrganizationId == scope.Current.Id
            select new { membership.OwnerId, membership.Role, Name = account == null ? null : account.DisplayName }).ToArrayAsync(ct);
        return Results.Ok(new
        {
            Rules = BookingRouting.Purposes.Select(purpose =>
            {
                rules.TryGetValue(purpose.Key, out var rule);
                return new { Purpose = purpose.Key, purpose.Name, purpose.DefaultType, Type = rule?.Type ?? purpose.DefaultType,
                    Equipment = rule?.Equipment ?? [], ReviewerId = rule?.ReviewerId, Revision = rule?.Revision };
            }),
            Reviewers = members.Select(item => new { Id = item.OwnerId, item.Role, Name = item.Name ?? DemoAccounts.LabelFor(item.OwnerId) })
                .OrderBy(item => item.Name, StringComparer.Ordinal)
        });
    }

    private static async Task<IResult> Save(string purpose, RuleInput input, BookingDb db, OrganizationScope scope,
        CalendarUpdates updates, CancellationToken ct)
    {
        if (!scope.IsAdmin)
            return Results.StatusCode(403);
        if (BookingRouting.FindPurpose(purpose) is null || input.Type is null || !ResourceTypes.IsValid(input.Type)
            || !ResourceDetails.ValidEquipment(input.Equipment))
            return Results.Problem(statusCode: 400, title: "Укажите цель, тип ресурса и корректный список оснащения.");
        var organizationId = scope.Current.Id;
        if (input.ReviewerId is { } reviewer && !await db.Memberships.AsNoTracking()
            .AnyAsync(item => item.OwnerId == reviewer && item.OrganizationId == organizationId, ct))
            return Results.Problem(statusCode: 400, title: "Выберите согласующего из этой организации.");

        var existing = await db.BookingRouteRules.SingleOrDefaultAsync(item => item.OrganizationId == organizationId && item.Purpose == purpose, ct);
        if (existing?.Revision != input.Revision)
            return RuleChanged();
        var equipment = ResourceDetails.NormalizeEquipment(input.Equipment);
        if (existing is null)
            db.BookingRouteRules.Add(new BookingRouteRule { OrganizationId = organizationId, Purpose = purpose,
                Type = input.Type, Equipment = equipment, ReviewerId = input.ReviewerId, Revision = Guid.NewGuid() });
        else
        {
            if (existing.Type == input.Type && existing.ReviewerId == input.ReviewerId && existing.Equipment.SequenceEqual(equipment))
                return Results.NoContent();
            existing.Type = input.Type;
            existing.Equipment = equipment;
            existing.ReviewerId = input.ReviewerId;
            existing.Revision = Guid.NewGuid();
        }
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { return RuleChanged(); }
        catch (DbUpdateException error) when (error.InnerException is PostgresException { ConstraintName: "PK_BookingRouteRules" })
        {
            return RuleChanged();
        }
        await updates.Publish(organizationId, "RoutingChanged");
        return Results.NoContent();
    }

    internal static IResult InvalidAnswers() =>
        Results.Problem(statusCode: 400, title: "Выберите цель и проверьте число участников и оснащение.");

    internal static IResult RuleChanged() =>
        Results.Problem(statusCode: 409, title: "Правило подбора изменилось. Обновите варианты.");
}
