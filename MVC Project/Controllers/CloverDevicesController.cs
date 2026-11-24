#nullable enable

using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orapmshms.Models;
using Orapmshms.Services;

namespace Orapmshms.Controllers;

/// <summary>
/// Stripe Devices (the page's file name is CloverDevices).
///
///     GET  /CloverDevices?hd=&lt;base64 hotel id&gt;&amp;UN=&lt;base64 user&gt;
///     POST /CloverDevices/ConnectDevice     btnConnectDevice_Click
///     POST /CloverDevices/InstallTokenization   btn_installtokenizationapp
///     POST /CloverDevices/OpenUpdate        openpopupandgetdata
///     POST /CloverDevices/SaveDevice        UpdateDevice
///     POST /CloverDevices/ConnectReader     btnConnectReader_Click
///     POST /CloverDevices/CreateLocation    btnCreateLocation_Click
///     POST /CloverDevices/Delete            DeleteStripeReader
///
/// Every post ends in a redirect back to the page, so a refresh cannot repeat
/// an action - the Web Forms page had that problem and this does not.
/// </summary>
[Authorize]
[Route("CloverDevices")]
public sealed class CloverDevicesController : Controller
{
    private const string MessageKey = "CloverDevicesMessage";
    private const string MessageIsErrorKey = "CloverDevicesMessageIsError";
    private const string ModalKey = "CloverDevicesModal";
    private const string ReaderIdKey = "CloverDevicesReaderId";
    private const string ReaderNameKey = "CloverDevicesReaderName";
    private const string ShowRegCodeKey = "CloverDevicesShowRegCode";
    private const string ReaderIsUpdateKey = "CloverDevicesReaderIsUpdate";

    private readonly IServiceProvider _services;
    private readonly IConfiguration _configuration;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<CloverDevicesController> _logger;

    public CloverDevicesController(
        IServiceProvider services,
        IConfiguration configuration,
        ILoggerFactory loggerFactory)
    {
        _services = services;
        _configuration = configuration;
        _loggerFactory = loggerFactory;

        _logger = loggerFactory.CreateLogger<CloverDevicesController>();
    }

    // ------------------------------------------------- services without DI

    private ICloverDevicesStripeService Stripe =>
        _services.GetService<ICloverDevicesStripeService>()
        ?? new CloverDevicesStripeService(
               _loggerFactory.CreateLogger<CloverDevicesStripeService>());

    /// <summary>
    /// The service from the container when it is registered, built here when it
    /// is not - so the page never depends on a Program.cs line being added.
    /// </summary>
    private ICloverDevicesService Service =>
        _services.GetService<ICloverDevicesService>()
        ?? new CloverDevicesService(
               _configuration,
               Stripe,
               _loggerFactory.CreateLogger<CloverDevicesService>());

    private ICloverDevicesChannexService Channex =>
        _services.GetService<ICloverDevicesChannexService>()
        ?? new CloverDevicesChannexService(
               _configuration,
               _loggerFactory.CreateLogger<CloverDevicesChannexService>());

    // ------------------------------------------------------------------ index

    [HttpGet("")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        string hotelId = HotelId();

        if (string.IsNullOrWhiteSpace(hotelId))
        {
            CloverDevicesViewModel missing = new()
            {
                Message = "Missing hotel_id.",
                IsError = true
            };

            // Read TempData even here, so a message from a previous post is
            // consumed rather than carried into the next request.
            ApplyTempData(missing);

            return View(missing);
        }

        try
        {
            CloverDevicesViewModel model = await Service
                .GetIndexAsync(hotelId, RawHotelId(), ct)
                .ConfigureAwait(false);

            // Independent read-only checks run together. API failures are displayed
            // on their respective cards without interrupting the device grid.
            var stripeStatus = Service.GetStripeConnectionStatusAsync(hotelId, ct);
            var tokenizationStatus = Channex.GetTokenizationStatusAsync(hotelId, ct);
            await Task.WhenAll(stripeStatus, tokenizationStatus).ConfigureAwait(false);
            model.StripeConnection = await stripeStatus.ConfigureAwait(false);
            model.Tokenization = await tokenizationStatus.ConfigureAwait(false);
            model.StatusCheckedAt = DateTimeOffset.UtcNow;

            ApplyTempData(model);

            return View(model);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CloverDevices: the page could not be loaded.");

            await Service
                .LogExceptionAsync(ex.Message, hotelId, UserName(), ct)
                .ConfigureAwait(false);

            // DisplayErrorMessage() - now the message strip, not an alert.
            return View(new CloverDevicesViewModel
            {
                HotelId = hotelId,
                Message = "Something went wrong. Please try again later.",
                IsError = true
            });
        }
    }

