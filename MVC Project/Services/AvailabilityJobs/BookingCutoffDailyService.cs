using System.Data;
using Microsoft.Data.SqlClient;
using Orapmshms.Services;

namespace Orapmshms.Services.AvailabilityJobs;

// The original job's response model is retained for scheduler/Postman clients.
public sealed class BookingCutoffDailyHotelResult
{
    public string HotelId { get; set; } = string.Empty;
    public bool Success { get; set; }
    public bool HasWarnings { get; set; }
    public int UpdatedRows { get; set; }
    public int InsertedRows { get; set; }
    public int PendingUploadRows { get; set; }
    public int UploadedRows { get; set; }
    public int SkippedMappingRows { get; set; }
    public List<string> Warnings { get; set; } = new();
    public string? Error { get; set; }
}

public sealed class BookingCutoffDailyRunSummary
{
    public DateTime StartedUtc { get; set; }
    public DateTime FinishedUtc { get; set; }
    public bool AlreadyRunning { get; set; }
    public int HotelCount { get; set; }
    public int SuccessfulHotels { get; set; }
    public int FailedHotels { get; set; }
    public int WarningHotels { get; set; }
    public int UpdatedRows { get; set; }
    public int InsertedRows { get; set; }
    public int PendingUploadRows { get; set; }
    public int UploadedRows { get; set; }
    public int SkippedMappingRows { get; set; }
    public List<BookingCutoffDailyHotelResult> Hotels { get; set; } = new();
}

/// <summary>
/// Runs the daily booking-cutoff sweep using the EXISTING MVC restriction and
/// Channex sync implementation, not the legacy WebForms ChannexUploadWorker.
/// Uses ConnectionStrings:con only. Constructed by the endpoint from services
/// already registered in Program.cs (so no startup changes are needed).
/// </summary>
public sealed class BookingCutoffDailyService
{
    private const string ApplicationLockName = "hotelsoftware.BookingCutoffDailyService";

    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _clientFactory;
    private readonly IHotelClock _hotelClock;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<BookingCutoffDailyService> _logger;

    public BookingCutoffDailyService(
        IConfiguration configuration,
        IHttpClientFactory clientFactory,
        IHotelClock hotelClock,
        ILoggerFactory loggerFactory)
    {
        _configuration = configuration;
        _clientFactory = clientFactory;
        _hotelClock = hotelClock;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<BookingCutoffDailyService>();
    }

