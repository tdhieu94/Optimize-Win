using OW.Core.Execution;

namespace OW.Service;

/// <summary>At start-up settles anything a crash interrupted, then purges expired quarantine once a day.</summary>
public sealed class MaintenanceWorker(SafeExecutor executor, ILogger<MaintenanceWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settled = executor.RecoverInterrupted();
        if (settled > 0)
        {
            logger.LogWarning("Settled {Count} operations interrupted by a previous crash", settled);
        }

        using var timer = new PeriodicTimer(TimeSpan.FromHours(24));
        do
        {
            try
            {
                var purged = executor.PurgeExpired(ServiceConstants.QuarantineRetention);
                if (purged > 0)
                {
                    logger.LogInformation("Purged {Count} expired quarantined files", purged);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Quarantine purge failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}
