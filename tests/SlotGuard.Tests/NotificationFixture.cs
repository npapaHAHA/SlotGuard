using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using RabbitMQ.Client;

namespace SlotGuard.Tests;

public sealed class NotificationFixture : SlotGuardFixture
{
    private IContainer? _broker;
    private BrokerLink? _link;
    private Uri _brokerUri = null!;
    private readonly string _queue = $"slotguard-test-{Guid.NewGuid():N}";

    internal BrokerLink Link => _link!;

    public override async Task InitializeAsync()
    {
        var external = Environment.GetEnvironmentVariable("SLOTGUARD_TEST_RABBITMQ");
        if (string.IsNullOrWhiteSpace(external))
        {
            _broker = new ContainerBuilder("rabbitmq:4.4-alpine")
                .WithEnvironment("RABBITMQ_DEFAULT_USER", "slotguard")
                .WithEnvironment("RABBITMQ_DEFAULT_PASS", "slotguard_test")
                .WithPortBinding(5672, assignRandomHostPort: true)
                .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(5672))
                .Build();
            await _broker.StartAsync();
            _brokerUri = new UriBuilder("amqp", _broker.Hostname, _broker.GetMappedPublicPort(5672), "/%2F")
                { UserName = "slotguard", Password = "slotguard_test" }.Uri;
        }
        else
        {
            _brokerUri = new Uri(external);
        }

        _link = new BrokerLink(_brokerUri);
        await base.InitializeAsync();
    }

    protected override void ConfigureSettings(Dictionary<string, string?> settings)
    {
        settings["RabbitMq:Uri"] = Link.Uri.AbsoluteUri;
        settings["RabbitMq:Queue"] = _queue;
    }

    public async Task PublishAgain(HoldOutboxEvent item)
    {
        await using var connection = await Connect();
        await using var channel = await connection.CreateChannelAsync(
            new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true));
        var body = JsonSerializer.SerializeToUtf8Bytes(new { EventId = item.Id, item.BookingId });
        var properties = new BasicProperties { MessageId = item.Id.ToString(), Persistent = true };
        await channel.BasicPublishAsync("", _queue, mandatory: true, properties, body);
    }

    public override async Task DisposeAsync()
    {
        try
        {
            await base.DisposeAsync();
        }
        finally
        {
            if (_link is not null)
                await _link.DisposeAsync();
            if (_broker is not null)
            {
                await _broker.DisposeAsync();
            }
            else if (_brokerUri is not null)
            {
                await using var connection = await Connect();
                await using var channel = await connection.CreateChannelAsync();
                await channel.QueueDeleteAsync(_queue, ifUnused: false, ifEmpty: false);
            }
        }
    }

    private Task<IConnection> Connect() => new ConnectionFactory { Uri = _brokerUri }.CreateConnectionAsync();
}

// Interrupts actual AMQP connections while leaving the RabbitMQ instance available to the test host.
internal sealed class BrokerLink : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly HashSet<TcpClient> _clients = [];
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _accept;
    private bool _available = true;

    public Uri Uri { get; }

    public BrokerLink(Uri broker)
    {
        _listener.Start();
        Uri = new UriBuilder(broker) { Host = "127.0.0.1", Port = ((IPEndPoint)_listener.LocalEndpoint).Port }.Uri;
        _accept = Accept(broker, _shutdown.Token);
    }

    public void SetAvailable(bool available)
    {
        lock (_gate)
        {
            _available = available;
            if (!available)
                foreach (var client in _clients)
                    client.Dispose();
        }
    }

    private async Task Accept(Uri broker, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var client = await _listener.AcceptTcpClientAsync(ct);
            lock (_gate)
            {
                if (!_available)
                {
                    client.Dispose();
                    continue;
                }
                _clients.Add(client);
            }
            _ = Forward(client, broker, ct);
        }
    }

    private async Task Forward(TcpClient incoming, Uri broker, CancellationToken ct)
    {
        using var outgoing = new TcpClient();
        using var transfer = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            await outgoing.ConnectAsync(broker.Host, broker.Port > 0 ? broker.Port : 5672, ct);
            var source = incoming.GetStream();
            var target = outgoing.GetStream();
            var toBroker = source.CopyToAsync(target, transfer.Token);
            var fromBroker = target.CopyToAsync(source, transfer.Token);
            await Task.WhenAny(toBroker, fromBroker);
            await transfer.CancelAsync();
            await Task.WhenAll(toBroker, fromBroker);
        }
        catch (Exception error) when (error is SocketException or IOException or OperationCanceledException or InvalidOperationException)
        {
            // Transport interruptions are part of the test.
        }
        finally
        {
            lock (_gate)
                _clients.Remove(incoming);
            incoming.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        SetAvailable(false);
        await _shutdown.CancelAsync();
        try
        {
            await _accept;
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        _listener.Stop();
        _shutdown.Dispose();
    }
}
