using System.Globalization;
using System.Text.Json;

namespace SlotGuard;

internal static class Schedule
{
    public const string DefaultZoneId = "Europe/Moscow";
    public static readonly TimeZoneInfo DefaultZone = TimeZoneInfo.FindSystemTimeZoneById(DefaultZoneId);
    public static readonly int[] Hours = [9, 11, 13, 15, 17];
    public const int WeekCount = 12;
    public const int DefaultDuration = 60;
    public const int FlexibleStep = 15;
    public static readonly int[] Durations = [30, DefaultDuration, 90];
    private static readonly DayHours DefaultDay = new(
        Hours.Select(hour => new SchedulePeriod(hour * 60, (hour + 1) * 60)).ToArray(), []);
    public static readonly ResourceSchedule Default = new(Enumerable.Repeat(DefaultDay, 7).ToArray(), []);

    public static ResourceSchedule Read(Resource resource) => resource.ScheduleDefinition is { } json
        ? JsonSerializer.Deserialize<ResourceSchedule>(json, JsonSerializerOptions.Web)!
        : Default;

    public static bool TryZone(string? id, out TimeZoneInfo zone)
    {
        zone = DefaultZone;
        if (id is null || id == DefaultZoneId)
            return true;
        if (id.Length is < 1 or > 80 || !TimeZoneInfo.TryConvertIanaIdToWindowsId(id, out _)
            || !TimeZoneInfo.TryFindSystemTimeZoneById(id, out var found))
            return false;
        zone = found;
        return true;
    }

    public static string Write(ResourceSchedule definition) => JsonSerializer.Serialize(definition, JsonSerializerOptions.Web);

