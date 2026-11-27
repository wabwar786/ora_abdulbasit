using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Orapmshms.Services;

// One hosted loop per application process; the shared SQL application lock and
// persisted unique slot coordinate across processes as well as manual backups.
public sealed class DatabaseBackupBackgroundWorker : BackgroundService
{
    private readonly IConfiguration config;
    private readonly ILogger<DatabaseBackupBackgroundWorker> log;

    public DatabaseBackupBackgroundWorker(IConfiguration config,
        ILogger<DatabaseBackupBackgroundWorker> log)
    {
        this.config = config;
        this.log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Do not delay application startup with a database connection or backup.
        await Task.Yield();
        log.LogInformation("Internal database backup worker started (60-second schedule checks).");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var service = new DatabaseBackupService(config, log);
                if (service.SchedulerConfigured)
                {
                    // No HTTP request or user session is required. SuperUser-only
                    // settings determine whether a backup is actually due.
                    var result = await service.RunDueAsync(stoppingToken);
                    if (result.Status is "unconfirmed" or "verification_failed" or "configuration_required")
                        log.LogWarning("Automatic database backup status: {Status}; next due: {NextDue}; file: {File}",
                            result.Status, result.NextDue, result.File);
                    if (result.RetentionWarning)
                        log.LogWarning("Automatic backup completed but retention cleanup needs attention.");
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // A SQL connection, configuration or migration error must not
                // terminate the host or interfere with other PMS services.
                log.LogError(ex, "Internal database backup check failed; will check again in one minute.");
            }

            try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
        log.LogInformation("Internal database backup worker stopped.");
    }
}
