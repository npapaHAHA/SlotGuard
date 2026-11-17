using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace SlotGuard.Tests;

public sealed class WaitlistTests(SlotGuardFixture app) : IClassFixture<SlotGuardFixture>
{
    [Fact]
    public async Task ExpiredTenMinuteHoldPassesToNextGuestWithoutAMutationRequest()
    {
        using var owner = await app.CreateGuest();
        using var first = await app.CreateGuest();
        using var second = await app.CreateGuest();
        var slot = new SlotRequest("microscope", SlotGuardFixture.Now.AddMinutes(30));
        var booking = await Book(owner, slot);

        using var joined = await Join(first, slot, HttpStatusCode.Created);
        using var duplicate = await Join(first, slot, HttpStatusCode.OK);
        Assert.Equal(await joined.Content.ReadFromJsonAsync<Entry>(), await duplicate.Content.ReadFromJsonAsync<Entry>());
        using var secondJoin = await Join(second, slot, HttpStatusCode.Created);
        Assert.Equal(1, (await ReadSlot(first, slot)).WaitlistPosition);
        Assert.Equal(2, (await ReadSlot(second, slot)).WaitlistPosition);
        using var cancelled = await owner.DeleteAsync($"/api/bookings/{booking}");
        Assert.Equal(HttpStatusCode.NoContent, cancelled.StatusCode);

        var reserve = await ReadSlot(first, slot);
        Assert.Equal("held", reserve.Status);
        Assert.Equal(SlotGuardFixture.Now.AddMinutes(10), reserve.HoldExpiresAt);
        Assert.Null(reserve.WaitlistPosition);
        Assert.Equal(1, (await ReadSlot(second, slot)).WaitlistPosition);
        using var unauthorized = await Confirm(second, reserve.BookingId);
        Assert.Equal(HttpStatusCode.NotFound, unauthorized.StatusCode);

        try
        {
            app.SetTime(SlotGuardFixture.Now.AddMinutes(10).AddSeconds(-1));
            Assert.Equal("held", (await ReadSlot(first, slot)).Status);
            app.SetTime(SlotGuardFixture.Now.AddMinutes(10));
            var next = await WaitForHold(second, slot);
            Assert.Equal(SlotGuardFixture.Now.AddMinutes(20), next.HoldExpiresAt);
            Assert.Null(next.WaitlistPosition);
            using var stale = await Confirm(first, reserve.BookingId);
            Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

            using var confirmed = await Confirm(second, next.BookingId);
            using var retried = await Confirm(second, next.BookingId);
            Assert.Equal(HttpStatusCode.NoContent, confirmed.StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, retried.StatusCode);
            Assert.Equal("mine", (await ReadSlot(second, slot)).Status);
            await AssertSingleActiveBooking(slot, next.BookingId);
        }
        finally
        {
            app.SetTime(SlotGuardFixture.Now);
        }
    }

    [Fact]
    public async Task ConcurrentCancellationAndBookingsRespectTheQueue()
    {
        using var owner = await app.CreateGuest();
        using var queued = await app.CreateGuest();
        using var outsider = await app.CreateGuest();
        var slot = new SlotRequest("electronics", SlotGuardFixture.Now.AddMinutes(30));
        var booking = await Book(owner, slot);
        using var joined = await Join(queued, slot, HttpStatusCode.Created);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var cancellations = Enumerable.Range(0, 2).Select(_ => Race(() => owner.DeleteAsync($"/api/bookings/{booking}"))).ToArray();
        var attempts = Enumerable.Range(0, 8).Select(_ => Race(() => outsider.PostAsJsonAsync("/api/bookings", slot))).ToArray();
        start.SetResult();

        Assert.Equal([HttpStatusCode.NoContent, HttpStatusCode.NotFound], (await Task.WhenAll(cancellations)).Order());
        Assert.All(await Task.WhenAll(attempts), status => Assert.Equal(HttpStatusCode.Conflict, status));
        var reserve = await ReadSlot(queued, slot);
        Assert.Equal("held", reserve.Status);
        await AssertSingleActiveBooking(slot, reserve.BookingId);

        async Task<HttpStatusCode> Race(Func<Task<HttpResponseMessage>> request)
        {
            await start.Task;
            using var response = await request();
            return response.StatusCode;
        }
    }

