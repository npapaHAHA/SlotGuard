using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace SlotGuard.Tests;

public sealed class AccountFixture : SlotGuardFixture
{
    internal const string Password = "SlotGuard!2026";
    internal TestMail Mail { get; } = new();

    protected override void ConfigureServices(IServiceCollection services) =>
        services.AddSingleton<IWorkspaceMail>(Mail);

    internal async Task CreateAccount(HttpClient client, string email)
    {
        using var registered = await client.PostAsJsonAsync("/api/account/register",
            new { name = email.Split('@')[0], email, password = Password });
        Assert.Equal(HttpStatusCode.NoContent, registered.StatusCode);
        var link = Mail.Latest(email, "confirm");
        using var confirmed = await client.PostAsJsonAsync("/api/account/confirm",
            new { userId = link.UserId, link.Token });
        Assert.Equal(HttpStatusCode.NoContent, confirmed.StatusCode);
    }

    internal async Task<string> SignIn(HttpClient client, string email, string password = Password)
    {
        using var response = await client.PostAsJsonAsync("/api/account/login", new { email, password });
        response.EnsureSuccessStatusCode();
        return response.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
    }

    internal static async Task<OrganizationAccess> CreateOrganization(HttpClient client, string name)
    {
        using var response = await client.PostAsJsonAsync("/api/account/organizations", new { name });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<OrganizationAccess>())!;
    }

    internal sealed class TestMail : IWorkspaceMail
    {
        private readonly ConcurrentQueue<(string Email, MailLink Link)> messages = new();
        public MailLink Latest(string email, string action) =>
            messages.Last(item => item.Email == email && item.Link.Action == action).Link;

        public Task<bool> Send(string email, string subject, string message, MailLink link, CancellationToken ct)
        {
            messages.Enqueue((email, link));
            return Task.FromResult(true);
        }
    }
}

public sealed class AccountTests(AccountFixture fixture) : IClassFixture<AccountFixture>
{
    [Fact]
    public async Task ConfirmsEmailAndResetsPasswordOnlyWithAValidToken()
    {
        const string email = "account-check@example.test";
        const string newPassword = "NewPassword!2026";
        using var client = fixture.CreateClient();
        using var registration = await client.PostAsJsonAsync("/api/account/register",
            new { name = "Account check", email, password = AccountFixture.Password });
        Assert.Equal(HttpStatusCode.NoContent, registration.StatusCode);
        using var unconfirmed = await client.PostAsJsonAsync("/api/account/login",
            new { email, password = AccountFixture.Password });
        Assert.Equal(HttpStatusCode.Unauthorized, unconfirmed.StatusCode);

        var confirmation = fixture.Mail.Latest(email, "confirm");
        using var verified = await client.PostAsJsonAsync("/api/account/confirm", new
        {
            userId = confirmation.UserId,
            confirmation.Token
        });
        Assert.Equal(HttpStatusCode.NoContent, verified.StatusCode);
        await fixture.SignIn(client, email);
        Assert.Empty((await client.GetFromJsonAsync<OrganizationAccess[]>("/api/organizations"))!);

        using var requested = await client.PostAsJsonAsync("/api/account/email", new { email, purpose = "reset" });
        Assert.Equal(HttpStatusCode.NoContent, requested.StatusCode);
        var reset = fixture.Mail.Latest(email, "reset");
        using var changed = await client.PostAsJsonAsync("/api/account/reset",
            new { userId = reset.UserId, reset.Token, password = newPassword });
        Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);
        using var replayed = await client.PostAsJsonAsync("/api/account/reset",
            new { userId = reset.UserId, reset.Token, password = "AnotherPassword!2026" });
        Assert.Equal(HttpStatusCode.BadRequest, replayed.StatusCode);
        using var oldLogin = await client.PostAsJsonAsync("/api/account/login", new { email, password = AccountFixture.Password });
        Assert.Equal(HttpStatusCode.Unauthorized, oldLogin.StatusCode);
        await fixture.SignIn(client, email, newPassword);
    }

    [Fact]
    public async Task InvitationIsScopedToItsEmailAndCanBeConsumedOnlyOnce()
    {
        const string owner = "team-owner@example.test";
        const string member = "team-member@example.test";
        using var admin = fixture.CreateClient();
        using var invited = fixture.CreateClient();
        await fixture.CreateAccount(admin, owner);
        await fixture.CreateAccount(invited, member);
        await fixture.SignIn(admin, owner);
        await fixture.SignIn(invited, member);
        var organization = await AccountFixture.CreateOrganization(admin, "Workshop");
        Assert.Equal("admin", organization.Role);
        admin.DefaultRequestHeaders.Add(OrganizationScope.HeaderName, organization.Id);
        invited.DefaultRequestHeaders.Add(OrganizationScope.HeaderName, organization.Id);
        using var isolated = await invited.GetAsync("/api/resources");
        Assert.Equal(HttpStatusCode.NotFound, isolated.StatusCode);

        using var invite = await admin.PostAsJsonAsync("/api/team/invitations", new { email = member });
        Assert.Equal(HttpStatusCode.NoContent, invite.StatusCode);
        var token = fixture.Mail.Latest(member, "invite").Token;
        using var wrongAccount = await admin.PostAsJsonAsync("/api/account/invitations/accept", new { token });
        Assert.Equal(HttpStatusCode.BadRequest, wrongAccount.StatusCode);

        var attempts = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ =>
            invited.PostAsJsonAsync("/api/account/invitations/accept", new { token })));
        Assert.Contains(attempts, response => response.StatusCode == HttpStatusCode.OK);
        Assert.Contains(attempts, response => response.StatusCode == HttpStatusCode.BadRequest);
        foreach (var response in attempts) response.Dispose();
        var access = Assert.Single((await invited.GetFromJsonAsync<OrganizationAccess[]>("/api/organizations"))!);
        Assert.Equal(new OrganizationAccess(organization.Id, organization.Name, "member"), access);
        using var noTeamAdmin = await invited.PostAsJsonAsync("/api/team/invitations", new { email = owner });
        Assert.Equal(HttpStatusCode.Forbidden, noTeamAdmin.StatusCode);

        const string expiredAddress = "expired@example.test";
        using var expiredUser = fixture.CreateClient();
        await fixture.CreateAccount(expiredUser, expiredAddress);
        await fixture.SignIn(expiredUser, expiredAddress);
        using var expiring = await admin.PostAsJsonAsync("/api/team/invitations", new { email = expiredAddress });
        expiring.EnsureSuccessStatusCode();
        fixture.SetTime(SlotGuardFixture.Now.AddHours(48));
        try
        {
            using var expired = await expiredUser.PostAsJsonAsync("/api/account/invitations/accept",
                new { token = fixture.Mail.Latest(expiredAddress, "invite").Token });
            Assert.Equal(HttpStatusCode.BadRequest, expired.StatusCode);
        }
        finally
        {
            fixture.SetTime(SlotGuardFixture.Now);
        }
    }
}

