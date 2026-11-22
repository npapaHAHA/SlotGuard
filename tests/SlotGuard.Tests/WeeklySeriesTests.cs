using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace SlotGuard.Tests;

public sealed class WeeklySeriesTests(SlotGuardFixture app) : IClassFixture<SlotGuardFixture>
{
    private static readonly DateTimeOffset Start = SlotGuardFixture.Now.AddMinutes(30);

    [Fact]
    public async Task ReplacesConflictAndSavesOnlyTheChosenDatesOnDuplicateSubmission()
    {
        using var admin = await app.CreateDemo("university-admin");
        using var member = await app.CreateGuest();
        var resource = await Create(admin, "Замена в серии");
        var path = WorkspacePath(resource);
        var blocked = Start.AddDays(7);
        using var maintenance = await admin.PostAsJsonAsync("/api/maintenance",
            new { resourceId = resource, startsAt = blocked, reason = "Проверка оборудования" });
        Assert.Equal(HttpStatusCode.Created, maintenance.StatusCode);
        var initial = new WeeklySeriesInput(Start, 3);
        var preview = await Preview(member, path, initial);
        var conflict = Assert.Single(preview.Occurrences, item => item.Conflict is not null);
        Assert.Equal(1, conflict.Index);
        Assert.Equal("Запланировано обслуживание.", conflict.Conflict);
        Assert.Contains(blocked.AddHours(2), conflict.Alternatives);
        Assert.Empty(await Rows(preview.SeriesId));

        var options = initial with { SeriesId = preview.SeriesId, Replacements = new() { [1] = blocked.AddHours(2) } };
        var resolved = await Preview(member, path, options);
        Assert.All(resolved.Occurrences, item => Assert.Null(item.Conflict));
        options = options with { SeenStarts = resolved.Occurrences.Select(item => item.StartsAt).ToArray() };
        using var stale = await member.PostAsJsonAsync(path,
            options with { SeenStarts = preview.Occurrences.Select(item => item.StartsAt).ToArray() });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Empty(await Rows(preview.SeriesId));
        using var confirmed = await member.PostAsJsonAsync(path, options);
        Assert.Equal(HttpStatusCode.Created, confirmed.StatusCode);
        var created = (await confirmed.Content.ReadFromJsonAsync<CreatedSeries>())!;
        Assert.Equal(new[] { Start, blocked.AddHours(2), Start.AddDays(14) }, created.Bookings.Select(item => item.StartsAt));
        using var replay = await member.PostAsJsonAsync(path, options);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(created.Bookings.Select(item => item.Id),
            (await replay.Content.ReadFromJsonAsync<CreatedSeries>())!.Bookings.Select(item => item.Id));
        Assert.Equal(3, (await Rows(preview.SeriesId)).Length);
    }

    [Fact]
    public async Task BookingRaceLeavesEitherTheWholeSeriesOrNoneOfItsDates()
    {
        using var admin = await app.CreateDemo("university-admin");
        using var member = await app.CreateGuest();
        using var other = await app.CreateGuest();
        var resource = await Create(admin, "Гонка за дату в серии");
        var path = WorkspacePath(resource);
        var input = new WeeklySeriesInput(Start, 3);
        var preview = await Preview(member, path, input);
        Assert.All(preview.Occurrences, item => Assert.Null(item.Conflict));
        var competingDate = Start.AddDays(14);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<HttpResponseMessage> Send(HttpClient client, string address, object payload)
        {
            await gate.Task;
            return await client.PostAsJsonAsync(address, payload);
        }
        var single = Send(other, "/api/bookings", new { resourceId = resource, startsAt = competingDate });
        var batch = Send(member, path, input with { SeriesId = preview.SeriesId });
        gate.SetResult();
        await Task.WhenAll(single, batch).WaitAsync(TimeSpan.FromSeconds(35));
        using var singleResult = await single;
        using var batchResult = await batch;
        Assert.Single(new[] { singleResult.StatusCode, batchResult.StatusCode }, code => code == HttpStatusCode.Created);
        if (singleResult.StatusCode == HttpStatusCode.Created)
        {
            Assert.Equal(HttpStatusCode.Conflict, batchResult.StatusCode);
            var updated = (await batchResult.Content.ReadFromJsonAsync<SeriesPreview>())!;
            Assert.Equal(competingDate, Assert.Single(updated.Occurrences, item => item.Conflict is not null).StartsAt);
            Assert.Empty(await Rows(preview.SeriesId));
        }
        else
        {
            Assert.Equal(HttpStatusCode.Conflict, singleResult.StatusCode);
            Assert.Equal(3, (await Rows(preview.SeriesId)).Length);
        }
    }

