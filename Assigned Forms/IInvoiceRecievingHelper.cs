#nullable enable

using System.Collections.Generic;
using Orapmshms.Models;

namespace Orapmshms.Services;

/// <summary>
/// The data the invoice needs from the project's existing InvoiceHelper.
///
/// The Web Forms page called six static methods on hotelsoftware.Utilities.InvoiceHelper.
/// This module does not reimplement any of them: it calls the same code.
///
/// Two ways this is satisfied, in order:
///   1. A class in this project implements this interface -> it is used directly.
///   2. Nothing implements it -> InvoiceRecievingHelperBridge finds the existing
///      InvoiceHelper type at runtime and forwards to its static methods.
///
/// Implementing this interface over the real helper is the faster path and is
/// what the handover recommends once the helper's namespace is known.
/// </summary>
public interface IInvoiceRecievingHelper
{
    InvoiceRecievingHotel GetHotel(string hotelId);

    InvoiceRecievingGuest GetGuest(string hotelId, string regId);

    IReadOnlyList<InvoiceRecievingPaymentLine>? GetPaymentLines(string hotelId, string regId);

    InvoiceRecievingPaymentSummary? GetLatestPaymentSummary(string hotelId, string regId);

    string BuildPayNowUrl(
        string hotelId,
        string regId,
        string fullName,
        decimal amount,
        string arrivalText,
        string departureText,
        string src,
        string extra);

    string GenerateQrDataUrl(string payUrl);
}
