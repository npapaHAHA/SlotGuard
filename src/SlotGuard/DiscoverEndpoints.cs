using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace SlotGuard;

internal static class DiscoverEndpoints
{
    public static void MapDiscover(this WebApplication app)
    {
        app.MapGet("/discover", (IWebHostEnvironment env) => Results.File(
            Path.Combine(env.WebRootPath, "discover.html"), "text/html; charset=utf-8")).ExcludeFromDescription();
        app.MapGet("/api/discover", Search);
    }

    public static void MapDiscoverSettings(this RouteGroupBuilder api) =>
        api.MapPut("/workspace/resources/{id}/discover", Save);

    private static async Task<IResult> Search(string? city, string? text, string? kind, BookingDb db,
        TimeProvider clock, HttpContext context, CancellationToken ct)
    {
        if (city?.Length > 70 || text?.Length > 120 || kind is not (null or "venues" or "events"))
            return Results.Problem(statusCode: 400, title: "Проверьте фильтры каталога.");
        context.Response.Headers.CacheControl = "no-store";
        var locality = (city ?? "").Trim();
        var term = (text ?? "").Trim();
        var shareable = OrganizationStructure.Shareable(db).AsNoTracking().Where(resource => !resource.IsArchived);
        var venues = from resource in shareable
                     join org in db.Organizations on resource.OrganizationId equals org.Id
                     where resource.PublicLinkId != null && resource.DiscoverCity != null
                     orderby resource.DiscoverFeatured descending, resource.Name, resource.Id
                     select new
                     {
                         Id = resource.PublicLinkId, resource.Name, resource.Location, City = resource.DiscoverCity,
                         Organization = org.Name, resource.Type, resource.Capacity, resource.PriceMinor,
                         resource.PublicPhotoUrl, Featured = resource.DiscoverFeatured
                     };
        var now = clock.GetUtcNow();
        var events = from publication in db.PublishedEvents.AsNoTracking()
                     join session in db.Bookings.AsNoTracking() on publication.SessionId equals session.Id
                     join resource in shareable on session.ResourceId equals resource.Id
                     join org in db.Organizations on resource.OrganizationId equals org.Id
                     where publication.Enabled && publication.City != null && session.CancelledAt == null && session.StartUtc > now
                     orderby session.StartUtc, session.Id
                     select new
                     {
                         session.Id, Name = session.SessionTitle, resource.Location, publication.City,
                         Organization = org.Name, session.StartUtc, session.SeatCapacity
                     };
        return Results.Ok(new
        {
            Venues = kind == "events" ? [] : await FirstMatches(venues, item => Match(item.City, locality)
                && (Match(item.Name, term) || Match(item.Organization, term) || Match(item.Location, term)), ct),
            Events = kind == "venues" ? [] : await FirstMatches(events, item => Match(item.City, locality)
                && (Match(item.Name, term) || Match(item.Organization, term)), ct)
        });
    }

    private static bool Match(string? text, string term) =>
        term.Length == 0 || text?.Contains(term, StringComparison.OrdinalIgnoreCase) == true;

    // Keep matching independent of the database collation for both Cyrillic and Latin names.
    private static async Task<T[]> FirstMatches<T>(IQueryable<T> query, Func<T, bool> matches, CancellationToken ct)
    {
        var results = new List<T>(40);
        await foreach (var item in query.AsAsyncEnumerable().WithCancellation(ct))
        {
            if (matches(item)) results.Add(item);
            if (results.Count == 40) break;
        }
        return results.ToArray();
    }

    private static async Task<IResult> Save(string id, Listing input, BookingDb db, OrganizationScope scope,
        ClaimsPrincipal user, TimeProvider clock, CancellationToken ct)
    {
        if (!scope.IsAdmin) return Results.StatusCode(403);
        var city = PublicEvents.Clean(input.City, 70);
        if (input.Enabled && city is null)
            return Results.Problem(statusCode: 422, title: "Укажите город площадки.");
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var resource = await WorkspaceEndpoints.LockEditableResource(db, scope, id, ct);
        if (resource is null) return Results.NotFound();
        if (input.Enabled && (resource.IsArchived || resource.PublicLinkId is null || !await OrganizationStructure.AllowsLinks(db, id, ct)))
            return Results.Problem(statusCode: 422, title: "Сначала опубликуйте ссылку на открытый ресурс.");
        await scope.Resources(db).Where(item => item.Id == id).ExecuteUpdateAsync(set => set
            .SetProperty(item => item.DiscoverCity, input.Enabled ? city : null)
            .SetProperty(item => item.DiscoverFeatured, input.Enabled && input.Featured), ct);
        Audit.Add(db, resource, WorkspaceEndpoints.Actor(user), "discover.listing",
            input.Enabled ? $"Площадка в каталоге · {city}" : "Площадка снята с публикации", clock.GetUtcNow());
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Results.NoContent();
    }

    private sealed record Listing(bool Enabled, string? City, bool Featured);
}
