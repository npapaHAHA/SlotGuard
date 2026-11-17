using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace SlotGuard.Tests;

public sealed class ApprovalTests(SlotGuardFixture app) : IClassFixture<SlotGuardFixture>
{
    private static readonly DateTimeOffset Start = SlotGuardFixture.Now.AddMinutes(30);

    [Fact]
    public async Task ApprovesOwnOrganizationRequestAndShowsReasonOnlyToItsOwner()
    {
        using var admin = await app.CreateDemo("university-admin");
        using var member = await app.CreateGuest();
        using var otherMember = await app.CreateGuest();
        using var foreignAdmin = await app.CreateDemo("coworking-admin");
        foreignAdmin.DefaultRequestHeaders.Add(OrganizationScope.HeaderName, "coworking");
        var resource = await CreateApprovalResource(admin, "Лазерная");
        var request = await Book(member, resource.Id, Start);
        Assert.Equal("pending", request.ApprovalStatus);
        Assert.Equal("pending", (await ReadSlot(member, resource.Id, Start)).Status);
        Assert.Equal("booked", (await ReadSlot(otherMember, resource.Id, Start)).Status);
        Assert.Equal("pending", Assert.Single((await Agenda(member)).Upcoming).ApprovalStatus);

        var list = await admin.GetFromJsonAsync<ApprovalEndpoints.ApprovalRequest[]>("/api/approvals");
        var item = Assert.Single(list!, row => row.Id == request.Id);
        Assert.Equal(resource.Name, item.ResourceName);
        Assert.StartsWith("Гость ", item.Requester);
        Assert.Empty((await foreignAdmin.GetFromJsonAsync<ApprovalEndpoints.ApprovalRequest[]>("/api/approvals"))!);
        using var deniedList = await member.GetAsync("/api/approvals");
        Assert.Equal(HttpStatusCode.Forbidden, deniedList.StatusCode);
        using var deniedDecision = await Decide(member, request.Id, "approved", "Причина");
        Assert.Equal(HttpStatusCode.Forbidden, deniedDecision.StatusCode);
        using var unrelatedDecision = await Decide(foreignAdmin, request.Id, "approved", "Причина");
        Assert.Equal(HttpStatusCode.NotFound, unrelatedDecision.StatusCode);
        using var missingReason = await Decide(admin, request.Id, "rejected", " ");
        Assert.Equal(HttpStatusCode.BadRequest, missingReason.StatusCode);
        using var conflict = await otherMember.PostAsJsonAsync("/api/bookings", new { resourceId = resource.Id, startsAt = Start });
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);

