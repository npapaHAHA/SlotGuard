using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.SignalR;

namespace SlotGuard;

internal sealed class MeetingRooms(IDataProtectionProvider protection)
{
    private readonly ITimeLimitedDataProtector tickets = protection.CreateProtector("SlotGuard.Meeting.v1").ToTimeLimitedDataProtector();
    private readonly Dictionary<Guid, HashSet<string>> rooms = [];
    private readonly object roomLock = new();

    public string Create() => tickets.Protect(Guid.NewGuid().ToString("N"), TimeSpan.FromHours(24));

    public Guid? Id(string token, out DateTimeOffset expiry)
    {
        expiry = default;
        if (token.Length is < 20 or > 512) return null;
        try { return Guid.TryParseExact(tickets.Unprotect(token, out expiry), "N", out var id) ? id : null; }
        catch (CryptographicException) { return null; }
    }

    public bool TryJoin(Guid room, string connection, out bool peer)
    {
        lock (roomLock)
        {
            if (!rooms.TryGetValue(room, out var participants)) rooms.Add(room, participants = []);
            peer = participants.Count > 0;
            return participants.Count < 2 && participants.Add(connection);
        }
    }

    public void Leave(Guid room, string connection)
    {
        lock (roomLock)
        {
            if (!rooms.TryGetValue(room, out var participants)) return;
            participants.Remove(connection);
            if (participants.Count == 0) rooms.Remove(room);
        }
    }

    public static void Map(WebApplication app)
    {
        app.MapGet("/meeting", (IWebHostEnvironment env) => Results.File(
            Path.Combine(env.WebRootPath, "meeting.html"), "text/html; charset=utf-8")).ExcludeFromDescription();
        app.MapPost("/api/meetings", (HttpContext http, MeetingRooms rooms) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            return AccountSessions.IsAccount(http.User) || http.User.Identity?.Name is not null
                ? Results.Json(new { link = $"/meeting#{rooms.Create()}" }, statusCode: 201)
                : Results.StatusCode(403);
        }).RequireAuthorization().RequireRateLimiting("account");
        app.MapHub<MeetingHub>("/hubs/meeting").RequireRateLimiting("public-booking");
    }
}

internal sealed class MeetingHub(MeetingRooms rooms, TimeProvider clock, IConfiguration configuration) : Hub
{
    private const string MembershipKey = "meeting";
    private sealed record Membership(Guid Room, DateTimeOffset Expires, string Name);

    public async Task<object> Join(string token, string displayName)
    {
        if (Context.Items.ContainsKey(MembershipKey)) throw new HubException("Вы уже в комнате.");
        var name = PublicEvents.Clean(displayName, 50) ?? throw new HubException("Введите имя до 50 символов.");
        var id = rooms.Id(token, out var expiry) ?? throw new HubException("Срок действия ссылки истёк.");
        if (!rooms.TryJoin(id, Context.ConnectionId, out var peer))
            throw new HubException("В комнате уже два участника.");
        Context.Items[MembershipKey] = new Membership(id, expiry, name);
        try
        {
            var group = Group(id);
            await Groups.AddToGroupAsync(Context.ConnectionId, group, Context.ConnectionAborted);
            if (peer) await Clients.OthersInGroup(group).SendAsync("PeerJoined", Context.ConnectionAborted);
            var stun = configuration["Meeting:StunUrl"];
            return new { Peer = peer, Stun = stun is { Length: > 5 and <= 200 } && stun.StartsWith("stun:", StringComparison.Ordinal) ? stun : null };
        }
        catch
        {
            rooms.Leave(id, Context.ConnectionId);
            Context.Items.Remove(MembershipKey);
            throw;
        }
    }

    public Task Signal(string type, string payload)
    {
        var member = Active();
        if (type is not ("offer" or "answer" or "ice") || payload.Length is < 1 or > 16000)
            throw new HubException("Некорректное сообщение соединения.");
        return Clients.OthersInGroup(Group(member.Room)).SendAsync("Signal", type, payload, Context.ConnectionAborted);
    }

    public Task Chat(string text)
    {
        var member = Active();
        var message = PublicEvents.Clean(text, 600) ?? throw new HubException("Введите сообщение до 600 символов.");
        var now = clock.GetUtcNow();
        if (Context.Items.TryGetValue("lastChat", out var last) && last is DateTimeOffset time && now - time < TimeSpan.FromSeconds(1))
            throw new HubException("Подождите секунду перед отправкой.");
        Context.Items["lastChat"] = now;
        return Clients.Group(Group(member.Room)).SendAsync("Chat", member.Name, message, Context.ConnectionAborted);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (Context.Items.TryGetValue(MembershipKey, out var value) && value is Membership membership)
        {
            var group = Group(membership.Room);
            try { await Clients.OthersInGroup(group).SendAsync("PeerLeft"); }
            finally { rooms.Leave(membership.Room, Context.ConnectionId); }
        }
        await base.OnDisconnectedAsync(exception);
    }

    private Membership Active() =>
        Context.Items.TryGetValue(MembershipKey, out var item) && item is Membership member && member.Expires > clock.GetUtcNow()
            ? member : throw new HubException("Ссылка больше не действует.");

    private static string Group(Guid id) => $"meeting:{id:N}";
}
