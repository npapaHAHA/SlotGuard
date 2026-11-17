using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace SlotGuard;

internal sealed class HoldNotificationWorker(
    IServiceScopeFactory scopes, HoldQueue queue, TimeProvider clock, ILogger<HoldNotificationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!queue.IsEnabled)
            return;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Listen(stoppingToken);
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(exception, "Hold notification consumer disconnected");
            }
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }
    }

    private async Task Listen(CancellationToken ct)
    {
        await using var connection = await queue.Connect(ct);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: ct);
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task OnDisconnect()
        {
            disconnected.TrySetResult();
            return Task.CompletedTask;
        }
        channel.ChannelShutdownAsync += (_, _) => OnDisconnect();
        await queue.Declare(channel, ct);
        await channel.BasicQosAsync(0, 1, global: false, ct);
        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.UnregisteredAsync += (_, _) => OnDisconnect();
        consumer.ReceivedAsync += (_, delivery) => HandleDelivery(channel, delivery, ct);
        await channel.BasicConsumeAsync(queue.Name, autoAck: false, consumer, ct);
        await disconnected.Task.WaitAsync(ct);
    }

    private async Task HandleDelivery(IChannel channel, BasicDeliverEventArgs delivery, CancellationToken ct)
    {
        HoldOffered? message;
        try
        {
            message = JsonSerializer.Deserialize<HoldOffered>(delivery.Body.Span);
        }
        catch (JsonException)
        {
            message = null;
        }

        if (message is null || message.EventId == Guid.Empty || message.BookingId == Guid.Empty)
        {
            await channel.BasicRejectAsync(delivery.DeliveryTag, requeue: false, ct);
            return;
        }

        try
        {
            await StoreNotification(message, ct);
        }
        catch (Exception exception) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Could not store hold notification {EventId}", message.EventId);
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
            await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: true, ct);
            return;
        }

        await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, ct);
    }

    private async Task StoreNotification(HoldOffered message, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BookingDb>();
        var slot = await db.Bookings.AsNoTracking().Where(booking => booking.Id == message.BookingId)
            .Select(booking => (SlotKey?)new SlotKey(booking.ResourceId, booking.StartUtc))
            .SingleOrDefaultAsync(ct);
        if (slot is not { } key)
            return;

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await SlotLock.Acquire(db, key, ct);
        var now = clock.GetUtcNow();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "HoldNotifications" ("EventId", "BookingId", "DeliveredAt")
            SELECT {message.EventId}, "Id", {now} FROM "Bookings"
            WHERE "Id" = {message.BookingId} AND "CancelledAt" IS NULL AND "HoldExpiresAt" > {now}
            ON CONFLICT DO NOTHING
            """, ct);
        await transaction.CommitAsync(ct);
    }
}
