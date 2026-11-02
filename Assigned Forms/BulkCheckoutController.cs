using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Orapmshms.Models;
using Orapmshms.Services;

namespace Orapmshms.Controllers;

/// <summary>
/// Bulk Check-out — ASP.NET Core port of CheckinList.aspx.
/// Session, account and subscription are checked by the global PmsMasterActionFilter.
/// Hotel and user always come from the signed-in session (IPmsMasterService), never from the URL.
/// </summary>
[Authorize]
public sealed class BulkCheckoutController : Controller
{
    private const string ResultTempDataKey = "BulkCheckoutResult";

    private readonly IPmsMasterService _masterService;
    private readonly IHotelClock _hotelClock;
    private readonly IBulkCheckoutService _service;
    private readonly IBulkCheckoutIntegration _integration;
    private readonly IConfiguration _configuration;
    private readonly ILogger<BulkCheckoutController> _logger;

    public BulkCheckoutController(
        IPmsMasterService masterService,
        IHotelClock hotelClock,
        IConfiguration configuration,
        ILoggerFactory loggerFactory,
        IServiceProvider services,
        IWebHostEnvironment environment)
    {
        _masterService = masterService;
        _hotelClock = hotelClock;
        _configuration = configuration;
        _logger = loggerFactory.CreateLogger<BulkCheckoutController>();

        // Uses DI registrations when they exist; otherwise the defaults, so Program.cs does not have to change.
        _integration = services.GetService<IBulkCheckoutIntegration>()
            ?? new BulkCheckoutIntegration(loggerFactory.CreateLogger<BulkCheckoutIntegration>());
        // Sample data only when running locally (Development) - see BulkCheckoutMockService.
        var useMockData = environment.IsDevelopment() && configuration.GetValue("BulkCheckout:UseMockData", true);

        _service = services.GetService<IBulkCheckoutService>()
            ?? (useMockData
                ? new BulkCheckoutMockService(hotelClock)
                : new BulkCheckoutService(configuration, hotelClock, _integration, loggerFactory.CreateLogger<BulkCheckoutService>()));
    }

    /// <summary>Old menu links (CheckinList.aspx?UD=...&hd=...) land here. The query values are ignored.</summary>
    [HttpGet("/CheckinList.aspx")]
    [HttpGet("/CheckinList")]
    public IActionResult LegacyIndex() => RedirectToAction(nameof(Index));

