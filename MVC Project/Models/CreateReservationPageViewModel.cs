using System.ComponentModel.DataAnnotations;

namespace Orapmshms.Models;

public sealed class CreateReservationPageViewModel
{
    public string HotelId { get; set; } = string.Empty;
    public string HotelName { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string HotelRole { get; set; } = string.Empty;
    public DateTime HotelToday { get; set; } = DateTime.Today;
    public string Currency { get; set; } = "£";
    public string CurrencyCode { get; set; } = "GBP";
    public bool IsMonthWise { get; set; }
    public bool IsCouncil { get; set; }
    public bool HasDiscountPermission { get; set; } = true;
    public bool HasGstPermission { get; set; } = true;
    public bool HasBedTaxPermission { get; set; } = true;
    public bool HasProvisionalPermission { get; set; } = true;
    public bool HasRoomShufflePermission { get; set; } = true;
    public bool HasReservationTypePermission { get; set; } = true;
    public string TaxLabel { get; set; } = "GST";
    public decimal GstPercent { get; set; }
    public decimal BedTaxPercent { get; set; }
    public bool TaxSelectionEnabled { get; set; }
    public bool HasPrimaryTaxConfigured { get; set; }
    public bool HasBedTaxConfigured { get; set; }
    public List<LookupOption> Countries { get; set; } = new();
    public List<LookupOption> Sources { get; set; } = new();
    public List<LookupOption> Categories { get; set; } = new();
    public string PrefillCategory { get; set; } = string.Empty;
    public string PrefillRoom { get; set; } = string.Empty;
    public string PrefillPlan { get; set; } = string.Empty;
    public DateTime? PrefillArrival { get; set; }
    public int? PrefillNights { get; set; }
}

public sealed class CreateReservationQuoteRequest
{
    [Required, StringLength(100)] public string CategoryId { get; set; } = string.Empty;
    [StringLength(100)] public string PlanId { get; set; } = string.Empty;
    [Range(1,20)] public int Rooms { get; set; } = 1;
    [Required] public DateTime ArrivalDate { get; set; }
    [Required] public DateTime DepartureDate { get; set; }
    [Range(0,10000000)] public decimal MonthlyRate { get; set; }
    [Range(0,10000000)] public decimal Discount { get; set;   }
    public bool ApplyGst { get; set; }
    public bool ApplyBedTax { get; set; }
}

public sealed class CreateReservationQuoteResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public decimal Rate { get; set; }
    public decimal Discount { get; set; }
    public decimal GstAmount { get; set; }
    public decimal BedTaxAmount { get; set; }
    public decimal Total { get; set; }
    public int StayCount { get; set; }
    public string StayUnit { get; set; } = "Nights";
}

public sealed class CreateReservationRequest
{
    [Required] public DateTime ArrivalDate { get; set; }
    [Required] public DateTime DepartureDate { get; set; }
    [Required, StringLength(60, MinimumLength=1)] public string FirstName { get; set; } = string.Empty;
    [Required, StringLength(60, MinimumLength=1)] public string LastName { get; set; } = string.Empty;
    [Required, StringLength(20, MinimumLength=7), RegularExpression(@"^[+()\-\s0-9]{7,20}$", ErrorMessage = "Enter a valid phone number.")] public string Phone { get; set; } = string.Empty;
    [Required, EmailAddress, StringLength(120)] public string Email { get; set; } = string.Empty;
    [StringLength(150)] public string Country { get; set; } = string.Empty;
    [StringLength(150)] public string City { get; set; } = string.Empty;
    [StringLength(200)] public string Address { get; set; } = string.Empty;
    [StringLength(180)] public string Identification { get; set; } = string.Empty;
    [Required, StringLength(80, MinimumLength=2)] public string Source { get; set; } = string.Empty;
    public bool IsProvisional { get; set; }
    public DateTime? PaymentDeadline { get; set; }
    public bool SendPaymentLink { get; set; }
    [EmailAddress, StringLength(120)] public string PaymentLinkEmail { get; set; } = string.Empty;
    [StringLength(20)] public string ShuffleType { get; set; } = "Manual";
    [StringLength(20)] public string ReservationMode { get; set; } = "Individual";
    public bool GroupSameDates { get; set; } = true;
    [MinLength(1)] public List<CreateReservationRoomRequest> Rooms { get; set; } = new();
}

public sealed class CreateReservationRoomRequest
{
    [Required, StringLength(100)] public string CategoryId { get; set; } = string.Empty;
    [StringLength(100)] public string PlanId { get; set; } = string.Empty;
    [Range(1,20)] public int Count { get; set; } = 1;
    [StringLength(1000)] public string RoomNo { get; set; } = string.Empty;
    [StringLength(120)] public string GuestName { get; set; } = string.Empty;
    [Required] public DateTime ArrivalDate { get; set; }
    [Required] public DateTime DepartureDate { get; set; }
    [Range(0,10000000)] public decimal MonthlyRate { get; set; }
    [Range(0,10000000)] public decimal Discount { get; set; }
    public bool ApplyGst { get; set; }
    public bool ApplyBedTax { get; set; }
    [Range(1,50)] public int Adults { get; set; }
    [Range(0,50)] public int Children { get; set; }
    [Range(0,20)] public int Infants { get; set; }
}

public sealed class CreateReservationSourceRequest
{
    [Required, StringLength(80, MinimumLength=2)] public string Value { get; set; } = string.Empty;
}

public sealed class PaymentLinkSendRequest
{
    [Required, StringLength(50)] public string RegistrationId { get; set; } = string.Empty;
    [Required, EmailAddress, StringLength(120)] public string Email { get; set; } = string.Empty;
    public DateTime? PaymentDeadline { get; set; }
}

public sealed class PaymentLinkSendResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public string CheckoutUrl { get; set; } = string.Empty;
}

public sealed class RoomOccupancyLimitsResult { public int Adults { get; set; } public int Children { get; set; } public int Infants { get; set; } }
public sealed class CreateReservationGuestResult
{
    public string GuestName { get; set; } = string.Empty; public string LastName { get; set; } = string.Empty; public string Phone { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty; public string Address { get; set; } = string.Empty; public string Country { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty; public string Identification { get; set; } = string.Empty;
}
public sealed class CreateReservationResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public string RegistrationId { get; set; } = string.Empty;
    public string RedirectUrl { get; set; } = string.Empty;
    public decimal GrandTotal { get; set; }
    public bool PaymentLinkSent { get; set; }
    public string PaymentLinkMessage { get; set; } = string.Empty;
}
