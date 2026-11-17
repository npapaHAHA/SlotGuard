namespace SlotGuard;

internal static class Schedule
{
    public static readonly TimeSpan MoscowOffset = TimeSpan.FromHours(3);
    public static readonly int[] Hours = [9, 11, 13, 15, 17];
    public const int WeekCount = 12;

    public static DateOnly LocalDate(DateTimeOffset instant) =>
        DateOnly.FromDateTime(instant.ToOffset(MoscowOffset).DateTime);

    public static DateOnly Monday(DateOnly date) =>
        date.AddDays(-(((int)date.DayOfWeek + 6) % 7));

    public static DateTimeOffset At(DateOnly date, int hour) =>
        new DateTimeOffset(date.ToDateTime(new TimeOnly(hour, 0)), MoscowOffset).ToUniversalTime();

    public static bool CanBook(DateTimeOffset start, DateTimeOffset now)
    {
        var firstWeek = Monday(LocalDate(now));
        if (start <= now || start >= At(firstWeek.AddDays(7 * WeekCount), 0))
            return false;

        var day = LocalDate(start);
        return Hours.Any(hour => At(day, hour) == start);
    }
}