public sealed class LiveCalendarTests(AccountFixture fixture) : IClassFixture<AccountFixture>
{
    [Fact]
    public async Task BookingSignalsOnlyItsOrganizationAfterCommit()
    {
        using var admin = fixture.CreateClient();
        using var other = fixture.CreateClient();
        const string owner = "live-owner@example.test";
        const string outsider = "different-team@example.test";
        await fixture.CreateAccount(admin, owner);
        await fixture.CreateAccount(other, outsider);
        var ownerCookie = await fixture.SignIn(admin, owner);
        var outsiderCookie = await fixture.SignIn(other, outsider);
        var first = await AccountFixture.CreateOrganization(admin, "Bureaux");
        var second = await AccountFixture.CreateOrganization(other, "Studio");
        admin.DefaultRequestHeaders.Add(OrganizationScope.HeaderName, first.Id);
        using var resource = await admin.PostAsJsonAsync("/api/workspace/resources",
            new { name = "Overlook", location = "Floor 2" });
        resource.EnsureSuccessStatusCode();
        var resourceId = (await resource.Content.ReadFromJsonAsync<Resource>())!.Id;

        await using var ownerHub = Hub(first.Id, ownerCookie);
        await using var otherHub = Hub(second.Id, outsiderCookie);
        await using var forbiddenHub = Hub(first.Id, outsiderCookie);
        var updated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var leaked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var ownerUpdate = ownerHub.On("CalendarChanged", () => updated.TrySetResult());
        using var otherUpdate = otherHub.On("CalendarChanged", () => leaked.TrySetResult());
        using var forbiddenUpdate = forbiddenHub.On("CalendarChanged", () => leaked.TrySetResult());
        await Connect(ownerHub);
        await Connect(otherHub);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        forbiddenHub.Closed += _ =>
        {
            closed.TrySetResult();
            return Task.CompletedTask;
        };
        try
        {
            await forbiddenHub.StartAsync();
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception error) when (error is HttpRequestException or InvalidOperationException)
        {
            Assert.Equal(HubConnectionState.Disconnected, forbiddenHub.State);
        }

        using var booking = await admin.PostAsJsonAsync("/api/bookings",
            new { resourceId, startsAt = Schedule.At(Schedule.LocalDate(SlotGuardFixture.Now), 11) });
        Assert.Equal(HttpStatusCode.Created, booking.StatusCode);
        await updated.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var calendar = await admin.GetStringAsync($"/api/calendar?resourceId={resourceId}");
        Assert.Contains("\"status\":\"mine\"", calendar);
        var observation = Task.Delay(300);
        Assert.Same(observation, await Task.WhenAny(leaked.Task, observation));
    }

    private HubConnection Hub(string organization, string cookie) => new HubConnectionBuilder()
        .WithUrl($"http://localhost/hubs/calendar?organization={organization}", HttpTransportType.LongPolling, options =>
        {
            options.Headers["Cookie"] = cookie;
            options.HttpMessageHandlerFactory = _ => fixture.CreateHandler();
        }).Build();

    private static async Task Connect(HubConnection hub)
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = hub.On("CalendarReady", () => ready.TrySetResult());
        await hub.StartAsync();
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
