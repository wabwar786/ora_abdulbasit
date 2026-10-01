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
}

public sealed class FrontDeskBookingDto
{
    public string Id { get; set; } = string.Empty;
    public string RegId { get; set; } = string.Empty;
    public int PaymentId { get; set; }
    public string VisitId { get; set; } = string.Empty;
    public string BookId { get; set; } = string.Empty;
    public string GuestName { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string RoomNo { get; set; } = string.Empty;
    public string CategoryId { get; set; } = string.Empty;
    public string CategoryName { get; set; } = string.Empty;
    public DateTime Arrival { get; set; }
    public DateTime Departure { get; set; }
    public string Status { get; set; } = string.Empty;
    // Legacy WebForms calendar status code used for bar colour/behaviour:
    // CO=checked out, O=checked in, R=reservation, P=provisional.
    public string StatusCode { get; set; } = string.Empty;
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

public sealed class FrontDeskBookingDetailsDto
{
    public string RegId { get; set; } = string.Empty;
    public int PaymentId { get; set; }
    public string VisitId { get; set; } = string.Empty;
    public string GuestName { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string BookingNo { get; set; } = string.Empty;
    public string RoomNo { get; set; } = string.Empty;
    public string CategoryName { get; set; } = string.Empty;
    public string CategoryId { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTime? CreatedAt { get; set; }
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

public sealed class FrontDeskBookingMoveRequest
{
    [Required, StringLength(100)] public string RegId { get; set; } = string.Empty;
    public int PaymentId { get; set; }
    [StringLength(50)] public string NewRoomNo { get; set; } = string.Empty;
    [Required, StringLength(100)] public string TargetCategoryId { get; set; } = string.Empty;
    [Required, StringLength(150)] public string TargetCategoryName { get; set; } = string.Empty;
    public DateTime NewArrival { get; set; }
    public decimal? NightlyRateOverride { get; set; }
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

public sealed class FrontDeskDirectCheckInRequest
{
    [Required, StringLength(100)] public string RegId { get; set; } = string.Empty;
    public int PaymentId { get; set; }
}


public sealed class FrontDeskEmailPrepareRequest
{
    [Required, StringLength(100)] public string RegId { get; set; } = string.Empty;
    public int PaymentId { get; set; }
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
