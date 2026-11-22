using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace SlotGuard;

internal enum BookingEmailKind
{
    Confirmation = 0,
    HourBefore = 1,
    DayBefore = 24
}

internal sealed class BookingEmail
{
    public Guid BookingId { get; init; }
    public BookingEmailKind Kind { get; init; }
    public int Attempts { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

internal static class BookingEmailQueue
{
    public static void Enqueue(BookingDb db, Booking booking, DateTimeOffset now)
    {
        if (!BookingCalendar.IsConfirmed(booking) || string.IsNullOrWhiteSpace(booking.ContactEmail))
            return;

        db.BookingEmails.Add(new BookingEmail { BookingId = booking.Id, Kind = BookingEmailKind.Confirmation, NextAttemptAt = now });
        AddReminder(BookingEmailKind.DayBefore);
        AddReminder(BookingEmailKind.HourBefore);

        void AddReminder(BookingEmailKind kind)
        {
            var due = booking.StartUtc.AddHours(-(int)kind);
            if (due >= now)
                db.BookingEmails.Add(new BookingEmail { BookingId = booking.Id, Kind = kind, NextAttemptAt = due });
        }
    }

    public static Task Suppress(BookingDb db, Guid[] bookingIds, DateTimeOffset now, CancellationToken ct) =>
        bookingIds.Length == 0 ? Task.CompletedTask : db.BookingEmails
            .Where(email => bookingIds.Contains(email.BookingId) && email.CompletedAt == null)
            .ExecuteUpdateAsync(set => set.SetProperty(email => email.CompletedAt, now), ct);
}

internal sealed record BookingConfirmation(string Email, string Resource, string Location, string Time,
    string? ManagementToken, byte[] Calendar, string? OrganizationId = null);
internal sealed record BookingReminder(string Email, string Resource, string Location, string Time,
    string? ManagementToken, BookingEmailKind Kind, string? OrganizationId = null);

internal interface IBookingMail
{
    Task<bool> SendConfirmation(BookingConfirmation confirmation, CancellationToken ct);
    Task<bool> SendReminder(BookingReminder reminder, CancellationToken ct);
}

internal sealed class BookingEmailWorker(IServiceScopeFactory scopes, TimeProvider clock, BookingManagementTokens tokens,
    IOptions<WorkspaceMailOptions> options, ILogger<BookingEmailWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.IsConfigured && !options.Value.Profiles.Values.Any(profile => profile.IsConfigured))
            return;

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                for (var sent = 0; sent < 20 && await ProcessNext(stoppingToken); sent++) { }
            }
            catch (Exception error) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning("Booking email dispatch will retry ({ErrorType}).", error.GetType().Name);
            }
        }
    }

    private async Task<bool> ProcessNext(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BookingDb>();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var now = clock.GetUtcNow();
        var due = await db.BookingEmails.FromSqlInterpolated($"""
            SELECT * FROM "BookingEmails"
            WHERE "CompletedAt" IS NULL AND "NextAttemptAt" <= {now}
            ORDER BY "NextAttemptAt", "BookingId", "Kind" LIMIT 1 FOR UPDATE SKIP LOCKED
            """).ToListAsync(ct);
        if (due.Count == 0)
            return false;

        var email = due[0];
        var row = await (from item in db.Bookings.AsNoTracking()
            join resource in db.Resources.AsNoTracking() on item.ResourceId equals resource.Id
            where item.Id == email.BookingId
            select new { Booking = item, Resource = resource }).SingleAsync(ct);
        var booking = row.Booking;

        if (!BookingCalendar.IsConfirmed(booking) || row.Resource.IsArchived || booking.StartUtc <= now
            || string.IsNullOrWhiteSpace(booking.ContactEmail)
            || email.Kind == BookingEmailKind.DayBefore && now >= booking.StartUtc.AddHours(-1))
            email.CompletedAt = now;
        else
        {
            var zone = Schedule.Read(row.Resource).Zone;
            var start = TimeZoneInfo.ConvertTime(booking.StartUtc, zone);
            var end = TimeZoneInfo.ConvertTime(booking.EndUtc, zone);
            var time = FormattableString.Invariant($"{start:dd.MM.yyyy HH:mm zzz} — {end:dd.MM.yyyy HH:mm zzz}");
            var token = booking.GuestManagement ? tokens.Issue(booking.CalendarRootId ?? booking.Id) : null;
            var mail = scope.ServiceProvider.GetRequiredService<IBookingMail>();
            var sent = email.Kind == BookingEmailKind.Confirmation
                ? await mail.SendConfirmation(new BookingConfirmation(booking.ContactEmail, row.Resource.Name,
                    row.Resource.Location, time, token, BookingCalendar.Create(booking, row.Resource, now),
                    row.Resource.OrganizationId), ct)
                : await mail.SendReminder(new BookingReminder(booking.ContactEmail, row.Resource.Name, row.Resource.Location,
                    time, token, email.Kind, row.Resource.OrganizationId), ct);
            if (sent)
                email.CompletedAt = clock.GetUtcNow();
            else
            {
                email.Attempts++;
                email.NextAttemptAt = clock.GetUtcNow().AddSeconds(Math.Min(300, 15d * email.Attempts));
            }
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return true;
    }
}
