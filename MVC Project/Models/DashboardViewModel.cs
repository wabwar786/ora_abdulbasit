namespace Orapmshms.Models;

// ============================================================
// DashboardViewModel
// ============================================================
public sealed class DashboardViewModel
{
    public string HotelId { get; set; } = string.Empty;
    public string HotelName { get; set; } = string.Empty;
    public string CurrencySymbol { get; set; } = "£";
    public DateTime HotelToday { get; set; }
    public DateTime GeneratedAt { get; set; }

    public int TotalRooms { get; set; }
    public int SellableRoomsToday { get; set; }
    public int RoomsSoldToday { get; set; }
    public int AvailableRoomsToday { get; set; }
    public int DirtyRoomsToday { get; set; }
    public int BlockedRoomsToday { get; set; }
    public int ArrivalsToday { get; set; }
    public int DeparturesToday { get; set; }
    public int InHouseGuests { get; set; }
    public decimal AverageStayNights { get; set; }
    public decimal RepeatGuestPercent { get; set; }
    public decimal GuestsPerRoom { get; set; }

    public decimal OccupancyToday { get; set; }
    public decimal OccupancyNext30 { get; set; }
    public decimal OccupancyNext60 { get; set; }
    public decimal OccupancyNext90 { get; set; }
    public decimal OccupancyNext30LastYear { get; set; }
    public decimal ForecastNext7 { get; set; }

    public decimal AccommodationRevenueToday { get; set; }
    public decimal AdrToday { get; set; }
    public decimal RevParToday { get; set; }

    public decimal RevenueToday { get; set; }
    public decimal RevenueTodayLastYear { get; set; }
    public decimal ExpensesToday { get; set; }
    public decimal ExpensesLast30 { get; set; }
    public decimal ProfitToday { get; set; }
    public decimal RevenueLast30 { get; set; }
    public decimal RevenueLast30LastYear { get; set; }
    public decimal ProfitLast30 { get; set; }
    public decimal ProfitLast30LastYear { get; set; }
    public decimal ProfitMarginMonthToDate { get; set; }
    public decimal ProfitMarginMonthToDateLastYear { get; set; }

    // Outstanding balance for reservations arriving today.
    public decimal ReceivablesToday { get; set; }

    // Outstanding balance for reservations whose arrival is within the
    // current hotel-local 30-day dashboard period (today - 29 days .. today).
    public decimal ReceivablesLast30Arrivals { get; set; }

    // Kept for compatibility with existing dashboard code.
    public decimal Receivables { get; set; }
    public decimal ReceivablesOver30Days { get; set; }

    public int NoShowsToday { get; set; }
    public int NoShowsMonthToDate { get; set; }
    public int NoShowsLast30 { get; set; }
    public int CancellationsToday { get; set; }
    public int CancellationsMonthToDate { get; set; }
    public int CancellationsLast30 { get; set; }
    public decimal CancellationRateLast30 { get; set; }

    public List<DashboardRoomStatusItem> RoomStatus { get; set; } = new();
    public List<DashboardGuestItem> InHouseGuestRows { get; set; } = new();
    public List<DashboardDailyPoint> OccupancySeries { get; set; } = new();
    public List<DashboardDailyPoint> OccupancyLastYearSeries { get; set; } = new();
    public List<DashboardFinancialPoint> FinancialSeries { get; set; } = new();
    public List<DashboardRevenueSourceItem> RevenueSources { get; set; } = new();
    public List<DashboardRoomTypeItem> RoomTypes { get; set; } = new();
    public List<DashboardExceptionPoint> ExceptionSeries { get; set; } = new();
    public List<DashboardExceptionSourceItem> ExceptionSources { get; set; } = new();
    public List<DashboardPriceRecommendation> PriceRecommendations { get; set; } = new();
    public List<DashboardDecisionItem> Decisions { get; set; } = new();

    public string Message { get; set; } = string.Empty;
}

// ============================================================
// DashboardAnalyticsResult
// ============================================================
public sealed class DashboardAnalyticsResult
{
    public decimal OccupancyNext30 { get; set; }
    public decimal OccupancyNext60 { get; set; }
    public decimal OccupancyNext90 { get; set; }
    public decimal OccupancyNext30LastYear { get; set; }
    public decimal ForecastNext7 { get; set; }

    public List<DashboardDailyPoint> OccupancySeries { get; set; } = new();
    public List<DashboardDailyPoint> OccupancyLastYearSeries { get; set; } = new();
    public List<DashboardFinancialPoint> FinancialSeries { get; set; } = new();
    public List<DashboardRevenueSourceItem> RevenueSources { get; set; } = new();
    public List<DashboardRoomTypeItem> RoomTypes { get; set; } = new();
    public List<DashboardPriceRecommendation> PriceRecommendations { get; set; } = new();
    public List<DashboardDecisionItem> Decisions { get; set; } = new();
}

