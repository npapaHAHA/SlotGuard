using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace SlotGuard.Tests;

public sealed class BookingReminderTests(BookingEmailFixture app) : IClassFixture<BookingEmailFixture>
{
    private static readonly DateTimeOffset Now = SlotGuardFixture.Now;
    private static readonly DateTimeOffset Start = Now.AddDays(2).AddHours(2.5);

    [Fact]
    public async Task DeliversEachReminderAtItsOwnDeadlineAndKeepsTheBookingLink()
    {
        const string address = "both-reminders@example.test";
        using var admin = await app.CreateDemo("university-admin");
        using var guest = app.CreateClient();
        var path = await Publish(admin, "Зал с напоминаниями");
        var booking = await Book(guest, path, Start, address);
        var jobs = await Jobs(booking.Id);
        Assert.Equal(3, jobs.Length);
        Assert.Equal(Start.AddHours(-24), Job(jobs, BookingEmailKind.DayBefore).NextAttemptAt);
        Assert.Equal(Start.AddHours(-1), Job(jobs, BookingEmailKind.HourBefore).NextAttemptAt);
        Assert.Empty(Delivered(address));
        using var replay = await guest.PostAsJsonAsync(path, new { startsAt = Start, email = address });
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(3, (await Jobs(booking.Id)).Length);

        try
        {
            app.SetTime(Start.AddHours(-24));
            var daily = await WaitForDelivery(address, BookingEmailKind.DayBefore);
            Assert.Contains("11:00 +03:00", daily.Time);
            Assert.Single(Delivered(address));
            Assert.Null(Job(await Jobs(booking.Id), BookingEmailKind.HourBefore).CompletedAt);
            app.SetTime(Start.AddHours(-1));
            var hourly = await WaitForDelivery(address, BookingEmailKind.HourBefore);
            await WaitForJob(booking.Id, BookingEmailKind.HourBefore, job => job.CompletedAt.HasValue);
            Assert.Equal(2, Delivered(address).Length);
            using var browser = app.CreateClient();
            browser.DefaultRequestHeaders.Add("X-SlotGuard-Booking-Token", Assert.IsType<string>(hourly.ManagementToken));
            using var ownBooking = await browser.GetAsync("/api/manage-booking");
            Assert.Equal(HttpStatusCode.OK, ownBooking.StatusCode);
        }
        finally { app.SetTime(Now); }
    }

    [Fact]
    public async Task ApprovalKeepsOnlyFutureDeadlinesAndRejectedRequestsSendNothing()
    {
        const string approvedEmail = "approved-reminder@example.test";
        const string rejectedEmail = "rejected-reminder@example.test";
        using var admin = await app.CreateDemo("university-admin");
        using var guest = app.CreateClient();
        var path = await Publish(admin, "Заявка с напоминанием", requiresApproval: true);
        var start = Start.AddDays(-1);
        var request = await Book(guest, path, start, approvedEmail);
        var rejected = await Book(guest, path, start.AddHours(2), rejectedEmail);
        Assert.Equal(BookingApproval.Pending, request.Status);
        Assert.Empty(await Jobs(request.Id));
        Assert.Empty(await Jobs(rejected.Id));

        app.SetTime(start.AddHours(-20));
        try
        {
            using var decision = await admin.PostAsJsonAsync($"/api/approvals/{request.Id}/decision",
                new { status = "approved", reason = "Подтверждено" });
            Assert.Equal(HttpStatusCode.NoContent, decision.StatusCode);
            using var rejection = await admin.PostAsJsonAsync($"/api/approvals/{rejected.Id}/decision",
                new { status = "rejected", reason = "Время больше не требуется" });
            Assert.Equal(HttpStatusCode.NoContent, rejection.StatusCode);
            var jobs = await Jobs(request.Id);
            Assert.Equal(2, jobs.Length);
            Assert.DoesNotContain(jobs, job => job.Kind == BookingEmailKind.DayBefore);
            Assert.Equal(start.AddHours(-1), Job(jobs, BookingEmailKind.HourBefore).NextAttemptAt);
            Assert.Empty(await Jobs(rejected.Id));

            app.SetTime(start.AddHours(-1));
            await WaitForDelivery(approvedEmail, BookingEmailKind.HourBefore);
            Assert.Empty(Delivered(rejectedEmail));
        }
        finally { app.SetTime(Now); }
    }

    [Fact]
    public async Task MoveReplacesTheDeadlinesAndCancellationSuppressesUndeliveredMail()
    {
        const string address = "moved-reminders@example.test";
        using var admin = await app.CreateDemo("university-admin");
        using var guest = app.CreateClient();
        var path = await Publish(admin, "Зал для переноса");
        var old = await Book(guest, path, Start, address);
        var newStart = Start.AddDays(1);
        using var response = await guest.PostAsJsonAsync($"{path}/{old.Id}/reschedule", new { startsAt = newStart });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var replacement = (await response.Content.ReadFromJsonAsync<PublicBookingEndpoints.Receipt>())!;
        Assert.All(await Jobs(old.Id), job => Assert.NotNull(job.CompletedAt));
        var scheduled = await Jobs(replacement.Id);
        Assert.Equal(newStart.AddHours(-24), Job(scheduled, BookingEmailKind.DayBefore).NextAttemptAt);
        Assert.Equal(newStart.AddHours(-1), Job(scheduled, BookingEmailKind.HourBefore).NextAttemptAt);

        try
        {
            app.SetTime(newStart.AddHours(-24));
            var reminder = await WaitForDelivery(address, BookingEmailKind.DayBefore);
            Assert.Contains("14.05.2026 11:00 +03:00", reminder.Time);
            using var cancelled = await guest.DeleteAsync($"{path}/{replacement.Id}");
            Assert.Equal(HttpStatusCode.NoContent, cancelled.StatusCode);
            Assert.All(await Jobs(replacement.Id), job => Assert.NotNull(job.CompletedAt));
            Assert.Single(Delivered(address));
        }
        finally { app.SetTime(Now); }
    }

