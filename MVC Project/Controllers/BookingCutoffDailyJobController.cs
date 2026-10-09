using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Orapmshms.Services;
using Orapmshms.Services.AvailabilityJobs;

namespace Orapmshms.Controllers;

/// <summary>
/// ASP.NET Core replacement for the old BookingCutoffDailyJob.ashx HTTP handler.
/// Keeps the original URL, X-Booking-Cutoff-Key header, JSON envelope and statuses.
/// </summary>
[ApiController]
[AllowAnonymous] // Authenticated below with the job key, not the PMS login cookie.
[Route("BookingCutoffDailyJob.ashx")]
public sealed class BookingCutoffDailyJobController : ControllerBase
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<BookingCutoffDailyJobController> _logger;
    private readonly BookingCutoffDailyService _job;

    public BookingCutoffDailyJobController(
        IConfiguration configuration,
        IHttpClientFactory clientFactory,
        IHotelClock hotelClock,
        ILoggerFactory loggerFactory,
        ILogger<BookingCutoffDailyJobController> logger)
    {
        _configuration = configuration;
        _logger = logger;
        _job = new BookingCutoffDailyService(configuration, clientFactory, hotelClock, loggerFactory);
    }

    [AcceptVerbs("GET", "POST")]
    public async Task<IActionResult> Run(CancellationToken cancellationToken)
    {
        var configured = _configuration["BookingCutoffDailyJobKey:Secret"] ?? _configuration["BookingCutoffDailyJobKey"];
        var supplied = Request.Headers["X-Booking-Cutoff-Key"].ToString();
        if (string.IsNullOrWhiteSpace(configured) || !SecureEquals(configured, supplied))
            return JsonBody(401, new { ok = false, message = "Unauthorized." });

        try
        {
            var configuredHorizon = _configuration["BookingCutoffDailyHorizonDays"];
            int horizonDays = int.TryParse(configuredHorizon, out var parsed) ? parsed : 730;
            horizonDays = Math.Clamp(horizonDays, 1, 1095);
            var summary = await _job.RunAsync(horizonDays, cancellationToken);

            if (summary.AlreadyRunning)
                return JsonBody(409, new
                {
                    ok = false,
                    message = "Booking Cutoff daily job is already running.",
                    summary
                });

            var success = summary.FailedHotels == 0;
            var hasWarnings = summary.WarningHotels > 0;
            var message = !success
                ? "Booking Cutoff daily job completed with database errors."
                : hasWarnings
                    ? "Booking Cutoff database processing completed; some Channex mappings are still pending."
                    : "Booking Cutoff daily job completed.";
            return JsonBody(success ? 200 : 500, new
            {
                ok = success,
                hasWarnings,
                message,
                summary
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Preserve request cancellation; SQL session disposal releases any held app lock.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Booking cutoff daily job failed");
            return JsonBody(500, new
            {
                ok = false,
                message = "Booking Cutoff daily job failed. See server logs for details."
            });
        }
    }

    private static bool SecureEquals(string expected, string? actual)
    {
        if (actual is null) return false;
        var first = Encoding.UTF8.GetBytes(expected);
        var second = Encoding.UTF8.GetBytes(actual);
        return CryptographicOperations.FixedTimeEquals(first, second);
    }

    private static ContentResult JsonBody(int statusCode, object payload) => new()
    {
        StatusCode = statusCode,
        ContentType = "application/json; charset=utf-8",
        Content = JsonConvert.SerializeObject(payload, Formatting.Indented)
    };
}