    // ---------------------------------------------------------------- actions

    [HttpPost("ConnectDevice")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> ConnectDevice(CancellationToken ct) =>
        RunAsync(
            async (service, hotelId) =>
            {
                var blocked = await CheckDeviceRegistrationAsync(service, hotelId, ct).ConfigureAwait(false);
                return blocked ?? await service.OpenReaderModalAsync(hotelId, ct).ConfigureAwait(false);
            },
            "Unable to open the device form.",
            ct);

    [HttpPost("OpenUpdate")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> OpenUpdate(string? readerId, CancellationToken ct) =>
        RunAsync(
            (service, hotelId) =>
                service.OpenReaderForUpdateAsync(hotelId, (readerId ?? string.Empty).Trim(), ct),
            "Something went wrong. Please try again later.",
            ct);

    [HttpPost("SaveDevice")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> SaveDevice(
        string? readerId,
        string? readerName,
        CancellationToken ct) =>
        RunAsync(
            (service, hotelId) => service.UpdateDeviceAsync(
                hotelId,
                (readerId ?? string.Empty).Trim(),
                readerName ?? string.Empty,
                ct),
            "Update failed",
            ct);

    [HttpPost("ConnectReader")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> ConnectReader(
        string? registrationCode,
        string? readerName,
        CancellationToken ct) =>
        RunAsync(
            async (service, hotelId) =>
            {
                var blocked = await CheckDeviceRegistrationAsync(service, hotelId, ct).ConfigureAwait(false);
                return blocked ?? await service.ConnectReaderAsync(
                    hotelId, registrationCode ?? string.Empty, readerName ?? string.Empty, ct)
                    .ConfigureAwait(false);
            },
            "Unable to connect the reader.",
            ct);

    [HttpPost("CreateLocation")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> CreateLocation(
        string? locationName,
        string? address1,
        string? city,
        string? state,
        string? postCode,
        string? country,
        CancellationToken ct) =>
        RunAsync(
            (service, hotelId) => service.CreateLocationAsync(
                hotelId,
                locationName ?? string.Empty,
                address1 ?? string.Empty,
                city ?? string.Empty,
                state ?? string.Empty,
                postCode ?? string.Empty,
                string.IsNullOrWhiteSpace(country) ? "GB" : country!,
                ct),
            "Unable to create the location.",
            ct);

    [HttpPost("Delete")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> Delete(string? readerId, CancellationToken ct) =>
        RunAsync(
            (service, hotelId) =>
                service.DeleteReaderAsync(hotelId, (readerId ?? string.Empty).Trim(), ct),
            "Unable to delete the device.",
            ct);

    [HttpPost("InstallTokenization")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> InstallTokenization(CancellationToken ct)
    {
        string hotelId = HotelId();

        if (string.IsNullOrWhiteSpace(hotelId))
        {
            return Back(new CloverDevicesActionResult
            {
                Message = "HotelId is missing. Please reopen this page from the hotel menu.",
                IsError = true
            });
        }

        try
        {
            var channex = Channex;
            var status = await channex.GetTokenizationStatusAsync(hotelId, ct).ConfigureAwait(false);
            if (status.State != "negative")
            {
                return Back(new CloverDevicesActionResult
                {
                    Message = "Tokenization installation blocked: " + status.Detail,
                    IsError = status.State == "unknown" || status.State == "warning"
                });
            }

            string message = await channex
                .InstallTokenizationAppAsync(hotelId, ct)
                .ConfigureAwait(false);

            return Back(new CloverDevicesActionResult { Message = message });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CloverDevices: the tokenization app could not be installed.");

            await Service
                .LogExceptionAsync(ex.Message, hotelId, UserName(), ct)
                .ConfigureAwait(false);

            // The page's own wording.
            return Back(new CloverDevicesActionResult
            {
                Message = "Error installing tokenization app: " + ex.Message,
                IsError = true
            });
        }
    }

    // Repeat the same checks on POST so a stale page or direct form submission
    // cannot register an additional reader after setup has completed.
    private async Task<CloverDevicesActionResult?> CheckDeviceRegistrationAsync(
        ICloverDevicesService service, string hotelId, CancellationToken ct)
    {
        var model = await service.GetIndexAsync(hotelId, RawHotelId(), ct).ConfigureAwait(false);
        if (model.HasRegisteredDevice)
            return new CloverDevicesActionResult { Message = "A Stripe reader is already registered for this hotel.", IsError = true };
        if (!model.CanConnectDevice)
            return new CloverDevicesActionResult { Message = "A Stripe location is required before connecting a device.", IsError = true };
        model.StripeConnection = await service.GetStripeConnectionStatusAsync(hotelId, ct).ConfigureAwait(false);
        if (!model.CanRegisterDevice)
            return new CloverDevicesActionResult { Message = "Device connection blocked: " + model.StripeConnection.Detail, IsError = true };
        return null;
    }

    // ---------------------------------------------------------------- plumbing

    private async Task<IActionResult> RunAsync(
        Func<ICloverDevicesService, string, Task<CloverDevicesActionResult>> action,
        string failureMessage,
        CancellationToken ct)
    {
        string hotelId = HotelId();

        if (string.IsNullOrWhiteSpace(hotelId))
        {
            return Back(new CloverDevicesActionResult
            {
                Message = "HotelId is missing. Please reopen this page from the hotel menu.",
                IsError = true
            });
        }

        try
        {
            CloverDevicesActionResult result =
                await action(Service, hotelId).ConfigureAwait(false);

            return Back(result);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CloverDevices: the action failed.");

            await Service
                .LogExceptionAsync(ex.Message, hotelId, UserName(), ct)
                .ConfigureAwait(false);

            return Back(new CloverDevicesActionResult
            {
                Message = failureMessage,
                IsError = true
            });
        }
    }

    /// <summary>
    /// Redirect back to the page, carrying the message and the modal state the
    /// action left behind.
    /// </summary>
    private IActionResult Back(CloverDevicesActionResult result)
    {
        if (!string.IsNullOrWhiteSpace(result.Message))
        {
            TempData[MessageKey] = result.Message;
            TempData[MessageIsErrorKey] = result.IsError;
        }

        if (result.Modal != CloverDevicesModal.None)
        {
            TempData[ModalKey] = (int)result.Modal;
            TempData[ReaderIdKey] = result.ReaderId;
            TempData[ReaderNameKey] = result.ReaderName;
            TempData[ShowRegCodeKey] = result.ShowRegistrationCode;
            TempData[ReaderIsUpdateKey] = result.ReaderIsUpdate;
        }

        return RedirectToAction(nameof(Index), CurrentRouteValues());
    }

    private void ApplyTempData(CloverDevicesViewModel model)
    {
        if (TempData[MessageKey] is string message && !string.IsNullOrWhiteSpace(message))
        {
            model.Message = message;
            model.IsError = TempData[MessageIsErrorKey] as bool? ?? false;
        }

        if (TempData[ModalKey] is int modal &&
            Enum.IsDefined(typeof(CloverDevicesModal), modal))
        {
            model.Modal = (CloverDevicesModal)modal;
            model.ReaderId = TempData[ReaderIdKey] as string ?? string.Empty;
            model.ReaderName = TempData[ReaderNameKey] as string ?? string.Empty;
            model.ShowRegistrationCode = TempData[ShowRegCodeKey] as bool? ?? true;
            model.ReaderIsUpdate = TempData[ReaderIsUpdateKey] as bool? ?? false;
        }
    }

    /// <summary>
    /// Every key the page was opened with, minus the ones that could steer
    /// RedirectToAction somewhere else.
    /// </summary>
    private RouteValueDictionary CurrentRouteValues()
    {
        RouteValueDictionary values = new();

        foreach (var item in Request.Query)
        {
            if (string.IsNullOrWhiteSpace(item.Key)) continue;

            if (string.Equals(item.Key, "controller", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item.Key, "action", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item.Key, "area", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item.Key, "page", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item.Key, "handler", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            values[item.Key] = item.Value.ToString();
        }

        return values;
    }

    // ------------------------------------------------- query string decoding

    /// <summary>The raw "hd" value, which the Stripe OAuth state carries as-is.</summary>
    private string RawHotelId() => Request.Query["hd"].ToString() ?? string.Empty;

    /// <summary>
    /// hd, decoded - the page read Request.QueryString["hd"] as base64. The
    /// session value is the fallback, the same one the product's other pages
    /// use when a link carries no hotel id.
    /// </summary>
    private string HotelId()
    {
        string hotelId = GetParam("hd") ?? GetParam("hotel_id") ?? GetParam("hotelId") ?? string.Empty;

        if (string.IsNullOrWhiteSpace(hotelId))
        {
            try
            {
                hotelId = HttpContext.Session.GetString("hotel") ?? string.Empty;
            }
            catch (InvalidOperationException)
            {
                hotelId = string.Empty;
            }
        }

        return hotelId;
    }

    /// <summary>UN, decoded - what LogException() wrote into ErrorLogTB.username.</summary>
    private string UserName()
    {
        string user = GetParam("UN") ?? GetParam("user_name") ?? string.Empty;

        if (string.IsNullOrWhiteSpace(user))
        {
            try
            {
                user = HttpContext.Session.GetString("UserName")
                       ?? HttpContext.Session.GetString("username")
                       ?? string.Empty;
            }
            catch (InvalidOperationException)
            {
                user = string.Empty;
            }
        }

        return user;
    }

    /// <summary>
    /// Try base64 first (accepting url-safe alphabets and missing padding),
    /// fall back to the plain url-decoded value - the same rule the product's
    /// other converted pages use.
    /// </summary>
    private string? GetParam(string key)
    {
        string? raw = Request.Query[key];
        if (string.IsNullOrWhiteSpace(raw)) return null;

        if (TryBase64Decode(raw, out string? decoded) &&
            !string.IsNullOrWhiteSpace(decoded))
        {
            return decoded!.Trim();
        }

        return Uri.UnescapeDataString(raw).Trim();
    }

    private static bool TryBase64Decode(string? input, out string? decoded)
    {
        decoded = null;
        if (string.IsNullOrWhiteSpace(input)) return false;

        string s = Uri.UnescapeDataString(input);

        s = s.Trim().Replace(' ', '+').Replace('-', '+').Replace('_', '/');

        int pad = 4 - (s.Length % 4);
        if (pad > 0 && pad < 4)
            s += new string('=', pad);

        try
        {
            byte[] bytes = Convert.FromBase64String(s);
            string text = Encoding.UTF8.GetString(bytes);

            // A plain id can itself be valid base64 and decode into binary.
            // Accept the decode only when the result reads like an id, so
            // plain and encoded ids both work.
            foreach (char c in text)
            {
                if (c < 0x20 || c > 0x7E) return false;
            }

            if (text.Trim().Length == 0) return false;

            decoded = text;
            return true;
        }
        catch
        {
            return false;
        }
    }
}
