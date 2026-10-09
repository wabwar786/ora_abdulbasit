using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Orapmshms.Services.LegacyApi;

/// <summary>The classic 10 AM (next-day-on-start) no-show task migrated into the MVC host.</summary>
public sealed class NoShowDailyTaskScheduler : BackgroundService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<NoShowDailyTaskScheduler> _logger;
    public NoShowDailyTaskScheduler(IConfiguration configuration, ILogger<NoShowDailyTaskScheduler> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The old API scheduled this destructive archiving job. Only one app/instance must enable it.
        if (!_configuration.GetValue<bool>("LegacyApi:EnableAutomaticNoShow"))
        {
            _logger.LogInformation("Legacy API no-show scheduler disabled; enable after API cutover on one instance only.");
            return;
        }
        while (!stoppingToken.IsCancellationRequested)
        {
            var now = DateTime.Now;
            var nextRun = now.Date.AddDays(1).AddHours(10); // faithfully preserve original schedule
            try
            {
                await Task.Delay(nextRun - now, stoppingToken);
                if (stoppingToken.IsCancellationRequested) break;
                new NoShowScheduler().NowShowDatabaseAndChannelManagerTask();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { _logger.LogError(ex, "Legacy API daily no-show task failed"); }
        }
    }
}