    [Fact]
    public async Task LeavingTheQueueSkipsThatGuestAndHoldEndsAtSlotStart()
    {
        using var owner = await app.CreateGuest();
        using var first = await app.CreateGuest();
        using var second = await app.CreateGuest();
        var slot = new SlotRequest("studio", SlotGuardFixture.Now.AddMinutes(30));
        using var freeSlot = await Join(first, slot, HttpStatusCode.Conflict);
        var booking = await Book(owner, slot);
        using var firstJoin = await Join(first, slot, HttpStatusCode.Created);
        using var secondJoin = await Join(second, slot, HttpStatusCode.Created);
        var entry = (await firstJoin.Content.ReadFromJsonAsync<Entry>())!;
        using var forbidden = await second.DeleteAsync($"/api/waitlist/{entry.Id}");
        Assert.Equal(HttpStatusCode.NotFound, forbidden.StatusCode);
        using var left = await first.DeleteAsync($"/api/waitlist/{entry.Id}");
        Assert.Equal(HttpStatusCode.NoContent, left.StatusCode);
        Assert.Equal(1, (await ReadSlot(second, slot)).WaitlistPosition);
        try
        {
            app.SetTime(slot.StartsAt.AddMinutes(-3));
            using var cancelled = await owner.DeleteAsync($"/api/bookings/{booking}");
            Assert.Equal(HttpStatusCode.NoContent, cancelled.StatusCode);
            var reserve = await ReadSlot(second, slot);
            Assert.Equal("held", reserve.Status);
            Assert.Equal(slot.StartsAt, reserve.HoldExpiresAt);

            app.SetTime(slot.StartsAt);
            using var late = await Confirm(second, reserve.BookingId);
            Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
            Assert.Equal("past", (await ReadSlot(second, slot)).Status);
        }
        finally
        {
            app.SetTime(SlotGuardFixture.Now);
        }
    }

    private async Task AssertSingleActiveBooking(SlotRequest slot, Guid? expected)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BookingDb>();
        var active = await db.Bookings.AsNoTracking().Where(booking => booking.CancelledAt == null
            && booking.ResourceId == slot.ResourceId && booking.StartUtc == slot.StartsAt).ToListAsync();
        Assert.Equal(expected, Assert.Single(active).Id);
    }

    private static async Task<CalendarSlot> WaitForHold(HttpClient client, SlotRequest slot)
    {
        var timeout = Stopwatch.StartNew();
        do
        {
            var current = await ReadSlot(client, slot);
            if (current.Status == "held")
                return current;
            await Task.Delay(150);
        }
        while (timeout.Elapsed < TimeSpan.FromSeconds(15));

        throw new TimeoutException("The background worker did not offer the slot to the next guest.");
    }

    private static async Task<Guid> Book(HttpClient client, SlotRequest slot)
    {
        using var created = await client.PostAsJsonAsync("/api/bookings", slot);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await created.Content.ReadFromJsonAsync<BookingResponse>())!.Id;
    }

    private static async Task<HttpResponseMessage> Join(HttpClient client, SlotRequest slot, HttpStatusCode expected)
    {
        var response = await client.PostAsJsonAsync("/api/waitlist", slot);
        Assert.Equal(expected, response.StatusCode);
        return response;
    }

    private static Task<HttpResponseMessage> Confirm(HttpClient client, Guid? id)
    {
        Assert.NotNull(id);
        return client.PostAsync($"/api/bookings/{id}/confirm", null);
    }

    private static async Task<CalendarSlot> ReadSlot(HttpClient client, SlotRequest slot)
    {
        var calendar = await client.GetFromJsonAsync<CalendarResponse>($"/api/calendar?resourceId={slot.ResourceId}");
        return Assert.Single(calendar!.Days.SelectMany(day => day.Slots), item => item.StartsAt == slot.StartsAt);
    }

    private sealed record SlotRequest(string ResourceId, DateTimeOffset StartsAt);
    private sealed record BookingResponse(Guid Id);
    private sealed record Entry(long Id);
    private sealed record CalendarResponse(Day[] Days);
    private sealed record Day(CalendarSlot[] Slots);
    private sealed record CalendarSlot(DateTimeOffset StartsAt, string Status, Guid? BookingId,
        DateTimeOffset? HoldExpiresAt, int? WaitlistPosition);
}
