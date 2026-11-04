using System.Collections.Concurrent;
using System.Data;
using System.Threading.Channels;
using Microsoft.Data.SqlClient;
using Orapmshms.Services.AvailabilityJobs;
using Orapmshms.Services.LegacyApi;

namespace Orapmshms.Services;

/// <summary>
/// Login is never delayed by automatic-charge or booking-cutoff maintenance.
/// A single bounded background consumer processes one authenticated hotel at a time.
/// SQL-backed throttles prevent repeated logins across multiple app instances from
/// starting the same hotel/job repeatedly. Existing scheduler endpoints still work.
/// Requires the accompanying HotelLoginJobRunStateTB migration before enabling.
/// </summary>
public sealed class HotelLoginJobsWorker : BackgroundService, IHotelLoginJobsQueue
{
    private readonly Channel<string> _channel;
    private readonly ConcurrentDictionary<string, byte> _pending =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IHotelClock _hotelClock;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<HotelLoginJobsWorker> _logger;
    private readonly string _connectionString;
    private readonly bool _enabled;

    public HotelLoginJobsWorker(
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory,
        IHotelClock hotelClock,
        ILoggerFactory loggerFactory,
        ILogger<HotelLoginJobsWorker> logger)
    {
        _configuration = configuration;
        _httpClientFactory = httpClientFactory;
        _hotelClock = hotelClock;
        _loggerFactory = loggerFactory;
        _logger = logger;
        _enabled = configuration.GetValue("HotelLoginJobs:Enabled", false);
        _connectionString = configuration.GetConnectionString("con")
            ?? throw new InvalidOperationException("ConnectionStrings:con is missing.");

        _channel = Channel.CreateBounded<string>(new BoundedChannelOptions(64)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });
    }

    public void QueueHotelJobs(string hotelId)
    {
        if (!_enabled) return;
        hotelId = (hotelId ?? string.Empty).Trim();
        // Never run a system-wide job for a missing or malformed account hotel ID.
        if (hotelId.Length is < 1 or > 50 ||
            !hotelId.All(ch => char.IsLetterOrDigit(ch) || ch == '-' || ch == '_'))
            return;

        if (!_pending.TryAdd(hotelId, 0)) return;
        if (!_channel.Writer.TryWrite(hotelId))
        {
            _pending.TryRemove(hotelId, out _);
            _logger.LogWarning("Hotel login maintenance queue is full. Hotel={HotelId}", hotelId);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled)
        {
            _logger.LogInformation("HotelLoginJobs disabled; existing scheduled endpoints are unchanged.");
            return;
        }

        await foreach (var hotelId in _channel.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                // Login response and redirect will have finished before doing any SQL work.
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                await ProcessHotelAsync(hotelId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Hotel login maintenance failed. Hotel={HotelId}", hotelId);
            }
            finally
            {
                _pending.TryRemove(hotelId, out _);
            }
        }
    }

    private async Task ProcessHotelAsync(string hotelId, CancellationToken stoppingToken)
    {
        // The payment job is intentionally small; outstanding jobs continue to be handled
        // by the existing recurring payment scheduler, not by repeated logins.
        if (_configuration.GetValue("HotelLoginJobs:AutoChargeEnabled", true))
        {
            await RunThrottledAsync(hotelId, "AutoCharge",
                cooldownMinutes: Math.Clamp(_configuration.GetValue("HotelLoginJobs:AutoChargeCooldownMinutes", 5), 1, 1440),
                async ct =>
                {
                    var batchSize = Math.Clamp(_configuration.GetValue("HotelLoginJobs:AutoChargeBatchSize", 3), 1, 10);
                    var result = await new VirtualCardInstantChargeService()
                        .ProcessDueScheduledChargesAsync(batchSize, hotelId: hotelId);
                    _logger.LogInformation(
                        "Login auto-charge done for hotel {HotelId}. Claimed={Claimed} Success={Succeeded} Failed={Failed}",
                        hotelId, result.Claimed, result.Succeeded, result.Failed);
                    // Individual reservation payment failures are handled by the original
                    // queue's retry policy. The login job itself completed successfully.
                }, stoppingToken);
        }

        if (_configuration.GetValue("HotelLoginJobs:CutoffEnabled", true))
        {
            await RunThrottledAsync(hotelId, "BookingCutoff",
                cooldownMinutes: Math.Clamp(_configuration.GetValue("HotelLoginJobs:CutoffCooldownMinutes", 720), 60, 1440),
                async ct =>
                {
                    // Near-term login maintenance only; the existing daily 730-day scheduler
                    // retains responsibility for the complete future horizon.
                    var horizon = Math.Clamp(_configuration.GetValue("HotelLoginJobs:CutoffHorizonDays", 31), 1, 60);
                    var maxSeconds = Math.Clamp(_configuration.GetValue("HotelLoginJobs:CutoffMaxSeconds", 90), 15, 180);
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(TimeSpan.FromSeconds(maxSeconds));
    
                    var job = new BookingCutoffDailyService(
                        _configuration, _httpClientFactory, _hotelClock, _loggerFactory);
                    var summary = await job.RunAsync(horizon, timeout.Token, hotelId: hotelId);
                    if (summary.AlreadyRunning)
                        throw new InvalidOperationException("Another booking cutoff job is currently running.");
                    if (summary.FailedHotels != 0)
                        throw new InvalidOperationException(
                            $"Cutoff failed for hotel {hotelId}. Failed hotels={summary.FailedHotels}.");
                    if (summary.WarningHotels > 0)
                        _logger.LogWarning(
                            "Cutoff completed but mapping/upload warnings remain for hotel {HotelId}: {Count} warnings. " +
                            "Normal cooldown applies; pending rows will be retried by existing scheduled synchronisation.",
                            hotelId, summary.WarningHotels);
                    _logger.LogInformation(
                        "Login booking-cutoff done for hotel {HotelId}. Updated={Updated} Inserted={Inserted}",
                        hotelId, summary.UpdatedRows, summary.InsertedRows);
            }, stoppingToken);
        }
    }

    private async Task RunThrottledAsync(
        string hotelId, string jobName, int cooldownMinutes,
        Func<CancellationToken, Task> work, CancellationToken cancellationToken)
    {
        bool reserved;
        try
        {
            reserved = await TryReserveAsync(hotelId, jobName, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Missing migration/SQL outage must NEVER break login or trigger
            // an unthrottled payment or cutoff job.
            _logger.LogError(ex,
                "Could not reserve background job; it will NOT run. Hotel={HotelId}, Job={Job}",
                hotelId, jobName);
            return;
        }
        if (!reserved) return;

        try
        {
            await work(cancellationToken);
            await FinishAsync(hotelId, jobName, "Succeeded", cooldownMinutes, null, CancellationToken.None);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // On app shutdown, the SQL lease naturally expires. No extra writes needed.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Background {Job} failed for Hotel={HotelId}", jobName, hotelId);
            try
            {
                await FinishAsync(hotelId, jobName, "Failed", 15, ex.Message, CancellationToken.None);
            }
            catch (Exception updateEx)
            {
                _logger.LogError(updateEx, "Could not store background job failure. Hotel={HotelId}, Job={Job}", hotelId, jobName);
            }
        }
    }

    private async Task<bool> TryReserveAsync(
        string hotelId, string jobName, CancellationToken ct)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using var tx = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        await using var select = new SqlCommand(@"
SELECT NextAllowedUtc
FROM dbo.HotelLoginJobRunStateTB WITH (UPDLOCK, HOLDLOCK)
WHERE HotelId=@HotelId AND JobName=@JobName;", connection, tx) { CommandTimeout = 5 };
        select.Parameters.Add("@HotelId", SqlDbType.NVarChar, 50).Value = hotelId;
        select.Parameters.Add("@JobName", SqlDbType.VarChar, 32).Value = jobName;
        var next = await select.ExecuteScalarAsync(ct);
        if (next is DateTime due && due > DateTime.UtcNow)
        {
            await tx.CommitAsync(ct);
            return false;
        }

        // 10-minute recovery lease if the process is recycled mid-operation.
        // Jobs normally complete much earlier and install their real cooldown.
        var commandText = next is null ? @"
INSERT dbo.HotelLoginJobRunStateTB
(HotelId, JobName, NextAllowedUtc, LastStartedUtc, LastResult)
VALUES (@HotelId, @JobName, DATEADD(MINUTE, 10, SYSUTCDATETIME()), SYSUTCDATETIME(), 'Running');"
            : @"
UPDATE dbo.HotelLoginJobRunStateTB
SET NextAllowedUtc=DATEADD(MINUTE, 10, SYSUTCDATETIME()),
    LastStartedUtc=SYSUTCDATETIME(), LastResult='Running', LastMessage=NULL
WHERE HotelId=@HotelId AND JobName=@JobName;";
        await using var command = new SqlCommand(commandText, connection, tx) { CommandTimeout = 5 };
        command.Parameters.Add("@HotelId", SqlDbType.NVarChar, 50).Value = hotelId;
        command.Parameters.Add("@JobName", SqlDbType.VarChar, 32).Value = jobName;
        await command.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
        return true;
    }

    private async Task FinishAsync(
        string hotelId, string jobName, string result, int nextMinutes,
        string? message, CancellationToken ct)
    {
        await using var connection = new SqlConnection(_connectionString);
        await using var cmd = new SqlCommand(@"
UPDATE dbo.HotelLoginJobRunStateTB
SET LastFinishedUtc=SYSUTCDATETIME(), LastResult=@Result, LastMessage=@Message,
    NextAllowedUtc=DATEADD(MINUTE, @Minutes, SYSUTCDATETIME())
WHERE HotelId=@HotelId AND JobName=@JobName;", connection) { CommandTimeout = 5 };
        cmd.Parameters.Add("@HotelId", SqlDbType.NVarChar, 50).Value = hotelId;
        cmd.Parameters.Add("@JobName", SqlDbType.VarChar, 32).Value = jobName;
        cmd.Parameters.Add("@Result", SqlDbType.VarChar, 20).Value = result;
        cmd.Parameters.Add("@Minutes", SqlDbType.Int).Value = nextMinutes;
        cmd.Parameters.Add("@Message", SqlDbType.NVarChar, 500).Value =
            (object?)(message is null ? null : message[..Math.Min(500, message.Length)]) ?? DBNull.Value;
        await connection.OpenAsync(ct);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
