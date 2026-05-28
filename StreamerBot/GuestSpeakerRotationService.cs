namespace StreamerBot;

public class GuestSpeakerRotationService(
    GuestStageManager guestStageManager,
    DashboardService dashboardService) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await guestStageManager.ProcessExpiredSpeakersAsync();
                await dashboardService.RefreshDashboardAsync(stoppingToken);
            }
            catch
            {
                // Keep worker alive if one guild update fails.
            }

            await timer.WaitForNextTickAsync(stoppingToken);
        }
    }
}