    [Fact]
    public async Task FailedSmtpRemainsInTheOutboxAndNoReminderIsSentAfterStart()
    {
        const string address = BookingEmailFixture.Mailbox.ReminderRetryAddress;
        using var admin = await app.CreateDemo("university-admin");
        using var guest = app.CreateClient();
        var path = await Publish(admin, "Напоминание после сбоя");
        var booking = await Book(guest, path, Start, address);
        var daily = Start.AddHours(-24);
        app.SetTime(daily);
        try
        {
            var failure = await WaitForJob(booking.Id, BookingEmailKind.DayBefore, job => job.Attempts == 1);
            Assert.Null(failure.CompletedAt);
            Assert.Equal(daily.AddSeconds(15), failure.NextAttemptAt);
            Assert.Empty(Delivered(address));
            app.SetTime(failure.NextAttemptAt);
            await WaitForDelivery(address, BookingEmailKind.DayBefore);
            await WaitForJob(booking.Id, BookingEmailKind.DayBefore, job => job.CompletedAt.HasValue);
            app.SetTime(Start);
            await WaitForJob(booking.Id, BookingEmailKind.HourBefore, job => job.CompletedAt.HasValue);
            Assert.Equal(BookingEmailKind.DayBefore, Assert.Single(Delivered(address)).Kind);
        }
        finally { app.SetTime(Now); }
    }

    [Fact]
    public async Task LongOutageDoesNotDeliverBothRemindersTogether()
    {
        const string address = "missed-window@example.test";
        using var admin = await app.CreateDemo("university-admin");
        using var guest = app.CreateClient();
        var path = await Publish(admin, "Длительный сбой почты");
        var booking = await Book(guest, path, Start, address);
        app.SetTime(Start.AddMinutes(-30));
        try
        {
            await WaitForDelivery(address, BookingEmailKind.HourBefore);
            await WaitForJob(booking.Id, BookingEmailKind.DayBefore, job => job.CompletedAt.HasValue);
            Assert.Equal(BookingEmailKind.HourBefore, Assert.Single(Delivered(address)).Kind);
        }
        finally { app.SetTime(Now); }
    }

    private async Task<BookingEmail[]> Jobs(Guid bookingId)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<BookingDb>().BookingEmails.AsNoTracking()
            .Where(job => job.BookingId == bookingId).ToArrayAsync();
    }

    private BookingReminder[] Delivered(string address) => app.Mail.Reminders.Where(reminder => reminder.Email == address).ToArray();

    private Task<BookingReminder> WaitForDelivery(string address, BookingEmailKind kind) =>
        WaitUntil(() => Task.FromResult(Delivered(address).FirstOrDefault(reminder => reminder.Kind == kind)));

    private Task<BookingEmail> WaitForJob(Guid id, BookingEmailKind kind, Func<BookingEmail, bool> matches) =>
        WaitUntil(async () => (await Jobs(id)).FirstOrDefault(job => job.Kind == kind && matches(job)));

    private static BookingEmail Job(BookingEmail[] jobs, BookingEmailKind kind) => Assert.Single(jobs, job => job.Kind == kind);

    private static async Task<T> WaitUntil<T>(Func<Task<T?>> read) where T : class
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (true)
        {
            if (await read() is { } found)
                return found;
            await Task.Delay(150, timeout.Token);
        }
    }

    private static async Task<PublicBookingEndpoints.Receipt> Book(HttpClient guest, string path, DateTimeOffset startsAt, string email)
    {
        using var created = await guest.PostAsJsonAsync(path, new { startsAt, email });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await created.Content.ReadFromJsonAsync<PublicBookingEndpoints.Receipt>())!;
    }

    private static async Task<string> Publish(HttpClient admin, string name, bool requiresApproval = false)
    {
        using var response = await admin.PostAsJsonAsync("/api/workspace/resources",
            new { name, location = "Третий этаж", requiresApproval });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var id = (await response.Content.ReadFromJsonAsync<Resource>())!.Id;
        using var linkResponse = await admin.PutAsJsonAsync($"/api/workspace/resources/{id}/public-link", new { enabled = true });
        Assert.Equal(HttpStatusCode.OK, linkResponse.StatusCode);
        var link = (await linkResponse.Content.ReadFromJsonAsync<PublishedLink>())!;
        return $"/api/public/resources/{link.PublicLinkId}/bookings";
    }

    private sealed record PublishedLink(Guid PublicLinkId);
}
