using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace SlotGuard;

internal static class IntegrationEndpoints
{
    private sealed record KeyInput(string? Name, string? Scope);
    private sealed record WebhookInput(string? Name, string? Url, string[]? Events);

    public static void MapIntegrationSettings(this RouteGroupBuilder workspace)
    {
        var admin = workspace.MapGroup("/integrations");
        admin.MapGet("", Get);
        admin.MapPost("/keys", CreateKey);
        admin.MapDelete("/keys/{id:guid}", RevokeKey);
        admin.MapPost("/webhooks", CreateWebhook);
        admin.MapPost("/webhooks/{id:guid}/test", TestWebhook).RequireRateLimiting("account");
        admin.MapDelete("/webhooks/{id:guid}", DisableWebhook);
    }

    private static async Task<IResult> Get(BookingDb db, OrganizationScope scope, HttpContext http, CancellationToken ct)
    {
        if (!scope.IsAdmin)
            return Results.StatusCode(403);
        http.Response.Headers.CacheControl = "no-store";
        var keys = await db.ExternalApiKeys.AsNoTracking().Where(item => item.OrganizationId == scope.Current.Id && item.RevokedAt == null)
            .OrderByDescending(item => item.CreatedAt).Select(item => new { item.Id, item.Name, item.Prefix, item.CanWrite, item.CreatedAt })
            .ToArrayAsync(ct);
        var endpoints = db.WebhookSubscriptions.AsNoTracking().Where(item => item.OrganizationId == scope.Current.Id);
        var webhooks = await endpoints.Where(item => item.DisabledAt == null).OrderByDescending(item => item.CreatedAt)
            .Select(item => new { item.Id, item.Name, item.Url, item.Events, item.CreatedAt }).ToArrayAsync(ct);
        var deliveries = await (from delivery in db.WebhookDeliveries.AsNoTracking()
            join webhook in endpoints on delivery.SubscriptionId equals webhook.Id
            orderby delivery.CreatedAt descending, delivery.Id
            select new { delivery.EventId, delivery.Type, Name = webhook.Name, delivery.CreatedAt, delivery.DeliveredAt,
                delivery.FailedAt, delivery.Attempts, delivery.NextAttemptAt, delivery.LastStatus, delivery.LastError })
            .Take(30).ToArrayAsync(ct);
        return Results.Ok(new { Keys = keys, Webhooks = webhooks, Deliveries = deliveries, Events = WebhookEvents.All });
    }

    private static async Task<IResult> CreateKey(KeyInput input, BookingDb db, OrganizationScope scope, TimeProvider clock,
        HttpContext http, CancellationToken ct)
    {
        if (!scope.IsAdmin)
            return Results.StatusCode(403);
        var name = ValidName(input.Name);
        if (name is null || input.Scope is not ("read" or "write"))
            return Results.Problem(statusCode: 400, title: "Укажите название и разрешения ключа.");
        var token = WebhookEvents.RandomSecret(ExternalApiFilter.KeyPrefix);
        var key = new ExternalApiKey
        {
            Id = Guid.NewGuid(), OrganizationId = scope.Current.Id, Name = name, Prefix = token[..12],
            TokenHash = WebhookEvents.HashKey(token), CanWrite = input.Scope == "write", CreatedAt = clock.GetUtcNow()
        };
        db.ExternalApiKeys.Add(key);
        await db.SaveChangesAsync(ct);
        http.Response.Headers.CacheControl = "no-store";
        return Results.Json(new { key.Id, key.Name, Token = token }, statusCode: 201);
    }

    private static async Task<IResult> RevokeKey(Guid id, BookingDb db, OrganizationScope scope, TimeProvider clock, CancellationToken ct)
    {
        if (!scope.IsAdmin)
            return Results.StatusCode(403);
        var now = clock.GetUtcNow();
        var updated = await db.ExternalApiKeys.Where(item => item.Id == id && item.OrganizationId == scope.Current.Id
                && item.RevokedAt == null).ExecuteUpdateAsync(set => set.SetProperty(item => item.RevokedAt, now), ct);
        return updated == 0 ? Results.NotFound() : Results.NoContent();
    }

    private static async Task<IResult> CreateWebhook(WebhookInput input, BookingDb db, OrganizationScope scope, TimeProvider clock,
        IWebHostEnvironment environment, IConfiguration config, IDataProtectionProvider protection, HttpContext http, CancellationToken ct)
    {
        if (!scope.IsAdmin)
            return Results.StatusCode(403);
        var name = ValidName(input.Name);
        var url = WebhookTransport.ValidateUrl(input.Url, WebhookTransport.LocalEnabled(environment, config));
        if (name is null || url is null || input.Events is not { Length: > 0 } || input.Events.Length > WebhookEvents.All.Length
            || input.Events.Distinct().Count() != input.Events.Length || input.Events.Any(item => !WebhookEvents.All.Contains(item)))
            return Results.Problem(statusCode: 400, title: "Укажите название, публичный HTTPS-адрес и выберите события.");
        var secret = WebhookEvents.RandomSecret("sgwh_");
        var endpoint = new WebhookSubscription
        {
            Id = Guid.NewGuid(), OrganizationId = scope.Current.Id, Name = name, Url = url.AbsoluteUri,
            Events = input.Events, CreatedAt = clock.GetUtcNow(),
            ProtectedSecret = protection.CreateProtector(WebhookEvents.ProtectionPurpose).Protect(secret)
        };
        db.WebhookSubscriptions.Add(endpoint);
        await db.SaveChangesAsync(ct);
        http.Response.Headers.CacheControl = "no-store";
        return Results.Json(new { endpoint.Id, endpoint.Name, Secret = secret }, statusCode: 201);
    }

    private static async Task<IResult> DisableWebhook(Guid id, BookingDb db, OrganizationScope scope, TimeProvider clock, CancellationToken ct)
    {
        if (!scope.IsAdmin)
            return Results.StatusCode(403);
        var now = clock.GetUtcNow();
        var updated = await db.WebhookSubscriptions.Where(item => item.Id == id && item.OrganizationId == scope.Current.Id
                && item.DisabledAt == null).ExecuteUpdateAsync(set => set.SetProperty(item => item.DisabledAt, now), ct);
        return updated == 0 ? Results.NotFound() : Results.NoContent();
    }

    private static async Task<IResult> TestWebhook(Guid id, BookingDb db, OrganizationScope scope, TimeProvider clock, CancellationToken ct)
    {
        if (!scope.IsAdmin) return Results.StatusCode(403);
        var organization = scope.Current.Id;
        if (!await db.WebhookSubscriptions.AnyAsync(item => item.Id == id && item.OrganizationId == organization
                && item.DisabledAt == null, ct))
            return Results.NotFound();
        var eventId = WebhookEvents.EnqueueTest(db, organization, id, clock.GetUtcNow());
        await db.SaveChangesAsync(ct);
        return Results.Json(new { EventId = eventId }, statusCode: 202);
    }

    private static string? ValidName(string? value)
    {
        var name = value?.Trim();
        return name is { Length: > 0 and <= 100 } && !name.Any(char.IsControl) ? name : null;
    }
}