    [Fact]
    public async Task RecurrenceKeepsLocalHourWhenUtcOffsetChanges()
    {
        using var admin = await app.CreateDemo("university-admin");
        var resource = await Create(admin, "Серия через смену часового пояса");
        var open = new DayHours([new SchedulePeriod(540, 1020)], []);
        var schedule = new ResourceSchedule(Enumerable.Repeat(open, 7).ToArray(), [], "America/New_York");
        using var saved = await admin.PutAsJsonAsync($"/api/workspace/resources/{resource}/schedule", schedule);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        app.SetTime(new DateTimeOffset(2026, 10, 22, 12, 0, 0, TimeSpan.Zero));
        try
        {
            using var member = await app.CreateGuest();
            var first = new DateTimeOffset(2026, 10, 27, 13, 0, 0, TimeSpan.Zero);
            var second = new DateTimeOffset(2026, 11, 3, 14, 0, 0, TimeSpan.Zero);
            var preview = await Preview(member, WorkspacePath(resource), new WeeklySeriesInput(first, 2));
            Assert.Equal("America/New_York", preview.TimeZone);
            Assert.Equal(new DateTimeOffset?[] { first, second }, preview.Occurrences.Select(item => item.StartsAt));
            Assert.All(preview.Occurrences, item => Assert.Null(item.Conflict));
        }
        finally { app.SetTime(SlotGuardFixture.Now); }
    }

    [Fact]
    public async Task PublicSeriesRespectsQuotaAndApprovalAndKeepsReceiptsPrivate()
    {
        using var admin = await app.CreateDemo("university-admin");
        using var foreign = await app.CreateDemo("coworking-admin");
        using var guest = app.CreateClient();
        using var stranger = app.CreateClient();
        var resource = await Create(admin, "Закрытая серия с согласованием", requiresApproval: true);
        var open = new DayHours([new SchedulePeriod(540, 1080)], []);
        var schedule = new ResourceSchedule(Enumerable.Repeat(open, 7).ToArray(), [], Policy: new BookingPolicy(MaxActiveBookings: 2));
        using var saved = await admin.PutAsJsonAsync($"/api/workspace/resources/{resource}/schedule", schedule);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        using var published = await admin.PutAsJsonAsync($"/api/workspace/resources/{resource}/public-link", new { enabled = true });
        var link = (await published.Content.ReadFromJsonAsync<PublicLink>())!.PublicLinkId;
        var publicResource = $"/api/public/resources/{link}";
        var path = publicResource + "/series";
        var input = new WeeklySeriesInput(Start, 3, Email: "series@example.test");
        using var forbidden = await foreign.PostAsJsonAsync(WorkspacePath(resource) + "/preview", input);
        Assert.Equal(HttpStatusCode.NotFound, forbidden.StatusCode);
        var limit = await Preview(guest, path, input);
        Assert.NotNull(limit.LimitMessage);
        using var refused = await guest.PostAsJsonAsync(path, input with { SeriesId = limit.SeriesId });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Empty(await Rows(limit.SeriesId));

        var reduced = input with { Count = 2, SeriesId = Guid.NewGuid() };
        using var accepted = await guest.PostAsJsonAsync(path, reduced);
        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        var created = (await accepted.Content.ReadFromJsonAsync<CreatedSeries>())!;
        Assert.Equal(2, created.Bookings.Length);
        Assert.All(created.Bookings, booking => Assert.Equal("pending", booking.ApprovalStatus));
        foreach (var item in created.Bookings)
        {
            using var privateRead = await stranger.GetAsync($"{publicResource}/bookings/{item.Id}");
            Assert.Equal(HttpStatusCode.NotFound, privateRead.StatusCode);
            using var owned = await guest.GetAsync($"{publicResource}/bookings/{item.Id}");
            Assert.Equal(HttpStatusCode.OK, owned.StatusCode);
        }

        var approvedId = created.Bookings[0].Id;
        using var approved = await admin.PostAsJsonAsync($"/api/approvals/{approvedId}/decision",
            new { status = "approved", reason = "График проверен" });
        Assert.Equal(HttpStatusCode.NoContent, approved.StatusCode);
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BookingDb>();
        Assert.Equal("approved", (await db.Bookings.FindAsync(approvedId))!.ApprovalStatus);
        Assert.Equal("pending", (await db.Bookings.FindAsync(created.Bookings[1].Id))!.ApprovalStatus);
        Assert.True(await db.BookingEmails.AnyAsync(item => item.BookingId == approvedId));
        Assert.False(await db.BookingEmails.AnyAsync(item => item.BookingId == created.Bookings[1].Id));
    }

    private static async Task<SeriesPreview> Preview(HttpClient client, string path, WeeklySeriesInput input)
    {
        using var response = await client.PostAsJsonAsync(path + "/preview", input);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<SeriesPreview>())!;
    }

    private async Task<Booking[]> Rows(Guid series)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<BookingDb>().Bookings.AsNoTracking()
            .Where(item => item.SeriesId == series).ToArrayAsync();
    }

    private static async Task<string> Create(HttpClient admin, string name, bool requiresApproval = false)
    {
        using var response = await admin.PostAsJsonAsync("/api/workspace/resources",
            new { name, location = "Этаж 2", requiresApproval });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<Resource>())!.Id;
    }

    private static string WorkspacePath(string resource) => $"/api/resources/{resource}/series";
    private sealed record PublicLink(Guid PublicLinkId);
}
