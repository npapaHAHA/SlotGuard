using Microsoft.AspNetCore.SignalR;

namespace SlotGuard;

internal sealed class CalendarHub(BookingDb db) : Hub
{
    public override async Task OnConnectedAsync()
    {
        var organization = Context.GetHttpContext()?.Request.Query["organization"].ToString();
        if (organization is not { Length: > 0 and <= 32 }
            || !(await OrganizationScope.ForUser(db, Context.User!, Context.ConnectionAborted))
                .Any(item => item.Id == organization))
        {
            Context.Abort();
            return;
        }
        await Groups.AddToGroupAsync(Context.ConnectionId, CalendarUpdates.Group(organization), Context.ConnectionAborted);
        await Clients.Caller.SendAsync("CalendarReady", Context.ConnectionAborted);
        await base.OnConnectedAsync();
    }
}

internal sealed class CalendarUpdates(IHubContext<CalendarHub> hub, ILogger<CalendarUpdates> logger)
{
    public static string Group(string organizationId) => $"calendar:{organizationId}";

    public async Task Publish(string organizationId)
    {
        try
        {
            await hub.Clients.Group(Group(organizationId)).SendAsync("CalendarChanged");
        }
        catch (Exception error)
        {
            // The database transaction is already committed. Clients can resync on reconnect.
            logger.LogWarning(error, "Could not notify calendar subscribers");
        }
    }
}
