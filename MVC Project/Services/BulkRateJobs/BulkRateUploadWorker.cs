using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Orapmshms.Models;

namespace Orapmshms.Services.BulkRateJobs;

/// <summary>
/// Self-contained bounded background queue for Bulk Rate Upload.
/// No Program.cs registration is required: the controller creates one process-wide
/// instance lazily through GetOrCreate().
/// </summary>
public sealed class BulkRateUploadWorker : IBulkRateUploadQueue
{
    private const int QueueCapacity = 64;
    private const int WorkerCount = 2;

    private static readonly TimeSpan StatusRetention = TimeSpan.FromHours(12);
    private static readonly object InstanceLock = new();
    private static BulkRateUploadWorker? _instance;

    private readonly Channel<BulkRateUploadJob> _queue;
    private readonly ConcurrentDictionary<string, BulkRateUploadModel> _statuses =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, string> _hotelJobs =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly IConfiguration _configuration;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<BulkRateUploadWorker> _logger;
    private readonly CancellationTokenSource _shutdown;
    private readonly Task[] _consumers;

    private BulkRateUploadWorker(
        IConfiguration configuration,
        ILoggerFactory loggerFactory,
        CancellationToken applicationStopping)
    {
        _configuration = configuration
            ?? throw new ArgumentNullException(nameof(configuration));

        _loggerFactory = loggerFactory
            ?? throw new ArgumentNullException(nameof(loggerFactory));

        _logger = loggerFactory.CreateLogger<BulkRateUploadWorker>();
        _shutdown = CancellationTokenSource.CreateLinkedTokenSource(applicationStopping);

        _queue = Channel.CreateBounded<BulkRateUploadJob>(
            new BoundedChannelOptions(QueueCapacity)
            {
                SingleReader = false,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait
            });

        _consumers = Enumerable.Range(0, WorkerCount)
            .Select(_ => Task.Run(() => ConsumeAsync(_shutdown.Token)))
            .ToArray();
    }

    public static BulkRateUploadWorker GetOrCreate(
        IConfiguration configuration,
        ILoggerFactory loggerFactory,
        CancellationToken applicationStopping)
    {
        var current = Volatile.Read(ref _instance);
        if (current != null)
            return current;

        lock (InstanceLock)
        {
            current = _instance;

            if (current == null)
            {
                current = new BulkRateUploadWorker(
                    configuration,
                    loggerFactory,
                    applicationStopping);

                Volatile.Write(ref _instance, current);
            }
        }

        return current;
    }

    public bool TryQueue(
        BulkRateUploadJob job,
        out string rejectionMessage)
    {
        rejectionMessage = string.Empty;

        if (job == null ||
            string.IsNullOrWhiteSpace(job.JobId) ||
            string.IsNullOrWhiteSpace(job.HotelId))
        {
            rejectionMessage = "Invalid bulk rate upload job.";
            return false;
        }

        CleanupExpiredStatuses();

        if (!_hotelJobs.TryAdd(job.HotelId, job.JobId))
        {
            rejectionMessage =
                "A bulk rate upload for this hotel is already queued or running. " +
                "Please wait for it to finish before starting another one.";

            return false;
        }

        var queuedStatus = new BulkRateUploadModel
        {
            Ok = true,
            Status = "queued",
            Message = "Rate upload is queued.",
            ProcessedRows = 0,
            UpdatedAtUtc = DateTime.UtcNow
        };

        var key = StatusKey(job.JobId, job.HotelId);

        if (!_statuses.TryAdd(key, queuedStatus))
        {
            _hotelJobs.TryRemove(job.HotelId, out _);

            rejectionMessage =
                "Unable to create the background rate job. Please try again.";

            return false;
        }

        if (_queue.Writer.TryWrite(job))
            return true;

        _statuses.TryRemove(key, out _);
        _hotelJobs.TryRemove(job.HotelId, out _);

        rejectionMessage =
            "The bulk-rate queue is busy. Please try again shortly.";

        return false;
    }

