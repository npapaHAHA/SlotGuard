using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace SlotGuard.Tests;

public sealed class AvailabilityTests(SlotGuardFixture app) : IClassFixture<SlotGuardFixture>
{
    private const string Availability = "/api/availability?date=2026-05-11";
    private static readonly DateOnly Day = Schedule.LocalDate(SlotGuardFixture.Now);

    [Fact]
    public async Task CombinesFiltersAndHidesArchivedAndForeignResources()
    {
        using var member = await app.CreateGuest();
        using var admin = await app.CreateDemo("university-admin");
        using var other = await app.CreateDemo("coworking-admin");
        other.DefaultRequestHeaders.Add(OrganizationScope.HeaderName, "coworking");
        var details = new ResourceDetails("room", 8, [" Экран ", "Доска", "ЭКРАН"]);
        var matching = await CreateResource(admin, "Кедр", details);
        await CreateResource(admin, "Малая", details with { Capacity = 4 });
        await CreateResource(admin, "Цех", details with { Type = "equipment" });
        await CreateResource(admin, "Липа", details with { Equipment = ["экран"] });
        await CreateResource(other, "Отдельная организация", details);
        var archived = await CreateResource(admin, "Архивная", details);
        using var archive = await admin.PutAsJsonAsync($"/api/workspace/resources/{archived.Id}",
            new { archived.Name, archived.Location, isArchived = true });
        Assert.Equal(HttpStatusCode.OK, archive.StatusCode);
        // A client from before the metadata fields were added can still edit the basic properties.
        using var rename = await admin.PutAsJsonAsync($"/api/workspace/resources/{matching.Id}",
            new { name = "Большой кедр", matching.Location });
        Assert.Equal(HttpStatusCode.OK, rename.StatusCode);

        var search = await Find(member, 9, "&type=room&capacity=6&equipment=ЭКРАН&equipment=доска");
        var result = Assert.Single(search.Exact);
        Assert.Equal(matching.Id, result.Resource.Id);
        Assert.Equal("Большой кедр", result.Resource.Name);
        Assert.Equal(8, result.Resource.Capacity);
        Assert.Equal(["доска", "экран"], result.Resource.Equipment);
        Assert.Empty(search.Alternatives);

        var foreignSearch = await Find(other, 9, "&type=room&capacity=6&equipment=экран&equipment=доска");
        Assert.DoesNotContain(foreignSearch.Exact, item => item.Resource.Id == matching.Id);
    }

    [Fact]
    public async Task LostBookingOffersAnotherResourceAtTheSameTimeAndNearestFreeSlots()
    {
        using var admin = await app.CreateDemo("university-admin");
        using var first = await app.CreateGuest();
        using var second = await app.CreateGuest();
        var details = new ResourceDetails("room", 8, ["маркер"]);
        var chosen = await CreateResource(admin, "Аудитория А", details);
        var alternate = await CreateResource(admin, "Аудитория Б", details);
        const string filters = "&type=room&equipment=маркер";
        const int hour = 11;
        var time = Schedule.At(Day, hour);
        Assert.Contains((await Find(second, hour, filters)).Exact, item => item.Resource.Id == chosen.Id);

        using var won = await first.PostAsJsonAsync("/api/bookings", new { resourceId = chosen.Id, startsAt = time });
        Assert.Equal(HttpStatusCode.Created, won.StatusCode);
        using var lost = await second.PostAsJsonAsync("/api/bookings", new { resourceId = chosen.Id, startsAt = time });
        Assert.Equal(HttpStatusCode.Conflict, lost.StatusCode);
        using var blocked = await admin.PostAsJsonAsync("/api/maintenance",
            new { resourceId = chosen.Id, startsAt = time.AddHours(2), reason = "Проверка оборудования" });
        Assert.Equal(HttpStatusCode.Created, blocked.StatusCode);

        var refreshed = await Find(second, hour, filters + "&includeAlternatives=true");
        Assert.Equal(alternate.Id, Assert.Single(refreshed.Exact).Resource.Id);
        Assert.Equal(6, refreshed.Alternatives.Length);
        Assert.Equal(time.AddHours(2), refreshed.Alternatives[0].StartsAt);
        Assert.Equal(alternate.Id, refreshed.Alternatives[0].Resource.Id);
        Assert.DoesNotContain(refreshed.Alternatives, item =>
            item.Resource.Id == chosen.Id && item.StartsAt == time.AddHours(2));
        Assert.All(refreshed.Alternatives, item => Assert.Contains("маркер", item.Resource.Equipment));
    }

