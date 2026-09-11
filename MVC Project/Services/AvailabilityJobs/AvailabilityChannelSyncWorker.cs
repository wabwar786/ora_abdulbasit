using System.Threading.Channels;

namespace Orapmshms.Services.AvailabilityJobs;

public enum AvailabilityChannelSyncKind
{
    Rates,
    Restrictions
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
    private readonly ILogger<AvailabilityChannelSyncWorker> _logger;

    public AvailabilityChannelSyncWorker(
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory,
        ILogger<AvailabilityChannelSyncWorker> logger)
    {
        _configuration = configuration;
        _httpClientFactory = httpClientFactory;
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
        await foreach (var job in _channel.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                var service = new AvailabilityChannexUploadService(
                    _configuration,
                    _httpClientFactory.CreateClient("ChannelManager"),
                    _logger);

                if (job.Kind == AvailabilityChannelSyncKind.Rates)
                {
                    await service.UploadRatesAsync(
                        job.HotelId, job.FromDate, job.ToDate, job.PlanIds, job.CategoryIds, stoppingToken);
                }
                else
                {
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
}