    public static DateOnly LocalDate(DateTimeOffset instant, TimeZoneInfo? zone = null) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, zone ?? DefaultZone).DateTime);

    internal static int DayIndex(DateOnly date) => ((int)date.DayOfWeek + 6) % 7;

    public static DateOnly Monday(DateOnly date) => date.AddDays(-DayIndex(date));

    public static DateOnly LastDay(DateOnly today) => Monday(today).AddDays(7 * WeekCount - 1);

    public static DateTimeOffset At(DateOnly date, int hour) => AtTime(date, new TimeOnly(hour, 0));

    public static DateTimeOffset AtTime(DateOnly date, TimeOnly time) =>
        new(TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(time), DefaultZone));

    public static DateTimeOffset[] Instants(DateOnly date, TimeOnly time, TimeZoneInfo zone) =>
        Instants(date.ToDateTime(time), zone);

    internal static DateTimeOffset[] Instants(DateTime wallTime, TimeZoneInfo zone)
    {
        if (zone.IsInvalidTime(wallTime))
            return [];
        if (zone.IsAmbiguousTime(wallTime))
            return zone.GetAmbiguousTimeOffsets(wallTime)
                .Select(offset => new DateTimeOffset(wallTime, offset).ToUniversalTime()).Order().ToArray();
        return [new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(wallTime, zone))];
    }

    public static bool TryTime(string? text, out TimeOnly time) =>
        TimeOnly.TryParseExact(text, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out time);

    internal static IEnumerable<(int From, int To)> WorkingSegments(DayHours hours)
    {
        foreach (var period in hours.Hours)
        {
            var from = period.From;
            foreach (var pause in hours.Breaks.Where(pause => pause.From >= period.From && pause.To <= period.To))
            {
                yield return (from, pause.From);
                from = pause.To;
            }

            yield return (from, period.To);
        }
    }

    public static IEnumerable<DateTimeOffset> StartsOn(DateOnly day, ResourceSchedule definition, int duration = DefaultDuration,
        bool forMaintenance = false)
    {
        var rules = definition.Rules;
        if (!rules.Allows(duration))
            yield break;
        var zone = definition.Zone;
        var hours = definition.HoursOn(day);
        var midnight = day.ToDateTime(TimeOnly.MinValue);
        var starts = new List<DateTimeOffset>();
        var before = forMaintenance ? 0 : rules.BufferBeforeMinutes;
        var after = forMaintenance ? 0 : rules.BufferAfterMinutes;
        var step = rules.Flexible ? FlexibleStep : DefaultDuration;
        foreach (var (from, to) in WorkingSegments(hours))
        {
            var first = rules.Flexible ? (from + step - 1) / step * step : from;
            var last = rules.Flexible ? to - 1 : to - duration;
            for (var minute = first; minute <= last; minute += step)
            {
                foreach (var instant in Instants(midnight.AddMinutes(minute), zone))
                    if (FitsWindow(BookingWindow.At(instant, duration, before, after),
                        midnight.AddMinutes(from), midnight.AddMinutes(to), zone))
                        starts.Add(instant);
            }
        }

        DateTimeOffset? previous = null;
        foreach (var start in starts.Order())
        {
            // A partial-hour rollback can put two civil starts less than an hour apart in UTC.
            if (!rules.Flexible && previous is { } earlier && start < earlier.AddMinutes(duration))
                continue;
            previous = start;
            yield return start;
        }
    }

    public static bool FitsHours(BookingWindow window, ResourceSchedule definition)
    {
        var zone = definition.Zone;
        var day = LocalDate(window.StartsAt, zone);
        var midnight = day.ToDateTime(TimeOnly.MinValue);
        var hours = definition.HoursOn(day);
        return WorkingSegments(hours).Any(segment => FitsWindow(window,
            midnight.AddMinutes(segment.From), midnight.AddMinutes(segment.To), zone));
    }

    private static bool FitsWindow(BookingWindow window, DateTime from, DateTime to, TimeZoneInfo zone)
    {
        var first = window.OccupiedFrom;
        var last = window.OccupiedUntil.AddTicks(-1);
        if (zone.GetUtcOffset(first) == zone.GetUtcOffset(last))
            return TimeZoneInfo.ConvertTime(first, zone).DateTime >= from && TimeZoneInfo.ConvertTime(last, zone).DateTime < to;

        // A clock change can skip or repeat part of the civil interval.
        for (var minute = first; minute < window.OccupiedUntil; minute = minute.AddMinutes(1))
        {
            var local = TimeZoneInfo.ConvertTime(minute, zone).DateTime;
            if (local < from || local >= to)
                return false;
        }
        return true;
    }

    public static (DateTimeOffset From, DateTimeOffset Until) WeekEnvelope(DateOnly monday) =>
        (new DateTimeOffset(monday.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).AddHours(-14),
         new DateTimeOffset(monday.AddDays(7).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).AddHours(14));

    public static IEnumerable<DateTimeOffset> WeekStarts(DateOnly monday, TimeZoneInfo viewer,
        ResourceSchedule definition, DateTimeOffset now, int duration = DefaultDuration)
    {
        var until = monday.AddDays(7);
        var horizon = definition.Policies.BookingWindowDays.HasValue ? MaximumHorizon(now) : Horizon(now);
        // The venue's date can differ from the viewer's by up to two days.
        return Enumerable.Range(-2, 11).SelectMany(offset => StartsOn(monday.AddDays(offset), definition, duration))
            .Where(start => start < horizon && LocalDate(start, viewer) >= monday && LocalDate(start, viewer) < until);
    }

    public static IEnumerable<DateTimeOffset> FutureStarts(DateTimeOffset now, ResourceSchedule definition, int duration = DefaultDuration)
    {
        var horizon = definition.Policies.Horizon(now);
        var first = LocalDate(now, definition.Zone);
        var last = LocalDate(horizon.AddTicks(-1), definition.Zone);
        return Enumerable.Range(0, last.DayNumber - first.DayNumber + 1)
            .SelectMany(offset => StartsOn(first.AddDays(offset), definition, duration))
            .Where(start => start.AddMinutes(-definition.Rules.BufferBeforeMinutes) > now && start < horizon
                && start >= now.AddMinutes(definition.Policies.MinimumNoticeMinutes)).Order();
    }

    public static IOrderedEnumerable<DateTimeOffset> Nearest(IEnumerable<DateTimeOffset> starts, DateTimeOffset requested) =>
        starts.Where(start => start != requested).OrderBy(start => (start - requested).Duration())
            .ThenByDescending(start => start);

    public static DateTimeOffset Horizon(DateTimeOffset now) => At(LastDay(LocalDate(now)).AddDays(1), 0);

    public static DateTimeOffset MaximumHorizon(DateTimeOffset now) => now.AddDays(BookingPolicy.MaximumDays).AddTicks(1);

    public static DateOnly DisplayLastDay(DateTimeOffset now, TimeZoneInfo zone) => LocalDate(MaximumHorizon(now).AddTicks(-1), zone);

    public static bool InBookableHorizon(DateTimeOffset start, DateTimeOffset now) => start > now && start < MaximumHorizon(now);

    public static bool CanBook(DateTimeOffset start, DateTimeOffset now, ResourceSchedule definition, int duration = DefaultDuration) =>
        InBookableHorizon(start, now) && definition.Policies.TimeRestriction(start, now) is null
        && start.AddMinutes(-definition.Rules.BufferBeforeMinutes) > now
        && Contains(start, definition, duration);

    public static bool Contains(DateTimeOffset start, ResourceSchedule definition, int duration = DefaultDuration,
        bool forMaintenance = false) =>
        StartsOn(LocalDate(start, definition.Zone), definition, duration, forMaintenance).Contains(start);
}

