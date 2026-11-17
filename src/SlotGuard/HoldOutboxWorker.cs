using Microsoft.EntityFrameworkCore;

namespace SlotGuard;

internal sealed class HoldOutboxWorker(
    IServiceScopeFactory scopes, HoldQueue queue, TimeProvider clock, ILogger<HoldOutboxWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!queue.IsEnabled)
            return;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await PublishNext(stoppingToken))
                    continue;
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(exception, "Could not process the hold outbox");
            }
            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
        }
    }

    private async Task<bool> PublishNext(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BookingDb>();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // Keep ownership of this event until the broker confirms it or a retry is scheduled.
        var candidates = await db.HoldOutbox.FromSqlInterpolated($"""
            SELECT * FROM "HoldOutbox"
            WHERE "PublishedAt" IS NULL AND "NextAttemptAt" <= {clock.GetUtcNow()}
            ORDER BY "NextAttemptAt", "Id"
            LIMIT 1
            FOR UPDATE SKIP LOCKED
            """).ToListAsync(ct);
        var item = candidates.SingleOrDefault();
        if (item is null)
            return false;

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(6));
        try
        {
            await queue.Publish(new HoldOffered(item.Id, item.BookingId), deadline.Token);
            item.PublishedAt = clock.GetUtcNow();
        }
        catch (Exception exception) when (!ct.IsCancellationRequested)
        {
            item.Attempts++;
            item.NextAttemptAt = clock.GetUtcNow().AddSeconds(Math.Min(60, 1 << Math.Min(item.Attempts, 6)));
            logger.LogWarning(exception, "Retrying hold event {EventId} after attempt {Attempt}", item.Id, item.Attempts);
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return true;
    }
}
