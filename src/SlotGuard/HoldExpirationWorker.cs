namespace SlotGuard;

internal sealed class HoldExpirationWorker(
    IServiceScopeFactory scopes, ILogger<HoldExpirationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<SlotBookings>().ExpireHolds(stoppingToken);
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(exception, "Could not process expired slot holds");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