internal readonly record struct BookingWindow(DateTimeOffset StartsAt, DateTimeOffset EndsAt,
    DateTimeOffset OccupiedFrom, DateTimeOffset OccupiedUntil)
{
    public static BookingWindow At(DateTimeOffset start, int duration, int before = 0, int after = 0) =>
        new(start, start.AddMinutes(duration), start.AddMinutes(-before), start.AddMinutes(duration + after));

    public bool Overlaps(BookingWindow other) => OccupiedFrom < other.OccupiedUntil && other.OccupiedFrom < OccupiedUntil;
}

internal sealed record BookingRules(bool Flexible = false, int BufferBeforeMinutes = 0, int BufferAfterMinutes = 0)
{
    public static readonly BookingRules Standard = new();
    public bool Allows(int duration) => Flexible ? Schedule.Durations.Contains(duration) : duration == Schedule.DefaultDuration;
    [System.Text.Json.Serialization.JsonIgnore]
    public int[] Options => Flexible ? Schedule.Durations : [Schedule.DefaultDuration];
    public bool IsValid() => Flexible ? ValidBuffer(BufferBeforeMinutes) && ValidBuffer(BufferAfterMinutes)
        : BufferBeforeMinutes == 0 && BufferAfterMinutes == 0;

    public BookingWindow Window(DateTimeOffset start, int duration) =>
        BookingWindow.At(start, duration, BufferBeforeMinutes, BufferAfterMinutes);

    private static bool ValidBuffer(int minutes) => minutes is >= 0 and <= 60 && minutes % 5 == 0;
}

internal sealed record ResourceSchedule(DayHours[] Week, ScheduleException[] Exceptions, string? TimeZone = null,
    BookingRules? Booking = null, BookingPolicy? Policy = null)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public BookingPolicy Policies => Policy ?? BookingPolicy.Standard;
    [System.Text.Json.Serialization.JsonIgnore]
    public BookingRules Rules => Booking ?? BookingRules.Standard;
    [System.Text.Json.Serialization.JsonIgnore]
    public TimeZoneInfo Zone => TimeZone is null ? Schedule.DefaultZone : TimeZoneInfo.FindSystemTimeZoneById(TimeZone);

    [System.Text.Json.Serialization.JsonIgnore]
    public string ZoneId => TimeZone ?? Schedule.DefaultZoneId;

    public DayHours HoursOn(DateOnly day) => Exceptions.FirstOrDefault(exception => exception.Date == day)?.Hours
        ?? Week[Schedule.DayIndex(day)];

    public ResourceSchedule? Normalize()
    {
        if (Week is not { Length: 7 } || Exceptions is null || Exceptions.Length > 366
            || Week.Any(day => day is null || !day.IsValid())
            || Exceptions.Any(item => item is null || item.Date == default || item.Hours is null || !item.Hours.IsValid())
            || Exceptions.Select(item => item.Date).Distinct().Count() != Exceptions.Length)
            return null;

        return new ResourceSchedule(Week.Select(day => day.Sort()).ToArray(), Exceptions.OrderBy(item => item.Date)
            .Select(item => item with { Hours = item.Hours.Sort() }).ToArray(), TimeZone, Booking, Policy);
    }
}

internal sealed record ScheduleException(DateOnly Date, DayHours Hours);
internal sealed record DayHours(SchedulePeriod[] Hours, SchedulePeriod[] Breaks)
{
    public bool IsValid() => ValidPeriods(Hours) && ValidPeriods(Breaks)
        && Breaks.All(pause => Hours.Any(period => period.From <= pause.From && pause.To <= period.To));

    public DayHours Sort() => new(Hours.OrderBy(period => period.From).ToArray(), Breaks.OrderBy(period => period.From).ToArray());

    private static bool ValidPeriods(SchedulePeriod[]? periods)
    {
        if (periods is null || periods.Length > 12 || periods.Any(period => period is null || period.From < 0
                || period.To > 1440 || period.From >= period.To))
            return false;

        var sorted = periods.OrderBy(period => period.From).ToArray();
        return !sorted.Zip(sorted.Skip(1)).Any(pair => pair.First.To > pair.Second.From);
    }
}

internal sealed record SchedulePeriod(int From, int To);