    [HttpGet("/BulkCheckout")]
    public async Task<IActionResult> Index(string? date = null, string? q = null, CancellationToken cancellationToken = default)
    {
        var master = await _masterService.GetOrBuildAsync(HttpContext, cancellationToken);
        if (!master.IsValidSession || string.IsNullOrWhiteSpace(master.HotelId))
            return RedirectToLogin();

        var today = _hotelClock.GetHotelToday(master.HotelId);
        var search = (q ?? string.Empty).Trim();
        if (search.Length > 100) search = search[..100];

        var model = new BulkCheckoutPageViewModel
        {
            Search = search,
            HotelToday = today,
            SelectedDate = ParseDate(date, today),
            CurrencySymbol = CurrencySymbol(master),
            LegacyBaseUrl = LegacyBaseUrl(absolute: false),
            SecuritySettlementAvailable = _integration.SecuritySettlementAvailable,
            Result = ReadResult()
        };

        try
        {
            model.Rows = await _service.GetDeparturesAsync(master.HotelId, model.SelectedDate, search, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Bulk checkout list failed for hotel {HotelId}.", master.HotelId);
            model.LoadFailed = true;
        }

        ViewData["Title"] = "Bulk Check-out";
        return View(model);
    }

    /// <summary>Bulk check-out. Only payment id|reg id keys are posted; everything else is read again from the database.</summary>
    [HttpPost("/BulkCheckout/CheckOut")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CheckOut(
        [FromForm(Name = "rooms")] string[]? rooms,
        [FromForm(Name = "refundSecurity")] bool refundSecurity,
        [FromForm(Name = "date")] string? date,
        [FromForm(Name = "q")] string? q,
        CancellationToken cancellationToken = default)
    {
        var master = await _masterService.GetOrBuildAsync(HttpContext, cancellationToken);
        if (!master.IsValidSession || string.IsNullOrWhiteSpace(master.HotelId))
            return RedirectToLogin();

        BulkCheckoutResult result;
        if (rooms is null || rooms.Length == 0)
        {
            result = new BulkCheckoutResult();
        }
        else
        {
            try
            {
                result = await _service.CheckOutAsync(BuildUser(master), rooms, refundSecurity, LegacyBaseUrl(absolute: true), cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Bulk checkout failed for hotel {HotelId}.", master.HotelId);
                result = new BulkCheckoutResult { Selected = rooms.Length, Failed = rooms.Length };
            }
        }

        // Post / Redirect / Get: a browser refresh can never submit the checkout twice.
        TempData[ResultTempDataKey] = JsonSerializer.Serialize(result);
        return RedirectToAction(nameof(Index), new
        {
            date = string.IsNullOrWhiteSpace(date) ? null : date.Trim(),
            q = string.IsNullOrWhiteSpace(q) ? null : q.Trim()
        });
    }

    /// <summary>Manual security settlement from the row button. JSON; anti-forgery token in the RequestVerificationToken header.</summary>
    [HttpPost("/BulkCheckout/SettleSecurity")]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting("api")]
    public async Task<IActionResult> SettleSecurity([FromBody] BulkCheckoutSettleRequest? request, CancellationToken cancellationToken = default)
    {
        var master = await _masterService.GetOrBuildAsync(HttpContext, cancellationToken);
        if (!master.IsValidSession || string.IsNullOrWhiteSpace(master.HotelId))
            return Unauthorized();

        if (request is null || string.IsNullOrWhiteSpace(request.RegId))
            return Json(new BulkCheckoutActionResponse { Message = "Missing reservation." });

        var response = await _service.SettleSecurityAsync(BuildUser(master), request.RegId, request.ReleaseAmount, cancellationToken);
        return Json(response);
    }

    // ------------------------------------------------------------------
    private static DateTime ParseDate(string? value, DateTime today)
    {
        // Sane window: one year either side of the hotel's today. Anything else falls back to today.
        if (DateTime.TryParseExact((value ?? string.Empty).Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) &&
            d.Date >= today.AddYears(-1) && d.Date <= today.AddYears(1))
        {
            return d.Date;
        }
        return today.Date;
    }

    private BulkCheckinUserContext BuildUser(PmsMasterViewModel master) => new(
        HotelId: master.HotelId,
        UserId: master.UserId,
        UserName: master.UserName,
        IdentityName: User?.Identity?.Name ?? master.UserName,
        IpAddress: ClientIp(),
        SystemName: Environment.MachineName);

    private string ClientIp()
    {
        var forwarded = Request.Headers["X-Forwarded-For"].ToString();
        var first = forwarded.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        var ip = !string.IsNullOrWhiteSpace(first) && System.Net.IPAddress.TryParse(first, out var parsed)
            ? parsed.ToString()
            : HttpContext.Connection.RemoteIpAddress?.ToString() ?? string.Empty;
        return ip.Length > 45 ? ip[..45] : ip;
    }

    private string CurrencySymbol(PmsMasterViewModel master)
    {
        if (!string.IsNullOrWhiteSpace(master.CurrencySymbol)) return master.CurrencySymbol.Trim();
        var fromSession = HttpContext.Session.GetString("currency_symbol");
        if (!string.IsNullOrWhiteSpace(fromSession)) return fromSession.Trim();
        return (_configuration["Dashboard:CurrencySymbol"] ?? string.Empty).Trim();
    }

    /// <summary>
    /// Legacy:BaseUrl for pages that are still Web Forms (RegistrationCard, InvoiceRecieving, Feedback).
    /// For e-mail links an absolute URL is needed, so the current site address is used when the setting is empty.
    /// </summary>
    private string LegacyBaseUrl(bool absolute)
    {
        var baseUrl = (_configuration["Legacy:BaseUrl"] ?? string.Empty).Trim();
        if (baseUrl.Length == 0 && absolute)
            baseUrl = Request.Scheme + "://" + Request.Host + Request.PathBase;
        return baseUrl.Length == 0 || baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/";
    }

    private BulkCheckoutResult? ReadResult()
    {
        if (TempData[ResultTempDataKey] is not string json || json.Length == 0) return null;
        try { return JsonSerializer.Deserialize<BulkCheckoutResult>(json); }
        catch { return null; }
    }

    private IActionResult RedirectToLogin()
    {
        var returnUrl = Uri.EscapeDataString(Request.Path + Request.QueryString);
        return Redirect("/LoginHMS?returnUrl=" + returnUrl);
    }
}



//comment