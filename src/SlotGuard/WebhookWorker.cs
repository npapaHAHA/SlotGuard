using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace SlotGuard;

internal sealed class WebhookWorker(IServiceScopeFactory scopes, TimeProvider clock, ILogger<WebhookWorker> logger) : BackgroundService
{
    private static readonly int[] RetrySeconds = [10, 30, 120, 600, 1800, 3600, 7200];

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<BookingDb>();
                var item = await Claim(db, clock.GetUtcNow(), ct);
                if (item is not null)
                {
                    await Deliver(scope.ServiceProvider, db, item, ct);
                    continue;
                }
            }
            catch (Exception error) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning("Webhook processing will retry after {ErrorType}", error.GetType().Name);
            }
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
    }

    private static async Task<WebhookDelivery?> Claim(BookingDb db, DateTimeOffset now, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var rows = await db.WebhookDeliveries.FromSqlInterpolated($"""
            SELECT * FROM "WebhookDeliveries"
            WHERE "DeliveredAt" IS NULL AND "FailedAt" IS NULL AND "NextAttemptAt" <= {now}
            ORDER BY "NextAttemptAt", "Id"
            LIMIT 1 FOR UPDATE SKIP LOCKED
            """).ToListAsync(ct);
        var item = rows.SingleOrDefault();
        if (item is null)
            return null;
        item.Lease = Guid.NewGuid();
        item.NextAttemptAt = now.AddMinutes(1);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return item;
    }

    private async Task Deliver(IServiceProvider services, BookingDb db, WebhookDelivery item, CancellationToken ct)
    {
        var endpoint = await db.WebhookSubscriptions.AsNoTracking()
            .SingleOrDefaultAsync(destination => destination.Id == item.SubscriptionId && destination.DisabledAt == null, ct);
        var result = endpoint is null
            ? new WebhookResult(false, false, null, "disabled")
            : await Send(services, endpoint, item, ct);
        var finished = clock.GetUtcNow();
        var attempts = item.Attempts + (endpoint is null ? 0 : 1);
        var retry = result.Retry && attempts <= RetrySeconds.Length;
        DateTimeOffset? delivered = result.Delivered ? finished : null;
        DateTimeOffset? failed = !result.Delivered && !retry ? finished : null;
        var next = retry ? finished.AddSeconds(RetrySeconds[attempts - 1]) : finished;
        await db.WebhookDeliveries.Where(row => row.Id == item.Id && row.Lease == item.Lease)
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.Attempts, attempts)
                .SetProperty(row => row.LastStatus, result.Status).SetProperty(row => row.LastError, result.Error)
                .SetProperty(row => row.DeliveredAt, delivered).SetProperty(row => row.FailedAt, failed)
                .SetProperty(row => row.NextAttemptAt, next).SetProperty(row => row.Lease, (Guid?)null), ct);
    }

    private async Task<WebhookResult> Send(IServiceProvider services, WebhookSubscription endpoint, WebhookDelivery item,
        CancellationToken ct)
    {
        var protection = services.GetRequiredService<IDataProtectionProvider>().CreateProtector(WebhookEvents.ProtectionPurpose);
        try
        {
            var secret = protection.Unprotect(endpoint.ProtectedSecret);
            return await services.GetRequiredService<WebhookTransport>().Send(endpoint, item, secret, clock.GetUtcNow(), ct);
        }
        catch (CryptographicException)
        {
            return new(false, false, null, "secret_unavailable");
        }
    }
}
