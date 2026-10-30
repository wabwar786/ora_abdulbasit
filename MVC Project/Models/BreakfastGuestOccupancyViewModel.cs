namespace Orapmshms.Models;

public sealed class BreakfastGuestOccupancyViewModel
{
    public string HotelName { get; set; } = string.Empty;
    public string HotelLogo { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public DateTime ReportDate { get; set; }
    public List<BreakfastGuestOccupancyRow> Rows { get; set; } = new();
    public int TotalAdults => Rows.Sum(r => r.Adults);
    public int TotalChildren => Rows.Sum(r => r.Children);
    public int TotalInfants => Rows.Sum(r => r.Infants);
}

public sealed class BreakfastGuestOccupancyRow
{
    public int? PaymentId { get; set; }
    public string RegId { get; set; } = string.Empty;
    public string GuestName { get; set; } = string.Empty;
    public string RoomNo { get; set; } = string.Empty;
    public int Adults { get; set; }
    public int Children { get; set; }
    public int Infants { get; set; }
    public string RoomCategory { get; set; } = string.Empty;
    public string RatePlanName { get; set; } = string.Empty;
    public DateTime? ArrivalDate { get; set; }
    public DateTime? DepartureDate { get; set; }
}
