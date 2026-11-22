using System.Globalization;
using System.Text;

namespace SlotGuard;

internal static class BookingCalendar
{
    public const string FileName = "slotguard.ics";
    public const string ContentType = "text/calendar; charset=utf-8";

    public static bool IsConfirmed(Booking booking) =>
        booking.CancelledAt is null && booking.HoldExpiresAt is null && booking.MaintenanceReason is null
        && booking.ApprovalStatus is null or BookingApproval.Approved;

    public static IResult Download(Booking booking, Resource resource, DateTimeOffset now) =>
        IsConfirmed(booking) ? Results.File(Create(booking, resource, now), ContentType, FileName) : Results.NotFound();

    public static byte[] Create(Booking booking, Resource resource, DateTimeOffset now)
    {
        string[] lines =
        [
            "BEGIN:VCALENDAR",
            "VERSION:2.0",
            "PRODID:-//SlotGuard//Booking Calendar//EN",
            "BEGIN:VEVENT",
            $"UID:{booking.CalendarRootId ?? booking.Id:N}@slotguard",
            $"SEQUENCE:{booking.CalendarSequence.ToString(CultureInfo.InvariantCulture)}",
            $"DTSTAMP:{Timestamp(now)}",
            $"DTSTART:{Timestamp(booking.StartUtc)}",
            $"DTEND:{Timestamp(booking.EndUtc)}",
            "STATUS:CONFIRMED",
            $"SUMMARY:{Escape(resource.Name)}",
            $"LOCATION:{Escape(resource.Location)}",
            "END:VEVENT",
            "END:VCALENDAR"
        ];
        var calendar = new StringBuilder();
        foreach (var line in lines)
        {
            var octets = 0;
            foreach (var rune in line.EnumerateRunes())
            {
                if (octets + rune.Utf8SequenceLength > 75)
                {
                    calendar.Append("\r\n ");
                    octets = 1;
                }
                calendar.Append(rune.ToString());
                octets += rune.Utf8SequenceLength;
            }
            calendar.Append("\r\n");
        }
        return Encoding.UTF8.GetBytes(calendar.ToString());
    }

    private static string Timestamp(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

    private static string Escape(string value) => value.Replace("\\", "\\\\")
        .ReplaceLineEndings("\n").Replace("\n", "\\n").Replace(";", "\\;").Replace(",", "\\,");
}
