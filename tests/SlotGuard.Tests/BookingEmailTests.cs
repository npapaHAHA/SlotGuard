using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace SlotGuard.Tests;

public sealed class BookingEmailFixture : SlotGuardFixture
{
    internal Mailbox Mail { get; } = new();
    internal AccountFixture.TestMail AccountMail { get; } = new();

    protected override void ConfigureSettings(Dictionary<string, string?> settings) => settings["Mail:Host"] = "test";

    protected override void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<IBookingMail>(Mail);
        services.AddSingleton<IWorkspaceMail>(AccountMail);
    }

    internal sealed class Mailbox : IBookingMail
    {
        public const string RetryAddress = "retry@example.test";
        public const string ReminderRetryAddress = "reminder-retry@example.test";
        public ConcurrentQueue<BookingConfirmation> Delivered { get; } = new();
        public ConcurrentQueue<BookingReminder> Reminders { get; } = new();
        private int failed;
        private int reminderFailed;

        public Task<bool> SendConfirmation(BookingConfirmation confirmation, CancellationToken ct)
        {
            if (confirmation.Email == RetryAddress && Interlocked.Exchange(ref failed, 1) == 0)
                return Task.FromResult(false);
            Delivered.Enqueue(confirmation);
            return Task.FromResult(true);
        }

        public Task<bool> SendReminder(BookingReminder reminder, CancellationToken ct)
        {
            if (reminder.Email == ReminderRetryAddress && Interlocked.Exchange(ref reminderFailed, 1) == 0)
                return Task.FromResult(false);
            Reminders.Enqueue(reminder);
            return Task.FromResult(true);
        }
    }
}

public sealed class BookingEmailTests(BookingEmailFixture app) : IClassFixture<BookingEmailFixture>
{
    private const string Manage = "/api/manage-booking";
    private static readonly DateTimeOffset Start = SlotGuardFixture.Now.AddHours(2.5);

    [Fact]
    public async Task WorkspaceBookingUsesTheVerifiedAccountEmail()
    {
        const string email = "member@example.test";
        using var member = app.CreateClient();
        using var signup = await member.PostAsJsonAsync("/api/account/register",
            new { name = "Member", email, password = AccountFixture.Password });
        Assert.Equal(HttpStatusCode.NoContent, signup.StatusCode);
        var confirmation = app.AccountMail.Latest(email, "confirm");
        using var verified = await member.PostAsJsonAsync("/api/account/confirm",
            new { confirmation.UserId, confirmation.Token });
        Assert.Equal(HttpStatusCode.NoContent, verified.StatusCode);
        using var signedIn = await member.PostAsJsonAsync("/api/account/login", new { email, password = AccountFixture.Password });
        signedIn.EnsureSuccessStatusCode();
        var organization = await AccountFixture.CreateOrganization(member, "Email workspace");
        member.DefaultRequestHeaders.Add(OrganizationScope.HeaderName, organization.Id);
        using var resource = await member.PostAsJsonAsync("/api/workspace/resources", new { name = "Зал команды", location = "Офис" });
        Assert.Equal(HttpStatusCode.Created, resource.StatusCode);
        var resourceId = (await resource.Content.ReadFromJsonAsync<Resource>())!.Id;
        using var booked = await member.PostAsJsonAsync("/api/bookings", new { resourceId, startsAt = Start });
        Assert.Equal(HttpStatusCode.Created, booked.StatusCode);
        var id = (await booked.Content.ReadFromJsonAsync<CreatedBooking>())!.Id;
        var delivered = await Delivered(email);
        CheckCalendar(delivered.Calendar, Start, id, 0);
        Assert.Null(delivered.ManagementToken);
    }

