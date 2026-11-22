using System.Data;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;

namespace SlotGuard;

internal sealed record AssistantInput(string? Text, string? TimeZone);
internal sealed record AssistantQuery(DateOnly Date, string Time, string? Type, int? Capacity, int DurationMinutes, string TimeZone);
internal sealed record AssistantAnswer(AssistantQuery Query, AvailabilitySearch.SearchResult Results);

internal static partial class BookingAssistant
{
    private static readonly string[] Numbers =
        ["ноль", "один", "два", "три", "четыре", "пять", "шесть", "семь", "восемь", "девять", "десять", "одиннадцать", "двенадцать"];

    public static void MapAssistant(this RouteGroupBuilder api)
    {
        api.MapPost("/assistant/search", async (AssistantInput input, BookingDb db, OrganizationScope scope,
            TimeProvider clock, CancellationToken ct) =>
            await Search(input, db, scope.Resources(db), scope.OwnerId, clock.GetUtcNow(), ct) is { } answer
                ? Results.Ok(answer)
                : Results.Problem(statusCode: 422, title: "Укажите будущий день и время. Например: «Переговорная на восемь человек завтра после обеда»."));
    }

    public static async Task<AssistantAnswer?> Search(AssistantInput input, BookingDb db, IQueryable<Resource> resources,
        Guid owner, DateTimeOffset now, CancellationToken ct)
    {
        if (input.Text?.Trim() is not { Length: > 0 and <= 240 } value || !Schedule.TryZone(input.TimeZone, out var zone))
            return null;
        var text = value.ToLowerInvariant().Replace('ё', 'е');
        var today = Schedule.LocalDate(now, zone);
        var numeric = IsoDate().Match(text);
        DateOnly day;
        if (numeric.Success)
        {
            if (!DateOnly.TryParseExact(numeric.Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day))
                return null;
        }
        else if (text.Contains("послезавтра")) day = today.AddDays(2);
        else if (text.Contains("завтра")) day = today.AddDays(1);
        else if (text.Contains("сегодня")) day = today;
        else return null;

        var timeMatch = ExplicitTime().Match(text);
        string? time = null;
        if (timeMatch.Success)
        {
            var hour = int.Parse(timeMatch.Groups["hour"].Value, CultureInfo.InvariantCulture);
            var minutes = timeMatch.Groups["minute"].Success ? timeMatch.Groups["minute"].Value : "00";
            time = FormattableString.Invariant($"{hour:00}:{minutes}");
        }
        else if (text.Contains("после обеда")) time = "14:00";
        else if (text.Contains("утром")) time = "10:00";
        else if (text.Contains("вечером")) time = "17:00";
        if (time is null || AvailabilitySearch.ReadStart(day, null, time, zone, now, out var requested) is not null)
            return null;

        var people = CapacityExpression().Match(text).Groups["count"].Value;
        int? seats = null;
        if (people.Length > 0)
        {
            var count = int.TryParse(people, CultureInfo.InvariantCulture, out var number) ? number : Array.IndexOf(Numbers, people);
            if (count is > 0 and <= 10000) seats = count;
        }
        var type = text.Contains("переговор") || text.Contains("зал") ? ResourceTypes.Room
            : text.Contains("студи") ? ResourceTypes.Studio : text.Contains("оборудован") ? ResourceTypes.Equipment : null;
        var duration = text.Contains("полтора часа") || text.Contains("90 мин") ? 90
            : text.Contains("полчаса") || text.Contains("30 мин") ? 30 : 60;
        var query = new AssistantQuery(day, time, type, seats, duration, zone.Id);
        await using var snapshot = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var catalog = AvailabilitySearch.Filter(resources.Where(item => item.PriceMinor == null), type, seats, []);
        var results = await AvailabilitySearch.Search(db, catalog, requested, duration, owner, now, true, ct);
        await snapshot.CommitAsync(ct);
        return new AssistantAnswer(query, results with { Exact = results.Exact.Take(12).ToArray() });
    }

