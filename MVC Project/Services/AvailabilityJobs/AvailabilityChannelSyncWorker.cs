using System.Threading.Channels;
using Orapmshms.Services;

namespace Orapmshms.Services.AvailabilityJobs;

public enum AvailabilityChannelSyncKind
{
    Rates,
    YieldRates,

    Restrictions,
    ReconcileMappings
}

public sealed record AvailabilityChannelSyncJob(
    AvailabilityChannelSyncKind Kind,
    string HotelId,
    DateTime FromDate,
    DateTime ToDate,
    IReadOnlyCollection<string> PlanIds,
    IReadOnlyCollection<string> CategoryIds);

public interface IAvailabilityChannelSyncQueue
{
    bool Queue(AvailabilityChannelSyncJob job);
}

public sealed class AvailabilityChannelSyncWorker : BackgroundService, IAvailabilityChannelSyncQueue
{
    private readonly Channel<AvailabilityChannelSyncJob> _channel;
    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IHotelClock _hotelClock;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<AvailabilityChannelSyncWorker> _logger;

    public AvailabilityChannelSyncWorker(
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory,
        IHotelClock hotelClock,
        ILoggerFactory loggerFactory,
        ILogger<AvailabilityChannelSyncWorker> logger)
    {
        _configuration = configuration;
        _httpClientFactory = httpClientFactory;
        _hotelClock = hotelClock;
        _loggerFactory = loggerFactory;
        _logger = logger;
        _channel = Channel.CreateUnbounded<AvailabilityChannelSyncJob>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
    }

    public bool Queue(AvailabilityChannelSyncJob job)
        => job != null && _channel.Writer.TryWrite(job);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var channelLoop = ProcessChannelJobsAsync(stoppingToken);
        var yieldLoop = ProcessYieldRulesAsync(stoppingToken);
        await Task.WhenAll(channelLoop, yieldLoop);
    }

    private async Task ProcessChannelJobsAsync(CancellationToken stoppingToken)
    {
        await foreach (var job in _channel.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                var service = new AvailabilityChannexUploadService(
                    _configuration,
                    _httpClientFactory.CreateClient("ChannelManager"),
                    _logger);

                // First materialise/update the effective rate calendar outside any long MVC transaction.
                // Derived plans use the parent's daily rate when one exists and fall back to the
                // parent's category_plan default. This also means Inventory and Channex read the
                // same effective values.
                if (job.Kind == AvailabilityChannelSyncKind.Rates)
                {
                    await service.PrepareRateRowsAsync(
                        job.HotelId,
                        job.FromDate,
                        job.ToDate,
                        job.PlanIds,
                        job.CategoryIds,
                        stoppingToken);
                }
                else if (job.Kind == AvailabilityChannelSyncKind.Restrictions)
                {
                    // Booking Cutoff/restriction preparation is also background work.
                    // This makes Save Details fast and guarantees that a new plan gets its
                    // cutoff immediately after Save Rates has created category_plan rows.
                    await service.PrepareBookingCutoffRowsAsync(
                        job.HotelId,
                        job.FromDate,
                        job.ToDate,
                        job.PlanIds,
                        job.CategoryIds,
                        "BookingCutoffWorker",
                        stoppingToken);
                }
                // ReconcileMappings intentionally does not rebuild datesrates. It is a lightweight
                // repair pass over every local plan/category mapping and uploads only rows whose
                // upload flags were reset because the Channex mapping was missing or stale.

                // Reconcile category_plan -> Channex rate-plan mappings after the database rate
                // calendar is ready, then upload the actual rates/restrictions.
                await service.EnsureRatePlansAsync(
                    job.HotelId,
                    job.PlanIds,
                    job.CategoryIds,
                    stoppingToken);

                if (job.Kind == AvailabilityChannelSyncKind.Rates ||
                    job.Kind == AvailabilityChannelSyncKind.YieldRates)
                {
                    await service.UploadRatesAsync(
                        job.HotelId, job.FromDate, job.ToDate, job.PlanIds, job.CategoryIds, stoppingToken);

                    // Run a short second reconciliation/upload pass. Newly-created Channex
                    // rate plans can take a moment to become available to the ARI endpoint.
                    // Already-successful dates are marked upload=1, so this pass only retries
                    // categories/dates that are still pending.
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);

                    await service.EnsureRatePlansAsync(
                        job.HotelId,
                        job.PlanIds,
                        job.CategoryIds,
                        stoppingToken);

                    await service.UploadRatesAsync(
                        job.HotelId, job.FromDate, job.ToDate, job.PlanIds, job.CategoryIds, stoppingToken);
                }
                else if (job.Kind == AvailabilityChannelSyncKind.Restrictions)
                {
                    await service.UploadRestrictionsAsync(
                        job.HotelId, job.FromDate, job.ToDate, job.PlanIds, job.CategoryIds, stoppingToken);
                }
                else
                {
                    // All-plan repair: EnsureRatePlansAsync verifies every local plan/category
                    // against Channex, fixes stale/missing plainid values, and resets upload flags
                    // only for repaired mappings. Therefore these uploads send only what needs repair.
                    await service.UploadRatesAsync(
                        job.HotelId, job.FromDate, job.ToDate, job.PlanIds, job.CategoryIds, stoppingToken);
                    await service.UploadRestrictionsAsync(
                        job.HotelId, job.FromDate, job.ToDate, job.PlanIds, job.CategoryIds, stoppingToken);

                    // One retry handles Channex eventual consistency after newly-created rate plans.
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                    await service.EnsureRatePlansAsync(
                        job.HotelId,
                        job.PlanIds,
                        job.CategoryIds,
                        stoppingToken);
                    await service.UploadRatesAsync(
                        job.HotelId, job.FromDate, job.ToDate, job.PlanIds, job.CategoryIds, stoppingToken);
                    await service.UploadRestrictionsAsync(
                        job.HotelId, job.FromDate, job.ToDate, job.PlanIds, job.CategoryIds, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Inventory channel sync failed. Hotel={HotelId}, Kind={Kind}, Range={From:yyyy-MM-dd}-{To:yyyy-MM-dd}",
                    job.HotelId, job.Kind, job.FromDate, job.ToDate);
            }
        }
    }

    private async Task ProcessYieldRulesAsync(CancellationToken stoppingToken)
    {
        // Reuse this already-registered hosted worker so Yield Management needs no new
        // Program.cs registration. Active rules are re-evaluated frequently against live
        // reservations, allowing a reservation/cancellation to change the rate automatically.
        var service = new YieldManagementService(
            _configuration,
            _hotelClock,
            this,
            _loggerFactory.CreateLogger<YieldManagementService>());

        // Give application startup a moment to finish before the first database sweep.
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var hotels = await service.GetHotelsNeedingEvaluationAsync(stoppingToken);
                foreach (var hotelId in hotels)
                {
                    stoppingToken.ThrowIfCancellationRequested();
                    try
                    {
                        await service.EvaluateActiveRulesAsync(hotelId, stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex,
                            "Automatic yield evaluation failed for hotel {HotelId}.", hotelId);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Automatic yield-management sweep failed.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

}
