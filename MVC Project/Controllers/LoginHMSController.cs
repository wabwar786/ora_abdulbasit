using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orapmshms.Models;
using Orapmshms.Services;

namespace Orapmshms.Controllers;

[AllowAnonymous]
public sealed class LoginHMSController : Controller
{
    private readonly ILoginService _loginService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<LoginHMSController> _logger;

    public LoginHMSController(
        ILoginService loginService,
        IConfiguration configuration,
        ILogger<LoginHMSController> logger)
    {
        _loginService = loginService;
        _configuration = configuration;
        _logger = logger;
    }

    [HttpGet("/")]
    [HttpGet("/LoginHMS")]
    [HttpGet("/loginHMS.aspx")]
    public async Task<IActionResult> Index(string? returnUrl = null, string? msg = null)
    {
        await ClearAuthSessionAsync();

        return View(new LoginPageViewModel
        {
            ReturnUrl = returnUrl ?? string.Empty,
            Message = string.IsNullOrWhiteSpace(msg) ? string.Empty : Uri.UnescapeDataString(msg),
            MessageIsSuccess = false
        });
    }

    [ValidateAntiForgeryToken]
    [HttpPost("/LoginHMS")]
    [HttpPost("/loginHMS.aspx")]
    public async Task<IActionResult> Index(
        LoginPageViewModel model,
        string submitAction = "login",
        CancellationToken cancellationToken = default)
    {
        model.EmailOrUsername = Clean(model.EmailOrUsername, 256);
        model.Password ??= string.Empty;
        model.ReturnUrl ??= string.Empty;

        var clientIp = GetClientIpAddress();

        if (string.Equals(submitAction, "forgot", StringComparison.OrdinalIgnoreCase))
        {
            var reset = await _loginService.SendPasswordResetLinkAsync(
                model.EmailOrUsername,
                clientIp,
                cancellationToken);

            model.Password = string.Empty;
            model.Message = reset.Message;
            model.MessageIsSuccess = reset.Success;
            return View(model);
        }

        var login = await _loginService.AuthenticateAsync(
            model.EmailOrUsername,
            model.Password,
            clientIp,
            model.ReturnUrl,
            cancellationToken);

        model.Password = string.Empty;

        if (!login.Success || login.Account is null)
        {
            model.Message = login.Message;
            model.MessageIsSuccess = false;
            return View(model);
        }

        SetLoginSession(login.Account);
        await SignInCookieAsync(login.Account);

        var migratedReturnUrl = GetSafeMigratedReturnUrl(model.ReturnUrl);
        if (!string.IsNullOrWhiteSpace(migratedReturnUrl))
            return Redirect(migratedReturnUrl);

        // Known converted pages stay inside ASP.NET Core MVC.
        if (IsYourHotelsTarget(login.RedirectUrl))
            return RedirectToAction("Index", "YourHotels");

        if (IsDashboardTarget(login.RedirectUrl))
            return RedirectToAction("Index", "Dashboard");

        if (IsCalendarTarget(login.RedirectUrl))
            return RedirectToAction("Index", "Calendar");

        if (IsCreateReservationTarget(login.RedirectUrl))
            return RedirectToAction("Index", "CreateReservation");

        var redirectToLegacy = _configuration.GetValue("Login:RedirectToLegacyPageAfterLogin", false);
        if (redirectToLegacy && !string.IsNullOrWhiteSpace(login.RedirectUrl))
            return Redirect(login.RedirectUrl);

        TempData["LegacyTargetUrl"] = login.RedirectUrl;
        return RedirectToAction(nameof(Success));
    }

    [HttpGet("/LoginHMS/Success")]
    public IActionResult Success()
    {
        if (string.IsNullOrWhiteSpace(HttpContext.Session.GetString("UserId")))
            return RedirectToAction(nameof(Index));

        ViewBag.UserName = HttpContext.Session.GetString("UserName") ?? string.Empty;
        ViewBag.HotelName = HttpContext.Session.GetString("HotelName") ?? string.Empty;
        ViewBag.TargetUrl = TempData["LegacyTargetUrl"]?.ToString() ?? string.Empty;
        return View();
    }

