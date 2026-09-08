using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orapmshms.Services;

namespace Orapmshms.Controllers;

[Authorize]
public sealed class PmsMasterController : Controller
{
    private readonly IPmsMasterService _masterService;
    private readonly ILogger<PmsMasterController> _logger;

    public PmsMasterController(
        IPmsMasterService masterService,
        ILogger<PmsMasterController> logger)
    {
        _masterService = masterService;
        _logger = logger;
    }

    [HttpPost("/PmsMaster/ChangeProperty")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ChangeProperty(
        string hotelId,
        string? returnUrl,
        CancellationToken cancellationToken = default)
    {
        var userId = HttpContext.Session.GetString("UserId")?.Trim() ?? string.Empty;
        if (userId.Length == 0)
            return Redirect("/LoginHMS");

        var result = await _masterService.SwitchPropertyAsync(userId, hotelId, cancellationToken);
        if (!result.Success)
        {
            TempData["PmsMasterMessage"] = result.Message;
            TempData["PmsMasterMessageType"] = "error";
            return Redirect(SafeReturnUrl(returnUrl, "/Dashboard"));
        }

        HttpContext.Session.SetString("UserId", result.UserId);
        HttpContext.Session.SetString("UserName", result.UserName);
        HttpContext.Session.SetString("hotel", result.HotelId);
        HttpContext.Session.SetString("HotelName", result.HotelName);
        HttpContext.Session.SetString("Role", result.Role);
        HttpContext.Session.SetString("HotelRole", result.HotelRole);
        HttpContext.Session.Remove("currency_symbol");

        await RefreshAuthenticationCookieAsync(result);

        return Redirect("/Dashboard");
    }

    [HttpPost("/PmsMaster/ChangePassword")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ChangePassword(
        string currentPassword,
        string newPassword,
        string confirmPassword,
        string? returnUrl,
        CancellationToken cancellationToken = default)
    {
        var userId = HttpContext.Session.GetString("UserId")?.Trim() ?? string.Empty;
        var hotelId = HttpContext.Session.GetString("hotel")?.Trim() ?? string.Empty;

        var result = await _masterService.ChangePasswordAsync(
            userId,
            hotelId,
            currentPassword,
            newPassword,
            confirmPassword,
            cancellationToken);

        TempData["PmsMasterMessage"] = result.Message;
        TempData["PmsMasterMessageType"] = result.Success ? "success" : "error";
        TempData["PmsOpenChangePassword"] = result.Success ? "0" : "1";

        return Redirect(SafeReturnUrl(returnUrl, "/Dashboard"));
    }

    [HttpPost("/PmsMaster/DismissNotification")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DismissNotification(
        int notificationId,
        string? returnUrl,
        CancellationToken cancellationToken = default)
    {
        var userId = HttpContext.Session.GetString("UserId")?.Trim() ?? string.Empty;
        var hotelId = HttpContext.Session.GetString("hotel")?.Trim() ?? string.Empty;
        if (userId.Length == 0 || hotelId.Length == 0)
            return Redirect("/LoginHMS");

        try
        {
            await _masterService.DismissNotificationAsync(
                userId,
                hotelId,
                notificationId,
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unable to dismiss notification {NotificationId}", notificationId);
        }

        TempData["PmsOpenNotifications"] = "1";
        return Redirect(SafeReturnUrl(returnUrl, "/Dashboard"));
    }

    [HttpPost("/PmsMaster/ClearNotifications")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ClearNotifications(
        string? returnUrl,
        CancellationToken cancellationToken = default)
    {
        var userId = HttpContext.Session.GetString("UserId")?.Trim() ?? string.Empty;
        var hotelId = HttpContext.Session.GetString("hotel")?.Trim() ?? string.Empty;
        var role = HttpContext.Session.GetString("Role")?.Trim() ?? string.Empty;
        if (userId.Length == 0 || hotelId.Length == 0)
            return Redirect("/LoginHMS");

        try
        {
            await _masterService.ClearNotificationsAsync(
                userId,
                hotelId,
                role,
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unable to clear notifications for user {UserId}", userId);
        }

        TempData["PmsOpenNotifications"] = "1";
        return Redirect(SafeReturnUrl(returnUrl, "/Dashboard"));
    }

    private async Task RefreshAuthenticationCookieAsync(Orapmshms.Models.PmsPropertySwitchResult account)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, account.UserId),
            new(ClaimTypes.Name, account.UserName),
            new(ClaimTypes.Role, account.Role),
            new("hotel_id", account.HotelId),
            new("hotel_role", account.HotelRole),
            new("hotel_name", account.HotelName)
        };

        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = false });
    }

    private string SafeReturnUrl(string? returnUrl, string fallback)
    {
        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
            return returnUrl;
        return fallback;
    }
}