    public async Task<BookingCutoffDailyRunSummary> RunAsync(
        int horizonDays = 730,
        CancellationToken cancellationToken = default)
    {
        var connectionString = _configuration.GetConnectionString("con");
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("The MVC connection string 'ConnectionStrings:con' is required.");

        horizonDays = Math.Clamp(horizonDays, 1, 1095);
        var summary = new BookingCutoffDailyRunSummary { StartedUtc = DateTime.UtcNow };

        // Session-level SQL application lock serializes requests across app instances.
        // A separate SQL connection is kept open for the full sweep, as in the old handler.
        await using var lockConnection = new SqlConnection(connectionString);
        await lockConnection.OpenAsync(cancellationToken);
        var lockResult = await AcquireApplicationLockAsync(lockConnection, cancellationToken);
        if (lockResult < 0)
        {
            if (lockResult != -1)
                throw new InvalidOperationException($"Unable to acquire booking cutoff lock (SQL result {lockResult}).");
            summary.AlreadyRunning = true;
            summary.FinishedUtc = DateTime.UtcNow;
            return summary;
        }

        try
        {
            var hotelIds = await LoadHotelsRequiringCutoffProcessingAsync(lockConnection, cancellationToken);
            summary.HotelCount = hotelIds.Count;

            foreach (var hotelId in hotelIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = new BookingCutoffDailyHotelResult { HotelId = hotelId };
                summary.Hotels.Add(result);

                try
                {
                    var plans = await LoadAffectedPlansAsync(lockConnection, hotelId, cancellationToken);
                    if (plans.Length != 0)
                    {
                        // Uses the hotel's existing timezone configuration, not the server clock.
                        var today = _hotelClock.GetHotelToday(hotelId).Date;
                        var through = today.AddDays(horizonDays - 1);
                        var uploader = new AvailabilityChannexUploadService(
                            _configuration,
                            _clientFactory.CreateClient("ChannelManager"),
                            _loggerFactory.CreateLogger<AvailabilityChannexUploadService>());

                        var preparation = await uploader.PrepareBookingCutoffRowsWithCountsAsync(
                            hotelId, today, through, plans, Array.Empty<string>(),
                            "BookingCutoffDailyJob", cancellationToken);
                        result.UpdatedRows = preparation.UpdatedRows;
                        result.InsertedRows = preparation.InsertedRows;

                        var pendingBefore = await ReadPendingAsync(
                            lockConnection, hotelId, today, through, plans, cancellationToken);
                        result.PendingUploadRows = pendingBefore.Pending;

                        if (pendingBefore.Pending != 0)
                        {
                            // Existing MVC sync path; preserves Channex mapping and upload rules.
                            await uploader.EnsureRatePlansAsync(
                                hotelId, plans, Array.Empty<string>(), cancellationToken);
                            await uploader.UploadRestrictionsAsync(
                                hotelId, today, through, plans, Array.Empty<string>(), cancellationToken);

                            var pendingAfter = await ReadPendingAsync(
                                lockConnection, hotelId, today, through, plans, cancellationToken);
                            result.UploadedRows = Math.Max(0, pendingBefore.Pending - pendingAfter.Pending);
                            result.SkippedMappingRows = pendingAfter.Unmapped;
                            if (pendingAfter.Pending > 0)
                            {
                                result.Warnings.Add(
                                    $"{pendingAfter.Pending} restriction rows remain pending in Channex synchronization. " +
                                    "Check property configuration, room/rate-plan mappings and application logs.");
                            }
                        }
                    }

                    result.Success = true;
                    result.HasWarnings = result.Warnings.Count > 0;
                    summary.SuccessfulHotels++;
                    summary.UpdatedRows += result.UpdatedRows;
                    summary.InsertedRows += result.InsertedRows;
                    summary.PendingUploadRows += result.PendingUploadRows;
                    summary.UploadedRows += result.UploadedRows;
                    summary.SkippedMappingRows += result.SkippedMappingRows;
                    if (result.HasWarnings) summary.WarningHotels++;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    result.Success = false;
                    result.Error = $"{ex.GetType().Name}: {ex.Message}";
                    summary.FailedHotels++;
                    _logger.LogError(ex, "Booking cutoff processing failed for hotel {HotelId}", hotelId);
                }
            }
        }
        finally
        {
            try
            {
                await ReleaseApplicationLockAsync(lockConnection, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Booking cutoff app-lock release failed; closing SQL session releases the lock.");
            }
            summary.FinishedUtc = DateTime.UtcNow;
        }

        return summary;
    }

    private static async Task<List<string>> LoadHotelsRequiringCutoffProcessingAsync(
        SqlConnection connection, CancellationToken cancellationToken)
    {
        const string sql = @"
SELECT DISTINCT H.hotel_id
FROM
(
    SELECT CONVERT(NVARCHAR(50), p.hotel_id) AS hotel_id
    FROM dbo.plans p
    WHERE p.hotel_id IS NOT NULL AND p.localplanid IS NOT NULL
      AND ISNULL(p.inactive,0)=0
      AND ISNULL(p.booking_cutoff_enabled,0)=1
      AND ISNULL(TRY_CONVERT(INT,p.booking_cutoff_days),0)>0
    UNION
    SELECT CONVERT(NVARCHAR(50), dr.hotel_id) AS hotel_id
    FROM dbo.datesrates dr
    WHERE dr.hotel_id IS NOT NULL AND ISNULL(dr.cutoff_stop_sell,0)=1
) H
WHERE NULLIF(LTRIM(RTRIM(H.hotel_id)),N'') IS NOT NULL
ORDER BY H.hotel_id;";

        var ids = new List<string>();
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 120 };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var hotelId = Convert.ToString(reader["hotel_id"])?.Trim();
            if (!string.IsNullOrWhiteSpace(hotelId)) ids.Add(hotelId);
        }
        return ids;
    }

    private static async Task<string[]> LoadAffectedPlansAsync(
        SqlConnection connection, string hotelId, CancellationToken cancellationToken)
    {
        const string sql = @"
SELECT DISTINCT plans.plan_id
FROM (
    SELECT CONVERT(NVARCHAR(50),p.localplanid) AS plan_id
    FROM dbo.plans p
    WHERE CONVERT(NVARCHAR(50),p.hotel_id)=@hotel
      AND ISNULL(p.inactive,0)=0
      AND ISNULL(p.booking_cutoff_enabled,0)=1
      AND ISNULL(TRY_CONVERT(INT,p.booking_cutoff_days),0)>0
    UNION
    SELECT CONVERT(NVARCHAR(50),dr.planid) AS plan_id
    FROM dbo.datesrates dr
    WHERE CONVERT(NVARCHAR(50),dr.hotel_id)=@hotel
      AND ISNULL(dr.cutoff_stop_sell,0)=1
) plans
WHERE NULLIF(LTRIM(RTRIM(plans.plan_id)),N'') IS NOT NULL;";

        var planIds = new List<string>();
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 120 };
        command.Parameters.Add("@hotel", SqlDbType.NVarChar, 50).Value = hotelId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = Convert.ToString(reader["plan_id"])?.Trim();
            if (!string.IsNullOrWhiteSpace(id)) planIds.Add(id);
        }
        return planIds.ToArray();
    }

    private sealed record PendingStats(int Pending, int Unmapped);

    private static async Task<PendingStats> ReadPendingAsync(
        SqlConnection connection,
        string hotelId,
        DateTime fromDate,
        DateTime through,
        string[] plans,
        CancellationToken cancellationToken)
    {
        if (plans.Length == 0) return new(0, 0);
        var placeholders = string.Join(",", plans.Select((_, i) => "@plan" + i));
        var sql = $@"
SELECT COUNT_BIG(*) AS Pending,
       COALESCE(SUM(CASE WHEN cr.HasMapping IS NULL OR cp.HasMapping IS NULL
           THEN CONVERT(BIGINT,1) ELSE CONVERT(BIGINT,0) END),0) AS Unmapped
FROM dbo.datesrates dr
OUTER APPLY
(
    SELECT TOP (1) 1 AS HasMapping FROM dbo.create_room room
    WHERE CONVERT(NVARCHAR(50),room.hotel_id)=CONVERT(NVARCHAR(50),dr.hotel_id)
      AND CONVERT(NVARCHAR(50),room.localcategoryid)=CONVERT(NVARCHAR(50),dr.category_id)
      AND NULLIF(LTRIM(RTRIM(CONVERT(NVARCHAR(100),room.category_id))),N'') IS NOT NULL
) cr
OUTER APPLY
(
    SELECT TOP (1) 1 AS HasMapping FROM dbo.category_plan planmap
    WHERE CONVERT(NVARCHAR(50),planmap.hotel_id)=CONVERT(NVARCHAR(50),dr.hotel_id)
      AND CONVERT(NVARCHAR(50),planmap.localplanid)=CONVERT(NVARCHAR(50),dr.planid)
      AND CONVERT(NVARCHAR(50),planmap.category_id)=CONVERT(NVARCHAR(50),dr.category_id)
      AND NULLIF(LTRIM(RTRIM(CONVERT(NVARCHAR(100),planmap.plainid))),N'') IS NOT NULL
) cp
WHERE CONVERT(NVARCHAR(50),dr.hotel_id)=@hotel
  AND dr.[date] BETWEEN @from AND @to
  AND ISNULL(dr.restr_upload,0)=0
  AND CONVERT(NVARCHAR(50),dr.planid) IN ({placeholders});";

        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 120 };
        command.Parameters.Add("@hotel", SqlDbType.NVarChar, 50).Value = hotelId;
        command.Parameters.Add("@from", SqlDbType.Date).Value = fromDate.Date;
        command.Parameters.Add("@to", SqlDbType.Date).Value = through.Date;
        for (var i = 0; i < plans.Length; i++)
            command.Parameters.Add("@plan" + i, SqlDbType.NVarChar, 50).Value = plans[i];
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return new(0, 0);
        return new(Convert.ToInt32(reader["Pending"]), Convert.ToInt32(reader["Unmapped"]));
    }

    private static async Task<int> AcquireApplicationLockAsync(
        SqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(@"
DECLARE @result INT;
EXEC @result = sys.sp_getapplock @Resource=@resource,
    @LockMode=N'Exclusive', @LockOwner=N'Session', @LockTimeout=0;
SELECT @result;", connection);
        command.Parameters.Add("@resource", SqlDbType.NVarChar, 255).Value = ApplicationLockName;
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null or DBNull ? -999 : Convert.ToInt32(result);
    }

    private static async Task ReleaseApplicationLockAsync(
        SqlConnection connection, CancellationToken cancellationToken)
    {
        if (connection.State != ConnectionState.Open) return;
        await using var command = new SqlCommand(@"
EXEC sys.sp_releaseapplock @Resource=@resource, @LockOwner=N'Session';", connection);
        command.Parameters.Add("@resource", SqlDbType.NVarChar, 255).Value = ApplicationLockName;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
