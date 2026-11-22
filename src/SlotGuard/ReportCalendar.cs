namespace SlotGuard;

internal readonly record struct ReportSpan(DateTimeOffset From, DateTimeOffset Until)
{
    public double Minutes => (Until - From).TotalMinutes;
}

internal static class ReportCalendar
{
    public static ReportSpan[] CivilSpans(DateOnly date, IEnumerable<(int From, int To)> segments, TimeZoneInfo zone)
    {
        var midnight = date.ToDateTime(TimeOnly.MinValue);
        var spans = new List<ReportSpan>();
        foreach (var (from, to) in segments)
        {
            var start = midnight.AddMinutes(from);
            var end = midnight.AddMinutes(to);
            var starts = Schedule.Instants(start, zone);
            var ends = Schedule.Instants(end, zone);
            if (starts.Length == 1 && ends.Length == 1 && ends[0] - starts[0] == end - start)
            {
                spans.Add(new ReportSpan(starts[0], ends[0]));
                continue;
            }

            // Enumerate civil minutes only around a clock change. Repeated minutes contribute twice.
            for (var minute = start; minute < end; minute = minute.AddMinutes(1))
                foreach (var instant in Schedule.Instants(minute, zone))
                    spans.Add(new ReportSpan(instant, instant.AddMinutes(1)));
        }
        return Merge(spans);
    }

    public static ReportSpan[] WorkingHours(ResourceSchedule schedule, DateTimeOffset from, DateTimeOffset until)
    {
        var zone = schedule.Zone;
        var start = Schedule.LocalDate(from, zone);
        var end = Schedule.LocalDate(until, zone);
        return Merge(Enumerable.Range(0, end.DayNumber - start.DayNumber + 1).SelectMany(offset =>
        {
            var date = start.AddDays(offset);
            return CivilSpans(date, Schedule.WorkingSegments(schedule.HoursOn(date)), zone);
        }));
    }

    private static ReportSpan[] Merge(IEnumerable<ReportSpan> spans)
    {
        var merged = new List<ReportSpan>();
        foreach (var span in spans.OrderBy(item => item.From))
        {
            if (merged.Count == 0 || merged[^1].Until < span.From)
                merged.Add(span);
            else if (merged[^1].Until < span.Until)
                merged[^1] = new ReportSpan(merged[^1].From, span.Until);
        }
        return merged.ToArray();
    }

    public static IEnumerable<ReportSpan> Intersect(ReportSpan[] a, ReportSpan[] b)
    {
        var i = 0;
        var j = 0;
        while (i < a.Length && j < b.Length)
        {
            var left = a[i];
            var right = b[j];
            var start = left.From > right.From ? left.From : right.From;
            var end = left.Until < right.Until ? left.Until : right.Until;
            if (end > start)
                yield return new ReportSpan(start, end);
            if (left.Until <= right.Until)
                i++;
            else
                j++;
        }
    }
}
