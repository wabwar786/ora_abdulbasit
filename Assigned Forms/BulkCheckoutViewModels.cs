using System.Globalization;

namespace Orapmshms.Models;

/// <summary>
/// Bulk Check-out page (ASP.NET Core port of CheckinList.aspx).
/// Lists in-house rooms ('check in') departing on the selected date.
/// </summary>
public sealed class BulkCheckoutPageViewModel
{
    public string Search { get; set; } = string.Empty;
    public DateTime SelectedDate { get; set; }
    public DateTime HotelToday { get; set; }
    public string CurrencySymbol { get; set; } = string.Empty;
    public string LegacyBaseUrl { get; set; } = string.Empty;
    public List<BulkCheckoutRow> Rows { get; set; } = new();
    public bool LoadFailed { get; set; }
    public BulkCheckoutResult? Result { get; set; }

    /// <summary>False until the real SecurityRequiredCapture settlement is connected (see BulkCheckoutIntegration).</summary>
    public bool SecuritySettlementAvailable { get; set; }

    // Reservation-level money repeats on each of its rooms: every reservation is counted once.
    public decimal TotalGrand => Once(r => r.GrandTotal);
    public decimal TotalPaid => Once(r => r.PaidAmount);
    public decimal TotalRemaining => Once(r => r.RemainingAmount);
    public decimal TotalSecurity => Once(r => r.SecurityBalance);

    private decimal Once(Func<BulkCheckoutRow, decimal> pick) =>
        Rows.GroupBy(r => r.RegId).Sum(g => pick(g.First()));

    public string Money(decimal value) =>
        CurrencySymbol + value.ToString("N2", CultureInfo.InvariantCulture);

    public string DateIso => SelectedDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public string DateLabel =>
        SelectedDate.ToString("d MMM yyyy", CultureInfo.InvariantCulture) +
        (SelectedDate.Date == HotelToday.Date ? " (today)" : SelectedDate.Date < HotelToday.Date ? " (past date)" : " (future date)");
}

public sealed class BulkCheckoutRow
{
    public int PaymentId { get; set; }
    public string RegId { get; set; } = string.Empty;
    public string VisitId { get; set; } = string.Empty;
    public string GuestName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string PhoneNo { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string RoomNo { get; set; } = string.Empty;
    public string RoomCategory { get; set; } = string.Empty;

    /// <summary>MM-dd-yyyy, as returned by the listing query.</summary>
    public string ArrivalDate { get; set; } = string.Empty;
    public string DepartureDate { get; set; } = string.Empty;

    public decimal GrandTotal { get; set; }
    public decimal PaidAmount { get; set; }
    public decimal RemainingAmount { get; set; }
    public decimal SecurityBalance { get; set; }
    public string SecurityCurrency { get; set; } = "gbp";
    public string SecurityMethod { get; set; } = "Cash";

    public string FullName => (GuestName + " " + LastName).Trim();

    /// <summary>Posted value for a selected room. The server re-reads everything else.</summary>
    public string Key => PaymentId.ToString(CultureInfo.InvariantCulture) + "|" + RegId;

    public string ArrivalDisplay => FormatDate(ArrivalDate);
    public string DepartureDisplay => FormatDate(DepartureDate);
    public string DepartureIso => ToIso(DepartureDate);

    public int Nights
    {
        get
        {
            if (!BulkCheckinDates.TryParseAnyDate(ArrivalDate, out var a)) return 0;
            if (string.IsNullOrWhiteSpace(DepartureDate)) return 1;
            if (!BulkCheckinDates.TryParseAnyDate(DepartureDate, out var d)) return 0;
            var days = (d.Date - a.Date).Days;
            return days <= 0 ? 1 : days;
        }
    }

    private static string FormatDate(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "N/A";
        return DateTime.TryParseExact(value.Trim(), "MM-dd-yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)
            ? dt.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture)
            : "Invalid Date";
    }

    private static string ToIso(string value) =>
        DateTime.TryParseExact((value ?? string.Empty).Trim(), "MM-dd-yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)
            ? dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : string.Empty;
}

public sealed class BulkCheckoutResult
{
    public int Selected { get; set; }
    public int CheckedOut { get; set; }
    public int Failed { get; set; }
    public int AutoSecurity { get; set; }
    public List<BulkCheckoutRoomResult> Rooms { get; set; } = new();
}

public sealed class BulkCheckoutRoomResult
{
    public string RegId { get; set; } = string.Empty;
    public string RoomNo { get; set; } = string.Empty;
    public string GuestName { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public string SecurityMessage { get; set; } = string.Empty;
    public string InvoiceUrl { get; set; } = string.Empty;
}

public sealed class BulkCheckoutSettleRequest
{
    public string? RegId { get; set; }
    public decimal ReleaseAmount { get; set; }
}

public sealed class BulkCheckoutActionResponse
{
    public bool Ok { get; set; }
    public string Message { get; set; } = string.Empty;
}

/// <summary>Current security held for a reservation, always read from RoomSecurityTB.</summary>
public sealed class BulkCheckoutSecurityInfo
{
    public decimal Balance { get; set; }
    public string Currency { get; set; } = "gbp";
    public string VisitId { get; set; } = string.Empty;
    public string PaymentIntentId { get; set; } = string.Empty;
    public string ChargeId { get; set; } = string.Empty;
    public string PaymentMethod { get; set; } = string.Empty;
}

/// <summary>Result of a security settlement call (same shape the Web Forms page read: ok + message).</summary>
public sealed record BulkCheckoutSettlementResult(bool Ok, string Message);


//commnetttttt
