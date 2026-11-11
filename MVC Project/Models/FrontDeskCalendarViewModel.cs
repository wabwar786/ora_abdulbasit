using System.ComponentModel.DataAnnotations;

namespace Orapmshms.Models;

public sealed class FrontDeskCalendarPageViewModel
{
    public string HotelId { get; set; } = string.Empty;
    public string HotelName { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string CurrencySymbol { get; set; } = "£";
    public bool IsRestaurantEnabled { get; set; }
    public DateTime HotelToday { get; set; }
    public DateTime StartDate { get; set; }
    public int ViewDays { get; set; } = 20;
    // Reusable Record Payment component visibility. These are resolved once per
    // property/user using the same permission/configuration rules as the PMS.
    public bool CanOnlineCardPayment { get; set; }
    public bool CanPdqPayment { get; set; }
    public FrontDeskCalendarPermissions Permissions { get; set; } = new();
}

public sealed class FrontDeskCalendarPermissions
{
    public bool CanViewDetails { get; set; } = true;
    public bool CanDragDrop { get; set; } = true;
    public bool CanExtendShrink { get; set; } = true;
    public bool CanBlockRoom { get; set; } = true;
    public bool CanMarkRoomClean { get; set; } = true;
    public bool CanChangeRoom { get; set; } = true;
    // Legacy WebForms action permissions. Keep card and PDQ separate because
    // properties can enable one without exposing the other.
    public bool CanOnlineCardPayment { get; set; } = true;
    public bool CanPdqPayment { get; set; } = true;
    public bool CanRefund { get; set; }
    public bool CanAddPastPayment { get; set; }
    public bool CanAdjustPayment { get; set; }
    public bool CanDeleteReservation { get; set; }
    public bool CanDeleteAfterCheckIn { get; set; }
    public bool CanDeleteAfterCheckOut { get; set; }
}

public sealed class FrontDeskCalendarPayload
{
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
    public DateTime HotelToday { get; set; }
    public int ViewDays { get; set; }
    public string CurrencySymbol { get; set; } = "£";
    public IReadOnlyList<FrontDeskCategoryDto> Categories { get; set; } = Array.Empty<FrontDeskCategoryDto>();
    public IReadOnlyList<FrontDeskRoomDto> Rooms { get; set; } = Array.Empty<FrontDeskRoomDto>();
    public IReadOnlyList<FrontDeskBookingDto> Bookings { get; set; } = Array.Empty<FrontDeskBookingDto>();
    public IReadOnlyList<FrontDeskBlockDto> Blocks { get; set; } = Array.Empty<FrontDeskBlockDto>();
    public FrontDeskCalendarPermissions Permissions { get; set; } = new();
}

public sealed class FrontDeskCategoryDto
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public decimal BaseRate { get; set; }
    public int TotalRooms { get; set; }
    public int Occupied { get; set; }
    public int Dirty { get; set; }
    public int Available { get; set; }
    public int NotBooked { get; set; }
    public int Blocked { get; set; }
}

public sealed class FrontDeskRoomDto
{
    public string RoomNo { get; set; } = string.Empty;
    public string CategoryId { get; set; } = string.Empty;
    public string CategoryName { get; set; } = string.Empty;
    public string Condition { get; set; } = "Clean";
    public DateTime? DirtyDate { get; set; }
    public bool IsAssignedToCouncil { get; set; }
    // Exact room-assignment window from dbo.UserRoomAccess.
    public DateTime? CouncilAssignmentStartDate { get; set; }
    public DateTime? CouncilAssignmentEndDate { get; set; }
}

