using Orapmshms.Models;

namespace Orapmshms.Services;

public interface ICheckInService
{
    Task<CheckInPageViewModel> GetPageAsync(string hotelId, string hotelName, string userId, string userName, string? lookup, CancellationToken ct = default);
    Task<CheckInPageViewModel> GetReservationStateAsync(string hotelId, string hotelName, string userId, string userName, string lookup, CancellationToken ct = default);
    Task<IReadOnlyList<CheckInSearchResult>> SearchAsync(string hotelId, string term, CancellationToken ct = default);
    Task<IReadOnlyList<CheckInSearchResult>> SearchGuestSuggestionsAsync(string hotelId, string term, CancellationToken ct = default);
    Task<GuestCheckInInput?> GetGuestByPhoneOrEmailAsync(string hotelId, string value, CancellationToken ct = default);
    Task<IReadOnlyList<LookupOption>> GetCitiesAsync(string country, CancellationToken ct = default);
    Task<IReadOnlyList<LookupOption>> GetChargeTypesAsync(string hotelId, string description, CancellationToken ct = default);
    Task<IReadOnlyList<LookupOption>> GetRoomsAsync(string hotelId, string userId, string category, DateTime arrival, DateTime departure, string? regId, CancellationToken ct = default);
    Task<IReadOnlyList<LookupOption>> GetRatePlansAsync(string hotelId, string category, CancellationToken ct = default);
    Task<RateQuoteResult> GetRateQuoteAsync(string hotelId, RateQuoteRequest request, CancellationToken ct = default);
    Task<CheckInOperationResult> SaveGuestAsync(string hotelId, string userId, string userName, string ip, SaveGuestRequest request, CancellationToken ct = default);
    Task<CheckInOperationResult> UpdateGuestAsync(string hotelId, string userId, string userName, string ip, UpdateGuestRequest request, CancellationToken ct = default);
    Task<CheckInOperationResult> UpdateGuestNameAsync(string hotelId, string userId, string userName, string ip, UpdateGuestNameRequest request, CancellationToken ct = default);
    Task<CheckInOperationResult> AddChargeAsync(string hotelId, string hotelName, string userId, string userName, string ip, AddCheckInChargeRequest request, CancellationToken ct = default);
    Task<CheckInOperationResult> DeleteChargeAsync(string hotelId, string hotelName, string userId, string userName, string ip, string regId, int paymentId, CancellationToken ct = default);
    Task<CheckInOperationResult> UpdateChargeRateAsync(string hotelId, string userId, string userName, string ip, UpdateChargeRateRequest request, CancellationToken ct = default);
    Task<CheckInOperationResult> UpdateChargeGuestNameAsync(string hotelId, string userId, string userName, string ip, UpdateChargeGuestNameRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<LookupOption>> GetRoomChangeOptionsAsync(string hotelId, string userId, int paymentId, CancellationToken ct = default);
    Task<CheckInOperationResult> ChangeRoomAsync(string hotelId, string hotelName, string userId, string userName, string ip, ChangeRoomRequest request, CancellationToken ct = default);
    Task<CheckInOperationResult> RecordPaymentAsync(string hotelId, string userId, string userName, string ip, RecordCheckInPaymentRequest request, CancellationToken ct = default);
    Task<CheckInOperationResult> RefundPaymentAsync(string hotelId, string userId, string userName, string ip, RefundPaymentRequest request, CancellationToken ct = default);
    Task<CheckInOperationResult> SaveGuestAndCheckInAsync(string hotelId, string userId, string userName, string ip, SaveCheckInRequest request, CancellationToken ct = default);
    Task<CheckInOperationResult> UndoCheckInAsync(string hotelId, string userId, string userName, string ip, string regId, CancellationToken ct = default);
    Task<CheckInOperationResult> CheckOutAsync(string hotelId, string userId, string userName, string ip, CheckOutRequest request, CancellationToken ct = default);
    Task<CheckInOperationResult> ExtendReservationAsync(string hotelId, string hotelName, string userId, string userName, string ip, ExtendReservationRequest request, CancellationToken ct = default);
    Task<CheckInOperationResult> ApplyDiscountAsync(string hotelId, string userId, string userName, string ip, ApplyDiscountRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<LookupOption>> GetLaundryCategoriesAsync(string hotelId, CancellationToken ct = default);
    Task<IReadOnlyList<LookupOption>> GetLaundryItemsAsync(string hotelId, string category, CancellationToken ct = default);
    Task<CheckInOperationResult> AddLaundryAsync(string hotelId, string userId, string userName, string ip, LaundryRequest request, CancellationToken ct = default);
    Task<CheckInOperationResult> DeleteLaundryAsync(string hotelId, string userId, string userName, string ip, string regId, int id, CancellationToken ct = default);
    Task<CheckInOperationResult> SecurityMovementAsync(string hotelId, string userId, string userName, string ip, SecurityMovementRequest request, CancellationToken ct = default);
    Task<CheckInOperationResult> AddCompanyAsync(string hotelId, CompanySourceRequest request, CancellationToken ct = default);
    Task<CheckInOperationResult> DeleteCompanyAsync(string hotelId, string value, CancellationToken ct = default);
    Task<CheckInOperationResult> AddSourceAsync(string hotelId, CompanySourceRequest request, CancellationToken ct = default);
    Task<CheckInOperationResult> DeleteSourceAsync(string hotelId, string value, CancellationToken ct = default);
    Task<object> GetPaymentAuditAsync(string hotelId, int logId, CancellationToken ct = default);
    Task<TerminalPaymentResult> StripeCreateAsync(string hotelId, TerminalPaymentRequest request, CancellationToken ct = default);
    Task<TerminalPaymentResult> StripeProcessAsync(string hotelId, TerminalPaymentRequest request, CancellationToken ct = default);
    Task<TerminalPaymentResult> StripeStatusAsync(string hotelId, string paymentIntentId, CancellationToken ct = default);
    Task<TerminalPaymentResult> StripeCancelAsync(string hotelId, TerminalPaymentRequest request, CancellationToken ct = default);
    Task<TerminalPaymentResult> StripeCheckoutAsync(string hotelId, TerminalPaymentRequest request, CancellationToken ct = default);
    Task<TerminalPaymentResult> StripeCheckoutStatusAsync(string hotelId, string sessionId, CancellationToken ct = default);
    Task<TerminalPaymentResult> CloverPaymentAsync(string hotelId, string userId, string userName, string ip, TerminalPaymentRequest request, CancellationToken ct = default);
    Task<CheckInOperationResult> ApplyFbrTaxModeAsync(string hotelId, FbrPostRequest request, CancellationToken ct = default);
    Task<CheckInOperationResult> PostToFbrAsync(string hotelId, string userId, string userName, string ip, FbrPostRequest request, CancellationToken ct = default);
}
