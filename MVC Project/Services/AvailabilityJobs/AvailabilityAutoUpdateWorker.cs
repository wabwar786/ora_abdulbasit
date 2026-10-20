using System.Data;
using System.Threading.Channels;
using Microsoft.Data.SqlClient;
using Orapmshms.Services;

namespace Orapmshms.Services.AvailabilityJobs;

public sealed record AvailabilityAutoUpdateJob(
    string HotelId,
    string HotelName,
    string UserId,
    string UserName,
    string ClientIp,
    DateTime StartDate,
    DateTime EndDate,
    string CategoryId,
    bool RunReservationYield = false);

public interface IAvailabilityAutoUpdateQueue
{
    bool Queue(AvailabilityAutoUpdateJob job);
}

/// <summary>
/// ASP.NET Core background replacement for WebForms CalendarAvailabilityBackgroundRunner.QueueSingle.
/// The sequence is kept the same: recalculate AvailabilityTB first, then upload availability to Channex.
/// </summary>
public sealed class AvailabilityAutoUpdateWorker : BackgroundService, IAvailabilityAutoUpdateQueue
{
    private readonly Channel<AvailabilityAutoUpdateJob> _queue;
    private readonly string _connectionString;
    private readonly IHotelClock _hotelClock;
    private readonly YieldManagementService _yieldManagementService;
    private readonly ILogger<AvailabilityAutoUpdateWorker> _logger;

