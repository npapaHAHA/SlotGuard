using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace SlotGuard;

internal sealed class GoogleCalendarOptions
{
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string RedirectUri { get; set; } = "";

    public bool Configured => !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret)
        && Uri.TryCreate(RedirectUri, UriKind.Absolute, out var callback)
        && (callback.Scheme == "https" || callback.Scheme == "http" && callback.IsLoopback)
        && callback.AbsolutePath == "/google-calendar/callback" && callback.Query.Length == 0
        && callback.Fragment.Length == 0 && callback.UserInfo.Length == 0;
}

internal sealed record GoogleToken([property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("refresh_token")] string? RefreshToken,
    [property: JsonPropertyName("expires_in")] int ExpiresIn);
internal sealed record GoogleCalendarInfo(string Id, string Summary, string TimeZone, string AccessRole);
internal sealed record GoogleEventTime(string? Date, DateTimeOffset? DateTime);
internal sealed record GoogleEventProperties(Dictionary<string, string>? Private);
internal sealed record GoogleAttendee(bool? Self, string? ResponseStatus);
internal sealed record GoogleCalendarEvent(string Id, string? Status, string? Transparency, GoogleEventTime? Start,
    GoogleEventTime? End, GoogleEventProperties? ExtendedProperties, string? Etag, GoogleAttendee[]? Attendees);

internal sealed class GoogleCalendarException(string message) : Exception(message);

internal sealed class GoogleCalendarClient(HttpClient http, IOptions<GoogleCalendarOptions> options)
{
    private const string TokenUrl = "https://oauth2.googleapis.com/token";
    private const string Api = "https://www.googleapis.com/calendar/v3";
    private const string Marker = "slotguardConnection";
    private const string Booking = "slotguardBooking";
    private sealed record Page(GoogleCalendarEvent[]? Items, string? NextPageToken);

    public Task<GoogleToken> Exchange(string code, string verifier, CancellationToken ct) => Token(new()
    {
        ["code"] = code, ["code_verifier"] = verifier, ["redirect_uri"] = options.Value.RedirectUri,
        ["grant_type"] = "authorization_code"
    }, ct);

    public Task<GoogleToken> Refresh(string token, CancellationToken ct) => Token(new()
    {
        ["refresh_token"] = token, ["grant_type"] = "refresh_token"
    }, ct);

    private async Task<GoogleToken> Token(Dictionary<string, string> fields, CancellationToken ct)
    {
        fields["client_id"] = options.Value.ClientId;
        fields["client_secret"] = options.Value.ClientSecret;
        using var form = new FormUrlEncodedContent(fields);
        using var response = await http.PostAsync(TokenUrl, form, ct);
        if (response.StatusCode == HttpStatusCode.BadRequest)
            throw new GoogleCalendarException("Google отклонил авторизацию. Подключите календарь заново.");
        var token = await Read<GoogleToken>(response, ct);
        return string.IsNullOrWhiteSpace(token.AccessToken)
            ? throw new GoogleCalendarException("Google не вернул ключ доступа.") : token;
    }

    public async Task<GoogleCalendarInfo> Calendar(string bearer, string id, CancellationToken ct)
    {
        using var response = await Send(HttpMethod.Get, $"{Api}/users/me/calendarList/{Uri.EscapeDataString(id)}", bearer, ct);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
            throw new GoogleCalendarException("Google не нашёл календарь в списке доступных. Проверьте его ID.");
        var calendar = await Read<GoogleCalendarInfo>(response, ct);
        return calendar.AccessRole is "owner" or "writer" && !string.IsNullOrWhiteSpace(calendar.Id)
            ? calendar : throw new GoogleCalendarException("Выберите календарь с правом редактировать события.");
    }

    public async Task<GoogleCalendarEvent[]> Events(string bearer, string calendar, DateTimeOffset now, CancellationToken ct)
    {
        var query = new Dictionary<string, string?>
        {
            ["singleEvents"] = "true", ["showDeleted"] = "false", ["maxResults"] = "2500", ["timeZone"] = "UTC",
            ["timeMin"] = now.ToString("O", CultureInfo.InvariantCulture),
            ["timeMax"] = Schedule.MaximumHorizon(now).AddHours(4).ToString("O", CultureInfo.InvariantCulture),
            ["fields"] = "nextPageToken,items(id,status,transparency,start,end,extendedProperties,etag,attendees(self,responseStatus))"
        };
        var events = new List<GoogleCalendarEvent>();
        for (var page = 0; page < 40; page++)
        {
            using var response = await Send(HttpMethod.Get, QueryHelpers.AddQueryString(EventsUrl(calendar), query), bearer, ct);
            var result = await Read<Page>(response, ct);
            events.AddRange(result.Items ?? []);
            if (string.IsNullOrEmpty(result.NextPageToken))
                return events.ToArray();
            query["pageToken"] = result.NextPageToken;
        }
        throw new GoogleCalendarException("В календаре слишком много событий для синхронизации.");
    }

