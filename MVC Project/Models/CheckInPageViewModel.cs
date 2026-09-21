using System.ComponentModel.DataAnnotations;

namespace Orapmshms.Models;

public sealed class CheckInPageViewModel
{
    public string HotelId { get; set; } = string.Empty;
    public string HotelName { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public string Currency { get; set; } = "£";
    public string CurrencyCode { get; set; } = "GBP";
    public string PropertyId { get; set; } = string.Empty;
    public DateTime HotelToday { get; set; } = DateTime.Today;

    public string ReservationId { get; set; } = string.Empty;
    public string VisitId { get; set; } = string.Empty;
    public string ReservationStatus { get; set; } = string.Empty;
    public string ReservationType { get; set; } = "Individual";
    public string ReservationDateMode { get; set; } = "single";
    public string BookingId { get; set; } = string.Empty;
    public string GroupName { get; set; } = string.Empty;
    public bool IsExistingBooking => !string.IsNullOrWhiteSpace(ReservationId);
    public bool IsMonthWise { get; set; }
    public bool CanEditDates { get; set; } = true;
    public bool CanUpdateGuest { get; set; } = true;
    public bool CanDeleteRoom { get; set; } = true;
    public bool CanDeleteAfterCheckIn { get; set; } = true;
    public bool CanDeleteAfterCheckOut { get; set; } = true;
    public bool CanUpdateRate { get; set; } = true;
    public bool CanChangeRoom { get; set; } = true;
    public bool CanRefund { get; set; } = true;
    public bool CanCardPayment { get; set; } = true;
    public bool AllowDirtyRoom { get; set; }
    public bool HasDiscountPermission { get; set; } = true;
    public bool HasGstPermission { get; set; } = true;
    public bool HasBedTaxPermission { get; set; } = true;
    public bool IsRoundTotal { get; set; }
    public bool HasGst { get; set; }
    public bool HasBedTax { get; set; }
    public bool TaxIncludedInRate { get; set; }
    public string TaxLabel { get; set; } = "GST";
    public decimal GstPercent { get; set; }
    public decimal BedTaxPercent { get; set; }
    public decimal BankTransferTaxPercent { get; set; }
    public bool FbrEnabled { get; set; }
    public bool CanPostAndPrint { get; set; }
    public bool IsCardPaymentEnabled { get; set; }
    public bool IsCloverConfigured { get; set; }
    public bool IsStripeConfigured { get; set; }
    public bool ShowSimulation { get; set; }

    public bool ShowCheckInAction { get; set; } = true;
    public bool ShowUndoCheckInAction { get; set; }
    public bool ShowCheckOutAction { get; set; }
    public bool CanExtendReservation { get; set; }

    public GuestCheckInInput Guest { get; set; } = new();
    public CheckInTotals Totals { get; set; } = new();
    public List<LookupOption> Companies { get; set; } = new();
    public List<LookupOption> Sources { get; set; } = new();
    public List<LookupOption> PaymentMethods { get; set; } = new();
    public List<LookupOption> Countries { get; set; } = new();
    public List<LookupOption> Cities { get; set; } = new();
    public List<LookupOption> ChargeDescriptions { get; set; } = new();
    public List<LookupOption> Categories { get; set; } = new();
    public List<LookupOption> PromoCodes { get; set; } = new();
    public List<LookupOption> StripeReaders { get; set; } = new();
    public List<CheckInChargeRow> Charges { get; set; } = new();
    public List<CheckInPaymentLogRow> PaymentLog { get; set; } = new();
    public List<CheckInSecurityRow> SecurityLog { get; set; } = new();
    public List<CheckInLaundryRow> Laundry { get; set; } = new();
    public List<CheckInDiscountRow> Discounts { get; set; } = new();
}

public sealed class GuestCheckInInput
{
    public string RegId { get; set; } = string.Empty;
    public string VisitId { get; set; } = string.Empty;
    [Required] public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    [EmailAddress] public string Email { get; set; } = string.Empty;
    public string IdType { get; set; } = "Passport";
    public string IdNumber { get; set; } = string.Empty;
    public string PassportNo { get; set; } = string.Empty;
    public string VatNo { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string Country { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    [Required] public DateTime ArrivalDate { get; set; }
    [Required] public DateTime DepartureDate { get; set; }
    public string ArrivalTime { get; set; } = "12:00 PM";
    public string DepartureTime { get; set; } = "12:00 PM";
    public int Adults { get; set; } = 1;
    public int Children { get; set; }
    public string Company { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public string CouncilId { get; set; } = string.Empty;
    public string ReservationType { get; set; } = "Individual";
    public string BookingId { get; set; } = string.Empty;
    public string GroupName { get; set; } = string.Empty;
    public string RoomCategory { get; set; } = string.Empty;
    public string RoomNo { get; set; } = string.Empty;
    public string MaxAdults { get; set; } = string.Empty;
    public string MaxMinors { get; set; } = string.Empty;
    public decimal AdvancePaid { get; set; }
    public bool Complementary { get; set; }
}

public sealed class CheckInTotals
{
    public decimal SubTotal { get; set; }
    public decimal TaxTotal { get; set; }
    public decimal GrandTotal { get; set; }
    public decimal AdvancePaid { get; set; }
    public decimal PaidAmount { get; set; }
    public decimal Payable { get; set; }
    public decimal Remaining { get; set; }
    public decimal RoomSecurity { get; set; }
    public string PaymentMethod { get; set; } = string.Empty;
}

public sealed class LookupOption
{
    public string Value { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public string Meta { get; set; } = string.Empty;
    public string Meta2 { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public bool Disabled { get; set; }
}

public sealed class CheckInChargeRow
{
    public int Id { get; set; }
    public string Description { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string TypeValue { get; set; } = string.Empty;
    public string DeductionInfo { get; set; } = string.Empty;
    public string RoomNo { get; set; } = string.Empty;
    public string RatePlanId { get; set; } = string.Empty;
    public string RatePlanName { get; set; } = string.Empty;
    public string GuestName { get; set; } = string.Empty;
    public DateTime? ArrivalDate { get; set; }
    public DateTime? DepartureDate { get; set; }
    public decimal Rate { get; set; }
    public decimal Charge { get; set; }
    public decimal Discount { get; set; }
    public decimal Gst { get; set; }
    public decimal BedTax { get; set; }
    public decimal Nights { get; set; }
    public decimal TotalAmount { get; set; }
    public decimal Total => TotalAmount;
    public string ReservationStatus { get; set; } = string.Empty;
    public bool SelectedForCheckIn { get; set; }
    public bool CanSelectForCheckIn { get; set; }
    public bool CanDelete { get; set; }
    public string DeleteLockTitle { get; set; } = string.Empty;
    public bool CanEditRate { get; set; }
    public bool CanChangeRoom { get; set; }
}

public sealed class CheckInPaymentLogRow
{
    public int Id { get; set; }
    public DateTime? Date { get; set; }
    public decimal Amount { get; set; }
    public string Method { get; set; } = string.Empty;
    public string Receipt { get; set; } = string.Empty;
    public string PaymentId { get; set; } = string.Empty;
    public string ChargeId { get; set; } = string.Empty;
    public string RefundId { get; set; } = string.Empty;
    public string ExternalRefundId { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public string IpAddress { get; set; } = string.Empty;
    public string SystemName { get; set; } = string.Empty;
    public decimal RemainingRefundable { get; set; }
    public bool CanRefund { get; set; }
}

public sealed class CheckInSecurityRow
{
    public int Id { get; set; }
    public DateTime? Date { get; set; }
    public string Description { get; set; } = string.Empty;
    public string Method { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public decimal Deducted { get; set; }
    public decimal Refunded { get; set; }
    public decimal Balance { get; set; }
    public string PaymentIntentId { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public bool CanSettle { get; set; }
}

public sealed class CheckInLaundryRow
{
    public int Id { get; set; }
    public string Category { get; set; } = string.Empty;
    public string Item { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public decimal Rate { get; set; }
    public decimal Amount { get; set; }
}

public sealed class CheckInDiscountRow
{
    public int Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public DateTime? Date { get; set; }
}

public sealed class CheckInSearchResult
{
    public string RegId { get; set; } = string.Empty;
    public int RowId { get; set; }
    public string GuestName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTime? Arrival { get; set; }
    public DateTime? Departure { get; set; }
    public string SourceTable { get; set; } = string.Empty;
}

public sealed class SaveGuestRequest
{
    public GuestCheckInInput Guest { get; set; } = new();
    public string LegacyReservationId { get; set; } = string.Empty;
}

public sealed class UpdateGuestRequest
{
    public GuestCheckInInput Guest { get; set; } = new();
}

/// <summary>Fast WebForms-compatible first/last name update.</summary>
public sealed class UpdateGuestNameRequest
{
    public string RegId { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
}

public sealed class AddCheckInChargeRequest
{
    public string RegId { get; set; } = string.Empty;
    public string VisitId { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string TypeValue { get; set; } = string.Empty;
    public string DeductionInfo { get; set; } = string.Empty;
    public string RoomNo { get; set; } = string.Empty;
    public string RatePlanId { get; set; } = string.Empty;
    public string RatePlanName { get; set; } = string.Empty;
    public string PromoCode { get; set; } = string.Empty;
    public DateTime ArrivalDate { get; set; }
    public DateTime DepartureDate { get; set; }
    public decimal Rate { get; set; }
    public int NumberOfRooms { get; set; } = 1;
    public decimal Discount { get; set; }
    public bool ApplyGst { get; set; }
    public bool ApplyBedTax { get; set; }
    public bool ConfirmRefund { get; set; }
    public string ReservationDateMode { get; set; } = "single";
    public decimal MonthlyRate { get; set; }
}

public sealed class RateQuoteRequest
{
    public string RegId { get; set; } = string.Empty;
    public string CategoryId { get; set; } = string.Empty;
    public string PlanId { get; set; } = string.Empty;
    public DateTime ArrivalDate { get; set; }
    public DateTime DepartureDate { get; set; }
    public bool MonthWise { get; set; }
    public decimal MonthlyRate { get; set; }
}

public sealed class RateQuoteResult
{
    public decimal Total { get; set; }
    public int StayCount { get; set; }
    public string StayUnit { get; set; } = "Nights";
    public List<DailyRateRow> Rates { get; set; } = new();
}

public sealed class DailyRateRow
{
    public DateTime Date { get; set; }
    public decimal Rate { get; set; }
    public string Source { get; set; } = string.Empty;
}

public sealed class SaveCheckInRequest
{
    public GuestCheckInInput Guest { get; set; } = new();
    public List<int> SelectedChargeIds { get; set; } = new();
    public List<string> SelectedRoomNos { get; set; } = new();
    public string PaymentMethod { get; set; } = string.Empty;
    public decimal PaidAmount { get; set; }
    public decimal RoomSecurity { get; set; }
    public string SecurityNote { get; set; } = string.Empty;
    public bool CompleteCheckIn { get; set; } = true;
    public bool SaveAndPrint { get; set; }
    public bool PostAndPrint { get; set; }
    public string PostPrintPaymentMethod { get; set; } = string.Empty;
}

public sealed class RecordCheckInPaymentRequest
{
    public string RegId { get; set; } = string.Empty;
    public string VisitId { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string Method { get; set; } = string.Empty;
    public decimal RoomSecurity { get; set; }
    public string Note { get; set; } = string.Empty;
    public string PaymentId { get; set; } = string.Empty;
    public string ChargeId { get; set; } = string.Empty;
    public string ReceiptUrl { get; set; } = string.Empty;
    public string PaymentStatus { get; set; } = string.Empty;
    public string PayMessage { get; set; } = string.Empty;
}

public sealed class RefundPaymentRequest
{
    public string RegId { get; set; } = string.Empty;
    public int LogId { get; set; }
    public decimal Amount { get; set; }
    public string Reason { get; set; } = string.Empty;
}

public sealed class ExtendReservationRequest
{
    public string RegId { get; set; } = string.Empty;
    public DateTime NewDepartureDate { get; set; }
    public bool RecalculateRates { get; set; } = true;
}

public sealed class ChangeRoomRequest
{
    public string RegId { get; set; } = string.Empty;
    public int PaymentId { get; set; }
    public string NewRoomNo { get; set; } = string.Empty;
}

public sealed class UpdateChargeRateRequest
{
    public string RegId { get; set; } = string.Empty;
    public int PaymentId { get; set; }
    public decimal Rate { get; set; }
}

public sealed class UpdateChargeGuestNameRequest
{
    public string RegId { get; set; } = string.Empty;
    public int PaymentId { get; set; }
    public string GuestName { get; set; } = string.Empty;
}

public sealed class ApplyDiscountRequest
{
    public string RegId { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
}

public sealed class LaundryRequest
{
    public string RegId { get; set; } = string.Empty;
    public string VisitId { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Item { get; set; } = string.Empty;
    public int Quantity { get; set; } = 1;
    public decimal Rate { get; set; }
}

public sealed class SecurityMovementRequest
{
    public string RegId { get; set; } = string.Empty;
    public string VisitId { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string Note { get; set; } = string.Empty;
    public string Method { get; set; } = "Cash";
    public string Movement { get; set; } = "deposit";
    public int SecurityId { get; set; }
    public string PaymentIntentId { get; set; } = string.Empty;
}

public sealed class TerminalPaymentRequest
{
    public string RegId { get; set; } = string.Empty;
    public string VisitId { get; set; } = string.Empty;
    public string ReaderId { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "gbp";
    public string Note { get; set; } = string.Empty;
    public string PaymentIntentId { get; set; } = string.Empty;
    public bool SecurityHold { get; set; }
    public bool Simulate { get; set; }
    public string SimulationResult { get; set; } = "success";

    // Set server-side by CheckInController for Stripe Checkout redirects.
    public string ReturnBaseUrl { get; set; } = string.Empty;
}

public sealed class TerminalPaymentResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public string PaymentIntentId { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string ChargeId { get; set; } = string.Empty;
    public string ReceiptUrl { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string SessionId { get; set; } = string.Empty;
    public string CheckoutUrl { get; set; } = string.Empty;
}

public sealed class CompanySourceRequest
{
    public string Value { get; set; } = string.Empty;
    public string ContactPerson { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string Country { get; set; } = string.Empty;
}

public sealed class FbrPostRequest
{
    public string RegId { get; set; } = string.Empty;
    public string VisitId { get; set; } = string.Empty;
    public string PaymentMethod { get; set; } = string.Empty;
}

public sealed class CheckOutRequest
{
    public string RegId { get; set; } = string.Empty;
    public string PaymentMethod { get; set; } = string.Empty;
    public bool Force { get; set; }
}

public sealed record CheckInOperationResult(
    bool Success,
    string Message,
    string RegId = "",
    int? Id = null,
    string RedirectUrl = "",
    object? Data = null)
{
    public static CheckInOperationResult Ok(string message, string regId = "", int? id = null, string redirectUrl = "", object? data = null)
        => new(true, message, regId, id, redirectUrl, data);
    public static CheckInOperationResult Fail(string message, object? data = null)
        => new(false, message, string.Empty, null, string.Empty, data);
}
