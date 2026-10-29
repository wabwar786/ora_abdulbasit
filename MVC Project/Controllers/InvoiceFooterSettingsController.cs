using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Orapmshms.Models;
using Orapmshms.Services;

namespace Orapmshms.Controllers;

/// <summary>
/// MVC replacement for InvoiceFooterSettings.aspx.
/// No Program.cs dependency injection changes are needed.
/// PmsMasterActionFilter continues to validate the authenticated account,
/// selected hotel access, and subscription before these actions execute.
/// </summary>
[Authorize]
[Route("InvoiceFooterSettings")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class InvoiceFooterSettingsController : Controller
{
    private readonly InvoiceFooterSettingsService _settings;
    private readonly ILogger<InvoiceFooterSettingsController> _logger;

    public InvoiceFooterSettingsController(
        IConfiguration configuration,
        ILogger<InvoiceFooterSettingsController> logger)
    {
        _settings = new InvoiceFooterSettingsService(configuration);
        _logger = logger;
    }

    [HttpGet("")]
    [HttpGet("Index")]
    [HttpGet("/InvoiceFooterSettings.aspx")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var hotelId = GetCurrentHotelId();
        if (hotelId.Length == 0) return RedirectToLogin();

        try
        {
            var data = await _settings.GetAsync(hotelId, cancellationToken);
            if (data == null)
                return View(new InvoiceFooterSettingsViewModel
                {
                    CanEdit = false,
                    ErrorMessage = "This property was not found. Please select another hotel."
                });

            return View(new InvoiceFooterSettingsViewModel
            {
                HotelName = data.HotelName,
                Instructions = data.Instructions
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unable to load invoice footer settings for hotel {HotelId}.", hotelId);
            return View(new InvoiceFooterSettingsViewModel
            {
                CanEdit = false,
                ErrorMessage = "Unable to load invoice settings. Please contact your administrator."
            });
        }
    }

    [HttpPost("Save")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(
        [FromForm] string? instructions, CancellationToken cancellationToken)
    {
        var hotelId = GetCurrentHotelId();
        if (hotelId.Length == 0) return RedirectToLogin();

        var normalized = (instructions ?? string.Empty).Replace("\r\n", "\n").Replace("\r", "\n");
        if (normalized.Length > InvoiceFooterSettingsViewModel.MaximumLength)
        {
            return await ReopenWithErrorAsync(
                hotelId, normalized, "Instructions cannot exceed 5,000 characters.", cancellationToken);
        }

        try
        {
            var updated = await _settings.SaveAsync(hotelId, normalized, cancellationToken);
            if (!updated)
                return await ReopenWithErrorAsync(
                    hotelId, normalized, "No property was updated. Please verify your hotel access.", cancellationToken);

            TempData["InvoiceFooterSuccess"] = "Invoice footer instructions saved successfully.";
            return RedirectToAction(nameof(Index)); // PRG avoids duplicate writes on refresh.
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unable to save invoice footer settings for hotel {HotelId}.", hotelId);
            return await ReopenWithErrorAsync(
                hotelId, normalized, "Unable to save instructions. Please try again.", cancellationToken);
        }
    }

    private async Task<IActionResult> ReopenWithErrorAsync(
        string hotelId, string instructions, string message, CancellationToken cancellationToken)
    {
        // Preserve edits if a save fails without trusting client-supplied hotel data.
        string hotelName = string.Empty;
        try
        {
            var data = await _settings.GetAsync(hotelId, cancellationToken);
            if (data != null) hotelName = data.HotelName;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to reload hotel name after invoice footer save failure.");
        }

        Response.StatusCode = 400;
        return View("Index", new InvoiceFooterSettingsViewModel
        {
            HotelName = hotelName,
            Instructions = instructions,
            ErrorMessage = message
        });
    }

    private string GetCurrentHotelId()
    {
        // Never use query-string hd/hotel_id, hidden fields or a posted hotel ID.
        // The PMS master filter validates the selected hotel's account scope.
        var userId = HttpContext.Session.GetString("UserId")?.Trim();
        var hotelId = HttpContext.Session.GetString("hotel")?.Trim();
        return string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(hotelId) || hotelId.Length > 50
            ? string.Empty : hotelId;
    }

    private IActionResult RedirectToLogin() =>
        Redirect("/LoginHMS?returnUrl=" + Uri.EscapeDataString(Request.Path + Request.QueryString));
}
