using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace SlotGuard;

internal sealed class WebhookTransport(HttpClient client, IWebHostEnvironment environment, IConfiguration config)
{
    public static bool LocalEnabled(IWebHostEnvironment environment, IConfiguration config) =>
        environment.IsDevelopment() && config.GetValue<bool>("Integrations:AllowLoopbackWebhooks");

    public static Uri? ValidateUrl(string? value, bool allowLocal)
    {
        if (value is not { Length: > 0 and <= 512 } || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
            || uri.AbsoluteUri.Length > 512 || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0
            || uri.HostNameType is not (UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6))
            return null;
        if (Literal(uri.IdnHost) is { } address)
        {
            if (allowLocal && IPAddress.IsLoopback(address) && uri.Scheme is ("http" or "https"))
                return uri;
            if (!IsPublic(address))
                return null;
        }
        return uri.Scheme == "https" ? uri : null;
    }

    public static SocketsHttpHandler Handler(bool allowLocal) => new()
    {
        AllowAutoRedirect = false, UseCookies = false, UseProxy = false,
        ConnectCallback = async (connection, ct) =>
        {
            var endpoint = connection.DnsEndPoint;
            var literal = Literal(endpoint.Host);
            var candidates = literal is not null ? [literal] : await Dns.GetHostAddressesAsync(endpoint.Host, ct);
            var addresses = candidates.Where(address =>
                IsPublic(address) || allowLocal && literal is not null && IPAddress.IsLoopback(address)).ToArray();
            if (addresses.Length == 0)
                throw new InvalidDestination();
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                // Dial validated addresses. The HTTP stack still validates TLS against the requested hostname.
                await socket.ConnectAsync(addresses, endpoint.Port, ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
    };

    public async Task<WebhookResult> Send(WebhookSubscription subscription, WebhookDelivery delivery, string secret,
        DateTimeOffset now, CancellationToken ct)
    {
        var uri = ValidateUrl(subscription.Url, LocalEnabled(environment, config));
        if (uri is null)
            return new(false, false, null, "destination");
        var time = now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent(delivery.Payload, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-SlotGuard-Event-Id", delivery.EventId.ToString());
        request.Headers.Add("X-SlotGuard-Event", delivery.Type);
        request.Headers.Add("X-SlotGuard-Timestamp", time);
        request.Headers.Add("X-SlotGuard-Signature", WebhookEvents.Sign(secret, time, delivery.Payload));

        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            var status = (int)response.StatusCode;
            var retry = status is 408 or 425 or 429 || status >= 500;
            return new(response.IsSuccessStatusCode, retry, status, response.IsSuccessStatusCode ? null : "http");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new(false, true, null, "timeout");
        }
        catch (HttpRequestException error) when (error.InnerException is InvalidDestination)
        {
            return new(false, false, null, "destination");
        }
        catch (HttpRequestException)
        {
            return new(false, true, null, "network");
        }
    }

    private static IPAddress? Literal(string host) =>
        IPAddress.TryParse(host.Trim('[', ']'), out var address) ? address : null;

    private static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        var bytes = address.GetAddressBytes();
        if (bytes.Length == 4)
        {
            return bytes[0] is not (0 or 10 or 127) && bytes[0] < 224
                && !(bytes[0] == 100 && bytes[1] is >= 64 and <= 127)
                && !(bytes[0] == 169 && bytes[1] == 254)
                && !(bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
                && !(bytes[0] == 192 && (bytes[1] is 0 or 168))
                && !(bytes[0] == 198 && (bytes[1] is 18 or 19 || bytes[1] == 51 && bytes[2] == 100))
                && !(bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113);
        }

        return (bytes[0] & 0xe0) == 0x20
            && !(bytes[0] == 0x20 && bytes[1] == 0x02)
            && !(bytes[0] == 0x20 && bytes[1] == 0x01 && (bytes[2] < 2 || bytes[2] == 0x0d && bytes[3] == 0xb8))
            && !(bytes[0] == 0x3f && bytes[1] == 0xff && bytes[2] < 0x10);
    }

    private sealed class InvalidDestination : IOException;
}

internal sealed record WebhookResult(bool Delivered, bool Retry, int? Status, string? Error);
