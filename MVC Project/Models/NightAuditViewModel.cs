using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Orapmshms.Models;

public sealed class NightAuditRatings
{
    public decimal? Booking { get; set; }
    public decimal? Google { get; set; }
    public decimal? Trip { get; set; }
    public decimal? Agoda { get; set; }
    public DateTime? LastSavedAtUtc { get; set; }
    public string LastSavedBy { get; set; } = "";
}

public sealed class NightAuditRatingInput
{
    public DateTime ReportDate { get; set; }
    public decimal? Booking { get; set; }
    public decimal? Google { get; set; }
    public decimal? Trip { get; set; }
    public decimal? Agoda { get; set; }
}

public sealed class NightAuditAccommodationLine
{
    public string ReservationNo { get; set; } = "";
    public string GuestName { get; set; } = "";
    public string RoomNo { get; set; } = "";
    public DateTime? ArrivalDate { get; set; }
    public string PaymentMethod { get; set; } = "";
    public string BookingSource { get; set; } = "";
    // Amounts are from RoomWiseRevenueService (saved payments, posted dates).
    // Taxes already include saved GST + Bed Tax, with no tax-rate recalculation.
    public decimal RoomNet { get; set; }
    public decimal RoomTax { get; set; }
    public decimal ExtraNet { get; set; }
    public decimal ExtraTax { get; set; }
    public decimal FoodNet { get; set; }
    public decimal FoodTax { get; set; }
    // RoomSecurityTB is reservation-level. Show it on ONE room line per
    // reservation to avoid multiplying a shared security deposit.
    public decimal RefundableSecurity { get; set; }
    public decimal RoomAmount => RoomNet + RoomTax;
    public decimal ExtraAmount => ExtraNet + ExtraTax;
    public decimal FoodAmount => FoodNet + FoodTax;
    public decimal Amount => RoomAmount + ExtraAmount + FoodAmount;
    public decimal? Unpaid { get; set; }
}

public sealed class NightAuditOrderLine
{
    public string ReservationNo { get; set; } = "";
    public string RoomOrBill { get; set; } = "";
    public string PaymentMode { get; set; } = "";
    public decimal BeforeTax { get; set; }
    public decimal Tax { get; set; }
    public decimal Gross => BeforeTax + Tax;
}

public sealed class NightAuditPaymentLine
{
    public string Method { get; set; } = "";
    public decimal Amount { get; set; }
}

public sealed class NightAuditViewModel
{
    public DateTime ReportDate { get; set; }
    public DateTime NextDate => ReportDate.AddDays(1);
    public string HotelId { get; set; } = "";
    public string HotelName { get; set; } = "";
    public string CurrencySign { get; set; } = "";
    public string? Error { get; set; }
    public string? Info { get; set; }
    public bool RatingsTableReady { get; set; }
    public NightAuditRatings Ratings { get; set; } = new();
    public List<NightAuditAccommodationLine> Accommodation { get; } = new();
    public List<NightAuditOrderLine> FoodOrders { get; } = new();
    public List<NightAuditPaymentLine> Received { get; } = new();
    // Physical sellable rooms: excludes dated blocks (and today's status blocks).
    public int TotalRoomsAvailable { get; set; }
    public int RoomsOccupied { get; set; }
    public int ArrivalsNextDay { get; set; }
    public int DeparturesNextDay { get; set; }
    public int InHouseNextDay { get; set; }
    public int NoShows { get; set; }
    public int Complimentary { get; set; }
    public int? OutOfServiceRooms { get; set; }
    public decimal RefundCash { get; set; }
    public decimal RefundCard { get; set; }
    public int RefundCashCount { get; set; }
    public int RefundCardCount { get; set; }
    public decimal BookingDiscount { get; set; }
    public decimal UnpaidBookings { get; set; }
    public decimal UnpaidFood { get; set; }
    public decimal FnbEventRevenue { get; set; } // No event source is mapped; remains zero.
    public decimal ExtraGross { get; set; }
    public decimal UnallocatedGross { get; set; }
    public decimal RoomRevenue => Accommodation.Sum(x => x.RoomAmount);
    public decimal ExtrasRevenue => Accommodation.Sum(x => x.ExtraAmount);
    public decimal FoodOrderRevenue => Accommodation.Sum(x => x.FoodAmount);
    public decimal TotalFoodRevenue => FoodOrderRevenue + FnbEventRevenue;
    public decimal RefundableSecurityTotal => Accommodation.Sum(x => x.RefundableSecurity);
    // Revenue totals do NOT include refundable room security (a liability).
    public decimal HotelRevenue => RoomRevenue + ExtrasRevenue + TotalFoodRevenue;
    public decimal Adr => RoomsOccupied == 0 ? 0 : RoomRevenue / RoomsOccupied;
    public decimal OccupancyPct => TotalRoomsAvailable == 0 ? 0 : (decimal)RoomsOccupied * 100 / TotalRoomsAvailable;
    public decimal ExpectedOccupancyPct => TotalRoomsAvailable == 0 ? 0 : (decimal)InHouseNextDay * 100 / TotalRoomsAvailable;
    public decimal TotalReceived => Received.Sum(x => x.Amount);
    public string Money(decimal value) => (string.IsNullOrWhiteSpace(CurrencySign) ? "" : CurrencySign.Trim() + " ")
        + value.ToString("N2", CultureInfo.InvariantCulture);
}