    public static bool ManagedBy(GoogleCalendarEvent item, Guid key) =>
        item.ExtendedProperties?.Private?.GetValueOrDefault(Marker) == key.ToString("N");

    public static Guid? BookingId(GoogleCalendarEvent item) =>
        Guid.TryParseExact(item.ExtendedProperties?.Private?.GetValueOrDefault(Booking), "N", out var id) ? id : null;

    public async Task Export(string bearer, GoogleCalendarLink link, Resource resource, Booking booking, CancellationToken ct)
    {
        var preferredId = $"sg{link.Key:N}{booking.Id:N}";
        async Task<HttpResponseMessage> Insert(string id) => await Send(HttpMethod.Post, EventsUrl(link.CalendarId), bearer, ct,
            new
            {
                id, summary = $"SlotGuard · {resource.Name}", visibility = "private",
                start = new { dateTime = booking.StartUtc }, end = new { dateTime = booking.EndUtc },
                reminders = new { useDefault = false },
                extendedProperties = new { @private = new Dictionary<string, string>
                {
                    [Marker] = link.Key.ToString("N"), [Booking] = booking.Id.ToString("N")
                } }
            });
        using var response = await Insert(preferredId);
        if (response.StatusCode != HttpStatusCode.Conflict)
        {
            EnsureSuccess(response);
            return;
        }
        using var previous = await Send(HttpMethod.Get, EventUrl(link.CalendarId, preferredId), bearer, ct);
        if (previous.IsSuccessStatusCode && await Read<GoogleCalendarEvent>(previous, ct) is { Status: not "cancelled" } active)
        {
            if (ManagedBy(active, link.Key) && BookingId(active) == booking.Id)
            {
                if (active.Start?.DateTime != booking.StartUtc || active.End?.DateTime != booking.EndUtc)
                    await UpdateTime(bearer, link.CalendarId, active, booking, ct);
                return;
            }
            throw new GoogleCalendarException("Идентификатор события уже занят в Google Calendar.");
        }
        if (!previous.IsSuccessStatusCode && previous.StatusCode is not (HttpStatusCode.Gone or HttpStatusCode.NotFound))
            EnsureSuccess(previous);
        using var replacement = await Insert($"sg{link.Key:N}{Guid.NewGuid():N}");
        EnsureSuccess(replacement);
    }

    public async Task UpdateTime(string bearer, string calendar, GoogleCalendarEvent item, Booking booking, CancellationToken ct)
    {
        using var response = await Send(HttpMethod.Patch, EventUrl(calendar, item.Id), bearer, ct,
            new { start = new { dateTime = booking.StartUtc }, end = new { dateTime = booking.EndUtc } }, item.Etag);
        EnsureSuccess(response);
    }

    public async Task Delete(string bearer, string calendar, GoogleCalendarEvent item, CancellationToken ct)
    {
        using var response = await Send(HttpMethod.Delete, EventUrl(calendar, item.Id), bearer, ct, etag: item.Etag);
        if (response.StatusCode is not (HttpStatusCode.NotFound or HttpStatusCode.Gone))
            EnsureSuccess(response);
    }

    private async Task<HttpResponseMessage> Send(HttpMethod method, string url, string token, CancellationToken ct,
        object? body = null, string? etag = null)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (etag is not null)
            request.Headers.TryAddWithoutValidation("If-Match", etag);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        return await http.SendAsync(request, ct);
    }

    private static async Task<T> Read<T>(HttpResponseMessage response, CancellationToken ct)
    {
        EnsureSuccess(response);
        return await response.Content.ReadFromJsonAsync<T>(ct)
            ?? throw new GoogleCalendarException("Google вернул пустой ответ.");
    }

    private static void EnsureSuccess(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
            throw new GoogleCalendarException($"Синхронизация с Google приостановлена (HTTP {(int)response.StatusCode}).");
    }

    private static string EventsUrl(string calendar) => $"{Api}/calendars/{Uri.EscapeDataString(calendar)}/events";
    private static string EventUrl(string calendar, string id) => $"{EventsUrl(calendar)}/{Uri.EscapeDataString(id)}";
}