// ============================================================
// DashboardDailyPoint
// ============================================================
public sealed class DashboardDailyPoint
{
    public DateTime Date { get; set; }
    public int Inventory { get; set; }
    public int OutOfInventory { get; set; }
    public int SellableInventory { get; set; }
    public int RoomsSold { get; set; }
    public decimal OccupancyPercent { get; set; }
    public decimal AccommodationRevenue { get; set; }
    public decimal Adr { get; set; }
    public decimal RevPar { get; set; }
}

// ============================================================
// DashboardDecisionItem
// ============================================================
public sealed class DashboardDecisionItem
{
    public string Tone { get; set; } = "info";
    public string Title { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
}

// ============================================================
// DashboardExceptionPoint
// ============================================================
public sealed class DashboardExceptionPoint
{
    public string Label { get; set; } = string.Empty;
    public int Cancellations { get; set; }
    public int NoShows { get; set; }
}

// ============================================================
// DashboardExceptionSourceItem
// ============================================================
public sealed class DashboardExceptionSourceItem
{
    public string Source { get; set; } = string.Empty;
    public int Count { get; set; }
    public decimal RatePercent { get; set; }
}

// ============================================================
// DashboardFinancialPoint
// ============================================================
public sealed class DashboardFinancialPoint
{
    public DateTime Date { get; set; }
    public decimal Revenue { get; set; }
    public decimal Expense { get; set; }
    public decimal Profit { get; set; }
    public decimal LastYearRevenue { get; set; }
    public decimal LastYearProfit { get; set; }
}

// ============================================================
// DashboardGuestItem
// ============================================================
public sealed class DashboardGuestItem
{
    public string RoomNo { get; set; } = string.Empty;
    public string GuestName { get; set; } = string.Empty;
    public string ReservationId { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string RatePlan { get; set; } = string.Empty;
    public DateTime ArrivalDate { get; set; }
    public DateTime DepartureDate { get; set; }
    public int Nights { get; set; }
    public int Guests { get; set; }
    public decimal Balance { get; set; }
    public bool DueOutToday { get; set; }
}

// ============================================================
// DashboardPriceRecommendation
// ============================================================
public sealed class DashboardPriceRecommendation
{
    public string Category { get; set; } = string.Empty;
    public string PlanName { get; set; } = string.Empty;
    public decimal CurrentRate { get; set; }
    public decimal RecommendedRate { get; set; }
    public decimal ChangePercent { get; set; }
    public decimal OccupancyNext30 { get; set; }
    public string Demand { get; set; } = "Normal";
    public string Reason { get; set; } = string.Empty;
}

// ============================================================
// DashboardRevenueSourceItem
// ============================================================
public sealed class DashboardRevenueSourceItem
{
    public string Source { get; set; } = string.Empty;
    public int Reservations { get; set; }
    public int RoomNights { get; set; }
    public decimal Adr { get; set; }
    public decimal Revenue { get; set; }
    public decimal SharePercent { get; set; }
}

// ============================================================
// DashboardRoomStatusItem
// ============================================================
public sealed class DashboardRoomStatusItem
{
    public string RoomNo { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string State { get; set; } = "vacant";
    public string GuestName { get; set; } = string.Empty;
    public string ReservationId { get; set; } = string.Empty;
    public DateTime? ArrivalDate { get; set; }
    public DateTime? DepartureDate { get; set; }
    public decimal Balance { get; set; }
    public string PaymentState { get; set; } = string.Empty;
    public bool IsDirty { get; set; }
}

// ============================================================
// DashboardRoomTypeItem
// ============================================================
public sealed class DashboardRoomTypeItem
{
    public string Category { get; set; } = string.Empty;
    public int Rooms { get; set; }
    public decimal OccupancyTonight { get; set; }
    public decimal OccupancyNext30 { get; set; }
    public decimal Adr { get; set; }
    public decimal RevPar { get; set; }
    public decimal Revenue { get; set; }
    public decimal ContributionPercent { get; set; }
    public decimal PaceVsLastYear { get; set; }
    public bool HasPaceVsLastYear { get; set; }
    public string Demand { get; set; } = "Normal";
}

// ============================================================
// DashboardStatisticDetailResult
// ============================================================
public sealed class DashboardStatisticDetailResult
{
    public string Title { get; set; } = string.Empty;
    public string Subtitle { get; set; } = string.Empty;
    public string SummaryLabel { get; set; } = string.Empty;
    public string SummaryValue { get; set; } = string.Empty;
    public List<string> Columns { get; set; } = new();
    public List<List<string>> Rows { get; set; } = new();
}
