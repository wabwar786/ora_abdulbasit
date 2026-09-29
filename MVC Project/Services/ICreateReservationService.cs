using Orapmshms.Models;

namespace Orapmshms.Services;

public interface ICreateReservationService
{
    Task<CreateReservationPageViewModel> GetPageAsync(string hotelId, string hotelName, string userId, string userName, string role, string hotelRole, CancellationToken ct = default);
    Task<IReadOnlyList<LookupOption>> GetCitiesAsync(string country, CancellationToken ct = default);
    Task<IReadOnlyList<LookupOption>> GetRatePlansAsync(string hotelId, string categoryId, string userId, string role, CancellationToken ct = default);
    Task<IReadOnlyList<LookupOption>> GetRoomsAsync(string hotelId, string userId, string categoryId, DateTime arrival, DateTime departure, CancellationToken ct = default);
    Task<RoomOccupancyLimitsResult> GetOccupancyLimitsAsync(string hotelId, string categoryId, CancellationToken ct = default);
    Task<IReadOnlyList<CreateReservationGuestResult>> SearchGuestsAsync(string hotelId, string term, CancellationToken ct = default);
    Task<LookupOption> AddSourceAsync(string hotelId, string userId, string userName, string clientIp, string value, CancellationToken ct = default);
    Task<CreateReservationQuoteResult> QuoteAsync(string hotelId, string userId, string role, CreateReservationQuoteRequest request, CancellationToken ct = default);
    Task<CreateReservationResult> CreateAsync(string hotelId, string hotelName, string userId, string userName, string role, string hotelRole, string clientIp, CreateReservationRequest request, CancellationToken ct = default);
    Task<PaymentLinkSendResult> SendPaymentLinkAsync(string hotelId, string regId, string toEmail, DateTime? deadline, string returnBaseUrl, CancellationToken ct = default);
}
