using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace SlotGuard.Tests;

public sealed class BookingTests(SlotGuardFixture app) : IClassFixture<SlotGuardFixture>
{
    [Fact]
    public async Task TwentyGuestsRacingForOneSlotCreateExactlyOneActiveBooking()
    {
        var clients = new List<HttpClient>(20);
        try
        {
            for (var i = 0; i < 20; i++)
                clients.Add(await app.CreateGuest());

            var slot = new SlotRequest("microscope", SlotGuardFixture.Now.AddMinutes(30));
            var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var requests = clients.Select(async client =>
            {
                await startGate.Task;
                using var response = await client.PostAsJsonAsync("/api/bookings", slot);
                return response.StatusCode;
            }).ToArray();

            startGate.SetResult();
            var statuses = await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(30));

            Assert.Single(statuses, status => status == HttpStatusCode.Created);
            Assert.Equal(19, statuses.Count(status => status == HttpStatusCode.Conflict));

            await using var scope = app.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<BookingDb>();
            var rows = await db.Bookings.AsNoTracking()
                .Where(booking => booking.ResourceId == slot.ResourceId && booking.StartUtc == slot.StartsAt)
                .ToListAsync();
            Assert.Null(Assert.Single(rows).CancelledAt);
        }
        finally
        {
            foreach (var client in clients)
                client.Dispose();
        }
    }

    [Fact]
    public async Task OwnerCanCancelAndAnotherGuestCanRebook()
    {
        using var owner = await app.CreateGuest();
        using var other = await app.CreateGuest();
        var slot = new SlotRequest("electronics", SlotGuardFixture.Now.AddMinutes(30));
        using var booked = await owner.PostAsJsonAsync("/api/bookings", slot);
        Assert.Equal(HttpStatusCode.Created, booked.StatusCode);
        var bookingId = (await booked.Content.ReadFromJsonAsync<BookingResponse>())!.Id;

        var ownerSlot = await GetSlot(owner, slot);
        var otherSlot = await GetSlot(other, slot);
        Assert.Equal(new CalendarSlot(slot.StartsAt, "mine", bookingId), ownerSlot);
        Assert.Equal(new CalendarSlot(slot.StartsAt, "booked", null), otherSlot);

        using var forbidden = await other.DeleteAsync($"/api/bookings/{bookingId}");
        Assert.Equal(HttpStatusCode.NotFound, forbidden.StatusCode);
        using var cancelled = await owner.DeleteAsync($"/api/bookings/{bookingId}");
        Assert.Equal(HttpStatusCode.NoContent, cancelled.StatusCode);
        Assert.Equal("available", (await GetSlot(other, slot)).Status);
        using var rebooked = await other.PostAsJsonAsync("/api/bookings", slot);
        Assert.Equal(HttpStatusCode.Created, rebooked.StatusCode);
    }

    [Fact]
    public async Task OnlyGuestsCanBookAndOnlyValidFutureSlotsAreAccepted()
    {
        using var anonymous = app.CreateClient();
        using var guest = await app.CreateGuest();
        var slot = new SlotRequest("studio", SlotGuardFixture.Now.AddMinutes(30));
        using var unauthorized = await anonymous.PostAsJsonAsync("/api/bookings", slot);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        using var invalid = await guest.PostAsJsonAsync("/api/bookings", slot with { StartsAt = slot.StartsAt.AddMinutes(5) });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using var past = await guest.PostAsJsonAsync("/api/bookings", slot with { StartsAt = slot.StartsAt.AddDays(-1) });
        Assert.Equal(HttpStatusCode.BadRequest, past.StatusCode);
    }

    private static async Task<CalendarSlot> GetSlot(HttpClient client, SlotRequest slot)
    {
        var calendar = await client.GetFromJsonAsync<CalendarResponse>($"/api/calendar?resourceId={slot.ResourceId}");
        return Assert.Single(calendar!.Days.SelectMany(day => day.Slots), item => item.StartsAt == slot.StartsAt);
    }

    private sealed record SlotRequest(string ResourceId, DateTimeOffset StartsAt);
    private sealed record BookingResponse(Guid Id);
    private sealed record CalendarResponse(Day[] Days);
    private sealed record Day(CalendarSlot[] Slots);
    private sealed record CalendarSlot(DateTimeOffset StartsAt, string Status, Guid? BookingId);
}