public sealed class FrontDeskBookingDto
{
    public string Id { get; set; } = string.Empty;
    public string RegId { get; set; } = string.Empty;
    public int PaymentId { get; set; }
    // Daily calendar only: reservation master exists but has no Room Rent row.
    public bool IsMissingRoomRent { get; set; }
    // Raw reservation master total, used ONLY to prefill the missing-charge editor.
    public decimal MasterTotalAmount { get; set; }
    // Number of rooms expected by the reservation master (noofrooms can be a CSV of counts).
    public int ExpectedRooms { get; set; } = 1;
    public string VisitId { get; set; } = string.Empty;
    public string BookId { get; set; } = string.Empty;
    public string GuestName { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string RoomNo { get; set; } = string.Empty;
    public string CategoryId { get; set; } = string.Empty;
    public string CategoryName { get; set; } = string.Empty;
    public DateTime Arrival { get; set; }
    public DateTime Departure { get; set; }
    public DateTime? ActualCheckoutDate { get; set; }
    public bool IsSameDayCheckout { get; set; }
    public string Status { get; set; } = string.Empty;
    // Legacy WebForms calendar status code used for bar colour/behaviour:
    // CO=checked out, O=checked in, R=reservation, P=provisional.
    public string StatusCode { get; set; } = string.Empty;
    // Hotel-local provisional payment deadline (datetime, not checkout/departure).
    public DateTime? ProvisionalExpiry { get; set; }
    public string Source { get; set; } = string.Empty;
    public string PlanName { get; set; } = string.Empty;
    // Selected Room Rent payment row amounts. Legacy payments.rate/GST/Bed are row totals,
    // so the calendar can derive the current per-night values for Extend/Shrink confirmation.
    public decimal Rate { get; set; }
    public decimal Gst { get; set; }
    public decimal Bed { get; set; }
    public decimal Total { get; set; }
    public decimal Paid { get; set; }
    public decimal Balance { get; set; }
    public decimal RoomSecurity { get; set; }
    public decimal Discount { get; set; }
    public string PaymentStatus { get; set; } = string.Empty;
    public string ChannexBookingId { get; set; } = string.Empty;
    public bool IsVirtualCard { get; set; }
    public bool ShowAutoPay { get; set; }
    public string CreatedRole { get; set; } = string.Empty;
    public string CreatedByUserId { get; set; } = string.Empty;
    public int Adults { get; set; }
    public int Children { get; set; }
    public int Infants { get; set; }
    public bool CanDrag { get; set; }
    public bool CanResize { get; set; }
    // Calendar-only indicators copied from the legacy WebForms behaviour.
    // These flags do not change booking/payment state; they only control the
    // small markers rendered on the booking bar.
    // Existing GuestNoteBook.description (same text as the right-side Notebook panel).
    public string FrontDeskNotes { get; set; } = string.Empty;
    public bool HasNote { get; set; }
    public bool HasRoomChange { get; set; }
    public bool HasRestaurantPendingBill { get; set; }
}

public sealed class FrontDeskBlockDto
{
    public int BlockId { get; set; }
    public string RoomNo { get; set; } = string.Empty;
    public string CategoryId { get; set; } = string.Empty;
    public string CategoryName { get; set; } = string.Empty;
    public DateTime StartDate { get; set; }
    // Client uses an exclusive end, while RoomBlocksTB stores an inclusive end.
    public DateTime EndDate { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string Kind { get; set; } = "Room Block";
}

public sealed class FrontDeskDeleteRoomSegmentRequest
{
    [System.ComponentModel.DataAnnotations.Required]
    [System.ComponentModel.DataAnnotations.StringLength(100)]
    public string RegId { get; set; } = string.Empty;
    [System.ComponentModel.DataAnnotations.Range(1, int.MaxValue)]
    public int PaymentId { get; set; }
}

public sealed class FrontDeskBookingDetailsDto
{
    public string RegId { get; set; } = string.Empty;
    public int PaymentId { get; set; }
    public string VisitId { get; set; } = string.Empty;
    public bool CanDeleteRoomSegment { get; set; }
    public string GuestName { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string BookingNo { get; set; } = string.Empty;
    public string RoomNo { get; set; } = string.Empty;
    public string CategoryName { get; set; } = string.Empty;
    public string CategoryId { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTime? CreatedAt { get; set; }
    public DateTime? ProvisionalExpiry { get; set; }
    public DateTime Arrival { get; set; }
    public DateTime Departure { get; set; }
    public int Nights { get; set; }
    public int Adults { get; set; }
    public int Children { get; set; }
    public int Infants { get; set; }
    public string Source { get; set; } = string.Empty;
    public string PlanName { get; set; } = string.Empty;
    public decimal Total { get; set; }
    public decimal Discount { get; set; }
    public decimal Paid { get; set; }
    public decimal Balance { get; set; }
    public decimal RoomSecurity { get; set; }
    public string Notes { get; set; } = string.Empty;
    public string FrontDeskNotes { get; set; } = string.Empty;
    public string ChannexBookingId { get; set; } = string.Empty;
    public bool IsVirtualCard { get; set; }
    public bool ShowAutoPay { get; set; }
    public bool ShowChannexChat { get; set; }
    public IReadOnlyList<FrontDeskPaymentLogDto> Payments { get; set; } = Array.Empty<FrontDeskPaymentLogDto>();
}

public sealed class FrontDeskPaymentLogDto
{
    public int Id { get; set; }
    public DateTime? Date { get; set; }
    public decimal Amount { get; set; }
    public string Method { get; set; } = string.Empty;
    public string Reference { get; set; } = string.Empty;
    public string ReceiptUrl { get; set; } = string.Empty;
    public decimal RemainingRefundable { get; set; }
    public bool CanRefund { get; set; }
    public bool CanAdjust { get; set; }
    public decimal CurrentAdjustedAmount { get; set; }
    public bool IsCorrection { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public string EntryName { get; set; } = string.Empty;

    // Used only server-side to match previous refunds to their original payment.
    [System.Text.Json.Serialization.JsonIgnore]
    public string PaymentId { get; set; } = string.Empty;
    [System.Text.Json.Serialization.JsonIgnore]
    public string ChargeId { get; set; } = string.Empty;
    [System.Text.Json.Serialization.JsonIgnore]
    public string RefundId { get; set; } = string.Empty;
    [System.Text.Json.Serialization.JsonIgnore]
    public string ExternalRefundId { get; set; } = string.Empty;
}

public sealed class FrontDeskGuestHistoryDto
{
    public string GuestName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public IReadOnlyList<FrontDeskGuestStayDto> Stays { get; set; } = Array.Empty<FrontDeskGuestStayDto>();
}

public sealed class FrontDeskGuestStayDto
{
    public string RegId { get; set; } = string.Empty;
    public string RoomNo { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTime? Arrival { get; set; }
    public DateTime? Departure { get; set; }
}

public sealed class FrontDeskNoteRequest
{
    [Required, StringLength(100)] public string RegId { get; set; } = string.Empty;
    [StringLength(4000)] public string Notes { get; set; } = string.Empty;
}

public sealed class FrontDeskRoomCleanRequest
{
    [Required, StringLength(50)] public string RoomNo { get; set; } = string.Empty;
}

public class FrontDeskRoomBlockRequest
{
    [Required, StringLength(50)] public string RoomNo { get; set; } = string.Empty;
    [Required, StringLength(100)] public string CategoryId { get; set; } = string.Empty;
    [Required, StringLength(150)] public string CategoryName { get; set; } = string.Empty;
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
    [Required, StringLength(500)] public string Reason { get; set; } = string.Empty;
    [StringLength(50)] public string Kind { get; set; } = "Room Block";
}

public sealed class FrontDeskRoomBlockUpdateRequest : FrontDeskRoomBlockRequest
{
    public int BlockId { get; set; }
}

public sealed class FrontDeskBlockActionRequest
{
    public int BlockId { get; set; }
    [Required, StringLength(50)] public string RoomNo { get; set; } = string.Empty;
    [Required, StringLength(100)] public string CategoryId { get; set; } = string.Empty;
    [Required, StringLength(150)] public string CategoryName { get; set; } = string.Empty;
}

public sealed class FrontDeskRoomOccupancyRequest
{
    [Required, StringLength(100)] public string RegId { get; set; } = string.Empty;
    [Range(1, int.MaxValue)] public int PaymentId { get; set; }
    [Range(0, 30)] public int Adults { get; set; }
    [Range(0, 30)] public int Children { get; set; }
    [Range(0, 30)] public int Infants { get; set; }
}

public sealed class FrontDeskBookingMoveRequest
{
    [Required, StringLength(100)] public string RegId { get; set; } = string.Empty;
    public int PaymentId { get; set; }
    [StringLength(50)] public string NewRoomNo { get; set; } = string.Empty;
    [Required, StringLength(100)] public string TargetCategoryId { get; set; } = string.Empty;
    [Required, StringLength(150)] public string TargetCategoryName { get; set; } = string.Empty;
    public DateTime NewArrival { get; set; }
    public decimal? NightlyRateOverride { get; set; }
    // Daily calendar master-only room assignment, ignored by ordinary moves.
    [StringLength(100)] public string RatePlanId { get; set; } = string.Empty;
    public decimal? RoomRentTotal { get; set; }
    // Optional room-level guest counts for single-room missing-charge repair.
    // Null preserves the previous behavior for callers that omit them.
    [Range(0,30)] public int? RoomAdults { get; set; }
    [Range(0,30)] public int? RoomChildren { get; set; }
    [Range(0,30)] public int? RoomInfants { get; set; }
    // Explicit multi-room repair only, never used by existing payment-based moves.
    public List<FrontDeskMissingRoomAssignmentDto>? RoomAssignments { get; set; }
    public bool ConfirmRoomCountIncrease { get; set; }
}

public sealed class FrontDeskMissingRoomAssignmentDto
{
    [Required, StringLength(50)] public string RoomNo { get; set; } = string.Empty;
    [Required, StringLength(100)] public string RatePlanId { get; set; } = string.Empty;
    [Range(typeof(decimal), "0.01", "100000000")] public decimal RoomRentTotal { get; set; }
    [Range(0,30)] public int Adults { get; set; }
    [Range(0,30)] public int Children { get; set; }
    [Range(0,30)] public int Infants { get; set; }
}

public sealed class FrontDeskBookingResizeRequest
{
    [Required, StringLength(100)] public string RegId { get; set; } = string.Empty;
    public int PaymentId { get; set; }
    public DateTime NewArrival { get; set; }
    public DateTime NewDeparture { get; set; }
    // Per-night room rate entered in the WebForms-style Extend/Shrink confirmation popup.
    public decimal? NightlyRateOverride { get; set; }
    // WebForms extension popup defaults tax on added nights to off and lets the user opt in.
    public bool IncludeTaxOnExtension { get; set; }
}

public sealed class FrontDeskConfirmReservationRequest
{
    [Required, StringLength(100)] public string RegId { get; set; } = string.Empty;
    public int PaymentId { get; set; }
}

public sealed class FrontDeskDirectCheckInRequest
{
    [Required, StringLength(100)] public string RegId { get; set; } = string.Empty;
    public int PaymentId { get; set; }
}


public sealed class FrontDeskEmailPrepareRequest
{
    [Required, StringLength(100)] public string RegId { get; set; } = string.Empty;
    public int PaymentId { get; set; }

    /// <summary>
    /// Staff-selected PayNow action. "charge" captures immediately; "hold" creates
    /// a manual-capture authorization that can be captured later from ORA PMS.
    /// </summary>
    [StringLength(20)]
    public string PaymentMode { get; set; } = "charge";

    /// <summary>Reservation source used by the legacy PayNow/webhook contract (NR or GI).</summary>
    [StringLength(10)]
    public string Source { get; set; } = "NR";

    /// <summary>
    /// Optional amount chosen in Record Payment. When omitted, the full outstanding balance is used.
    /// </summary>
    public decimal? Amount { get; set; }
}

public sealed class FrontDeskEmailSendRequest
{
    [Required, StringLength(100)] public string RegId { get; set; } = string.Empty;
    [Required, EmailAddress, StringLength(320)] public string Email { get; set; } = string.Empty;
    [Required, StringLength(20)] public string EmailType { get; set; } = "GEN";
    [Required, StringLength(8000)] public string Message { get; set; } = string.Empty;
    [StringLength(4000)] public string Url { get; set; } = string.Empty;
}

public sealed class FrontDeskEmailComposerDto
{
    public string RegId { get; set; } = string.Empty;
    public string GuestName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string GenericMessage { get; set; } = string.Empty;
    public string PaymentUrl { get; set; } = string.Empty;
    public string InvoiceUrl { get; set; } = string.Empty;
    public string InvoicePdfUrl { get; set; } = string.Empty;
    public string PaymentMessage { get; set; } = string.Empty;
    public string InvoiceMessage { get; set; } = string.Empty;
    public string InvoicePdfMessage { get; set; } = string.Empty;
}

public sealed class FrontDeskNoShowRequest
{
    [Required, StringLength(100)] public string RegId { get; set; } = string.Empty;
    public int PaymentId { get; set; }
}

public sealed class FrontDeskCancelReservationRequest
{
    [Required, StringLength(100)] public string RegId { get; set; } = string.Empty;
    public int PaymentId { get; set; }
    [Required, StringLength(500)] public string Reason { get; set; } = string.Empty;
}

public sealed record FrontDeskOperationResult(bool Success, string Message, object? Data = null)
{
    public static FrontDeskOperationResult Ok(string message, object? data = null) => new(true, message, data);
    public static FrontDeskOperationResult Fail(string message, object? data = null) => new(false, message, data);
}
