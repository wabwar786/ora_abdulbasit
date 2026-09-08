using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orapmshms.Models;
using Orapmshms.Services;

namespace Orapmshms.Controllers;

[Authorize]
public sealed class YourHotelsController : Controller
{
    private readonly IYourHotelsService _yourHotelsService;
    private readonly ILogger<YourHotelsController> _logger;

    public YourHotelsController(
        IYourHotelsService yourHotelsService,
        ILogger<YourHotelsController> logger)
    {
        _yourHotelsService = yourHotelsService;
        _logger = logger;
    }

    [HttpGet("/YourHotels")]
    [HttpGet("/YourHotels.aspx")]
    public async Task<IActionResult> Index(
        int? year = null,
        string? UD = null,
        CancellationToken cancellationToken = default)
    {
        var userId = GetAuthenticatedUserId();
        if (string.IsNullOrWhiteSpace(userId))
            return RedirectToLoginWithReturnUrl();

        if (!string.IsNullOrWhiteSpace(UD))
        {
            var decodedUserId = DecodeBase64Safe(UD);
            if (string.IsNullOrWhiteSpace(decodedUserId) ||
                !string.Equals(decodedUserId, userId, StringComparison.Ordinal))
            {
                return Forbid();
            }
        }

        try
        {
            var model = await _yourHotelsService.BuildPageAsync(userId, year, cancellationToken);
            return View(model);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unable to load YourHotels for user {UserId}", userId);

            return View(new YourHotelsPageViewModel
            {
                SelectedYear = year ?? DateTime.Today.Year,
                Years = Enumerable.Range(DateTime.Today.Year - 6, Math.Max(1, 2050 - (DateTime.Today.Year - 6) + 1)).ToList(),
                OccupancyLabels = new[] { "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec" },
                BookingSourceLabels = new[]
                {
                    "January", "February", "March", "April", "May", "June",
                    "July", "August", "September", "October", "November", "December"
                },
                Message = "Error: " + ex.Message
            });
        }
    }

    [ValidateAntiForgeryToken]
    [HttpPost("/YourHotels/OpenHotel")]
    public async Task<IActionResult> OpenHotel(
        string hotelId,
        CancellationToken cancellationToken = default)
    {
        var userId = GetAuthenticatedUserId();
        if (string.IsNullOrWhiteSpace(userId))
            return OpenHotelError("Your login session has expired. Please sign in again.");

        try
        {
            var result = await _yourHotelsService.OpenHotelAsync(userId, hotelId, cancellationToken);
            if (!result.Success || result.Account is null || string.IsNullOrWhiteSpace(result.RedirectUrl))
                return OpenHotelError(result.Message);

            // Keep the same selected-property session values used by the Web Forms page.
            HttpContext.Session.SetString("UserId", result.Account.UserId);
            HttpContext.Session.SetString("HotelName", result.Account.Email);
            HttpContext.Session.SetString("UserName", result.Account.UserName);
            HttpContext.Session.SetString("hotel", result.Account.HotelId);
            HttpContext.Session.SetString("Role", result.Account.Role);
            HttpContext.Session.SetString("HotelRole", result.Account.HotelRole);

            return Redirect(result.RedirectUrl);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unable to open hotel {HotelId} for user {UserId}", hotelId, userId);
            return OpenHotelError("Error: " + ex.Message);
        }
    }

    private string GetAuthenticatedUserId()
    {
        var sessionUserId = HttpContext.Session.GetString("UserId")?.Trim();
        var claimUserId = User.FindFirstValue(ClaimTypes.NameIdentifier)?.Trim();

        if (!string.IsNullOrWhiteSpace(sessionUserId) &&
            !string.IsNullOrWhiteSpace(claimUserId) &&
            !string.Equals(sessionUserId, claimUserId, StringComparison.Ordinal))
        {
            return string.Empty;
        }

        var userId = !string.IsNullOrWhiteSpace(sessionUserId) ? sessionUserId : claimUserId;
        if (!string.IsNullOrWhiteSpace(userId) && string.IsNullOrWhiteSpace(sessionUserId))
            HttpContext.Session.SetString("UserId", userId);

        return userId ?? string.Empty;
    }

    private IActionResult RedirectToLoginWithReturnUrl()
    {
        var returnUrl = Uri.EscapeDataString(Request.Path + Request.QueryString);
        return Redirect("/LoginHMS?returnUrl=" + returnUrl);
    }

    private IActionResult OpenHotelError(string? message)
    {
        ViewBag.Message = string.IsNullOrWhiteSpace(message)
            ? "Unable to open this hotel dashboard."
            : message;
        return View("OpenHotelError");
    }

    private static string DecodeBase64Safe(string value)
    {
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(value));
        }
        catch
        {
            return string.Empty;
        }
    }
}
