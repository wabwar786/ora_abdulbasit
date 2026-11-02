using System.Globalization;

namespace Orapmshms.Models;

/// <summary>
/// Bulk Check-in page (ASP.NET Core port of BulkCheckin.aspx).
/// Lists today's arrivals (hotel date) that are still in 'reservation' status.
/// </summary>
public sealed class BulkCheckinPageViewModel
{
    public string Search { get; set; } = string.Empty;
    public DateTime HotelToday { get; set; }
    public string CurrencySymbol { get; set; } = string.Empty;

    /// <summary>Legacy:BaseUrl (appsettings) for pages that are still Web Forms, e.g. RegistrationCard.aspx. Ends with "/" or is empty.</summary>
    public string LegacyBaseUrl { get; set; } = string.Empty;

    public List<BulkCheckinRow> Rows { get; set; } = new();

    /// <summary>True when the list could not be loaded (a generic message is shown).</summary>
    public bool LoadFailed { get; set; }

    /// <summary>True when the page shows sample data (Development only, BulkCheckin:UseMockData).</summary>
    public bool UsingMockData { get; set; }

    /// <summary>Per-room outcome of the last bulk check-in (shown once after the redirect).</summary>
    public BulkCheckinResult? Result { get; set; }

    // Total / Paid / Remaining belong to the RESERVATION and repeat on each of its rooms,
    // so every reservation is counted once.
    public decimal TotalGrand => Rows.GroupBy(r => r.RegId).Sum(g => g.First().GrandTotal);
    public decimal TotalPaid => Rows.GroupBy(r => r.RegId).Sum(g => g.First().PaidAmount);
    public decimal TotalRemaining => Rows.GroupBy(r => r.RegId).Sum(g => g.First().RemainingAmount);

    public string Money(decimal value) =>
        CurrencySymbol + value.ToString("N2", CultureInfo.InvariantCulture);
}

public sealed class BulkCheckinRow
{
    public string RegId { get; set; } = string.Empty;
    public string RoomNo { get; set; } = string.Empty;
    public string RoomCategory { get; set; } = string.Empty;

    /// <summary>Where the booking came from (NewReservationsTB / GuestInformationLogTB "Agency"): Expedia, Booking.com, Walk In...</summary>
    public string BookingSourceRaw { get; set; } = string.Empty;

