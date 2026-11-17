using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Orapmshms.Models;

public sealed class RoomWiseRevenueRow
{
    public string ReservationNo { get; set; } = "";
    public string RoomNo { get; set; } = "";
    public string GuestName { get; set; } = "";
    public string SourceName { get; set; } = "Walk In";
    // Occupied-night date for room charges; actual payments.currentdate for extras.
    public DateTime RevenueDate { get; set; }
    public DateTime? ArrivalDate { get; set; }
    public DateTime? DepartureDate { get; set; }
    public string StayDates => ArrivalDate.HasValue && DepartureDate.HasValue
        ? ArrivalDate.Value.ToString("dd MMM,yyyy", CultureInfo.InvariantCulture) + " - "
          + DepartureDate.Value.ToString("dd MMM,yyyy", CultureInfo.InvariantCulture)
        : "—";
    public int RoomNights { get; set; }
    // Saved Rate minus saved discount, allocated to the occupied nights selected.
    public decimal RoomRevenue { get; set; }
    public decimal RoomVat { get; set; }
    public decimal ExtraRevenue { get; set; }
    public decimal ExtraVat { get; set; }
    public decimal FnbRevenue { get; set; }
    public decimal FnbVat { get; set; }
    public decimal NetRevenue => RoomRevenue + ExtraRevenue + FnbRevenue;
    // Saved GST and Bed Tax from each corresponding charge, never a calculated rate.
    public decimal GrossRevenue => NetRevenue + RoomVat + ExtraVat + FnbVat;
}

public sealed class RoomWiseRevenueIssue
{
    public string ReservationNo { get; set; } = "";
    public string RoomNo { get; set; } = "";
    public string GuestName { get; set; } = "";
    public string SourceName { get; set; } = "";
    public DateTime RevenueDate { get; set; }
    public DateTime? ArrivalDate { get; set; }
    public DateTime? DepartureDate { get; set; }
    public string StayDates => ArrivalDate.HasValue && DepartureDate.HasValue
        ? ArrivalDate.Value.ToString("dd MMM,yyyy", CultureInfo.InvariantCulture) + " - "
          + DepartureDate.Value.ToString("dd MMM,yyyy", CultureInfo.InvariantCulture)
        : "—";
    public string Reason { get; set; } = "";
    // Financial value omitted from room-wise totals. No tax is estimated.
    public decimal ExcludedGross { get; set; }
}

// Context only: extras for a displayed reservation posted OUTSIDE the selected day.
// These amounts must never enter RoomWiseRevenueViewModel financial totals.
public sealed class RoomWiseOtherDateExtra
{
    public string ReservationNo { get; set; } = "";
    public string RoomNo { get; set; } = "";
    public string GuestName { get; set; } = "";
    public DateTime PostingDate { get; set; }
    public int Entries { get; set; }
    public decimal ExtraRevenue { get; set; }
    public decimal ExtraVat { get; set; }
    public decimal FnbRevenue { get; set; }
    public decimal FnbVat { get; set; }
    public decimal Gross => ExtraRevenue + ExtraVat + FnbRevenue + FnbVat;
}

public sealed class RoomWiseRevenueViewModel
{
    public string HotelName { get; set; } = "";
    public string CurrencySign { get; set; } = "";
    public DateTime From { get; set; }
    public DateTime To { get; set; }
    public string Search { get; set; } = "";
    public string Source { get; set; } = "";
    public string? Error { get; set; }
    public List<string> Sources { get; } = new();
    public List<RoomWiseRevenueRow> Rows { get; } = new();
    public List<RoomWiseRevenueIssue> Issues { get; } = new();
    public List<RoomWiseOtherDateExtra> OtherDateExtras { get; } = new();
    public bool IsSingleDay => From.Date == To.Date;
    public decimal OtherDateExtrasGross => OtherDateExtras.Sum(x => x.Gross);

    public decimal RoomRevenue => Rows.Sum(x => x.RoomRevenue);
    public decimal RoomVat => Rows.Sum(x => x.RoomVat);
    public decimal ExtraRevenue => Rows.Sum(x => x.ExtraRevenue);
    public decimal ExtraVat => Rows.Sum(x => x.ExtraVat);
    public decimal FnbRevenue => Rows.Sum(x => x.FnbRevenue);
    public decimal FnbVat => Rows.Sum(x => x.FnbVat);
    public decimal GrossRevenue => Rows.Sum(x => x.GrossRevenue);
    public decimal NetRevenue => Rows.Sum(x => x.NetRevenue);
    public decimal ExcludedGross => Issues.Sum(x => x.ExcludedGross);
    public int RoomNights => Rows.Sum(x => x.RoomNights);
    public int Reservations => Rows.Select(x => x.ReservationNo)
        .Distinct(StringComparer.OrdinalIgnoreCase).Count();
    public string Format(decimal amount) => string.IsNullOrWhiteSpace(CurrencySign)
        ? amount.ToString("N2") : CurrencySign.Trim() + " " + amount.ToString("N2");
}
