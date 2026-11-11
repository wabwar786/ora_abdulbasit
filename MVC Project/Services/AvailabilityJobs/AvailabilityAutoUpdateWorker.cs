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
    bool RunReservationYield = false,
    bool YieldOnly = false,
    string YieldSource = "NewReservation");

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
        if (job.YieldOnly && !job.RunReservationYield) return false;
        return _queue.Writer.TryWrite(job);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Expiry runs separately from the existing availability/channel queue so
        // a quiet hotel still releases provisional inventory when the deadline passes.
        var expiryTask = RunProvisionalExpiryLoopAsync(stoppingToken);
        try
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
                            "YieldManagement/" + job.YieldSource,
                            job.YieldSource,
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
                if (job.YieldOnly)
                    continue;
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
        finally
        {
            // The host cancels stoppingToken during shutdown; do not leave an
            // unobserved background sweep task running.
            try { await expiryTask; }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        }
    }

    private async Task RunProvisionalExpiryLoopAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(8), stoppingToken); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await SweepProvisionalDeadlinesAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "ORA provisional expiry sweep failed; the booking was not silently discarded. Check database migration and SQL connection.");
            }
            try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    private async Task SweepProvisionalDeadlinesAsync(CancellationToken ct)
    {
        // Safe deployment: when migration has not been installed, or explicit
        // activation is OFF, do not enumerate or touch any reservations.
        const string enabledSql = @"
IF OBJECT_ID(N'dbo.OraProvisionalFeatureFlags',N'U') IS NULL
  SELECT CAST(0 AS bit);
ELSE
  EXEC(N'SELECT TOP (1) auto_expire_unpaid FROM dbo.OraProvisionalFeatureFlags WHERE id=1;');";
        await using (var guardCn = new SqlConnection(_connectionString))
        {
            await guardCn.OpenAsync(ct);
            await using var guardCmd = new SqlCommand(enabledSql, guardCn) { CommandTimeout = 10 };
            if (Convert.ToString(await guardCmd.ExecuteScalarAsync(ct)) != "True")
                return;
        }

        // Read-only scan ordered by earliest deadline; do not affect confirmed,
        // checked-in, manually cancelled, or no-show reservations.
        const string candidatesSql = @"
;WITH active AS
(
 SELECT DISTINCT CONVERT(varchar(50),hotel_id) hotel_id,
                 CONVERT(varchar(100),reg_id) reg_id
 FROM dbo.payments WITH (READPAST)
 WHERE LOWER(LTRIM(RTRIM(ISNULL(res_status,'')))) IN ('provisional','tentative')
   AND LTRIM(RTRIM(ISNULL(descr,'')))='Room Rent'
), deadlines AS
(
 SELECT a.hotel_id,a.reg_id,
  COALESCE(d.deadline_at,
           DATEADD(day,1,COALESCE(
              TRY_CONVERT(datetime2,NULLIF(LTRIM(RTRIM(g.expirydate)),''),110),
              TRY_CONVERT(datetime2,NULLIF(LTRIM(RTRIM(g.expirydate)),''),23),
              TRY_CONVERT(datetime2,NULLIF(LTRIM(RTRIM(g.expirydate)),''),103),
              TRY_CONVERT(datetime2,NULLIF(LTRIM(RTRIM(n.expirydate)),''),110),
              TRY_CONVERT(datetime2,NULLIF(LTRIM(RTRIM(n.expirydate)),''),23),
              TRY_CONVERT(datetime2,NULLIF(LTRIM(RTRIM(n.expirydate)),''),103))),
           CONVERT(datetime2,'9999-01-01')) AS deadline_sort
 FROM active a
 LEFT JOIN dbo.ProvisionalReservationDeadlines d ON d.hotel_id=a.hotel_id AND d.reg_id=a.reg_id
 OUTER APPLY (SELECT TOP 1 CONVERT(varchar(50),expirydate) expirydate
              FROM dbo.GuestInformationLogTB WHERE hotel_id=a.hotel_id AND reg_id=a.reg_id ORDER BY id DESC) g
 OUTER APPLY (SELECT TOP 1 CONVERT(varchar(50),expirydate) expirydate
              FROM dbo.NewReservationsTB WHERE hotel_id=a.hotel_id AND reg_id=a.reg_id ORDER BY id DESC) n
)
SELECT TOP (250) hotel_id,reg_id
FROM deadlines
WHERE deadline_sort < DATEADD(day,2,SYSUTCDATETIME())
ORDER BY deadline_sort,hotel_id,reg_id;";
        var candidates = new List<(string HotelId, string RegId)>();
        await using (var cn = new SqlConnection(_connectionString))
        {
            await cn.OpenAsync(ct);
            await using var cmd = new SqlCommand(candidatesSql, cn) { CommandTimeout = 45 };
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
                candidates.Add((rd.GetString(0), rd.GetString(1)));
        }

        foreach (var (hotelId, regId) in candidates)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var now = _hotelClock.GetHotelNow(hotelId);
                await using var cn = new SqlConnection(_connectionString);
                await cn.OpenAsync(ct);
                await using var cmd = new SqlCommand("dbo.OraExpireProvisionalReservation", cn)
                {
                    CommandType = CommandType.StoredProcedure,
                    CommandTimeout = 35
                };
                cmd.Parameters.Add("@hotelId", SqlDbType.VarChar, 50).Value = hotelId;
                cmd.Parameters.Add("@regId", SqlDbType.VarChar, 100).Value = regId;
                cmd.Parameters.Add("@hotelNow", SqlDbType.DateTime2).Value = now;
                var action = cmd.Parameters.Add("@action", SqlDbType.VarChar, 16);
                action.Direction = ParameterDirection.Output;
                var start = cmd.Parameters.Add("@availabilityStart", SqlDbType.Date);
                start.Direction = ParameterDirection.Output;
                var end = cmd.Parameters.Add("@availabilityEnd", SqlDbType.Date);
                end.Direction = ParameterDirection.Output;
                await cmd.ExecuteNonQueryAsync(ct);
                var outcome = Convert.ToString(action.Value) ?? string.Empty;
                if (outcome == "cancelled")
                {
                    _logger.LogInformation(
                        "Provisional reservation {RegId} at hotel {HotelId} expired and was automatically cancelled.", regId, hotelId);
                    if (start.Value is DateTime from && end.Value is DateTime until)
                    {
                        var begin = from.Date < now.Date ? now.Date : from.Date;
                        if (until.Date >= begin)
                        {
                            // Existing worker performs Yield, availability recalculation,
                            // and Channex sync after the cancellation is committed.
                            // Wait for room in the bounded queue rather than
                            // losing the mandatory inventory release on overload.
                            await _queue.Writer.WriteAsync(new AvailabilityAutoUpdateJob(
                                hotelId, string.Empty, "0", "Automatic Provisional Expiry",
                                "SYSTEM", begin, until.Date, "0", RunReservationYield: true,
                                YieldSource: "ProvisionalExpiry"), ct);
                        }
                    }
                }
                else if (outcome == "confirmed")
                {
                    _logger.LogInformation(
                        "Provisional reservation {RegId} at hotel {HotelId} confirmed because a payment was already logged.",
                        regId, hotelId);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (SqlException ex) when (ex.Number == 1205)
            {
                // Payment arrival vs expiry may deadlock under concurrent SQL writes.
                // Deadlock victim transaction rolled back; next minute retries safely.
                _logger.LogWarning(ex, "Payment/expiry concurrency retry scheduled for {HotelId}/{RegId}.", hotelId, regId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unable to expire provisional reservation {RegId} at hotel {HotelId}.", regId, hotelId);
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
