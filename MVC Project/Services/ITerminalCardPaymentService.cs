using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Orapmshms.Models;

namespace Orapmshms.Services
{
    /// <summary>What the PDQ page needs from the database and from Stripe Terminal.</summary>
    public interface ITerminalCardPaymentService
    {
        /// <summary>The hotel's readers (the old BindReaders).</summary>
        Task<IReadOnlyList<TerminalCardPaymentReader>> GetReadersAsync(string hotelId, CancellationToken cancellationToken);

        /// <summary>
        /// The hotel's Stripe account: newest AccessToken + StripeUserId, whether it is a test
        /// account (the old IsStripeTestMode) and the platform fee percent. Cached briefly, so the
        /// 1.5-second status poll does not hit the database every time.
        /// </summary>
        Task<TerminalCardPaymentAccount> GetAccountAsync(string hotelId, CancellationToken cancellationToken);

        /// <summary>The hotel's currency (the old GetHotelCurrencyAndSign), "GBP" when it has none.</summary>
        Task<string> GetCurrencyAsync(string hotelId, CancellationToken cancellationToken);

        /// <summary>
        /// Creates a card-present PaymentIntent. Reservation charges use automatic capture; holds use
        /// manual capture and finish in requires_capture until CaptureHoldAsync is called.
        /// </summary>
        Task<string> CreatePaymentIntentAsync(string hotelId, long amountMinor, string currency,
            TerminalCardPaymentPayload payload, string description, CancellationToken cancellationToken);

        /// <summary>The old ProcessPaymentIntent: hand the intent to the reader.</summary>
        Task<string> ProcessOnReaderAsync(string hotelId, string readerId, string paymentIntentId, CancellationToken cancellationToken);

        /// <summary>The old CheckStatus: the intent's status plus the reader's action status.</summary>
        Task<TerminalCardPaymentStatus> GetStatusAsync(string hotelId, string paymentIntentId, string readerId, CancellationToken cancellationToken);

        /// <summary>The old CancelReaderAction.</summary>
        Task CancelReaderActionAsync(string hotelId, string readerId, CancellationToken cancellationToken);

        /// <summary>The old CancelPaymentIntent.</summary>
        Task<bool> CancelPaymentIntentAsync(string hotelId, string paymentIntentId, CancellationToken cancellationToken);


        /// <summary>Active reservation card holds saved by stripe_webhook.</summary>
        Task<IReadOnlyList<TerminalPaymentHoldDto>> GetReservationHoldsAsync(
            string hotelId, string regId, CancellationToken cancellationToken);

        /// <summary>Capture all or part of an authorized hold. The normal webhook then posts the captured amount to PaymentsLogTB/PaymentsUpdateTB.</summary>
        Task<TerminalPaymentHoldActionResult> CaptureHoldAsync(
            string hotelId, string regId, string paymentIntentId, long? amountMinor, CancellationToken cancellationToken);

        /// <summary>Release an authorized hold without posting a reservation payment.</summary>
        Task<TerminalPaymentHoldActionResult> ReleaseHoldAsync(
            string hotelId, string regId, string paymentIntentId, CancellationToken cancellationToken);

        /// <summary>The old SimulatePresentPaymentMethod: test accounts only.</summary>
        Task<string> SimulateCardAsync(string hotelId, string readerId, string paymentIntentId, string scenario, CancellationToken cancellationToken);

        /// <summary>
        /// Opens the connection to Stripe in the background so the first real call does not pay
        /// for DNS, TCP and TLS. Returns at once and never throws.
        /// </summary>
        void WarmStripeConnection(TerminalCardPaymentAccount account);

        /// <summary>The old LogStripeTerminalFailure: one row in StripeTerminalFailLogTB. Never throws.</summary>
        Task LogFailureAsync(string hotelId, string userId, string userName, string ipAddress,
            System.DateTime hotelNow, TerminalCardPaymentFailLogInput input, CancellationToken cancellationToken);
    }
}