    [Fact]
    public async Task LinkWorksWithoutCookiesAndFollowsOnlyItsOwnBookingAfterMove()
    {
        const string email = "guest@example.test";
        const string name = "Студия, Видеозапись и трансляция мероприятий";
        using var admin = await app.CreateDemo("university-admin");
        using var guest = app.CreateClient();
        using var cleanBrowser = app.CreateClient();
        var address = await Publish(admin, name);
        using var invalidEmail = await Book(guest, address, Start, "Display name <guest@example.test>");
        Assert.Equal(HttpStatusCode.BadRequest, invalidEmail.StatusCode);
        using var created = await Book(guest, address, Start, email);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var receipt = (await created.Content.ReadFromJsonAsync<PublicBookingEndpoints.Receipt>())!;
        using var repeated = await Book(guest, address, Start, email);
        Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
        var original = await Delivered(email);

        Assert.Equal(name, original.Resource);
        var calendar = CheckCalendar(original.Calendar, Start, receipt.Id, 0);
        Assert.Contains("SUMMARY:Студия\\, Видеозапись и трансляция мероприятий\r\n", calendar);
        Assert.Contains("LOCATION:Корпус А\\; этаж 2\r\n", calendar);
        using var guestFile = await guest.GetAsync($"{address}/{receipt.Id}/calendar.ics");
        Assert.Equal("text/calendar", guestFile.Content.Headers.ContentType?.MediaType);
        using var page = await cleanBrowser.GetAsync("/manage-booking");
        Assert.Contains("manage-booking.js", await page.Content.ReadAsStringAsync());
        using var missing = await cleanBrowser.GetAsync(Manage);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        cleanBrowser.DefaultRequestHeaders.Add("X-SlotGuard-Booking-Token", Assert.IsType<string>(original.ManagementToken));
        using var read = await cleanBrowser.GetAsync(Manage);
        Assert.True(read.Headers.CacheControl?.NoStore);
        Assert.False(read.Headers.Contains("Set-Cookie"));
        Assert.Equal(receipt.Id, (await read.Content.ReadFromJsonAsync<ManagedView>())!.Booking.Id);
        using var foreignFile = await cleanBrowser.GetAsync($"{address}/{receipt.Id}/calendar.ics");
        Assert.Equal(HttpStatusCode.NotFound, foreignFile.StatusCode);
        using var forbiddenWorkspace = await cleanBrowser.GetAsync("/api/workspace/resources");
        Assert.Equal(HttpStatusCode.Unauthorized, forbiddenWorkspace.StatusCode);
        using var managedFile = await cleanBrowser.GetAsync(Manage + "/calendar.ics");
        CheckCalendar(await managedFile.Content.ReadAsByteArrayAsync(), Start, receipt.Id, 0);

        var target = Start.AddHours(2);
        using var another = await Book(guest, address, target, null);
        Assert.Equal(HttpStatusCode.Created, another.StatusCode);
        var otherId = (await another.Content.ReadFromJsonAsync<PublicBookingEndpoints.Receipt>())!.Id;
        using var noAccess = await cleanBrowser.DeleteAsync($"{Manage}/{otherId}");
        Assert.Equal(HttpStatusCode.NotFound, noAccess.StatusCode);
        using var unavailable = await cleanBrowser.PostAsJsonAsync($"{Manage}/{receipt.Id}/reschedule", new { startsAt = target });
        Assert.Equal(HttpStatusCode.Conflict, unavailable.StatusCode);
        Assert.Equal(receipt.Id, (await cleanBrowser.GetFromJsonAsync<ManagedView>(Manage))!.Booking.Id);

        target = target.AddHours(2);
        using var moved = await cleanBrowser.PostAsJsonAsync($"{Manage}/{receipt.Id}/reschedule", new { startsAt = target });
        Assert.Equal(HttpStatusCode.Created, moved.StatusCode);
        var successor = (await cleanBrowser.GetFromJsonAsync<ManagedView>(Manage))!.Booking;
        Assert.NotEqual(receipt.Id, successor.Id);
        Assert.Equal(target, successor.StartsAt);
        var update = await Delivered(email, 2);
        CheckCalendar(update.Calendar, target, receipt.Id, 1);
        Assert.Equal(2, app.Mail.Delivered.Count(message => message.Email == email));
        using var cancelled = await cleanBrowser.DeleteAsync($"{Manage}/{successor.Id}");
        Assert.Equal(HttpStatusCode.NoContent, cancelled.StatusCode);
        Assert.Equal("cancelled", (await cleanBrowser.GetFromJsonAsync<ManagedView>(Manage))!.Booking.Status);
        using var cancelledFile = await cleanBrowser.GetAsync(Manage + "/calendar.ics");
        Assert.Equal(HttpStatusCode.NotFound, cancelledFile.StatusCode);

        app.SetTime(SlotGuardFixture.Now.AddDays(181));
        try
        {
            using var expired = await cleanBrowser.GetAsync(Manage);
            Assert.Equal(HttpStatusCode.NotFound, expired.StatusCode);
        }
        finally { app.SetTime(SlotGuardFixture.Now); }
    }

