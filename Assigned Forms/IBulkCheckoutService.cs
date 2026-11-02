using Orapmshms.Models;

namespace Orapmshms.Services;

public interface IBulkCheckoutService
{
    /// <summary>
    /// In-house rooms ('check in'), one row per room line. When <paramref name="departureDate"/> is set,
    /// only rooms departing on that date (same query as CheckinList.aspx).
    /// </summary>
    Task<List<BulkCheckoutRow>> GetDeparturesAsync(string hotelId, DateTime? departureDate, string? search, CancellationToken cancellationToken = default);

    /// <summary>Checks out the posted rooms (one transaction per room, partial success like the Web Forms page).</summary>
    Task<BulkCheckoutResult> CheckOutAsync(BulkCheckinUserContext user, IReadOnlyCollection<string> roomKeys, bool autoRefundSecurity, string legacyBaseUrl, CancellationToken cancellationToken = default);

    /// <summary>Manual security settlement for a reservation. The amount held is read from the database.</summary>
    Task<BulkCheckoutActionResponse> SettleSecurityAsync(BulkCheckinUserContext user, string regId, decimal releaseAmount, CancellationToken cancellationToken = default);
}

/// <summary>
/// Calls the Web Forms page made into code that is NOT in this MVC project:
///  - SecurityRequiredCapture.SettleSecurityDeposit (Stripe / cash security refund),
///  - hotelsoftware.Utilities.EmailSender.SendCheckoutThanksEmailOnce,
///  - Log_helper.InsertReservLog.
/// The default implementation (BulkCheckoutIntegration) never moves money; see that class.
/// </summary>
public interface IBulkCheckoutIntegration
{
    /// <summary>True only when a real settlement implementation is connected.</summary>
    bool SecuritySettlementAvailable { get; }

    Task<BulkCheckoutSettlementResult> SettleSecurityDepositAsync(
        string hotelId, string regId, string visitId, string currency, string method,
        decimal roomSecurity, decimal refundAmount, string note, string userId, string userName,
        CancellationToken cancellationToken = default);

    Task SendCheckoutThanksEmailOnceAsync(string hotelId, string regId, string reviewUrl, CancellationToken cancellationToken = default);

    Task WriteReservationLogAsync(BulkCheckinUserContext user, string regId, string description, string status, CancellationToken cancellationToken = default);

    /// <summary>Log_helper.Log("Bulk Check out", "Bulk Checkout", hotelId, userId, summary).</summary>
    Task WriteActivityLogAsync(string hotelId, string userId, string summary, CancellationToken cancellationToken = default);
}


//commnett