    [Fact]
    public async Task WaitlistHoldIsBusyButCancelledSlotCanBeAnAlternative()
    {
        using var admin = await app.CreateDemo("university-admin");
        using var owner = await app.CreateGuest();
        using var waiting = await app.CreateGuest();
        var resource = await CreateResource(admin, "Студия С", new ResourceDetails("studio", null, ["пульт-qa"]));
        var held = Schedule.At(Day, 13);
        using var booked = await owner.PostAsJsonAsync("/api/bookings", new { resourceId = resource.Id, startsAt = held });
        Assert.Equal(HttpStatusCode.Created, booked.StatusCode);
        var bookedId = (await booked.Content.ReadFromJsonAsync<CreatedBooking>())!.Id;
        using var joined = await waiting.PostAsJsonAsync("/api/waitlist", new { resourceId = resource.Id, startsAt = held });
        Assert.Equal(HttpStatusCode.Created, joined.StatusCode);
        using var cancelled = await owner.DeleteAsync($"/api/bookings/{bookedId}");
        Assert.Equal(HttpStatusCode.NoContent, cancelled.StatusCode);
        using var blocked = await admin.PostAsJsonAsync("/api/maintenance",
            new { resourceId = resource.Id, startsAt = held.AddHours(2), reason = "Проверка" });
        Assert.Equal(HttpStatusCode.Created, blocked.StatusCode);
        using var adjacent = await owner.PostAsJsonAsync("/api/bookings",
            new { resourceId = resource.Id, startsAt = held.AddHours(-2) });
        Assert.Equal(HttpStatusCode.Created, adjacent.StatusCode);
        var adjacentId = (await adjacent.Content.ReadFromJsonAsync<CreatedBooking>())!.Id;
        using var freed = await owner.DeleteAsync($"/api/bookings/{adjacentId}");
        Assert.Equal(HttpStatusCode.NoContent, freed.StatusCode);

        var result = await Find(owner, 13, "&type=studio&equipment=пульт-qa");
        Assert.Empty(result.Exact);
        Assert.Equal(held.AddHours(-2), result.Alternatives[0].StartsAt);
        Assert.All(result.Alternatives, slot => Assert.Equal(resource.Id, slot.Resource.Id));
        Assert.DoesNotContain(result.Alternatives, slot => slot.StartsAt == held.AddHours(2));
        Assert.Empty((await Find(owner, 13, "&type=studio&equipment=пульт-qa&capacity=1")).Alternatives);
    }

    [Fact]
    public async Task RejectsInvalidFiltersAndTimesOutsideTheSchedule()
    {
        using var member = await app.CreateGuest();
        foreach (var query in new[]
        {
            "?date=2026-05-10&hour=17", "?date=2026-05-11&hour=10", "?date=2027-01-01&hour=9",
            "?date=2026-05-11&hour=9&capacity=0", "?date=2026-05-11&hour=9&type=unknown"
        })
        {
            using var response = await member.GetAsync("/api/availability" + query);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }

    private static async Task<AvailabilitySearch.SearchResult> Find(HttpClient client, int hour, string filters) =>
        (await client.GetFromJsonAsync<AvailabilitySearch.SearchResult>($"{Availability}&hour={hour}{filters}"))!;

    private static async Task<Resource> CreateResource(HttpClient client, string name, ResourceDetails details)
    {
        using var response = await client.PostAsJsonAsync("/api/workspace/resources", new { name, location = "Кабинет 101", details });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<Resource>())!;
    }
}
