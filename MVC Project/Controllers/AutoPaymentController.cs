using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orapmshms.Models;
using Orapmshms.Services;

namespace Orapmshms.Controllers;

/// <summary>
/// MVC replacement for Autopayment.aspx.
/// Existing calls such as /Autopayment.aspx?autorun=1&amp;hotelId=... continue to work.
/// </summary>
[AllowAnonymous]
public sealed class AutoPaymentController : Controller
{
    private readonly AutoPaymentService _service;
    private readonly ILogger<AutoPaymentController> _logger;

    public AutoPaymentController(
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory,
        ILoggerFactory loggerFactory,
        ILogger<AutoPaymentController> logger)
    {
        _logger = logger;
        _service = new AutoPaymentService(
            configuration,
            httpClientFactory,
            loggerFactory.CreateLogger<AutoPaymentService>());
    }

    [HttpGet("/Autopayment")]
    [HttpGet("/Autopayment.aspx")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> Index(
        string? autorun,
        string? hotelId,
        string? bookingId,
        string? regId,
        string? src,
        string? amount,
        CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store, no-cache";

        if (!string.Equals((autorun ?? string.Empty).Trim(), "1", StringComparison.Ordinal))
        {
            return View(new AutoPaymentPageViewModel
            {
                Attempted = false,
                Success = false,
                Status = "Ready",
                Message = "Automatic payment is ready. No charge was requested."
            });
        }

        try
        {
            var result = await _service.RunChargeFlowAsync(
                hotelId ?? string.Empty,
                bookingId ?? string.Empty,
                regId ?? string.Empty,
                src ?? string.Empty,
                amount ?? string.Empty,
                cancellationToken);

            return View(result);
        }
        catch (OperationCanceledException)
        {
            return new EmptyResult();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Autopayment MVC page failed. Hotel={HotelId}, Reg={RegId}", hotelId, regId);
            return View(new AutoPaymentPageViewModel
            {
                Attempted = true,
                Success = false,
                Status = "error",
                Message = "Automatic payment could not be completed.",
                RegId = regId ?? string.Empty
            });
        }
    }
}
