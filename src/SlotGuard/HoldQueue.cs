using System.Text.Json;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace SlotGuard;

internal sealed class RabbitMqOptions
{
    public string? Uri { get; set; }
    public string Queue { get; set; } = "slotguard.hold-notifications.v1";
}

internal sealed record HoldOffered(Guid EventId, Guid BookingId);

internal sealed class HoldQueue(IOptions<RabbitMqOptions> options)
{
    public string Name => options.Value.Queue;
    public bool IsEnabled => !string.IsNullOrWhiteSpace(options.Value.Uri);

    public Task<IConnection> Connect(CancellationToken ct)
    {
        var factory = new ConnectionFactory
        {
            Uri = new Uri(options.Value.Uri!),
            AutomaticRecoveryEnabled = false,
            RequestedConnectionTimeout = TimeSpan.FromSeconds(3),
            RequestedHeartbeat = TimeSpan.FromSeconds(10)
        };
        return factory.CreateConnectionAsync(ct);
    }

    public Task Declare(IChannel channel, CancellationToken ct) =>
        channel.QueueDeclareAsync(Name, durable: true, exclusive: false, autoDelete: false, cancellationToken: ct);

    public async Task Publish(HoldOffered message, CancellationToken ct)
    {
        await using var connection = await Connect(ct);
        await using var channel = await connection.CreateChannelAsync(
            new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true),
            ct);
        await Declare(channel, ct);
        var properties = new BasicProperties
        {
            ContentType = "application/json",
            MessageId = message.EventId.ToString(),
            Persistent = true
        };
        await channel.BasicPublishAsync("", Name, mandatory: true, properties,
            JsonSerializer.SerializeToUtf8Bytes(message), ct);
    }
}