    public AvailabilityAutoUpdateWorker(
        IConfiguration configuration,
        IHotelClock hotelClock,
        IAvailabilityChannelSyncQueue channelSyncQueue,
        ILoggerFactory loggerFactory,
        ILogger<AvailabilityAutoUpdateWorker> logger)
    {
        _connectionString = configuration.GetConnectionString("con")
            ?? throw new InvalidOperationException("ConnectionStrings:con is missing from configuration.");
        _hotelClock = hotelClock;
        _yieldManagementService = new YieldManagementService(
            configuration, hotelClock, channelSyncQueue,
            loggerFactory.CreateLogger<YieldManagementService>());
        _logger = logger;

        _queue = Channel.CreateBounded<AvailabilityAutoUpdateJob>(new BoundedChannelOptions(64)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });
    }

    public bool Queue(AvailabilityAutoUpdateJob job)
    {
        if (job == null || string.IsNullOrWhiteSpace(job.HotelId)) return false;
        return _queue.Writer.TryWrite(job);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var job in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                // Exactly ONE yield engine handles a reservation-triggered update:
                // YieldManagementService (also used by the automatic sweep).
                // It acquires the hotel lock internally. Do NOT run it inside
                // HotelAvailabilityJobCoordinator.RunAsync: that would deadlock.
                if (job.RunReservationYield)
                {
                    try
                    {
                        var audit = RateAuditMetadata.Create(
                            job.HotelId,
                            "YieldManagement/NewReservation",
                            "ReservationCreated",
                            job.UserId,
                            job.UserName,
                            job.ClientIp);

                        var evaluation = await _yieldManagementService.EvaluateActiveRulesAsync(
                            job.HotelId, stoppingToken, audit);
                        _logger.LogInformation(
                            "NewReservation Yield evaluation completed. Hotel={HotelId}, User={UserId}, Applied={Applied}, Restored={Restored}.",
                            job.HotelId, job.UserId, evaluation.AppliedRows, evaluation.RestoredRows);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex,
                            "New Reservation yield evaluation failed for hotel {HotelId}, range {Start:yyyy-MM-dd}-{End:yyyy-MM-dd}. Reservation remains committed; rate retry may be required.",
                            job.HotelId, job.StartDate, job.EndDate);
                    }
                }

                // Continue with the existing availability / Channex refresh.
                await HotelAvailabilityJobCoordinator.RunAsync(
                    job.HotelId,
                    stoppingToken,
                    () => ExecuteJob(job));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Availability Auto Update failed for hotel {HotelId}, range {Start:yyyy-MM-dd} to {End:yyyy-MM-dd}.",
                    job.HotelId, job.StartDate, job.EndDate);
            }
        }
    }

    private void ExecuteJob(AvailabilityAutoUpdateJob job)
    {
        var start = job.StartDate.Date;
        var end = job.EndDate.Date;
        if (end < start) (start, end) = (end, start);

        // Yield has already been evaluated above, using YieldManagementService
        // only. Keep this method dedicated to availability + channel updates.

        var categoryId = string.IsNullOrWhiteSpace(job.CategoryId) ? "0" : job.CategoryId.Trim();
        var selectedIndex = categoryId == "0" ? 0 : 1;

        // Same WebForms calculation utility/rules: physical rooms - assigned bookings -
        // unassigned bookings - room blocks; AvailabilityTB is MERGE-updated with upload=0.
        var availabilityService = new AvailabilityBackgroundService(_connectionString);
        availabilityService.AutoUpdateAvailability(
            start,
            end,
            job.HotelId,
            job.UserName,
            job.ClientIp,
            categoryId,
            job.UserId,
            string.Empty,
            actionLogFn: null!,
            hotelName: job.HotelName,
            originPage: "Availability",
            originFunction: "AutoUpdate");

        // Same WebForms Channex selection rules (property_id, staging/app link and API key).
        var settings = LoadChannelSettings(job.HotelId);
        if (string.IsNullOrWhiteSpace(settings.PropertyId) ||
            string.IsNullOrWhiteSpace(settings.BaseUrl) ||
            string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            _logger.LogInformation(
                "Availability recalculated for hotel {HotelId}; Channex upload skipped because channel-manager settings are incomplete.",
                job.HotelId);
            return;
        }

        var channelService = new ChannelManagerBackgroundService(_connectionString, _hotelClock);
        channelService.UploadToChannelManager(
            job.HotelId,
            start,
            end,
            selectedIndex,
            categoryId,
            settings.PropertyId,
            settings.BaseUrl.TrimEnd('/') + "/api/v1/availability",
            settings.ApiKey,
            string.Empty,
            job.UserId,
            job.HotelName,
            "Availability",
            "AutoUpdate");

        _logger.LogInformation(
            "Availability Auto Update completed for hotel {HotelId}, range {Start:yyyy-MM-dd} to {End:yyyy-MM-dd}.",
            job.HotelId, start, end);
    }

    private ChannelSettings LoadChannelSettings(string hotelId)
    {
        using var connection = new SqlConnection(_connectionString);
        connection.Open();

        string propertyId = string.Empty;
        bool staging = false;

        using (var cmd = new SqlCommand(@"
SELECT TOP (1) property_id, channexstaging
FROM dbo.HotelsSignUpTB
WHERE hotel_id=@hotel;", connection))
        {
            cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                propertyId = Convert.ToString(reader["property_id"])?.Trim() ?? string.Empty;
                if (reader["channexstaging"] != DBNull.Value)
                    bool.TryParse(Convert.ToString(reader["channexstaging"]), out staging);
            }
        }

        var channelName = staging ? "app" : "staging";
        string baseUrl = string.Empty;
        using (var cmd = new SqlCommand(@"
SELECT TOP (1) link
FROM dbo.channexlink
WHERE channelname=@channelname;", connection))
        {
            cmd.Parameters.Add("@channelname", SqlDbType.VarChar, 50).Value = channelName;
            baseUrl = Convert.ToString(cmd.ExecuteScalar())?.Trim() ?? string.Empty;
        }

        string apiKey = string.Empty;
        using (var cmd = new SqlCommand(@"
SELECT TOP (1) apikey, username
FROM dbo.channelmanagerapikey
ORDER BY id DESC;", connection))
        using (var reader = cmd.ExecuteReader())
        {
            if (reader.Read())
            {
                var normalizedBase = baseUrl.TrimEnd('/');
                apiKey = normalizedBase.Equals("https://app.channex.io", StringComparison.OrdinalIgnoreCase)
                    ? Convert.ToString(reader["apikey"])?.Trim() ?? string.Empty
                    : Convert.ToString(reader["username"])?.Trim() ?? string.Empty;
            }
        }

        return new ChannelSettings(propertyId, baseUrl, apiKey);
    }

    private sealed record ChannelSettings(string PropertyId, string BaseUrl, string ApiKey);
}