    [Fact]
    public async Task ApprovalQueuesTheConfirmationAndRetriesAfterAnSmtpFailure()
    {
        const string email = BookingEmailFixture.Mailbox.RetryAddress;
        using var admin = await app.CreateDemo("university-admin");
        using var guest = app.CreateClient();
        var address = await Publish(admin, "Зал с согласованием", requiresApproval: true,
            policy: new BookingPolicy(CancellationNoticeMinutes: 60, RescheduleNoticeMinutes: 120));
        using var created = await Book(guest, address, Start, email);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var receipt = (await created.Content.ReadFromJsonAsync<PublicBookingEndpoints.Receipt>())!;
        Assert.Equal("pending", receipt.Status);
        Assert.Null(await Outbox(receipt.Id));
        using var pendingFile = await guest.GetAsync($"{address}/{receipt.Id}/calendar.ics");
        Assert.Equal(HttpStatusCode.NotFound, pendingFile.StatusCode);

        using var approval = await admin.PostAsJsonAsync($"/api/approvals/{receipt.Id}/decision",
            new { status = "approved", reason = "Доступ согласован" });
        Assert.Equal(HttpStatusCode.NoContent, approval.StatusCode);
        var failed = await WaitUntil(async () => await Outbox(receipt.Id) is { Attempts: 1 } entry ? entry : null);
        Assert.Null(failed.CompletedAt);
        Assert.DoesNotContain(app.Mail.Delivered, message => message.Email == email);
        app.SetTime(failed.NextAttemptAt);
        try
        {
            var sent = await Delivered(email);
            CheckCalendar(sent.Calendar, Start, receipt.Id, 0);
            await WaitUntil(async () => await Outbox(receipt.Id) is { CompletedAt: not null } entry ? entry : null);
            using var manager = app.CreateClient();
            manager.DefaultRequestHeaders.Add("X-SlotGuard-Booking-Token", sent.ManagementToken);
            app.SetTime(Start.AddMinutes(-30));
            var status = (await manager.GetFromJsonAsync<ManagedView>(Manage))!.Booking;
            Assert.Equal("approved", status.Status);
            Assert.False(status.Changes!.CanCancel);
            Assert.False(status.Changes.CanReschedule);
            using var lateCancel = await manager.DeleteAsync($"{Manage}/{receipt.Id}");
            using var lateMove = await manager.PostAsJsonAsync($"{Manage}/{receipt.Id}/reschedule", new { startsAt = Start.AddHours(2) });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, lateCancel.StatusCode);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, lateMove.StatusCode);
        }
        finally { app.SetTime(SlotGuardFixture.Now); }
    }

    private async Task<BookingEmail?> Outbox(Guid id)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<BookingDb>().BookingEmails.AsNoTracking()
            .SingleOrDefaultAsync(email => email.BookingId == id && email.Kind == BookingEmailKind.Confirmation);
    }

    private Task<BookingConfirmation> Delivered(string email, int count = 1) => WaitUntil(() =>
        Task.FromResult(app.Mail.Delivered.Where(message => message.Email == email).Skip(count - 1).FirstOrDefault()));

    private static async Task<T> WaitUntil<T>(Func<Task<T?>> observe) where T : class
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        while (true)
        {
            if (await observe() is { } visible)
                return visible;
            await Task.Delay(150, timeout.Token);
        }
    }

    private static string CheckCalendar(byte[] file, DateTimeOffset start, Guid root, int sequence)
    {
        var content = Encoding.UTF8.GetString(file);
        Assert.StartsWith("BEGIN:VCALENDAR\r\n", content);
        Assert.EndsWith("END:VCALENDAR\r\n", content);
        Assert.All(content.Split("\r\n"), line => Assert.InRange(Encoding.UTF8.GetByteCount(line), 0, 75));
        Assert.Contains($"UID:{root:N}@slotguard\r\n", content);
        Assert.Contains($"SEQUENCE:{sequence}\r\n", content);
        Assert.Contains($"DTSTART:{start:yyyyMMdd'T'HHmmss'Z'}\r\n", content);
        Assert.Contains($"DTEND:{start.AddHours(1):yyyyMMdd'T'HHmmss'Z'}\r\n", content);
        return content.Replace("\r\n ", "");
    }

    private static async Task<string> Publish(HttpClient admin, string name, bool requiresApproval = false, BookingPolicy? policy = null)
    {
        using var resource = await admin.PostAsJsonAsync("/api/workspace/resources",
            new { name, location = "Корпус А; этаж 2", requiresApproval });
        Assert.Equal(HttpStatusCode.Created, resource.StatusCode);
        var id = (await resource.Content.ReadFromJsonAsync<Resource>())!.Id;
        if (policy is not null)
        {
            var open = new DayHours([new(480, 1080)], []);
            var schedule = new ResourceSchedule(Enumerable.Repeat(open, 7).ToArray(), [], Policy: policy);
            using var saved = await admin.PutAsJsonAsync($"/api/workspace/resources/{id}/schedule", schedule);
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        }
        using var response = await admin.PutAsJsonAsync($"/api/workspace/resources/{id}/public-link", new { enabled = true });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var published = await response.Content.ReadFromJsonAsync<PublishedLink>();
        return $"/api/public/resources/{published!.PublicLinkId}/bookings";
    }

    private static Task<HttpResponseMessage> Book(HttpClient guest, string address, DateTimeOffset startsAt, string? email) =>
        guest.PostAsJsonAsync(address, new { startsAt, email });

    private sealed record PublishedLink(Guid PublicLinkId);
    private sealed record ManagedView(PublicBookingEndpoints.Receipt Booking);
}
