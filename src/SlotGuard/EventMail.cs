using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace SlotGuard;

internal sealed class EventMail
{
    public Guid Id { get; init; }
    public Guid SeatId { get; init; }
    public string? Message { get; init; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public int Attempts { get; set; }

    public static void Queue(BookingDb db, SeatReservation seat, DateTimeOffset now, string? message = null)
    {
        if (seat.GuestEmail is not null && seat.Status == SeatStatus.Confirmed)
            db.EventMails.Add(new EventMail { Id = Guid.NewGuid(), SeatId = seat.Id, Message = message, NextAttemptAt = now });
    }
}

internal sealed class EventMailWorker(IServiceScopeFactory scopes, TimeProvider clock, IOptions<WorkspaceMailOptions> options,
    ILogger<EventMailWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!options.Value.IsConfigured && !options.Value.Profiles.Values.Any(profile => profile.IsConfigured)) return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync(ct))
        {
            try
            {
                for (var count = 0; count < 10 && await SendNext(ct); count++) { }
            }
            catch (Exception error) when (!ct.IsCancellationRequested)
            {
                log.LogWarning("Event email dispatch will retry ({ErrorType}).", error.GetType().Name);
            }
        }
    }

    private async Task<bool> SendNext(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BookingDb>();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var now = clock.GetUtcNow();
        var rows = await db.EventMails.FromSqlInterpolated($"""
            SELECT * FROM "EventMails" WHERE "CompletedAt" IS NULL AND "NextAttemptAt" <= {now}
            ORDER BY "NextAttemptAt", "Id" LIMIT 1 FOR UPDATE SKIP LOCKED
            """).ToListAsync(ct);
        if (rows.SingleOrDefault() is not { } job) return false;
        var row = await (from attendee in db.SeatReservations.AsNoTracking()
                         join session in db.Bookings.AsNoTracking() on attendee.SessionId equals session.Id
                         join resource in db.Resources.AsNoTracking() on session.ResourceId equals resource.Id
                         where attendee.Id == job.SeatId
                         select new { attendee, session, resource.OrganizationId }).SingleAsync(ct);
        if (row.attendee.GuestEmail is not { } email || row.attendee.Status != SeatStatus.Confirmed ||
            row.session.CancelledAt is not null || row.session.EndUtc <= now)
            job.CompletedAt = now;
        else
        {
            var mail = scope.ServiceProvider.GetRequiredService<IWorkspaceMail>();
            var subject = job.Message is null ? "SlotGuard · Ваш билет" : "SlotGuard · Сообщение организатора";
            var body = job.Message ?? $"Ваша запись на «{row.session.SessionTitle}» подтверждена. Код билета: {row.attendee.Id:D}.";
            var sent = await mail.Send(email, subject, body, new MailLink("event", "",
                OrganizationId: row.OrganizationId, EventId: row.session.Id), ct);
            if (sent) job.CompletedAt = clock.GetUtcNow();
            else job.NextAttemptAt = clock.GetUtcNow().AddSeconds(Math.Min(3600, 15 << Math.Min(job.Attempts++, 8)));
        }
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return true;
    }
}
