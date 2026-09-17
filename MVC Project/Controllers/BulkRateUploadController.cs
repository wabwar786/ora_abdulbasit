using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Orapmshms.Models;
using Orapmshms.Services;
using Orapmshms.Services.BulkRateJobs;
namespace Orapmshms.Controllers;
[Route("BulkRateUpload")]
public sealed class BulkRateUploadController : Controller
{
    private readonly IBulkRateUploadService _service;
    private readonly IBulkRateUploadQueue _queue;
    private readonly IHotelClock _hotelClock;
    private readonly ILogger<BulkRateUploadController> _logger;
    // No custom Program.cs registration is required.
    // IConfiguration / ILoggerFactory are framework services already available in ASP.NET Core.
    public BulkRateUploadController(
        IConfiguration configuration,
        ILoggerFactory loggerFactory,
        IHostApplicationLifetime applicationLifetime,
        IHotelClock hotelClock,
        ILogger<BulkRateUploadController> logger)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentNullException.ThrowIfNull(applicationLifetime);
        _hotelClock = hotelClock ?? throw new ArgumentNullException(nameof(hotelClock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        // Keep one constructor contract everywhere: pass IConfiguration, never a raw
        // connection-string string. Each service resolves ConnectionStrings:con itself.
        var channelUploadService = new BulkRateChannelUploadService(
            configuration,
            loggerFactory.CreateLogger<BulkRateChannelUploadService>());
        _service = new BulkRateUploadService(
            configuration,
            channelUploadService,
            loggerFactory.CreateLogger<BulkRateUploadService>());
        // One process-wide bounded queue, created lazily the first time this feature is used.
        _queue = BulkRateUploadWorker.GetOrCreate(
            configuration,
            loggerFactory,
            applicationLifetime.ApplicationStopping);
    }
    // Clean MVC URL: /BulkRateUpload
    [HttpGet("")]
    [HttpGet("Index")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var hotelId = SessionValue("hotel");
        if (hotelId.Length == 0)
            return RedirectToAction("Index", "LoginHMS");
        try
        {
            var model = await _service.GetPageAsync(
                hotelId,
                SessionValue("HotelName"),
                cancellationToken);
            model.HotelTodayIso = _hotelClock.GetHotelToday(hotelId).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            return View(model);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Bulk rate upload page failed to load for hotel {HotelId}.", hotelId);
            ViewBag.LoadError = "The rate plans and room types could not be loaded. Please check the database connection.";
            return View(new BulkRateUploadModel
            {
                HotelId = hotelId,
                HotelName = SessionValue("HotelName"),
                HotelTodayIso = _hotelClock.GetHotelToday(hotelId).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            });
        }
    }
    // Backward-compatible entry point for any old menu/bookmark that still points
    // to UpdateRateValues.aspx with WebForms query-string values. The MVC feature
    // uses Session, so discard the legacy query string and redirect to the clean URL.
    [HttpGet("/UpdateRateValues.aspx")]
    public IActionResult LegacyUpdateRateValues()
    {
        return RedirectToAction(nameof(Index));
    }
    [HttpPost("Preview")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Preview(
        [FromBody] BulkRateUploadModel request,
        CancellationToken cancellationToken)
    {
        var hotelId = SessionValue("hotel");
        if (hotelId.Length == 0)
            return Unauthorized(new { ok = false, message = "Your login session is missing. Please sign in again." });
        try
        {
            var dateValidation = ValidateDatesAgainstHotelToday(request, _hotelClock.GetHotelToday(hotelId));
            if (dateValidation != null)
                return BadRequest(new { ok = false, message = dateValidation });
            var rows = await _service.PreviewAsync(hotelId, request, cancellationToken);
            return Json(new
            {
                ok = true,
                rows = rows.Select(row => new
                {
                    row.DateRangeText,
                    row.RoomTypeText,
                    row.PlanText,
                    row.Currency,
                    row.AdjustedRate
                })
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new EmptyResult();
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { ok = false, message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Bulk rate preview failed for hotel {HotelId}.", hotelId);
            return StatusCode(500, new { ok = false, message = "Unable to preview these rates." });
        }
    }
    [HttpPost("Start")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Start(
        [FromBody] BulkRateUploadModel request,
        CancellationToken cancellationToken)
    {
        var hotelId = SessionValue("hotel");
        var userId = SessionValue("UserId");
        if (hotelId.Length == 0 || userId.Length == 0)
            return Unauthorized(new { ok = false, message = "Your login session is missing. Please sign in again." });

        var propertyBaseRate = await _service.GetPropertyBaseRateAsync(hotelId, cancellationToken);
        var validation = ValidateStartRequest(
            request,
            _hotelClock.GetHotelToday(hotelId),
            propertyBaseRate);
        if (validation != null)
            return BadRequest(new { ok = false, message = validation });

        var jobId = Guid.NewGuid().ToString("N");
        var job = new BulkRateUploadJob(
            jobId,
            hotelId,
            SessionValue("HotelName"),
            userId,
            SessionValue("UserName"),
            HttpContext.Connection.RemoteIpAddress?.ToString() ?? string.Empty,
            Environment.MachineName,
            request,
            DateTime.UtcNow);
        if (!_queue.TryQueue(job, out var rejectionMessage))
        {
            var statusCode = rejectionMessage.StartsWith("A bulk rate upload for this hotel", StringComparison.OrdinalIgnoreCase)
                ? StatusCodes.Status409Conflict
                : StatusCodes.Status429TooManyRequests;

            return StatusCode(statusCode, new
            {
                ok = false,
                message = rejectionMessage
            });
        }
        return Json(new
        {
            ok = true,
            jobId,
            message = "Rate upload was queued. You can keep this page open while it processes in the background."
        });
    }

    [HttpGet("Status/{jobId}")]
    public IActionResult Status(string jobId)
    {
        var hotelId = SessionValue("hotel");
        if (hotelId.Length == 0)
            return Unauthorized(new { ok = false, status = "unauthorized", message = "Your login session is missing." });

        if (!_queue.TryGetStatus(jobId, hotelId, out var status))
        {
            return NotFound(new
            {
                status.Ok,
                status.Status,
                status.Message,
                status.ProcessedRows,
                status.UpdatedAtUtc
            });
        }
        Response.Headers.CacheControl = "no-store, no-cache";
        return Json(new
        {
            status.Ok,
            status.Status,
            status.Message,
            status.ProcessedRows,
            status.UpdatedAtUtc
        });
    }
    [HttpGet("History")]
    public async Task<IActionResult> History(
        string? search,
        string? from,
        string? to,
        int page = 1,
        int pageSize = 15,
        CancellationToken cancellationToken = default)
    {
        var hotelId = SessionValue("hotel");
        if (hotelId.Length == 0)
            return Unauthorized(new { ok = false, message = "Your login session is missing." });

        try
        {
            var result = await _service.GetHistoryAsync(
                hotelId,
                search,
                ParseDate(from),
                ParseDate(to),
                page,
                pageSize,
                cancellationToken);

            return Json(new
            {
                ok = true,
                total = result.Total,
                rows = result.Rows.Select(row => new
                {
                    row.DateCreated,
                    row.UpdatedBy,
                    row.RatePlan,
                    row.RoomType,
                    Days = row.DaysText,
                    row.DateFrom,
                    row.DateTo,
                    row.BaseRateSet
                })
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Bulk rate history failed for hotel {HotelId}.", hotelId);
            return StatusCode(500, new { ok = false, message = "Unable to load rate upload history." });
        }
    }

    private string SessionValue(string key)
        => Convert.ToString(HttpContext.Session.GetString(key), CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;

    private static DateTime? ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var formats = new[] { "yyyy-MM-dd", "dd/MM/yyyy", "MM/dd/yyyy" };
        return DateTime.TryParseExact(
            value.Trim(),
            formats,
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var parsed)
            ? parsed.Date
            : null;
    }

    private static string? ValidateStartRequest(
        BulkRateUploadModel? request,
        DateTime hotelToday,
        decimal propertyBaseRate)
    {
        if (request == null) return "Invalid rate upload request.";
        if (request.Plans == null || request.Plans.Count == 0) return "Select at least one rate plan.";
        if (request.Rooms == null || request.Rooms.Count == 0) return "Select at least one room type.";
        if (request.Ranges == null || request.Ranges.Count == 0) return "Add at least one date range.";
        if (request.Days == null || request.Days.Count == 0) return "Select at least one applicable day.";
        if (request.Plans.Count > 50) return "Select no more than 50 rate plans in one upload.";
        if (request.Rooms.Count > 50) return "Select no more than 50 room types in one upload.";
        if (request.Ranges.Count > 200) return "Use no more than 200 date ranges in one upload.";

        if (propertyBaseRate > 0m)
        {
            var invalidRange = request.Ranges.FirstOrDefault(x => x != null && x.BaseRate < propertyBaseRate);
            if (invalidRange != null)
                return $"Base rate cannot be less than the property base rate {propertyBaseRate:0.00}.";
        }

        return ValidateDatesAgainstHotelToday(request, hotelToday);
    }

    private static string? ValidateDatesAgainstHotelToday(BulkRateUploadModel? request, DateTime hotelToday)
    {
        if (request?.Ranges == null || request.Ranges.Count == 0)
            return null;

        var minimumDate = hotelToday.Date;
        foreach (var range in request.Ranges)
        {
            if (range == null)
                return "A date range is invalid.";

            if (!DateTime.TryParseExact(range.Start, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start)
                || !DateTime.TryParseExact(range.End, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end))
                return "Select a valid start and end date.";

            start = start.Date;
            end = end.Date;
            if (end < start)
                return "The end date cannot be before the start date.";
            if (start < minimumDate || end < minimumDate)
                return $"Past dates cannot be selected. The earliest date for this hotel is {minimumDate:dd/MM/yyyy}.";
        }

        return null;
    }
}