    /// <summary>Same value, tidied for the screen: blanks and "--Select--" become "Walk In".</summary>
    public string BookingSource
    {
        get
        {
            var v = (BookingSourceRaw ?? string.Empty).Trim();
            return v.Length == 0 || v.Equals("--Select--", StringComparison.OrdinalIgnoreCase) ? "Walk In" : v;
        }
        set => BookingSourceRaw = value;
    }
    public string GuestName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string PhoneNo { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;

    /// <summary>Arrival / departure as stored for the room line: MM-dd-yyyy.</summary>
    public string ArrivalDate { get; set; } = string.Empty;
    public string DepartureDate { get; set; } = string.Empty;

    public decimal GrandTotal { get; set; }
    public decimal PaidAmount { get; set; }
    public decimal RemainingAmount { get; set; }

    public string FullName => (GuestName + " " + LastName).Trim();

    /// <summary>Posted value for a selected room: reg_id|room_no (nothing else is trusted from the browser).</summary>
    public string Key => RegId + "|" + RoomNo;

    public string ArrivalDisplay => FormatDate(ArrivalDate);

    /// <summary>yyyy-MM-dd, used to sort the Stay column by arrival date.</summary>
    public string ArrivalSortKey =>
        DateTime.TryParseExact(ArrivalDate ?? string.Empty, "MM-dd-yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : string.Empty;
    public string DepartureDisplay => FormatDate(DepartureDate);

    /// <summary>Same rule as the Web Forms GetDayDifference: minimum 1 night.</summary>
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

    /// <summary>Same output as the Web Forms FormatDate: MM-dd-yyyy -> dd-MM-yyyy.</summary>
    private static string FormatDate(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "N/A";
        return DateTime.TryParseExact(value.Trim(), "MM-dd-yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)
            ? dt.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture)
            : "Invalid Date";
    }
}

public sealed class BulkCheckinResult
{
    public int Selected { get; set; }
    public int CheckedIn { get; set; }
    public int Failed { get; set; }
    public List<BulkCheckinRoomResult> Rooms { get; set; } = new();
}

public sealed class BulkCheckinRoomResult
{
    public string RegId { get; set; } = string.Empty;
    public string RoomNo { get; set; } = string.Empty;
    public string GuestName { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
}

public sealed class BulkCheckinAvailableRooms
{
    public bool Ok { get; set; }
    public string Message { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string CurrentRoom { get; set; } = string.Empty;
    public List<string> Rooms { get; set; } = new();
}

public sealed class BulkCheckinChangeRoomRequest
{
    public string? RegId { get; set; }
    public string? OldRoom { get; set; }
    public string? NewRoom { get; set; }
}

public sealed class BulkCheckinActionResponse
{
    public bool Ok { get; set; }
    public string Message { get; set; } = string.Empty;
}

/// <summary>Who is acting. Always built on the server from the signed-in session, never from the URL.</summary>
public sealed record BulkCheckinUserContext(
    string HotelId,
    string UserId,
    string UserName,
    string IdentityName,
    string IpAddress,
    string SystemName);

/// <summary>Date parsing used by the original page (TryParseAnyDate), kept identical.</summary>
public static class BulkCheckinDates
{
    private static readonly string[] Formats =
    {
        "MM-dd-yyyy", "M-d-yyyy", "MM/dd/yyyy", "M/d/yyyy",
        "dd/MM/yyyy", "d/M/yyyy",
        "yyyy-MM-dd", "yyyy-M-d",
        "MM-dd-yyyy hh:mm tt", "M-d-yyyy hh:mm tt",
        "MM/dd/yyyy hh:mm tt", "M/d/yyyy hh:mm tt",
        "yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-ddTHH:mm:ss.fff"
    };

    public static bool TryParseAnyDate(object? value, out DateTime date)
    {
        date = DateTime.MinValue;
        if (value is null || value is DBNull) return false;
        if (value is DateTime dt) { date = dt.Date; return true; }

        var s = Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
        if (s.Length == 0) return false;

        if (DateTime.TryParseExact(s, Formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out date) ||
            DateTime.TryParse(s, out date))
        {
            date = date.Date;
            return true;
        }
        return false;
    }
}

/// <summary>
/// Guest Profile popup (read-only). Only fields that exist in NewReservationsTB / GuestInformationLogTB,
/// payments (Room Rent lines) and PaymentsUpdateTB. ID / passport numbers are masked before they leave the server.
/// </summary>
public sealed class BulkCheckinGuestProfile
{
    public string RegId { get; set; } = string.Empty;
    public string GuestName { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string PhoneNo { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public string Country { get; set; } = string.Empty;
    public string IdNumberMasked { get; set; } = string.Empty;
    public string PassportMasked { get; set; } = string.Empty;
    public string Adults { get; set; } = string.Empty;
    public string Children { get; set; } = string.Empty;
    public string BookingSource { get; set; } = string.Empty;
    public string BookingReference { get; set; } = string.Empty;
    public string RatePlan { get; set; } = string.Empty;
    public string PaymentMethod { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
    public string ArrivalDate { get; set; } = string.Empty;   // dd-MM-yyyy
    public string DepartureDate { get; set; } = string.Empty; // dd-MM-yyyy
    public int Nights { get; set; }
    public decimal GrandTotal { get; set; }
    public decimal PaidAmount { get; set; }
    public decimal RemainingAmount { get; set; }
    public int? PreviousStays { get; set; }
    public string CurrencySymbol { get; set; } = string.Empty;
    public List<BulkCheckinProfileRoom> Rooms { get; set; } = new();

    public string Money(decimal value) => CurrencySymbol + value.ToString("N2", CultureInfo.InvariantCulture);
    public string NightsText => Nights <= 0 ? "—" : Nights + (Nights == 1 ? " Night" : " Nights");
    public string Location => string.Join(", ", new[] { Address, City, Country }.Where(x => !string.IsNullOrWhiteSpace(x)));

    /// <summary>Shows only the last 4 characters: "•••• 1234".</summary>
    public static string Mask(string? value)
    {
        var v = (value ?? string.Empty).Trim();
        if (v.Length == 0) return string.Empty;
        return v.Length <= 4 ? "••••" : "•••• " + v[^4..];
    }
}

public sealed class BulkCheckinProfileRoom
{
    public string RoomNo { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public decimal RoomTotal { get; set; }
}