    public bool TryGetStatus(
        string jobId,
        string hotelId,
        out BulkRateUploadModel status)
    {
        status = new BulkRateUploadModel
        {
            Ok = false,
            Status = "not_found",
            Message = "Rate upload job was not found.",
            UpdatedAtUtc = DateTime.UtcNow
        };

        if (string.IsNullOrWhiteSpace(jobId) ||
            string.IsNullOrWhiteSpace(hotelId))
        {
            return false;
        }

        CleanupExpiredStatuses();

        if (!_statuses.TryGetValue(
                StatusKey(jobId, hotelId),
                out var scopedStatus))
        {
            return false;
        }

        status = Clone(scopedStatus);
        return true;
    }

    private async Task ConsumeAsync(
        CancellationToken stoppingToken)
    {
        try
        {
            await foreach (
                var job in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    SetStatus(
                        job,
                        true,
                        "processing",
                        "Rates are being processed in controlled background batches.",
                        0);

                    // IMPORTANT:
                    // BulkRateChannelUploadService expects IConfiguration,
                    // not a connection-string string.
                    var channelService =
                        new BulkRateChannelUploadService(
                            _configuration,
                            _loggerFactory.CreateLogger<BulkRateChannelUploadService>());

                    // BulkRateUploadService also receives IConfiguration.
                    var service =
                        new BulkRateUploadService(
                            _configuration,
                            channelService,
                            _loggerFactory.CreateLogger<BulkRateUploadService>());

                    var result =
                        await service.ProcessJobAsync(
                            job,
                            stoppingToken);

                    SetStatus(
                        job,
                        result.Success,
                        result.Success
                            ? (result.HasWarning
                                ? "completed_with_warning"
                                : "completed")
                            : "failed",
                        result.Message,
                        result.ProcessedRows);
                }
                catch (OperationCanceledException)
                    when (stoppingToken.IsCancellationRequested)
                {
                    SetStatus(
                        job,
                        false,
                        "cancelled",
                        "Rate upload was cancelled because the application is stopping.",
                        0);

                    break;
                }
                catch (Exception ex)
                {
                    var message =
                        BuildFailureMessage(ex, job.JobId);

                    SetStatus(
                        job,
                        false,
                        "failed",
                        message,
                        0);

                    _logger.LogError(
                        ex,
                        "Bulk rate upload failed. Job={JobId}, Hotel={HotelId}, User={UserId}",
                        job.JobId,
                        job.HotelId,
                        job.UserId);
                }
                finally
                {
                    _hotelJobs.TryRemove(
                        job.HotelId,
                        out _);
                }
            }
        }
        catch (OperationCanceledException)
            when (stoppingToken.IsCancellationRequested)
        {
            // Normal process shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Bulk rate background queue stopped unexpectedly.");
        }
    }

    private void SetStatus(
        BulkRateUploadJob job,
        bool ok,
        string status,
        string message,
        int processedRows)
    {
        _statuses[
            StatusKey(job.JobId, job.HotelId)] =
            new BulkRateUploadModel
            {
                Ok = ok,
                Status = status,
                Message = message,
                ProcessedRows = processedRows,
                UpdatedAtUtc = DateTime.UtcNow
            };
    }

    private void CleanupExpiredStatuses()
    {
        var threshold =
            DateTime.UtcNow - StatusRetention;

        foreach (var item in _statuses)
        {
            if (item.Value.UpdatedAtUtc < threshold)
            {
                _statuses.TryRemove(
                    item.Key,
                    out _);
            }
        }
    }

    private static string StatusKey(
        string jobId,
        string hotelId)
        => hotelId.Trim() + "|" + jobId.Trim();

    private static BulkRateUploadModel Clone(
        BulkRateUploadModel source)
        => new()
        {
            Ok = source.Ok,
            Status = source.Status,
            Message = source.Message,
            ProcessedRows = source.ProcessedRows,
            UpdatedAtUtc = source.UpdatedAtUtc
        };

    private static string BuildFailureMessage(
        Exception exception,
        string jobId)
    {
        var actual = exception.GetBaseException();

        if (actual is SqlException sql)
        {
            var procedure =
                string.IsNullOrWhiteSpace(sql.Procedure)
                    ? string.Empty
                    : " | Procedure: " + sql.Procedure;

            var line =
                sql.LineNumber <= 0
                    ? string.Empty
                    : " | SQL line: " + sql.LineNumber;

            return
                $"Rate upload failed: database error {sql.Number}: " +
                $"{sql.Message}{procedure}{line} | Reference: {jobId}";
        }

        return
            $"Rate upload failed: {actual.Message} | Reference: {jobId}";
    }
}