    [GeneratedRegex(@"\b\d{4}-\d{2}-\d{2}\b")]
    private static partial Regex IsoDate();
    [GeneratedRegex(@"(?<!\d)(?<hour>\d{1,2}):(?<minute>\d{2})(?!\d)|\b(?:в|к)\s+(?<hour>\d{1,2})\b")]
    private static partial Regex ExplicitTime();
    [GeneratedRegex(@"\b(?:на|для)\s+(?<count>\d{1,4}|[а-я]+)\s+(?:человек|чел\.?|гостей|участник[а-я]*)\b")]
    private static partial Regex CapacityExpression();
}

internal static class SlotGuardMcp
{
    private const string Version = "2025-06-18";
    private const string Tool = "search_availability";
    private static readonly object SearchTool = new
    {
        name = Tool, description = "Find available workspace slots from a Russian booking request. Search does not reserve a slot.",
        inputSchema = new
        {
            type = "object",
            properties = new { text = new { type = "string", maxLength = 240 }, timeZone = new { type = "string" } },
            required = new[] { "text" }, additionalProperties = false
        },
        annotations = new { readOnlyHint = true }
    };

    public static void MapSlotGuardMcp(this WebApplication app)
    {
        app.MapPost("/mcp", Handle).AddEndpointFilter<ExternalApiFilter>()
            .RequireRateLimiting("external-api").ExcludeFromDescription();
        app.MapGet("/mcp", () => Results.StatusCode(405)).ExcludeFromDescription();
    }

    private static async Task<IResult> Handle(HttpContext http, BookingDb db, ExternalApiAccess access, TimeProvider clock,
        CancellationToken ct)
    {
        if (http.Request.Headers.Origin.Count > 0 && !string.Equals(http.Request.Headers.Origin.ToString(),
            $"{http.Request.Scheme}://{http.Request.Host}", StringComparison.OrdinalIgnoreCase))
            return Results.StatusCode(403);
        if (!http.Request.HasJsonContentType()) return Results.StatusCode(415);
        const int limit = 16 * 1024;
        if (http.Request.ContentLength > limit) return Results.StatusCode(413);
        if (http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } body)
            body.MaxRequestBodySize = limit;
        JsonElement input;
        try { input = await http.Request.ReadFromJsonAsync<JsonElement>(ct); }
        catch (JsonException) { return Error(null, -32700, "Parse error"); }
        if (input.ValueKind != JsonValueKind.Object || String(input, "jsonrpc") != "2.0" || String(input, "method") is not { } method)
            return Error(null, -32600, "Invalid request");
        var hasId = input.TryGetProperty("id", out var id);
        if (!hasId) return Results.StatusCode(202);
        if (id.ValueKind is not (JsonValueKind.Number or JsonValueKind.String)) return Error(null, -32600, "Invalid request ID");
        var requestedVersion = http.Request.Headers["MCP-Protocol-Version"].ToString();
        if (method != "initialize" && requestedVersion.Length > 0 && requestedVersion != Version)
            return Results.BadRequest();
        switch (method)
        {
            case "initialize": return Reply(id, new { protocolVersion = Version, capabilities = new { tools = new { listChanged = false } },
                serverInfo = new { name = "SlotGuard", version = "1.0.0" } });
            case "ping": return Reply(id, new { });
            case "tools/list": return Reply(id, new { tools = new[] { SearchTool } });
            case "tools/call":
                if (!input.TryGetProperty("params", out var parameters) || String(parameters, "name") != Tool
                    || !parameters.TryGetProperty("arguments", out var args))
                    return Error(id, -32602, "Unknown tool or invalid parameters");
                var answer = await BookingAssistant.Search(new(String(args, "text"), String(args, "timeZone")), db,
                    access.Resources(db), access.Owner, clock.GetUtcNow(), ct);
                if (answer is null) return Reply(id, new { content = new[] { new { type = "text", text = "Укажите будущую дату и время." } }, isError = true });
                return Reply(id, new { content = new[] { new { type = "text", text = JsonSerializer.Serialize(answer, JsonSerializerOptions.Web) } },
                    structuredContent = answer });
            default: return Error(id, -32601, "Method not found");
        }
    }

    private static string? String(JsonElement value, string field) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(field, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() : null;
    private static IResult Reply(JsonElement id, object result) => Results.Json(new { jsonrpc = "2.0", id, result });
    private static IResult Error(JsonElement? id, int code, string message) => Results.Json(new { jsonrpc = "2.0", id, error = new { code, message } });
}
