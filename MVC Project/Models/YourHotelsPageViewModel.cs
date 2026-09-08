namespace Orapmshms.Models;

public sealed class YourHotelsPageViewModel
{
    public int SelectedYear { get; set; }
    public List<int> Years { get; set; } = new();
    public List<HotelCardViewModel> Hotels { get; set; } = new();

    public string[] OccupancyLabels { get; set; } = Array.Empty<string>();
    public List<OccupancyChartDataset> OccupancyDatasets { get; set; } = new();

    public string[] BookingSourceLabels { get; set; } = Array.Empty<string>();
    public List<BookingSourceChartDataset> BookingSourceDatasets { get; set; } = new();
    public List<string> BookingSourceFilter { get; set; } = new();

    public string Message { get; set; } = string.Empty;
}

public sealed class HotelCardViewModel
{
    public string HotelId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string ImageUrl { get; set; } = string.Empty;
    public string CityInfo { get; set; } = string.Empty;
    public int TodayReservations { get; set; }
    public int TodayCheckIns { get; set; }
    public int ExpectedCheckIns { get; set; }
    public int TotalRooms { get; set; }
    public int FreeRoomsToday { get; set; }
    public string CheckInDashOffset { get; set; } = "339.3";
    public string FreeDashOffset { get; set; } = "339.3";
    public bool CanOpenDashboard { get; set; }
}

public sealed class OccupancyChartDataset
{
    public string Label { get; set; } = string.Empty;
    public double[] Data { get; set; } = new double[12];
    public string BorderColor { get; set; } = string.Empty;
    public string BackgroundColor { get; set; } = string.Empty;
    public int BorderWidth { get; set; } = 2;
    public double Tension { get; set; } = 0.35;
    public int PointRadius { get; set; } = 2;
    public int PointHoverRadius { get; set; } = 4;
}

public sealed class BookingSourceChartDataset
{
    public string Label { get; set; } = string.Empty;
    public string HotelName { get; set; } = string.Empty;
    public string SourceName { get; set; } = string.Empty;
    public double[] Data { get; set; } = new double[12];
    public string BackgroundColor { get; set; } = string.Empty;
    public string BorderColor { get; set; } = string.Empty;
    public int BorderWidth { get; set; } = 1;
    public int BorderRadius { get; set; } = 4;
    public double BarPercentage { get; set; } = 0.72;
    public double CategoryPercentage { get; set; } = 0.62;
    public int MaxBarThickness { get; set; } = 16;
}

public sealed record OpenHotelResult(
    bool Success,
    string Message,
    string RedirectUrl = "",
    OpenHotelAccount? Account = null);

public sealed class OpenHotelAccount
{
    public string Email { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public string HotelId { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string HotelRole { get; set; } = string.Empty;
    public string ActiveStatus { get; set; } = string.Empty;
}