        using var approved = await Decide(admin, request.Id, "approved", " Инструктаж пройден ");
        Assert.Equal(HttpStatusCode.NoContent, approved.StatusCode);
        var updated = Assert.Single((await Agenda(member)).Upcoming);
        Assert.Equal("approved", updated.ApprovalStatus);
        Assert.Equal("Инструктаж пройден", updated.ApprovalReason);
        Assert.Empty((await Agenda(otherMember)).Upcoming);
        Assert.Equal("mine", (await ReadSlot(member, resource.Id, Start)).Status);
        Assert.DoesNotContain((await admin.GetFromJsonAsync<ApprovalEndpoints.ApprovalRequest[]>("/api/approvals"))!,
            row => row.Id == request.Id);
        using var retry = await Decide(admin, request.Id, "rejected", "Повтор");
        Assert.Equal(HttpStatusCode.Conflict, retry.StatusCode);
    }

    [Fact]
    public async Task RejectionEntersHistoryAndPassesTheSlotToTheWaitlist()
    {
        using var admin = await app.CreateDemo("university-admin");
        using var owner = await app.CreateGuest();
        using var queued = await app.CreateGuest();
        var resource = await CreateApprovalResource(admin, "Съёмочная");
        var request = await Book(owner, resource.Id, Start);
        using var waiting = await queued.PostAsJsonAsync("/api/waitlist", new { resourceId = resource.Id, startsAt = Start });
        Assert.Equal(HttpStatusCode.Created, waiting.StatusCode);
        using var rejected = await Decide(admin, request.Id, "rejected", "Нужен инструктаж");
        Assert.Equal(HttpStatusCode.NoContent, rejected.StatusCode);

        var agenda = await Agenda(owner);
        Assert.Empty(agenda.Upcoming);
        var history = Assert.Single(agenda.History);
        Assert.Equal("rejected", history.ApprovalStatus);
        Assert.Equal("Нужен инструктаж", history.ApprovalReason);
        Assert.NotNull(history.CancelledAt);
        var hold = await ReadSlot(queued, resource.Id, Start);
        Assert.Equal("held", hold.Status);
        using var confirmed = await queued.PostAsync($"/api/bookings/{hold.BookingId}/confirm", null);
        Assert.Equal(HttpStatusCode.NoContent, confirmed.StatusCode);
        Assert.Equal("pending", (await ReadSlot(queued, resource.Id, Start)).Status);
        Assert.Equal("pending", Assert.Single((await Agenda(queued)).Upcoming).ApprovalStatus);

        using var approved = await Decide(admin, hold.BookingId!.Value, "approved", "Инструктаж пройден");
        Assert.Equal(HttpStatusCode.NoContent, approved.StatusCode);
        using var confirmRetry = await queued.PostAsync($"/api/bookings/{hold.BookingId}/confirm", null);
        Assert.Equal(HttpStatusCode.NoContent, confirmRetry.StatusCode);
        Assert.Equal("approved", Assert.Single((await Agenda(queued)).Upcoming).ApprovalStatus);
    }

    [Fact]
    public async Task CompetingDecisionsCommitOneOutcomeAndOneAuditEntry()
    {
        using var admin = await app.CreateDemo("university-admin");
        using var otherAdminSession = await app.CreateDemo("university-admin");
        using var member = await app.CreateGuest();
        var resource = await CreateApprovalResource(admin, "Графическая");
        var request = await Book(member, resource.Id, Start);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var approve = Submit(admin, "approved");
        var reject = Submit(otherAdminSession, "rejected");
        gate.SetResult();
        var outcomes = await Task.WhenAll(approve, reject).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Single(outcomes, status => status == HttpStatusCode.NoContent);
        Assert.Single(outcomes, status => status == HttpStatusCode.Conflict);

        var won = outcomes[0] == HttpStatusCode.NoContent ? "approved" : "rejected";
        var audit = await admin.GetFromJsonAsync<Activity[]>("/api/workspace/activity");
        var decision = Assert.Single(audit!, item => item.ResourceName == resource.Name && item.Action.StartsWith("booking."));
        Assert.Equal($"booking.{won}", decision.Action);
        var records = await Agenda(member);
        var saved = Assert.Single(records.Upcoming.Concat(records.History));
        Assert.Equal(won, saved.ApprovalStatus);
        Assert.Equal(decision.Details, saved.ApprovalReason);
        Assert.Equal(won == "approved" ? "mine" : "available", (await ReadSlot(member, resource.Id, Start)).Status);

        async Task<HttpStatusCode> Submit(HttpClient client, string status)
        {
            await gate.Task;
            using var response = await Decide(client, request.Id, status, status);
            return response.StatusCode;
        }
    }

    [Fact]
    public async Task ExpiredOrWithdrawnRequestCannotBeApproved()
    {
        using var admin = await app.CreateDemo("university-admin");
        using var member = await app.CreateGuest();
        var resource = await CreateApprovalResource(admin, "Медиацентр");
        var original = await Book(member, resource.Id, Start);
        using var cancelled = await member.DeleteAsync($"/api/bookings/{original.Id}");
        Assert.Equal(HttpStatusCode.NoContent, cancelled.StatusCode);
        using var closed = await Decide(admin, original.Id, "approved", "Поздно");
        Assert.Equal(HttpStatusCode.Conflict, closed.StatusCode);

        var pending = await Book(member, resource.Id, Start);
        app.SetTime(Start);
        try
        {
            using var expired = await Decide(admin, pending.Id, "approved", "Истекло время");
            Assert.Equal(HttpStatusCode.Conflict, expired.StatusCode);
            Assert.DoesNotContain((await admin.GetFromJsonAsync<ApprovalEndpoints.ApprovalRequest[]>("/api/approvals"))!,
                row => row.Id == pending.Id);
            var records = await Agenda(member);
            Assert.Empty(records.Upcoming);
            Assert.Contains(records.History, row => row.Id == pending.Id && row.ApprovalStatus == "pending");
        }
        finally
        {
            app.SetTime(SlotGuardFixture.Now);
        }
    }

    private static Task<HttpResponseMessage> Decide(HttpClient client, Guid id, string status, string reason) =>
        client.PostAsJsonAsync($"/api/approvals/{id}/decision", new ApprovalEndpoints.Decision(status, reason));

    private static async Task<Resource> CreateApprovalResource(HttpClient client, string name)
    {
        using var response = await client.PostAsJsonAsync("/api/workspace/resources",
            new { name, location = "Аудитория 101", requiresApproval = true });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var resource = (await response.Content.ReadFromJsonAsync<Resource>())!;
        Assert.True(resource.RequiresApproval);
        return resource;
    }

    private static async Task<CreatedBooking> Book(HttpClient client, string resourceId, DateTimeOffset startsAt)
    {
        using var response = await client.PostAsJsonAsync("/api/bookings", new { resourceId, startsAt });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CreatedBooking>())!;
    }

    private static async Task<PersonalAgenda> Agenda(HttpClient client) =>
        (await client.GetFromJsonAsync<PersonalAgenda>("/api/workspace/me"))!;

    private static async Task<CalendarSlot> ReadSlot(HttpClient client, string id, DateTimeOffset startsAt)
    {
        var result = (await client.GetFromJsonAsync<Calendar>($"/api/calendar?resourceId={id}"))!;
        return Assert.Single(result.Days.SelectMany(day => day.Slots), slot => slot.StartsAt == startsAt);
    }

    private sealed record PersonalAgenda(Entry[] Upcoming, Entry[] History);
    private sealed record Entry(Guid Id, string? ApprovalStatus, string? ApprovalReason, DateTimeOffset? CancelledAt);
    private sealed record Calendar(CalendarDay[] Days);
    private sealed record CalendarDay(CalendarSlot[] Slots);
    private sealed record CalendarSlot(DateTimeOffset StartsAt, string Status, Guid? BookingId);
    private sealed record Activity(string ResourceName, string Action, string Details);
}
