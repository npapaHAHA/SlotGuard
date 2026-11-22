using System.Globalization;
using System.Text;

namespace SlotGuard;

internal sealed record CalendarImportRow(int Number, string Title, DateTimeOffset? Start, DateTimeOffset? End, string Status);

internal static class CalendarImportFile
{
    public const int MaxBytes = 256 * 1024;
    public const int MaxEvents = 250;

    public static CalendarImportRow[]? Read(string content, TimeZoneInfo zone)
    {
        var lines = content.TrimStart('\uFEFF').ReplaceLineEndings("\n").Replace("\n ", "").Replace("\n\t", "")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length < 2 || !Equal(lines[0], "BEGIN:VCALENDAR") || !Equal(lines[^1], "END:VCALENDAR"))
            return null;
        var types = new Stack<string>();
        var events = new List<CalendarImportRow>();
        var properties = new Dictionary<string, Property>(StringComparer.OrdinalIgnoreCase);
        var duplicateTimes = false;
        foreach (var line in lines[1..^1])
        {
            var separator = Separator(line);
            if (separator < 1) return null;
            var key = line[..separator];
            var value = line[(separator + 1)..];
            if (Equal(key, "BEGIN"))
            {
                if (Equal(value, "VCALENDAR") || Equal(value, "VEVENT") && types.Count != 0)
                    return null;
                types.Push(value);
                continue;
            }
            if (Equal(key, "END"))
            {
                if (!types.TryPop(out var ended) || !Equal(ended, value))
                    return null;
                if (Equal(ended, "VEVENT"))
                {
                    if (events.Count == MaxEvents) return null;
                    events.Add(Event(properties, duplicateTimes, events.Count + 1, zone));
                    properties.Clear();
                    duplicateTimes = false;
                }
                continue;
            }
            if (types.Count != 1 || !Equal(types.Peek(), "VEVENT")) continue;
            var name = key.Split(';', 2)[0];
            if (!properties.TryAdd(name, new Property(key, value)) && (Equal(name, "DTSTART") || Equal(name, "DTEND")))
                duplicateTimes = true;
        }
        return types.Count == 0 ? events.ToArray() : null;
    }

    private static CalendarImportRow Event(Dictionary<string, Property> data, bool duplicateTimes,
        int number, TimeZoneInfo zone)
    {
        var title = ShortTitle(data.GetValueOrDefault("SUMMARY")?.Value, number);
        if (Equal(data.GetValueOrDefault("STATUS")?.Value, "CANCELLED")
            || Equal(data.GetValueOrDefault("TRANSP")?.Value, "TRANSPARENT"))
            return new(number, title, null, null, "transparent");
        if (data.Keys.Any(key => key.ToUpperInvariant() is "RRULE" or "RDATE" or "RECURRENCE-ID" or "EXDATE" or "EXRULE"))
            return new(number, title, null, null, "recurring");
        if (duplicateTimes || !data.TryGetValue("DTSTART", out var startProperty)
            || !ReadDate(startProperty, zone, out var start, out var allDay))
            return new(number, title, null, null, "date");

        DateTimeOffset end;
        if (data.TryGetValue("DTEND", out var endProperty))
        {
            if (!ReadDate(endProperty, zone, out end, out var dateEnd) || dateEnd != allDay || data.ContainsKey("DURATION"))
                return new(number, title, null, null, "date");
        }
        else if (allDay && !data.ContainsKey("DURATION") && startProperty.Value != "99991231"
            && DateOnly.TryParseExact(startProperty.Value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
            && AtZone(day.AddDays(1).ToDateTime(TimeOnly.MinValue), zone, out var following))
            end = following;
        else return new(number, title, null, null, "date");
        return end > start ? new(number, title, start, end, "candidate") : new(number, title, null, null, "date");
    }

    private static bool ReadDate(Property property, TimeZoneInfo defaultZone, out DateTimeOffset result, out bool allDay)
    {
        result = default;
        allDay = property.Value.Length == 8;
        var timeZone = property.Parameter("TZID");
        var valueType = property.Parameter("VALUE");
        if (valueType is not null && !Equal(valueType, allDay ? "DATE" : "DATE-TIME") || allDay && timeZone is not null)
            return false;
        var utc = property.Value.EndsWith('Z');
        var format = allDay ? "yyyyMMdd" : utc ? "yyyyMMdd'T'HHmmss'Z'" : "yyyyMMdd'T'HHmmss";
        if (!DateTime.TryParseExact(property.Value, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
            return false;
        if (utc)
        {
            result = new DateTimeOffset(local, TimeSpan.Zero);
            return timeZone is null;
        }
        var zone = defaultZone;
        if (timeZone is not null && !Schedule.TryZone(timeZone, out zone))
            return false;
        return AtZone(local, zone, out result);
    }

    private static bool AtZone(DateTime local, TimeZoneInfo zone, out DateTimeOffset utc)
    {
        utc = default;
        if (zone.IsInvalidTime(local) || zone.IsAmbiguousTime(local)) return false;
        var offset = zone.GetUtcOffset(local);
        if (local.Ticks - offset.Ticks is < 0 || local.Ticks - offset.Ticks > DateTime.MaxValue.Ticks) return false;
        utc = new DateTimeOffset(local, offset).ToUniversalTime();
        return true;
    }

    private static string ShortTitle(string? raw, int number)
    {
        if (string.IsNullOrWhiteSpace(raw)) return $"Событие {number}";
        var title = new StringBuilder(100);
        for (var index = 0; index < raw.Length && title.Length < 100; index++)
        {
            var value = raw[index];
            if (value == '\\' && index + 1 < raw.Length)
            {
                value = raw[++index];
                if (value is 'n' or 'N') value = ' ';
            }
            if (!char.IsControl(value)) title.Append(value);
        }
        return title.Length == 0 ? $"Событие {number}" : title.ToString();
    }

    private static int Separator(string line)
    {
        var quoted = false;
        for (var index = 0; index < line.Length; index++)
        {
            if (line[index] == '"') quoted = !quoted;
            if (line[index] == ':' && !quoted) return index;
        }
        return -1;
    }

    private static bool Equal(string? left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private sealed record Property(string Key, string Value)
    {
        public string? Parameter(string name)
        {
            var prefix = name + "=";
            return Key.Split(';').Skip(1).FirstOrDefault(item => item.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                ?[prefix.Length..].Trim('"');
        }
    }
}