    [HttpPost("/LoginHMS/Logout")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        await ClearAuthSessionAsync();
        return RedirectToAction(nameof(Index));
    }

    [Route("/LoginHMS/Error")]
    public IActionResult Error()
    {
        return View("Index", new LoginPageViewModel
        {
            Message = "Unable to login. Please try again.",
            MessageIsSuccess = false
        });
    }

    private void SetLoginSession(LoginAccount user)
    {
        HttpContext.Session.SetString("UserId", user.UserId);
        HttpContext.Session.SetString("HotelName", user.HotelName);
        HttpContext.Session.SetString("UserName", user.UserName);
        HttpContext.Session.SetString("hotel", user.HotelId);
        HttpContext.Session.SetString("Role", user.Role);
        HttpContext.Session.SetString("HotelRole", user.HotelRole);
    }

    private async Task SignInCookieAsync(LoginAccount user)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.UserId),
            new(ClaimTypes.Name, user.UserName),
            new(ClaimTypes.Role, user.Role),
            new("hotel_id", user.HotelId),
            new("hotel_role", user.HotelRole),
            new("hotel_name", user.HotelName)
        };

        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = false });
    }

    private async Task ClearAuthSessionAsync()
    {
        // Match the Web Forms master-page logout behavior: clear the entire session.
        HttpContext.Session.Clear();

        if (User.Identity?.IsAuthenticated == true)
        {
            try
            {
                await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Cookie sign-out during login page initialization failed.");
            }
        }
    }

    private static string GetSafeMigratedReturnUrl(string? returnUrl)
    {
        if (string.IsNullOrWhiteSpace(returnUrl))
            return string.Empty;

        var value = Uri.UnescapeDataString(returnUrl).Trim();
        if (value.StartsWith("//", StringComparison.Ordinal) ||
            value.StartsWith("\\", StringComparison.Ordinal) ||
            value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        var normalized = value.StartsWith('/') ? value : "/" + value;
        var queryIndex = normalized.IndexOf('?');
        var path = queryIndex >= 0 ? normalized[..queryIndex] : normalized;
        var query = queryIndex >= 0 ? normalized[queryIndex..] : string.Empty;

        var isYourHotels = string.Equals(path, "/YourHotels", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(path, "/YourHotels.aspx", StringComparison.OrdinalIgnoreCase);
        var isDashboard = string.Equals(path, "/Dashboard", StringComparison.OrdinalIgnoreCase) ||
                          string.Equals(path, "/dashboard.aspx", StringComparison.OrdinalIgnoreCase);
        var isCalendar = string.Equals(path, "/Calendar", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(path, "/Calendar/Index", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(path, "/FrontDeskCalender", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(path, "/FrontDeskCalender.aspx", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(path, "/FrontDeskCalendar", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(path, "/FrontDeskCalendar.aspx", StringComparison.OrdinalIgnoreCase);
        var isCreateReservation = string.Equals(path, "/CreateReservation", StringComparison.OrdinalIgnoreCase) ||
                                  string.Equals(path, "/ExtendedReservation", StringComparison.OrdinalIgnoreCase) ||
                                  string.Equals(path, "/ExtendedReservation.aspx", StringComparison.OrdinalIgnoreCase);
        if (!isYourHotels && !isDashboard && !isCalendar && !isCreateReservation)
            return string.Empty;

        // Legacy identity/hotel query values are intentionally discarded. The authenticated
        // cookie/session is authoritative after login.
        if (isYourHotels && !string.IsNullOrWhiteSpace(query))
        {
            var parsed = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(query);
            if (parsed.TryGetValue("year", out var yearValue) &&
                int.TryParse(yearValue.ToString(), out var year) &&
                year is >= 1900 and <= 2050)
            {
                return "/YourHotels?year=" + year;
            }
        }

        if (isCalendar)
        {
            if (!string.IsNullOrWhiteSpace(query))
            {
                var parsed = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(query);
                var allowed = new List<string>();
                if (parsed.TryGetValue("start", out var startValue))
                    allowed.Add("start=" + Uri.EscapeDataString(startValue.ToString()));
                if (parsed.TryGetValue("end", out var endValue))
                    allowed.Add("end=" + Uri.EscapeDataString(endValue.ToString()));
                return allowed.Count == 0 ? "/Calendar" : "/Calendar?" + string.Join("&", allowed);
            }
            return "/Calendar";
        }

        if (isCreateReservation) return "/CreateReservation";
        return isDashboard ? "/Dashboard" : "/YourHotels";
    }

    private static bool IsYourHotelsTarget(string? targetUrl)
    {
        if (string.IsNullOrWhiteSpace(targetUrl))
            return false;

        var path = targetUrl.Trim();
        var queryIndex = path.IndexOf('?');
        if (queryIndex >= 0)
            path = path[..queryIndex];

        path = path.Trim().Trim('/');
        var lastSlash = path.LastIndexOf('/');
        if (lastSlash >= 0)
            path = path[(lastSlash + 1)..];

        return string.Equals(path, "YourHotels.aspx", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(path, "YourHotels", StringComparison.OrdinalIgnoreCase);
    }


    private static bool IsDashboardTarget(string? targetUrl)
    {
        if (string.IsNullOrWhiteSpace(targetUrl))
            return false;

        var path = targetUrl.Trim();
        var queryIndex = path.IndexOf('?');
        if (queryIndex >= 0) path = path[..queryIndex];
        path = path.Trim().Trim('/');
        var lastSlash = path.LastIndexOf('/');
        if (lastSlash >= 0) path = path[(lastSlash + 1)..];

        return string.Equals(path, "dashboard.aspx", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(path, "Dashboard", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCalendarTarget(string? targetUrl)
    {
        if (string.IsNullOrWhiteSpace(targetUrl))
            return false;

        var path = targetUrl.Trim();
        var queryIndex = path.IndexOf('?');
        if (queryIndex >= 0) path = path[..queryIndex];
        path = path.Trim().Trim('/');
        var lastSlash = path.LastIndexOf('/');
        if (lastSlash >= 0) path = path[(lastSlash + 1)..];

        return string.Equals(path, "FrontDeskCalender.aspx", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(path, "FrontDeskCalender", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(path, "FrontDeskCalendar.aspx", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(path, "FrontDeskCalendar", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(path, "Calendar", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCreateReservationTarget(string? targetUrl)
    {
        if (string.IsNullOrWhiteSpace(targetUrl)) return false;
        var path = targetUrl.Trim();
        var queryIndex = path.IndexOf('?');
        if (queryIndex >= 0) path = path[..queryIndex];
        path = path.Trim().Trim('/');
        var lastSlash = path.LastIndexOf('/');
        if (lastSlash >= 0) path = path[(lastSlash + 1)..];
        return string.Equals(path, "ExtendedReservation.aspx", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(path, "ExtendedReservation", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(path, "CreateReservation", StringComparison.OrdinalIgnoreCase);
    }

    private string GetClientIpAddress()
    {
        var forwardedFor = Request.Headers["X-Forwarded-For"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(forwardedFor))
        {
            var first = forwardedFor
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
            if (!string.IsNullOrWhiteSpace(first))
                return Clean(first, 100);
        }

        return Clean(HttpContext.Connection.RemoteIpAddress?.ToString(), 100);
    }

    private static string Clean(string? value, int maxLength)
    {
        value = value?.Trim() ?? string.Empty;
        return value.Length > maxLength ? value[..maxLength] : value;
    }
}
