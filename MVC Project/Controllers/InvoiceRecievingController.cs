#nullable enable

using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orapmshms.Models;
using Orapmshms.Services;
namespace Orapmshms.Controllers;
[Route("Invoice")]
[Route("InvoiceRecieving")]
public sealed class InvoiceRecievingController : Controller
{
    private readonly IServiceProvider _services;
    private readonly IConfiguration _configuration;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<InvoiceRecievingController> _logger;
    private readonly IDataProtector _invoiceShareProtector;

    private const string InvoiceSharePurpose = "ORAPMS.InvoiceShare.v1";

    public InvoiceRecievingController(
        IServiceProvider services,
        IConfiguration configuration,
        ILoggerFactory loggerFactory,
        IDataProtectionProvider dataProtectionProvider)
    {
        _services = services;
        _configuration = configuration;
        _loggerFactory = loggerFactory;

        _logger = loggerFactory.CreateLogger<InvoiceRecievingController>();
        _invoiceShareProtector = dataProtectionProvider.CreateProtector(InvoiceSharePurpose);
    }
    private IInvoiceRecievingService Service =>
        _services.GetService<IInvoiceRecievingService>()
        ?? new InvoiceRecievingService(
               _configuration,
               _loggerFactory.CreateLogger<InvoiceRecievingService>(),
               _services.GetService<IInvoiceRecievingHelper>(),
               _services.GetService<IHttpContextAccessor>()
                   ?? new HttpContextAccessor { HttpContext = HttpContext });

    [HttpGet("")]
    [HttpGet("/InvoiceRecieving.aspx")]
    [HttpGet("/InvoiceRecieving")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        // The reservation list sends regId; the old links send reg_id.
        string? regId = GetParam("regId") ?? GetParam("reg_id");
        string? hotelId = GetParam("hotel_id") ?? GetParam("hotelId");
        if (string.IsNullOrWhiteSpace(hotelId))
            hotelId = HttpContext.Session.GetString("hotel");
        string? src = GetParam("src");
        try
        {
            InvoiceRecievingPageViewModel model =
                await Service.GetInvoiceAsync(hotelId, regId, src, ct).ConfigureAwait(false);
            return View(model);
        }
        catch (OperationCanceledException)
        {
            // The browser went away mid-request; nothing to report.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "InvoiceRecieving: invoice could not be built for {RegId}.", regId);

            return View(new InvoiceRecievingPageViewModel
            {
                EmptyReason = "The invoice could not be loaded: " + ex.Message
            });
        }
    }

    /// <summary>
    /// Calendar-only bridge: the reservation id is posted in the request body,
    /// then the browser is redirected to a clean, shareable protected-token URL.
    /// Nothing sensitive is placed in the query string.
    /// </summary>
    [HttpPost("OpenShare")]
    [ValidateAntiForgeryToken]
    public IActionResult OpenShare([FromForm] string? regId)
    {
        regId = (regId ?? string.Empty).Trim();
        string hotelId = (HttpContext.Session.GetString("hotel") ?? string.Empty).Trim();

        if (regId.Length == 0 || hotelId.Length == 0)
            return BadRequest("The invoice reference is missing.");

        string token = ProtectInvoiceReference(hotelId, regId);
        return Redirect(BuildSharePath(token));
    }

    /// <summary>
    /// Public/shareable invoice URL. The path contains only a protected token;
    /// hotel_id and reg_id are recovered server-side and never appear in the URL.
    /// No database token record is required.
    /// </summary>
    [AllowAnonymous]
    [HttpGet("i/{token}")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> Share(string token, CancellationToken ct)
    {
        if (!TryUnprotectInvoiceReference(token, out string hotelId, out string regId))
            return NotFound();

        try
        {
            InvoiceRecievingPageViewModel model =
                await Service.GetInvoiceAsync(hotelId, regId, null, ct).ConfigureAwait(false);

            return View("Index", model);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "InvoiceRecieving: shared invoice could not be built for {RegId}.", regId);

            return View("Index", new InvoiceRecievingPageViewModel
            {
                EmptyReason = "The invoice could not be loaded: " + ex.Message
            });
        }
    }

    private string ProtectInvoiceReference(string hotelId, string regId)
    {
        // New-line separator keeps the payload compact and is safe for these identifiers.
        return _invoiceShareProtector.Protect(hotelId.Trim() + "\n" + regId.Trim());
    }

    private bool TryUnprotectInvoiceReference(
        string? token,
        out string hotelId,
        out string regId)
    {
        hotelId = string.Empty;
        regId = string.Empty;

        if (string.IsNullOrWhiteSpace(token))
            return false;

        try
        {
            string payload = _invoiceShareProtector.Unprotect(token.Trim());
            int separator = payload.IndexOf('\n');

            if (separator <= 0 || separator >= payload.Length - 1)
                return false;

            hotelId = payload.Substring(0, separator).Trim();
            regId = payload.Substring(separator + 1).Trim();

            return hotelId.Length > 0 && regId.Length > 0;
        }
        catch
        {
            // Invalid, truncated or tampered token.
            return false;
        }
    }

    private string BuildSharePath(string token)
    {
        string pathBase = Request.PathBase.HasValue ? Request.PathBase.Value! : string.Empty;
        return pathBase.TrimEnd('/') + "/InvoiceRecieving/i/" + Uri.EscapeDataString(token);
    }

    private string? GetParam(string key)
    {
        string? raw = Request.Query[key];
        if (string.IsNullOrWhiteSpace(raw)) return null;

        string plain = Uri.UnescapeDataString(raw).Trim();

        if (TryBase64Decode(raw, out string? decoded) && IsReadable(decoded))
            return decoded!.Trim();

        return plain;
    }

    private static bool TryBase64Decode(string? input, out string? decoded)
    {
        decoded = null;
        if (string.IsNullOrWhiteSpace(input)) return false;

        string s = Uri.UnescapeDataString(input);

        s = s.Trim().Replace(' ', '+').Replace('-', '+').Replace('_', '/');

        // A length that leaves a remainder of 1 is never valid base64.
        if (s.Length % 4 == 1) return false;

        int pad = 4 - (s.Length % 4);
        if (pad > 0 && pad < 4)
            s += new string('=', pad);

        try
        {
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(s));
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Printable text, which is what a reg id, a hotel id and src all are.
    ///
    /// This is the test the decode was missing. Anything carrying a control
    /// character, a replacement character or a byte outside printable ASCII
    /// was not text to begin with, so the value was never base64 and the raw
    /// one is the right answer.
    /// </summary>
    private static bool IsReadable(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        foreach (char c in text!)
        {
            if (c < 0x20 || c == 0x7F || c > 0x7E) return false;
        }

        return true;
    }
}
