using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace SlotGuard.Tests;

public sealed class NotificationTests(NotificationFixture app) : IClassFixture<NotificationFixture>
{
    private static readonly DateTimeOffset FirstSlot = SlotGuardFixture.Now.AddMinutes(30);

    [Fact]
    public async Task CommittedOutboxEventIsDeliveredAfterRabbitMqReconnects()
    {
        using var owner = await app.CreateGuest();
        using var next = await app.CreateGuest();
        app.Link.SetAvailable(false);
        try
        {
            var hold = await Offer(owner, next, new SlotRequest("microscope", FirstSlot));
            var failed = await WaitFor(() => FindOutbox(hold, item => item.Attempts > 0));
            Assert.Null(failed.PublishedAt);
            Assert.Empty(await GetNotifications(next));

            app.Link.SetAvailable(true);
            app.SetTime(failed.NextAttemptAt);
            var delivered = await WaitForDelivery(next, hold);
            Assert.Equal(failed.Id, delivered.EventId);
            await WaitFor(() => FindOutbox(hold, item => item.PublishedAt is not null));
            Assert.Empty(await GetNotifications(owner));
        }
        finally
        {
            RestoreDefaults();
        }
    }

    [Fact]
    public async Task RedeliveryCreatesOnlyOneNotification()
    {
        using var owner = await app.CreateGuest();
        using var next = await app.CreateGuest();
        var hold = await Offer(owner, next, new SlotRequest("electronics", FirstSlot));
        await WaitForDelivery(next, hold);
        var published = await WaitFor(() => FindOutbox(hold, item => item.PublishedAt is not null));

        await app.PublishAgain(published);
        await app.PublishAgain(published);
        // The consumer takes one message at a time and the next hold is queued after both duplicates.
        var barrier = await Offer(owner, next, new SlotRequest("electronics", FirstSlot.AddHours(2)));
        await WaitForDelivery(next, barrier);

        Assert.Equal(1, await NotificationCount(hold));
        Assert.Single(await GetNotifications(next), notification => notification.BookingId == hold);
    }

    [Fact]
    public async Task RecoveredDeliverySkipsAnExpiredHold()
    {
        using var owner = await app.CreateGuest();
        using var next = await app.CreateGuest();
        app.Link.SetAvailable(false);
        try
        {
            var hold = await Offer(owner, next, new SlotRequest("studio", FirstSlot));
            await WaitFor(() => FindOutbox(hold, item => item.Attempts > 0));

            app.SetTime(SlotGuardFixture.Now.AddMinutes(10));
            app.Link.SetAvailable(true);
            await WaitFor(() => FindOutbox(hold, item => item.PublishedAt is not null));
            var barrier = await Offer(owner, next, new SlotRequest("studio", FirstSlot.AddHours(2)));
            await WaitForDelivery(next, barrier);

            Assert.Equal(0, await NotificationCount(hold));
            Assert.DoesNotContain(await GetNotifications(next), notification => notification.BookingId == hold);
        }
        finally
        {
            RestoreDefaults();
        }
    }

    private void RestoreDefaults()
    {
        app.Link.SetAvailable(true);
        app.SetTime(SlotGuardFixture.Now);
    }

    private async Task<HoldOutboxEvent?> FindOutbox(Guid bookingId, Func<HoldOutboxEvent, bool> matches)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BookingDb>();
        var item = await db.HoldOutbox.AsNoTracking().SingleOrDefaultAsync(message => message.BookingId == bookingId);
        return item is not null && matches(item) ? item : null;
    }

    private async Task<int> NotificationCount(Guid bookingId)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BookingDb>();
        return await db.HoldNotifications.CountAsync(message => message.BookingId == bookingId);
    }

    private static async Task<Guid> Offer(HttpClient owner, HttpClient next, SlotRequest slot)
    {
        using var booked = await owner.PostAsJsonAsync("/api/bookings", slot);
        Assert.Equal(HttpStatusCode.Created, booked.StatusCode);
        var id = (await booked.Content.ReadFromJsonAsync<Identifier>())!.Id;
        using var waiting = await next.PostAsJsonAsync("/api/waitlist", slot);
        Assert.Equal(HttpStatusCode.Created, waiting.StatusCode);
        using var cancelled = await owner.DeleteAsync($"/api/bookings/{id}");
        Assert.Equal(HttpStatusCode.NoContent, cancelled.StatusCode);
        var calendar = (await next.GetFromJsonAsync<CalendarResponse>($"/api/calendar?resourceId={slot.ResourceId}"))!;
        var reserve = Assert.Single(calendar.Days.SelectMany(day => day.Slots), item => item.StartsAt == slot.StartsAt);
        Assert.Equal("held", reserve.Status);
        return reserve.BookingId!.Value;
    }

    private static async Task<Notification[]> GetNotifications(HttpClient client) =>
        (await client.GetFromJsonAsync<Notification[]>("/api/notifications"))!;

    private static Task<Notification> WaitForDelivery(HttpClient client, Guid bookingId) =>
        WaitFor(async () => (await GetNotifications(client)).SingleOrDefault(item => item.BookingId == bookingId));

    private static async Task<T> WaitFor<T>(Func<Task<T?>> read) where T : class
    {
        var timer = Stopwatch.StartNew();
        do
        {
            if (await read() is { } result)
                return result;
            await Task.Delay(200);
        }
        while (timer.Elapsed < TimeSpan.FromSeconds(25));

        throw new TimeoutException($"Timed out waiting for {typeof(T).Name}.");
    }

    private sealed record SlotRequest(string ResourceId, DateTimeOffset StartsAt);
    private sealed record Identifier(Guid Id);
    private sealed record CalendarResponse(Day[] Days);
    private sealed record Day(CalendarSlot[] Slots);
    private sealed record CalendarSlot(DateTimeOffset StartsAt, string Status, Guid? BookingId);
    private sealed record Notification(Guid EventId, Guid BookingId);
}
