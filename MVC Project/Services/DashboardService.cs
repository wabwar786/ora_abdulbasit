using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Caching.Memory;
using Orapmshms.Models;

namespace Orapmshms.Services;

public sealed class DashboardService : IDashboardService
{
    private readonly string _connectionString;
    private readonly IHotelClock _hotelClock;
    private readonly IMemoryCache _cache;
    private readonly ILogger<DashboardService> _logger;

    public DashboardService(
        IConfiguration configuration,
        IHotelClock hotelClock,
        IMemoryCache cache,
        ILogger<DashboardService> logger)
    {
        _connectionString = configuration.GetConnectionString("con")
            ?? throw new InvalidOperationException("ConnectionStrings:con is missing from configuration.");
        _hotelClock = hotelClock;
        _cache = cache;
        _logger = logger;
    }

    public async Task<DashboardViewModel> GetAsync(
        string hotelId,
        CancellationToken cancellationToken = default)
    {
        hotelId = Clean(hotelId, 50);
        if (string.IsNullOrWhiteSpace(hotelId))
            throw new UnauthorizedAccessException("Hotel context is missing.");

        var today = _hotelClock.GetHotelToday(hotelId).Date;
        var now = _hotelClock.GetHotelNow(hotelId);

        // Keep the first paint intentionally small. The expensive 60/90-day
        // charts, revenue-source breakdown, room-type analysis and pricing
        // recommendations are fetched by /Dashboard/Analytics after the page
        // is interactive. These seven tasks are the live headline data only.
        var hotelTask = SafeLoadAsync(
            () => CachedAsync(
                $"dashboard:hotel:{hotelId}",
                TimeSpan.FromMinutes(10),
                () => LoadHotelAsync(hotelId, cancellationToken)),
            new DashboardHotelInfo(string.Empty, "£"),
            "hotel header");

        var roomTask = SafeLoadAsync(
            () => CachedAsync(
                $"dashboard:rooms:{hotelId}:{today:yyyyMMdd}",
                TimeSpan.FromSeconds(10),
                () => LoadRoomStatusInternalAsync(hotelId, today, cancellationToken)),
            new List<DashboardRoomStatusItem>(),
            "room status");

        var operationsTask = SafeLoadAsync(
            () => CachedAsync(
                $"dashboard:operations:v28:{hotelId}:{today:yyyyMMdd}",
                TimeSpan.FromSeconds(5),
                () => LoadOperationsAsync(hotelId, today, cancellationToken)),
            new DashboardOperationsData(),
            "operations");

        var guestsTask = SafeLoadAsync(
            () => CachedAsync(
                $"dashboard:guests:v28:{hotelId}:{today:yyyyMMdd}",
                TimeSpan.FromSeconds(15),
                () => LoadInHouseGuestsAsync(hotelId, today, cancellationToken)),
            new List<DashboardGuestItem>(),
            "in-house guests");

        var occupancy30Task = SafeLoadAsync(
            () => CachedAsync(
                $"dashboard:occupancy30:{hotelId}:{today:yyyyMMdd}",
                TimeSpan.FromSeconds(45),
                () => LoadOccupancySeriesAsync(hotelId, today, today.AddDays(29), cancellationToken)),
            new List<DashboardDailyPoint>(),
            "30-day occupancy");

        var occupancy30LastYearTask = SafeLoadAsync(
            () => CachedAsync(
                $"dashboard:occupancy30-ly:{hotelId}:{today:yyyyMMdd}",
                TimeSpan.FromMinutes(2),
                () => LoadOccupancySeriesAsync(hotelId, today.AddYears(-1), today.AddYears(-1).AddDays(29), cancellationToken)),
            new List<DashboardDailyPoint>(),
            "30-day last-year occupancy");

        // Keep financial, receivable and exception formulas isolated. This is
        // faster in practice on the Dashboard and, more importantly, one slow
        // or malformed legacy data set can no longer zero every headline KPI.
        // Each query mirrors the active WebForms Dashboard formula and is
        // cached independently for a very short period.
        var financialTask = SafeLoadAsync(
            () => CachedAsync(
                $"dashboard:financial-snapshot:{hotelId}:{today:yyyyMMdd}",
                TimeSpan.FromSeconds(5),
                () => LoadFinancialSnapshotAsync(hotelId, today, cancellationToken)),
            new DashboardFinancialSnapshot(),
            "financial headline");

        var receivablesTask = SafeLoadAsync(
            () => CachedAsync(
                $"dashboard:receivables:{hotelId}:{today:yyyyMMdd}",
                TimeSpan.FromSeconds(10),
                () => LoadReceivablesAsync(hotelId, today, cancellationToken)),
            new DashboardReceivableData(),
            "receivables headline");

        var exceptionsTask = SafeLoadAsync(
            () => CachedAsync(
                $"dashboard:exceptions:{hotelId}:{today:yyyyMMdd}",
                TimeSpan.FromSeconds(15),
                () => LoadExceptionsAsync(hotelId, today, cancellationToken)),
            new DashboardExceptionData(),
            "exception headline");

        await Task.WhenAll(
            hotelTask,
            roomTask,
            operationsTask,
            guestsTask,
            occupancy30Task,
            occupancy30LastYearTask,
            financialTask,
            receivablesTask,
            exceptionsTask);

        var hotel = await hotelTask;
        var rooms = await roomTask;
        var operations = await operationsTask;
        var guests = await guestsTask;
        var occupancy30 = await occupancy30Task;
        var occupancy30LastYear = await occupancy30LastYearTask;
        var financial = await financialTask;
        var receivables = await receivablesTask;
        var exceptions = await exceptionsTask;
        var todayOcc = occupancy30.FirstOrDefault(x => x.Date.Date == today);

        var model = new DashboardViewModel
        {
            HotelId = hotelId,
            HotelName = string.IsNullOrWhiteSpace(hotel.Name) ? "Hotel" : hotel.Name,
            CurrencySymbol = string.IsNullOrWhiteSpace(hotel.CurrencySymbol) ? "£" : hotel.CurrencySymbol,
            HotelToday = today,
            GeneratedAt = now,
            RoomStatus = rooms,
            InHouseGuestRows = guests,
            // The first 30 days are enough for the headline card and for the
            // initial on-the-books chart. Longer analytics arrive lazily.
            OccupancySeries = occupancy30,
            OccupancyLastYearSeries = occupancy30LastYear,
            ExceptionSeries = exceptions.Series,
            ExceptionSources = exceptions.Sources,
            ArrivalsToday = operations.Arrivals,
            DeparturesToday = operations.Departures,
            InHouseGuests = operations.InHouseGuests,
            AverageStayNights = operations.AverageStay,
            RepeatGuestPercent = operations.RepeatGuestPercent,
            GuestsPerRoom = operations.GuestsPerRoom,
            ReceivablesToday = receivables.Today,
            ReceivablesLast30Arrivals = receivables.Last30Arrivals,
            Receivables = receivables.Today,
            ReceivablesOver30Days = receivables.Over30Days,
            NoShowsToday = exceptions.NoShowsToday,
            NoShowsMonthToDate = exceptions.NoShowsMonthToDate,
            NoShowsLast30 = exceptions.NoShowsLast30,
            CancellationsToday = exceptions.CancellationsToday,
            CancellationsMonthToDate = exceptions.CancellationsMonthToDate,
            CancellationsLast30 = exceptions.CancellationsLast30,
            CancellationRateLast30 = exceptions.CancellationRateLast30
        };

        // RoomStatus is already loaded with the same active-stay/block precedence
        // as the WebForms room query. Reuse it for the visible counts instead of
        // running a second room summary query on every Dashboard request.
        model.TotalRooms = rooms.Count;
        model.DirtyRoomsToday = rooms.Count(x => x.IsDirty || string.Equals(x.State, "dirty", StringComparison.OrdinalIgnoreCase));
        model.BlockedRoomsToday = rooms.Count(x => string.Equals(x.State, "ooo", StringComparison.OrdinalIgnoreCase));
        model.RoomsSoldToday = rooms.Count(x => x.State is "checkin" or "reservation");
        model.AvailableRoomsToday = Math.Max(0, model.TotalRooms - model.BlockedRoomsToday - model.RoomsSoldToday);

        model.SellableRoomsToday = Math.Max(0, model.TotalRooms - model.BlockedRoomsToday);
        model.OccupancyToday = Percent(model.RoomsSoldToday, model.SellableRoomsToday);
        model.AccommodationRevenueToday = todayOcc?.AccommodationRevenue ?? 0m;
        model.AdrToday = model.RoomsSoldToday <= 0
            ? 0m
            : Math.Round(model.AccommodationRevenueToday / model.RoomsSoldToday, 2);
        model.RevParToday = model.SellableRoomsToday <= 0
            ? 0m
            : Math.Round(model.AccommodationRevenueToday / model.SellableRoomsToday, 2);

        if (model.InHouseGuests > 0 && model.GuestsPerRoom <= 0m)
            model.GuestsPerRoom = 1m;

        model.OccupancyNext30 = WeightedOccupancy(occupancy30, today, today.AddDays(29));
        model.OccupancyNext30LastYear = WeightedOccupancy(
            occupancy30LastYear,
            today.AddYears(-1),
            today.AddYears(-1).AddDays(29));
        model.ForecastNext7 = WeightedOccupancy(occupancy30, today, today.AddDays(6));

        // Use the same WebForms sources for the visible headline values.
        model.RevenueToday = financial.RevenueToday;
        model.RevenueTodayLastYear = financial.RevenueTodayLastYear;
        model.ExpensesToday = financial.ExpensesToday;
        model.ProfitToday = financial.RevenueToday - financial.ExpensesToday;
        model.RevenueLast30 = financial.RevenueLast30;
        model.RevenueLast30LastYear = financial.RevenueLast30LastYear;
        model.ExpensesLast30 = financial.ExpenseLast30;
        model.ProfitLast30 = financial.RevenueLast30 - financial.ExpenseLast30;
        model.ProfitLast30LastYear = financial.RevenueLast30LastYear - financial.ExpenseLast30LastYear;

        var monthProfit = financial.MonthRevenue - financial.MonthExpense;
        model.ProfitMarginMonthToDate = financial.MonthRevenue <= 0m
            ? 0m
            : Math.Round(monthProfit * 100m / financial.MonthRevenue, 1);

        var lastYearMonthProfit = financial.LastYearMonthRevenue - financial.LastYearMonthExpense;
        model.ProfitMarginMonthToDateLastYear = financial.LastYearMonthRevenue <= 0m
            ? 0m
            : Math.Round(lastYearMonthProfit * 100m / financial.LastYearMonthRevenue, 1);

        return model;
    }

    public async Task<DashboardAnalyticsResult> GetAnalyticsAsync(
        string hotelId,
        CancellationToken cancellationToken = default)
    {
        hotelId = Clean(hotelId, 50);
        if (string.IsNullOrWhiteSpace(hotelId))
            throw new UnauthorizedAccessException("Hotel context is missing.");

        var today = _hotelClock.GetHotelToday(hotelId).Date;

        // These are lower-page analytics. Loading them after first paint keeps
        // Dashboard interaction responsive even on hotels with a long history.
        var occupancyTask = SafeLoadAsync(
            () => CachedAsync(
                $"dashboard:analytics-occupancy:{hotelId}:{today:yyyyMMdd}",
                TimeSpan.FromMinutes(2),
                () => LoadOccupancySeriesAsync(hotelId, today.AddDays(-15), today.AddDays(89), cancellationToken)),
            new List<DashboardDailyPoint>(),
            "analytics occupancy");

        var occupancyLastYearTask = SafeLoadAsync(
            () => CachedAsync(
                $"dashboard:analytics-occupancy-ly:{hotelId}:{today:yyyyMMdd}",
                TimeSpan.FromMinutes(5),
                () => LoadOccupancySeriesAsync(hotelId, today.AddYears(-1).AddDays(-15), today.AddYears(-1).AddDays(89), cancellationToken)),
            new List<DashboardDailyPoint>(),
            "analytics last-year occupancy");

        var financialTask = SafeLoadAsync(
            () => CachedAsync(
                $"dashboard:financial:{hotelId}:{today:yyyyMMdd}",
                TimeSpan.FromMinutes(2),
                () => LoadFinancialSeriesAsync(hotelId, today, cancellationToken)),
            new List<DashboardFinancialPoint>(),
            "financial trend");

        var revenueSourcesTask = SafeLoadAsync(
            () => CachedAsync(
                $"dashboard:revenue-sources:{hotelId}:{today:yyyyMMdd}",
                TimeSpan.FromMinutes(5),
                () => LoadRevenueSourcesAsync(hotelId, today, today.AddDays(29), cancellationToken)),
            new List<DashboardRevenueSourceItem>(),
            "revenue sources");

        var roomTypesTask = SafeLoadAsync(
            () => CachedAsync(
                $"dashboard:room-types:{hotelId}:{today:yyyyMMdd}",
                TimeSpan.FromMinutes(5),
                () => LoadRoomTypesAsync(hotelId, today, cancellationToken)),
            new List<DashboardRoomTypeItem>(),
            "room type performance");

        var ratesTask = SafeLoadAsync(
            () => CachedAsync(
                $"dashboard:rates:{hotelId}:{today:yyyyMMdd}",
                TimeSpan.FromMinutes(5),
                () => LoadCurrentRatesAsync(hotelId, today, cancellationToken)),
            new List<DashboardCurrentRate>(),
            "current rates");

        var receivablesTask = SafeLoadAsync(
            () => CachedAsync(
                $"dashboard:receivables:{hotelId}:{today:yyyyMMdd}",
                TimeSpan.FromSeconds(10),
                () => LoadReceivablesAsync(hotelId, today, cancellationToken)),
            new DashboardReceivableData(),
            "analytics receivables");

        var exceptionsTask = SafeLoadAsync(
            () => CachedAsync(
                $"dashboard:exceptions:{hotelId}:{today:yyyyMMdd}",
                TimeSpan.FromSeconds(15),
                () => LoadExceptionsAsync(hotelId, today, cancellationToken)),
            new DashboardExceptionData(),
            "analytics exceptions");

        var hotelTask = SafeLoadAsync(
            () => CachedAsync(
                $"dashboard:hotel:{hotelId}",
                TimeSpan.FromMinutes(10),
                () => LoadHotelAsync(hotelId, cancellationToken)),
            new DashboardHotelInfo(string.Empty, "£"),
            "analytics hotel");

        await Task.WhenAll(
            occupancyTask,
            occupancyLastYearTask,
            financialTask,
            revenueSourcesTask,
            roomTypesTask,
            ratesTask,
            receivablesTask,
            exceptionsTask,
            hotelTask);

        var occupancy = await occupancyTask;
        var occupancyLastYear = await occupancyLastYearTask;
        var financial = await financialTask;
        var revenueSources = await revenueSourcesTask;
        var roomTypes = await roomTypesTask;
        var rates = await ratesTask;
        var receivables = await receivablesTask;
        var exceptions = await exceptionsTask;
        var hotel = await hotelTask;

        ApplyRoomTypeContribution(roomTypes);

        var decisionModel = new DashboardViewModel
        {
            CurrencySymbol = string.IsNullOrWhiteSpace(hotel.CurrencySymbol) ? "£" : hotel.CurrencySymbol,
            RoomTypes = roomTypes,
            RevenueSources = revenueSources,
            ReceivablesOver30Days = receivables.Over30Days,
            CancellationsLast30 = exceptions.CancellationsLast30,
            CancellationRateLast30 = exceptions.CancellationRateLast30
        };

        return new DashboardAnalyticsResult
        {
            OccupancyNext30 = WeightedOccupancy(occupancy, today, today.AddDays(29)),
            OccupancyNext60 = WeightedOccupancy(occupancy, today, today.AddDays(59)),
            OccupancyNext90 = WeightedOccupancy(occupancy, today, today.AddDays(89)),
            OccupancyNext30LastYear = WeightedOccupancy(
                occupancyLastYear,
                today.AddYears(-1),
                today.AddYears(-1).AddDays(29)),
            ForecastNext7 = WeightedOccupancy(occupancy, today, today.AddDays(6)),
            OccupancySeries = occupancy,
            OccupancyLastYearSeries = occupancyLastYear,
            FinancialSeries = financial,
            RevenueSources = revenueSources.Take(8).ToList(),
            RoomTypes = roomTypes,
            PriceRecommendations = BuildPriceRecommendations(rates, roomTypes),
            Decisions = BuildDecisions(decisionModel)
        };
    }

    public async Task<IReadOnlyList<DashboardRoomStatusItem>> GetRoomStatusAsync(
        string hotelId,
        DateTime date,
        CancellationToken cancellationToken = default)
    {
        hotelId = Clean(hotelId, 50);
        if (string.IsNullOrWhiteSpace(hotelId))
            return Array.Empty<DashboardRoomStatusItem>();

        return await CachedAsync(
            $"dashboard:rooms:{hotelId}:{date:yyyyMMdd}",
            TimeSpan.FromSeconds(10),
            () => LoadRoomStatusInternalAsync(hotelId, date.Date, cancellationToken));
    }

    public async Task<DashboardStatisticDetailResult> GetStatisticDetailsAsync(
        string hotelId,
        string statistic,
        DateTime date,
        CancellationToken cancellationToken = default)
    {
        hotelId = Clean(hotelId, 50);
        statistic = Clean(statistic, 30).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(hotelId))
            throw new UnauthorizedAccessException("Hotel context is missing.");

        var hotel = await CachedAsync(
            $"dashboard:hotel:{hotelId}",
            TimeSpan.FromMinutes(10),
            () => LoadHotelAsync(hotelId, cancellationToken));
        var currency = string.IsNullOrWhiteSpace(hotel.CurrencySymbol) ? "£" : hotel.CurrencySymbol;

        return statistic switch
        {
            "revenue" => await LoadRevenueDetailsAsync(hotelId, date.Date, currency, cancellationToken),
            "receivables" => await LoadReceivableDetailsAsync(hotelId, date.Date, currency, cancellationToken),
            "expenses" => await LoadExpenseDetailsAsync(hotelId, date.Date, currency, cancellationToken),
            "profit" => await LoadProfitDetailsAsync(hotelId, date.Date, currency, cancellationToken),
            "noshow" => await LoadExceptionDetailsAsync(hotelId, date.Date, false, cancellationToken),
            "cancellations" => await LoadExceptionDetailsAsync(hotelId, date.Date, true, cancellationToken),
            "occupancy" => await LoadOccupancyDetailsAsync(hotelId, date.Date, cancellationToken),
            "profitmargin" => await LoadProfitMarginDetailsAsync(hotelId, date.Date, currency, cancellationToken),
            "checkin" => await LoadMovementDetailsAsync(hotelId, date.Date, true, cancellationToken),
            "checkout" => await LoadMovementDetailsAsync(hotelId, date.Date, false, cancellationToken),
            "available" => await LoadRoomStateDetailsAsync(hotelId, date.Date, "vacant", cancellationToken),
            "occupied" => await LoadOccupiedRoomDetailsAsync(hotelId, date.Date, cancellationToken),
            "blocked" => await LoadRoomStateDetailsAsync(hotelId, date.Date, "ooo", cancellationToken),
            "dirty" => await LoadRoomStateDetailsAsync(hotelId, date.Date, "dirty", cancellationToken),
            _ => new DashboardStatisticDetailResult
            {
                Title = "Dashboard details",
                Subtitle = date.ToString("dd MMM yyyy", CultureInfo.InvariantCulture),
                SummaryLabel = "Status",
                SummaryValue = "No detail view is available for this statistic."
            }
        };
    }

    private async Task<DashboardStatisticDetailResult> LoadRevenueDetailsAsync(
        string hotelId,
        DateTime date,
        string currency,
        CancellationToken cancellationToken)
    {
        const string sql = @"
DECLARE @isClosing bit = 0;

SELECT TOP (1)
    @isClosing =
        CASE
            WHEN TRY_CONVERT(int, ISNULL(isClosing,0)) = 1 THEN 1
            ELSE 0
        END
FROM dbo.HotelsSignUpTB
WHERE CONVERT(varchar(50),hotel_id) = @hotel;

WITH RevenueRows AS
(
    SELECT
        TRY_CONVERT(
            date,
            REPLACE(NULLIF(LTRIM(RTRIM(CONVERT(varchar(100),pl.currentdate))), ''), '  ', ' '),
            100
        ) AS tx_date,
        CONVERT(varchar(100),pl.currentdate) AS transaction_date,
        ISNULL(CONVERT(varchar(100),pl.status),'') AS [status],
        TRY_CONVERT(
            decimal(18,2),
            NULLIF(
                REPLACE(
                    REPLACE(LTRIM(RTRIM(CONVERT(varchar(100),pl.paid_amount))), ',', ''),
                    '£',
                    ''
                ),
                ''
            )
        ) AS paid_amount,
        pl.ID
    FROM dbo.PaymentsLogTB AS pl
    WHERE CONVERT(varchar(50),pl.hotel_id) = @hotel
      AND (@isClosing = 0 OR LTRIM(RTRIM(CONVERT(varchar(20),pl.cb_status))) = '2')
)
SELECT transaction_date, [status], COALESCE(paid_amount,0) AS paid_amount
FROM RevenueRows
WHERE tx_date = @date
ORDER BY ID DESC;";

        var result = NewDetail(
            "Total revenue today",
            date,
            "Total",
            string.Empty,
            "Date",
            "Status",
            "Paid amount");

        decimal total = 0m;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand(sql, connection)
        {
            CommandTimeout = 20
        };

        command.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId.Trim();
        command.Parameters.Add("@date", SqlDbType.Date).Value = date.Date;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var amount = ToDecimal(reader["paid_amount"]);
            total += amount;

            result.Rows.Add(new List<string>
            {
                Convert.ToString(reader["transaction_date"])?.Trim() ?? string.Empty,
                Convert.ToString(reader["status"])?.Trim() ?? string.Empty,
                currency + amount.ToString("N2", CultureInfo.InvariantCulture)
            });
        }

        result.SummaryValue = currency + total.ToString("N2", CultureInfo.InvariantCulture);
        return result;
    }

    private async Task<DashboardStatisticDetailResult> LoadReceivableDetailsAsync(
        string hotelId,
        DateTime date,
        string currency,
        CancellationToken cancellationToken)
    {
        const string sql = @"
;WITH Unified AS
(
    SELECT
        LTRIM(RTRIM(g.hotel_id)) AS hotel_id,
        LTRIM(RTRIM(g.reg_id)) AS reg_id,
        LTRIM(RTRIM(ISNULL(g.GuestName,''))) AS guest_name,
        COALESCE(
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(g.ArrivalDate)), ''), 110),
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(g.ArrivalDate)), ''), 103),
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(g.ArrivalDate)), ''))
        ) AS arr_date,
        COALESCE(
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(g.dept_date)), ''), 110),
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(g.dept_date)), ''), 103),
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(g.dept_date)), ''))
        ) AS dept_date
    FROM dbo.NewReservationsTB g
    WHERE g.hotel_id = @hotel

    UNION ALL

    SELECT
        LTRIM(RTRIM(g.hotel_id)),
        LTRIM(RTRIM(g.reg_id)),
        LTRIM(RTRIM(ISNULL(g.GuestName,''))),
        COALESCE(
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(g.ArrivalDate)), ''), 110),
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(g.ArrivalDate)), ''), 103),
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(g.ArrivalDate)), ''))
        ),
        COALESCE(
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(g.DepartureDate)), ''), 110),
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(g.DepartureDate)), ''), 103),
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(g.DepartureDate)), ''))
        )
    FROM dbo.GuestInformationLogTB g
    WHERE g.hotel_id = @hotel
),
Res AS
(
    SELECT
        hotel_id,
        reg_id,
        MAX(guest_name) AS guest_name,
        MIN(arr_date) AS arr_date,
        MAX(dept_date) AS dept_date
    FROM Unified
    WHERE NULLIF(reg_id,'') IS NOT NULL
    GROUP BY hotel_id, reg_id
)
SELECT
    r.reg_id,
    r.guest_name,
    r.arr_date,
    r.dept_date,
    ISNULL(CONVERT(varchar(100),pu.payment_method),'') AS payment_method,
    CONVERT(varchar(100),pu.currentdate) AS last_update,
    pu.remaining_dec AS remaining_amount
FROM Res r
OUTER APPLY
(
    SELECT TOP (1)
        TRY_CONVERT(decimal(18,2),
            NULLIF(REPLACE(REPLACE(REPLACE(LTRIM(RTRIM(CONVERT(varchar(100),p.remaining_amount))), ',', ''), '£', ''), '$', ''), '')
        ) AS remaining_dec,
        p.payment_method,
        p.currentdate,
        p.id
    FROM dbo.PaymentsUpdateTB p
    WHERE LTRIM(RTRIM(CONVERT(varchar(100),p.hotel_id))) = r.hotel_id
      AND LTRIM(RTRIM(p.reg_id)) = r.reg_id
    ORDER BY p.currentdate DESC, p.id DESC
) pu
WHERE r.arr_date >= @startdate
  AND r.arr_date <= @enddate
  AND r.hotel_id = @hotel
  AND pu.remaining_dec IS NOT NULL
  AND pu.remaining_dec > 0
ORDER BY r.arr_date, r.reg_id;";

        var result = NewDetail("Receivables", date, "Last 30-day arrivals", string.Empty,
            "Reservation", "Guest", "Arrival", "Departure", "Method", "Remaining");
        result.Subtitle = date.AddDays(-29).ToString("dd MMM yyyy", CultureInfo.InvariantCulture)
            + " – " + date.ToString("dd MMM yyyy", CultureInfo.InvariantCulture);
        decimal total = 0m;
        decimal todayTotal = 0m;
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 30 };
        command.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
        command.Parameters.Add("@startdate", SqlDbType.Date).Value = date.Date.AddDays(-29);
        command.Parameters.Add("@enddate", SqlDbType.Date).Value = date.Date;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var remaining = ToDecimal(reader["remaining_amount"]);
            total += remaining;
            if (reader["arr_date"] != DBNull.Value && Convert.ToDateTime(reader["arr_date"], CultureInfo.InvariantCulture).Date == date.Date)
                todayTotal += remaining;
            result.Rows.Add(new List<string>
            {
                Convert.ToString(reader["reg_id"])?.Trim() ?? string.Empty,
                Convert.ToString(reader["guest_name"])?.Trim() ?? string.Empty,
                FormatDate(reader["arr_date"]),
                FormatDate(reader["dept_date"]),
                Convert.ToString(reader["payment_method"])?.Trim() ?? string.Empty,
                currency + remaining.ToString("N2", CultureInfo.InvariantCulture)
            });
        }
        result.Subtitle = "Today arrivals outstanding: " + currency + todayTotal.ToString("N2", CultureInfo.InvariantCulture)
            + " · Last 30-day arrivals outstanding: " + currency + total.ToString("N2", CultureInfo.InvariantCulture);
        result.SummaryValue = currency + total.ToString("N2", CultureInfo.InvariantCulture);
        return result;
    }

    private async Task<DashboardStatisticDetailResult> LoadExpenseDetailsAsync(
        string hotelId,
        DateTime date,
        string currency,
        CancellationToken cancellationToken)
    {
        const string sql = @"
SELECT
    ISNULL(CONVERT(varchar(150),e.category),'') AS Category,
    ISNULL(CONVERT(varchar(300),e.description),'') AS Description,
    ISNULL(CONVERT(varchar(100),e.expense_type),'') AS ExpenseType,
    CONVERT(varchar(100),e.currentdate) AS TransactionDate,
    COALESCE(TRY_CONVERT(decimal(18,2),e.amount),0) AS Amount
FROM dbo.ExpenseDetailTB e
WHERE CONVERT(varchar(50),e.hotel_id) = @hotel
  AND COALESCE(
        TRY_CONVERT(date, CONVERT(varchar(100),e.currentdate), 101),
        TRY_CONVERT(date, CONVERT(varchar(100),e.currentdate), 103),
        TRY_CONVERT(date, CONVERT(varchar(100),e.currentdate), 100),
        TRY_CONVERT(date, CONVERT(varchar(100),e.currentdate))
      ) = @date
  AND LTRIM(RTRIM(ISNULL(e.transaction_type,''))) = 'Credit'
ORDER BY
    COALESCE(
        TRY_CONVERT(datetime, CONVERT(varchar(100),e.currentdate), 101),
        TRY_CONVERT(datetime, CONVERT(varchar(100),e.currentdate), 103),
        TRY_CONVERT(datetime, CONVERT(varchar(100),e.currentdate), 100),
        TRY_CONVERT(datetime, CONVERT(varchar(100),e.currentdate))
    ) DESC;";

        var result = NewDetail(
            "Total expenses today",
            date,
            "Total",
            string.Empty,
            "Category",
            "Description",
            "Expense type",
            "Date",
            "Amount");

        decimal total = 0m;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand(sql, connection)
        {
            CommandTimeout = 20
        };

        command.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId.Trim();
        command.Parameters.Add("@date", SqlDbType.Date).Value = date.Date;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var amount = ToDecimal(reader["Amount"]);
            total += amount;

            result.Rows.Add(new List<string>
            {
                Convert.ToString(reader["Category"])?.Trim() ?? string.Empty,
                Convert.ToString(reader["Description"])?.Trim() ?? string.Empty,
                Convert.ToString(reader["ExpenseType"])?.Trim() ?? string.Empty,
                Convert.ToString(reader["TransactionDate"])?.Trim() ?? string.Empty,
                currency + amount.ToString("N2", CultureInfo.InvariantCulture)
            });
        }

        result.SummaryValue = currency + total.ToString("N2", CultureInfo.InvariantCulture);
        return result;
    }

    private async Task<DashboardStatisticDetailResult> LoadProfitDetailsAsync(
        string hotelId,
        DateTime date,
        string currency,
        CancellationToken cancellationToken)
    {
        var financial = await CachedAsync(
            $"dashboard:financial-snapshot:{hotelId}:{date:yyyyMMdd}",
            TimeSpan.FromSeconds(5),
            () => LoadFinancialSnapshotAsync(hotelId, date, cancellationToken));
        var revenue = financial.RevenueToday;
        var expense = financial.ExpensesToday;
        var profit = revenue - expense;
        return new DashboardStatisticDetailResult
        {
            Title = "Profit / loss",
            Subtitle = date.ToString("dd MMM yyyy", CultureInfo.InvariantCulture),
            SummaryLabel = "Profit / loss",
            SummaryValue = currency + profit.ToString("N2", CultureInfo.InvariantCulture),
            Columns = new List<string> { "Component", "Amount" },
            Rows = new List<List<string>>
            {
                new() { "Revenue", currency + revenue.ToString("N2", CultureInfo.InvariantCulture) },
                new() { "Expenses", currency + expense.ToString("N2", CultureInfo.InvariantCulture) },
                new() { "Profit / loss", currency + profit.ToString("N2", CultureInfo.InvariantCulture) }
            }
        };
    }

    private async Task<DashboardStatisticDetailResult> LoadExceptionDetailsAsync(
        string hotelId,
        DateTime date,
        bool cancellations,
        CancellationToken cancellationToken)
    {
        var sql = cancellations
            ? @"SELECT reg_id, ISNULL(GuestName,'') AS GuestName, TRY_CONVERT(date,ArrivalDate) AS ArrivalDate,
                       TRY_CONVERT(date,dept_date) AS DepartureDate, ISNULL(room_no,'') AS RoomNo
                FROM dbo.CancelledReservationsTB
                WHERE hotel_id=@hotel
                  AND TRY_CONVERT(date,ArrivalDate) >= @day
                  AND TRY_CONVERT(date,ArrivalDate) < DATEADD(day,1,@day)
                ORDER BY TRY_CONVERT(date,ArrivalDate) DESC;"
            : @"SELECT reg_id, ISNULL(GuestName,'') AS GuestName, TRY_CONVERT(date,ArrivalDate) AS ArrivalDate,
                       TRY_CONVERT(date,dept_date) AS DepartureDate, ISNULL(room_no,'') AS RoomNo
                FROM dbo.NoShowTB
                WHERE hotel_id=@hotel
                  AND TRY_CONVERT(date,ArrivalDate) >= @day
                  AND TRY_CONVERT(date,ArrivalDate) < DATEADD(day,1,@day)
                ORDER BY TRY_CONVERT(date,ArrivalDate) DESC;";

        var title = cancellations ? "Cancellations" : "No shows";
        var result = NewDetail(title, date, "Count", "0",
            "Reservation", "Guest", "Room", "Arrival", "Departure");
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 25 };
        command.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
        command.Parameters.Add("@day", SqlDbType.Date).Value = date.Date;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Rows.Add(new List<string>
            {
                Convert.ToString(reader["reg_id"])?.Trim() ?? string.Empty,
                Convert.ToString(reader["GuestName"])?.Trim() ?? string.Empty,
                Convert.ToString(reader["RoomNo"])?.Trim() ?? string.Empty,
                FormatDate(reader["ArrivalDate"]),
                FormatDate(reader["DepartureDate"])
            });
        }
        result.SummaryValue = result.Rows.Count.ToString(CultureInfo.InvariantCulture);
        return result;
    }

    private async Task<DashboardStatisticDetailResult> LoadOccupancyDetailsAsync(
        string hotelId,
        DateTime date,
        CancellationToken cancellationToken)
    {
        var rooms = await GetRoomStatusAsync(hotelId, date, cancellationToken);
        var blocked = rooms.Count(x => x.State == "ooo");
        var sold = rooms.Count(x => x.State is "checkin" or "reservation");
        var sellable = Math.Max(0, rooms.Count - blocked);
        var occupancy = Percent(sold, sellable);
        return new DashboardStatisticDetailResult
        {
            Title = "Occupancy",
            Subtitle = date.ToString("dd MMM yyyy", CultureInfo.InvariantCulture),
            SummaryLabel = "Occupancy",
            SummaryValue = occupancy.ToString("0.#", CultureInfo.InvariantCulture) + "%",
            Columns = new List<string> { "Metric", "Count" },
            Rows = new List<List<string>>
            {
                new() { "Total rooms", rooms.Count.ToString(CultureInfo.InvariantCulture) },
                new() { "Sellable rooms", sellable.ToString(CultureInfo.InvariantCulture) },
                new() { "Occupied / reserved", sold.ToString(CultureInfo.InvariantCulture) },
                new() { "Blocked / OOO", blocked.ToString(CultureInfo.InvariantCulture) }
            }
        };
    }

    private async Task<DashboardStatisticDetailResult> LoadProfitMarginDetailsAsync(
        string hotelId,
        DateTime date,
        string currency,
        CancellationToken cancellationToken)
    {
        var financial = await CachedAsync(
            $"dashboard:financial-snapshot:{hotelId}:{date:yyyyMMdd}",
            TimeSpan.FromSeconds(5),
            () => LoadFinancialSnapshotAsync(hotelId, date, cancellationToken));
        var revenue = financial.MonthRevenue;
        var expense = financial.MonthExpense;
        var profit = revenue - expense;
        var margin = revenue <= 0m ? 0m : Math.Round(profit * 100m / revenue, 1);
        return new DashboardStatisticDetailResult
        {
            Title = "Profit margin MTD",
            Subtitle = new DateTime(date.Year, date.Month, 1).ToString("dd MMM", CultureInfo.InvariantCulture) +
                " - " + date.ToString("dd MMM yyyy", CultureInfo.InvariantCulture),
            SummaryLabel = "Margin",
            SummaryValue = margin.ToString("0.#", CultureInfo.InvariantCulture) + "%",
            Columns = new List<string> { "Metric", "Value" },
            Rows = new List<List<string>>
            {
                new() { "Revenue MTD", currency + revenue.ToString("N2", CultureInfo.InvariantCulture) },
                new() { "Expenses MTD", currency + expense.ToString("N2", CultureInfo.InvariantCulture) },
                new() { "Profit MTD", currency + profit.ToString("N2", CultureInfo.InvariantCulture) }
            }
        };
    }

    private async Task<DashboardStatisticDetailResult> LoadMovementDetailsAsync(
        string hotelId,
        DateTime date,
        bool checkIn,
        CancellationToken cancellationToken)
    {
        const string sql = @"
WITH MovementRows AS
(
    SELECT
        p.ID,
        LTRIM(RTRIM(ISNULL(CONVERT(varchar(100),p.reg_id),''))) AS reg_id,
        LTRIM(RTRIM(ISNULL(CONVERT(varchar(100),p.room_no),''))) AS room_no,
        LTRIM(RTRIM(ISNULL(CONVERT(varchar(150),p.[Type]),''))) AS room_category,

        LOWER(
            REPLACE(
                REPLACE(
                    LTRIM(RTRIM(ISNULL(CONVERT(varchar(50),p.res_status),''))),
                    '-',
                    ' '
                ),
                '  ',
                ' '
            )
        ) AS status_norm,

        COALESCE(
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(CONVERT(varchar(100),p.ArrivalDate))), ''), 110),
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(CONVERT(varchar(100),p.ArrivalDate))), ''), 103),
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(CONVERT(varchar(100),p.ArrivalDate))), ''), 101),
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(CONVERT(varchar(100),p.ArrivalDate))), ''), 100),
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(CONVERT(varchar(100),p.ArrivalDate))), ''))
        ) AS arrival_date,

        COALESCE(
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(CONVERT(varchar(100),p.DepartureDate))), ''), 110),
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(CONVERT(varchar(100),p.DepartureDate))), ''), 103),
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(CONVERT(varchar(100),p.DepartureDate))), ''), 101),
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(CONVERT(varchar(100),p.DepartureDate))), ''), 100),
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(CONVERT(varchar(100),p.DepartureDate))), ''))
        ) AS departure_date
    FROM dbo.payments p
    WHERE LTRIM(RTRIM(CONVERT(varchar(50),p.hotel_id))) = @hotel
      AND LOWER(LTRIM(RTRIM(ISNULL(CONVERT(varchar(100),p.descr),'')))) = 'room rent'
      AND NULLIF(LTRIM(RTRIM(ISNULL(CONVERT(varchar(100),p.room_no),''))), '') IS NOT NULL
      AND UPPER(LTRIM(RTRIM(ISNULL(CONVERT(varchar(100),p.room_no),'')))) <> 'UNASSIGNED'
)
SELECT
    mr.room_no,
    mr.room_category,
    mr.reg_id,
    ISNULL(g.GuestName,'') AS GuestName
FROM MovementRows mr
OUTER APPLY
(
    SELECT TOP (1)
        LTRIM(RTRIM(
            ISNULL(CONVERT(varchar(100),gi.GuestName),'') + ' ' +
            ISNULL(CONVERT(varchar(100),gi.LastName),'')
        )) AS GuestName
    FROM dbo.GuestInformationLogTB gi
    WHERE LTRIM(RTRIM(CONVERT(varchar(50),gi.hotel_id))) = @hotel
      AND LTRIM(RTRIM(CONVERT(varchar(100),gi.reg_id))) = mr.reg_id
) g
WHERE
    (
        @isCheckIn = 1
        AND mr.arrival_date = @date
        AND mr.status_norm IN ('check in','checked in','check out','checked out')
    )
    OR
    (
        @isCheckIn = 0
        AND mr.departure_date = @date
        AND mr.status_norm IN ('check out','checked out')
    )
ORDER BY TRY_CONVERT(int,mr.room_no), mr.room_no, mr.ID;";

        var result = NewDetail(
            checkIn ? "Check-ins today" : "Check-outs today",
            date,
            "Rooms",
            "0",
            "Room",
            "Guest",
            "Category",
            "Reservation");

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand(sql, connection)
        {
            CommandTimeout = 15
        };

        command.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId.Trim();
        command.Parameters.Add("@date", SqlDbType.Date).Value = date.Date;
        command.Parameters.Add("@isCheckIn", SqlDbType.Bit).Value = checkIn;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Rows.Add(new List<string>
            {
                Convert.ToString(reader["room_no"])?.Trim() ?? string.Empty,
                Convert.ToString(reader["GuestName"])?.Trim() ?? string.Empty,
                Convert.ToString(reader["room_category"])?.Trim() ?? string.Empty,
                Convert.ToString(reader["reg_id"])?.Trim() ?? string.Empty
            });
        }

        result.SummaryValue = result.Rows.Count.ToString(CultureInfo.InvariantCulture);
        return result;
    }

    private async Task<DashboardStatisticDetailResult> LoadRoomStateDetailsAsync(
        string hotelId,
        DateTime date,
        string state,
        CancellationToken cancellationToken)
    {
        var rooms = await GetRoomStatusAsync(hotelId, date, cancellationToken);
        var selected = rooms.Where(x =>
            state == "dirty" ? x.State == "dirty" || x.IsDirty : x.State == state).ToList();
        return BuildRoomDetail(state, date, selected);
    }

    private async Task<DashboardStatisticDetailResult> LoadOccupiedRoomDetailsAsync(
        string hotelId,
        DateTime date,
        CancellationToken cancellationToken)
    {
        var rooms = await GetRoomStatusAsync(hotelId, date, cancellationToken);
        return BuildRoomDetail("occupied", date,
            rooms.Where(x => x.State is "checkin" or "reservation").ToList());
    }

    private static DashboardStatisticDetailResult BuildRoomDetail(
        string state,
        DateTime date,
        IReadOnlyList<DashboardRoomStatusItem> rooms)
    {
        var title = state switch
        {
            "checkin" => "Checked-in rooms",
            "checkout" => "Check-outs",
            "vacant" => "Available rooms",
            "ooo" => "Blocked / out of order rooms",
            "dirty" => "Dirty rooms",
            "occupied" => "Occupied / reserved rooms",
            _ => "Room details"
        };
        var result = NewDetail(title, date, "Rooms", rooms.Count.ToString(CultureInfo.InvariantCulture),
            "Room", "Status", "Guest", "Category", "Reservation");
        foreach (var room in rooms)
        {
            result.Rows.Add(new List<string>
            {
                room.RoomNo,
                room.State == "ooo" ? "Blocked" :
                    room.State == "checkin" ? "Checked in" :
                    room.State == "reservation" ? "Reserved" :
                    room.State == "checkout" ? "Check out" :
                    room.State == "dirty" ? "Dirty" : "Available",
                room.GuestName,
                room.Category,
                room.ReservationId
            });
        }
        return result;
    }

    private static DashboardStatisticDetailResult NewDetail(
        string title,
        DateTime date,
        string summaryLabel,
        string summaryValue,
        params string[] columns)
        => new()
        {
            Title = title,
            Subtitle = date.ToString("dd MMM yyyy", CultureInfo.InvariantCulture),
            SummaryLabel = summaryLabel,
            SummaryValue = summaryValue,
            Columns = columns.ToList()
        };

    private static string FormatDate(object value)
    {
        if (value == null || value == DBNull.Value) return string.Empty;
        if (value is DateTime date) return date.ToString("dd MMM yyyy", CultureInfo.InvariantCulture);
        return DateTime.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out var parsed)
            ? parsed.ToString("dd MMM yyyy", CultureInfo.InvariantCulture)
            : Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private async Task<DashboardHotelInfo> LoadHotelAsync(string hotelId, CancellationToken cancellationToken)
    {
        const string sql = @"
SELECT TOP (1)
    ISNULL(NULLIF(LTRIM(RTRIM(name)), ''), 'Hotel') AS HotelName,
    ISNULL(NULLIF(LTRIM(RTRIM(currency_sign)), ''), '£') AS CurrencySymbol
FROM dbo.HotelsSignUpTB
WHERE hotel_id = @hotel;";

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 15 };
        command.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return new DashboardHotelInfo(string.Empty, "£");

        return new DashboardHotelInfo(
            Convert.ToString(reader["HotelName"])?.Trim() ?? string.Empty,
            Convert.ToString(reader["CurrencySymbol"])?.Trim() ?? "£");
    }

    private async Task<List<DashboardRoomStatusItem>> LoadRoomStatusInternalAsync(
        string hotelId,
        DateTime date,
        CancellationToken cancellationToken)
    {
        const string sql = @"
WITH RoomBase AS
(
    SELECT
        LTRIM(RTRIM(room_no)) AS room_no,
        MAX(LTRIM(RTRIM(ISNULL(room_category, '')))) AS room_category,
        MAX(LOWER(LTRIM(RTRIM(ISNULL(room_status, ''))))) AS room_status
    FROM dbo.RoomsTB
    WHERE Hotel_id = @hotel
      AND NULLIF(LTRIM(RTRIM(room_no)), '') IS NOT NULL
    GROUP BY LTRIM(RTRIM(room_no))
),
PaymentClean AS
(
    SELECT
        p.ID,
        NULLIF(LTRIM(RTRIM(p.reg_id)), '') AS reg_id,
        NULLIF(LTRIM(RTRIM(p.room_no)), '') AS room_no,
        NULLIF(LTRIM(RTRIM(ISNULL(p.guestname, ''))), '') AS payment_guest_name,
        LOWER(LTRIM(RTRIM(ISNULL(p.res_status, '')))) AS res_status,
        COALESCE(
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(p.ArrivalDate)), ''), 110),
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(p.ArrivalDate)), ''), 103),
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(p.ArrivalDate)), ''), 101),
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(p.ArrivalDate)), ''))
        ) AS arrival_date,
        COALESCE(
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(p.DepartureDate)), ''), 110),
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(p.DepartureDate)), ''), 103),
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(p.DepartureDate)), ''), 101),
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(p.DepartureDate)), ''))
        ) AS departure_date
    FROM dbo.payments p
    WHERE p.hotel_id = @hotel
      AND LTRIM(RTRIM(ISNULL(p.descr, ''))) = 'Room Rent'
      AND NULLIF(LTRIM(RTRIM(p.room_no)), '') IS NOT NULL
      AND UPPER(LTRIM(RTRIM(p.room_no))) <> 'UNASSIGNED'
),
PaymentRanked AS
(
    SELECT
        ID,
        reg_id,
        room_no,
        payment_guest_name,
        res_status,
        arrival_date,
        departure_date,
        ROW_NUMBER() OVER
        (
            PARTITION BY room_no
            ORDER BY
                CASE WHEN res_status IN ('check in','check-in','checked in','checked-in') THEN 0
                     WHEN res_status = 'reservation' THEN 1 ELSE 2 END,
                ID DESC
        ) AS rn
    FROM PaymentClean pc
    WHERE
        (
            pc.arrival_date <= @day
            AND @day < pc.departure_date
            AND pc.res_status IN ('check in','check-in','checked in','checked-in','reservation')
            AND (
                EXISTS (
                    SELECT 1
                    FROM dbo.GuestInformationLogTB gi
                    WHERE gi.hotel_id = @hotel
                      AND gi.reg_id = pc.reg_id
                )
                OR EXISTS (
                    SELECT 1
                    FROM dbo.NewReservationsTB nr
                    WHERE nr.hotel_id = @hotel
                      AND nr.reg_id = pc.reg_id
                )
            )
        )
        OR
        (
            pc.departure_date = @day
            AND pc.res_status IN ('check out','check-out','checked out','checked-out')
        )
),
GuestRanked AS
(
    SELECT
        g.hotel_id,
        g.reg_id,
        NULLIF(LTRIM(RTRIM(ISNULL(g.GuestName, '') + ' ' + ISNULL(g.LastName, ''))), '') AS GuestName,
        ROW_NUMBER() OVER (PARTITION BY g.hotel_id, g.reg_id ORDER BY g.id DESC) AS rn
    FROM dbo.GuestInformationLogTB g
    WHERE g.hotel_id = @hotel
),
ReservationGuest AS
(
    SELECT
        nr.hotel_id,
        nr.reg_id,
        MAX(NULLIF(LTRIM(RTRIM(ISNULL(nr.GuestName, '') + ' ' + ISNULL(nr.LastName, ''))), '')) AS GuestName
    FROM dbo.NewReservationsTB nr
    WHERE nr.hotel_id = @hotel
    GROUP BY nr.hotel_id, nr.reg_id
),
ActiveRegIds AS
(
    SELECT DISTINCT reg_id
    FROM PaymentRanked
    WHERE reg_id IS NOT NULL
),
ChargeTotals AS
(
    SELECT
        LTRIM(RTRIM(p.reg_id)) AS reg_id,
        SUM(
            COALESCE(
                TRY_CONVERT(
                    decimal(18,2),
                    NULLIF(
                        REPLACE(REPLACE(REPLACE(LTRIM(RTRIM(CONVERT(varchar(100),p.totalamount))), ',', ''), '£', ''), '$', ''),
                        ''
                    )
                ),
                0
            )
        ) AS grand_total
    FROM dbo.payments p
    INNER JOIN ActiveRegIds a
        ON a.reg_id = LTRIM(RTRIM(p.reg_id))
    WHERE LTRIM(RTRIM(CONVERT(varchar(100),p.hotel_id))) = @hotel
    GROUP BY LTRIM(RTRIM(p.reg_id))
),
PaidTotals AS
(
    SELECT
        LTRIM(RTRIM(pl.reg_id)) AS reg_id,
        SUM(
            COALESCE(
                TRY_CONVERT(
                    decimal(18,2),
                    NULLIF(
                        REPLACE(REPLACE(REPLACE(LTRIM(RTRIM(CONVERT(varchar(100),pl.paid_amount))), ',', ''), '£', ''), '$', ''),
                        ''
                    )
                ),
                0
            )
        ) AS paid_amount
    FROM dbo.PaymentsLogTB pl
    INNER JOIN ActiveRegIds a
        ON a.reg_id = LTRIM(RTRIM(pl.reg_id))
    WHERE LTRIM(RTRIM(CONVERT(varchar(100),pl.hotel_id))) = @hotel
    GROUP BY LTRIM(RTRIM(pl.reg_id))
),
PaymentSummary AS
(
    SELECT
        a.reg_id,
        ISNULL(c.grand_total, 0) AS grand_total,
        ISNULL(p.paid_amount, 0) AS paid_amount,
        CASE
            WHEN ISNULL(c.grand_total, 0) - ISNULL(p.paid_amount, 0) > 0
                THEN ISNULL(c.grand_total, 0) - ISNULL(p.paid_amount, 0)
            ELSE 0
        END AS balance
    FROM ActiveRegIds a
    LEFT JOIN ChargeTotals c ON c.reg_id = a.reg_id
    LEFT JOIN PaidTotals p ON p.reg_id = a.reg_id
),
BlockRanked AS
(
    SELECT
        LTRIM(RTRIM(RoomNo)) AS room_no,
        ROW_NUMBER() OVER (PARTITION BY LTRIM(RTRIM(RoomNo)) ORDER BY BlockStartDate DESC) AS rn
    FROM dbo.RoomBlocksTB
    WHERE HotelID = @hotel
      AND IsActive = 1
      AND CAST(BlockStartDate AS date) <= @day
      AND @day < DATEADD(day, 1, CAST(ISNULL(BlockEndDate, '9999-12-31') AS date))
)
SELECT
    r.room_no,
    r.room_category,
    r.room_status,
    p.reg_id,
    p.res_status,
    p.arrival_date,
    p.departure_date,
    COALESCE(g.GuestName, nr.GuestName, p.payment_guest_name, '') AS GuestName,
    ISNULL(bal.grand_total, 0) AS grand_total,
    ISNULL(bal.balance, 0) AS balance,
    ISNULL(bal.paid_amount, 0) AS paid_amount,
    CASE WHEN b.room_no IS NULL THEN 0 ELSE 1 END AS is_blocked
FROM RoomBase r
LEFT JOIN PaymentRanked p ON p.room_no = r.room_no AND p.rn = 1
LEFT JOIN GuestRanked g ON g.reg_id = p.reg_id AND g.hotel_id = @hotel AND g.rn = 1
LEFT JOIN ReservationGuest nr ON nr.reg_id = p.reg_id AND nr.hotel_id = @hotel
LEFT JOIN PaymentSummary bal ON bal.reg_id = p.reg_id
LEFT JOIN BlockRanked b ON b.room_no = r.room_no AND b.rn = 1
ORDER BY r.room_category, TRY_CONVERT(int, r.room_no), r.room_no;";

        var result = new List<DashboardRoomStatusItem>();
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 30 };
        command.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
        command.Parameters.Add("@day", SqlDbType.Date).Value = date.Date;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var rawRoomStatus = Convert.ToString(reader["room_status"])?.Trim().ToLowerInvariant() ?? string.Empty;
            var resStatus = Convert.ToString(reader["res_status"])?.Trim().ToLowerInvariant() ?? string.Empty;
            var isBlocked = ToInt(reader["is_blocked"]) == 1;
            var isDirty = rawRoomStatus is "notclean" or "notcleaned" or "check out" or "checkout";

            string state;
            if (isBlocked)
                state = "ooo";
            else if (resStatus is "check in" or "check-in" or "checked in" or "checked-in")
                state = "checkin";
            else if (resStatus == "reservation")
                state = "reservation";
            else if (isDirty)
                state = "dirty";
            else if (resStatus is "check out" or "check-out" or "checked out" or "checked-out")
                state = "checkout";
            else
                state = "vacant";

            result.Add(new DashboardRoomStatusItem
            {
                RoomNo = Convert.ToString(reader["room_no"])?.Trim() ?? string.Empty,
                Category = Convert.ToString(reader["room_category"])?.Trim() ?? string.Empty,
                State = state,
                GuestName = Convert.ToString(reader["GuestName"])?.Trim() ?? string.Empty,
                ReservationId = Convert.ToString(reader["reg_id"])?.Trim() ?? string.Empty,
                ArrivalDate = ToNullableDate(reader["arrival_date"]),
                DepartureDate = ToNullableDate(reader["departure_date"]),
                Balance = ToDecimal(reader["balance"]),
                PaymentState = ToDecimal(reader["grand_total"]) <= 0m
                    ? string.Empty
                    : ToDecimal(reader["paid_amount"]) <= 0m
                        ? "pending"
                        : ToDecimal(reader["balance"]) > 0.009m
                            ? "partial"
                            : "paid",
                IsDirty = isDirty
            });
        }

        return result;
    }

    private async Task<DashboardOperationsData> LoadOperationsAsync(
        string hotelId,
        DateTime today,
        CancellationToken cancellationToken)
    {
        // Keep today's actual room movements separate from the secondary
        // operational metrics. This prevents a legacy guest/phone/date issue
        // from zeroing Check-ins Today / Check-outs Today.
        //
        // Movement rules follow the active WebForms Dashboard:
        // - Check-in today: Room Rent payment row is currently "check in"
        //   and its ArrivalDate is the hotel-local today.
        // - Check-out today: Room Rent payment row is currently "check out"
        //   and its DepartureDate is the hotel-local today.
        // - UNASSIGNED rows are excluded.
        var movementTask = SafeLoadAsync(
            () => LoadTodayMovementCountsAsync(hotelId, today, cancellationToken),
            new DashboardOperationsData(),
            "today check-in/check-out");

        var metricsTask = SafeLoadAsync(
            () => LoadOperationalMetricsAsync(hotelId, today, cancellationToken),
            new DashboardOperationsData(),
            "operational metrics");

        await Task.WhenAll(movementTask, metricsTask);

        var movements = await movementTask;
        var metrics = await metricsTask;

        return new DashboardOperationsData
        {
            Arrivals = movements.Arrivals,
            Departures = movements.Departures,
            InHouseGuests = metrics.InHouseGuests,
            OccupiedRooms = metrics.OccupiedRooms,
            AverageStay = metrics.AverageStay,
            RepeatGuestPercent = metrics.RepeatGuestPercent,
            GuestsPerRoom = metrics.GuestsPerRoom
        };
    }

    private async Task<DashboardOperationsData> LoadTodayMovementCountsAsync(
        string hotelId,
        DateTime today,
        CancellationToken cancellationToken)
    {
        /*
         * IMPORTANT:
         * Today's movement cards must represent room movements, not only the
         * guest's current state.
         *
         * A room that checked in today and later checked out today must still
         * remain in BOTH figures:
         *   - Check-ins today
         *   - Check-outs today
         *
         * Therefore Check-ins Today accepts Room Rent rows whose CURRENT
         * payment status is either check-in OR check-out, provided the room's
         * ArrivalDate is hotel-local today.
         *
         * This also avoids the previous dependency on GuestInformationLogTB
         * for the headline counts. Front Desk writes the room-level movement
         * status directly into dbo.payments, so dbo.payments is the correct
         * source for these room movement cards.
         */
        const string sql = @"
WITH RoomMovements AS
(
    SELECT
        p.ID,
        LOWER(
            REPLACE(
                REPLACE(
                    LTRIM(RTRIM(ISNULL(CONVERT(varchar(50),p.res_status),''))),
                    '-',
                    ' '
                ),
                '  ',
                ' '
            )
        ) AS status_norm,

        COALESCE(
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(CONVERT(varchar(100),p.ArrivalDate))), ''), 110),
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(CONVERT(varchar(100),p.ArrivalDate))), ''), 103),
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(CONVERT(varchar(100),p.ArrivalDate))), ''), 101),
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(CONVERT(varchar(100),p.ArrivalDate))), ''), 100),
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(CONVERT(varchar(100),p.ArrivalDate))), ''))
        ) AS arrival_date,

        COALESCE(
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(CONVERT(varchar(100),p.DepartureDate))), ''), 110),
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(CONVERT(varchar(100),p.DepartureDate))), ''), 103),
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(CONVERT(varchar(100),p.DepartureDate))), ''), 101),
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(CONVERT(varchar(100),p.DepartureDate))), ''), 100),
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(CONVERT(varchar(100),p.DepartureDate))), ''))
        ) AS departure_date
    FROM dbo.payments p
    WHERE LTRIM(RTRIM(CONVERT(varchar(50),p.hotel_id))) = @hotel
      AND LOWER(LTRIM(RTRIM(ISNULL(CONVERT(varchar(100),p.descr),'')))) = 'room rent'
      AND NULLIF(LTRIM(RTRIM(ISNULL(CONVERT(varchar(100),p.room_no),''))), '') IS NOT NULL
      AND UPPER(LTRIM(RTRIM(ISNULL(CONVERT(varchar(100),p.room_no),'')))) <> 'UNASSIGNED'
)
SELECT
    COALESCE(SUM(
        CASE
            WHEN arrival_date = @today
             AND status_norm IN
                 ('check in','checked in','check out','checked out')
            THEN 1
            ELSE 0
        END
    ),0) AS CheckInsToday,

    COALESCE(SUM(
        CASE
            WHEN departure_date = @today
             AND status_norm IN ('check out','checked out')
            THEN 1
            ELSE 0
        END
    ),0) AS CheckOutsToday
FROM RoomMovements;";

        var result = new DashboardOperationsData();

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand(sql, connection)
        {
            CommandTimeout = 15
        };

        command.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId.Trim();
        command.Parameters.Add("@today", SqlDbType.Date).Value = today.Date;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            result.Arrivals = ToInt(reader["CheckInsToday"]);
            result.Departures = ToInt(reader["CheckOutsToday"]);
        }

        _logger.LogInformation(
            "Dashboard movements for hotel {HotelId} on {Date}: check-ins={CheckIns}, check-outs={CheckOuts}.",
            hotelId,
            today.Date,
            result.Arrivals,
            result.Departures);

        return result;
    }

    private async Task<DashboardOperationsData> LoadOperationalMetricsAsync(
        string hotelId,
        DateTime today,
        CancellationToken cancellationToken)
    {
        /*
         * Current in-house people:
         *   Adults + Children + Infants.
         *
         * Match the Front Desk Calendar occupancy rule:
         * - Prefer payments.room_adults + room_children + room_infants when
         *   room-level occupancy has been stored.
         * - For legacy rows with no room-level occupancy, fall back once per
         *   reservation to GuestInformationLogTB.NumberOfAdults +
         *   NumberOfMinors. GuestInformationLogTB has no infant field in the
         *   legacy structure, so infant fallback is zero.
         *
         * Average stay:
         * - one active reservation once
         * - DepartureDate - ArrivalDate, minimum 1 night.
         *
         * Repeat guests:
         * - repeat active reservations / active reservations
         * - earlier stay at same hotel matched by normalized phone or email.
         */
        const string sql = @"
WITH ActivePaymentRows AS
(
    SELECT
        p.ID,
        NULLIF(LTRIM(RTRIM(CONVERT(varchar(100),p.reg_id))), '') AS reg_id,
        NULLIF(LTRIM(RTRIM(CONVERT(varchar(100),p.room_no))), '') AS room_no,

        COALESCE(TRY_CONVERT(int,p.room_adults),0) AS room_adults,
        COALESCE(TRY_CONVERT(int,p.room_children),0) AS room_children,
        COALESCE(TRY_CONVERT(int,p.room_infants),0) AS room_infants,

        COALESCE(
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(CONVERT(varchar(100),p.ArrivalDate))), ''), 110),
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(CONVERT(varchar(100),p.ArrivalDate))), ''), 103),
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(CONVERT(varchar(100),p.ArrivalDate))), ''), 101),
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(CONVERT(varchar(100),p.ArrivalDate))), ''), 23),
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(CONVERT(varchar(100),p.ArrivalDate))), ''), 100),
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(CONVERT(varchar(100),p.ArrivalDate))), ''))
        ) AS arrival_date,

        COALESCE(
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(CONVERT(varchar(100),p.DepartureDate))), ''), 110),
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(CONVERT(varchar(100),p.DepartureDate))), ''), 103),
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(CONVERT(varchar(100),p.DepartureDate))), ''), 101),
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(CONVERT(varchar(100),p.DepartureDate))), ''), 23),
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(CONVERT(varchar(100),p.DepartureDate))), ''), 100),
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(CONVERT(varchar(100),p.DepartureDate))), ''))
        ) AS departure_date
    FROM dbo.payments p
    WHERE LTRIM(RTRIM(CONVERT(varchar(50),p.hotel_id))) = @hotel
      AND LOWER(LTRIM(RTRIM(ISNULL(CONVERT(varchar(100),p.descr),'')))) = 'room rent'
      AND NULLIF(LTRIM(RTRIM(ISNULL(CONVERT(varchar(100),p.room_no),''))), '') IS NOT NULL
      AND UPPER(LTRIM(RTRIM(ISNULL(CONVERT(varchar(100),p.room_no),'')))) <> 'UNASSIGNED'
      AND LOWER(
            REPLACE(
                REPLACE(
                    LTRIM(RTRIM(ISNULL(CONVERT(varchar(50),p.res_status),''))),
                    '-',
                    ' '
                ),
                '  ',
                ' '
            )
          ) IN ('check in','checked in')
),
ActiveRooms AS
(
    SELECT
        reg_id,
        room_no,
        room_adults,
        room_children,
        room_infants,
        arrival_date,
        departure_date,
        ROW_NUMBER() OVER
        (
            PARTITION BY room_no
            ORDER BY ID DESC
        ) AS rn
    FROM ActivePaymentRows
    WHERE reg_id IS NOT NULL
      AND arrival_date IS NOT NULL
      AND departure_date IS NOT NULL
      AND arrival_date <= @today
      AND departure_date >= @today
),
CurrentRooms AS
(
    SELECT
        reg_id,
        room_no,
        room_adults,
        room_children,
        room_infants,
        arrival_date,
        departure_date
    FROM ActiveRooms
    WHERE rn = 1
),
ActiveStays AS
(
    SELECT
        reg_id,
        MIN(arrival_date) AS arrival_date,
        MAX(departure_date) AS departure_date,
        COUNT(*) AS room_count,
        SUM(ISNULL(room_adults,0)) AS room_adults,
        SUM(ISNULL(room_children,0)) AS room_children,
        SUM(ISNULL(room_infants,0)) AS room_infants
    FROM CurrentRooms
    GROUP BY reg_id
),
LatestGuest AS
(
    SELECT
        LTRIM(RTRIM(CONVERT(varchar(100),g.reg_id))) AS reg_id,

        NULLIF(
            REPLACE(
                REPLACE(
                    REPLACE(
                        REPLACE(
                            REPLACE(
                                LTRIM(RTRIM(CONVERT(varchar(100),g.PhoneNo))),
                                ' ',
                                ''
                            ),
                            '-',
                            ''
                        ),
                        '(',
                        ''
                    ),
                    ')',
                    ''
                ),
                '+',
                ''
            ),
            ''
        ) AS phone_norm,

        NULLIF(
            LOWER(LTRIM(RTRIM(CONVERT(varchar(250),g.Email)))),
            ''
        ) AS email_norm,

        COALESCE(
            TRY_CONVERT(int, NULLIF(LTRIM(RTRIM(CONVERT(varchar(30),g.NumberOfAdults))), '')),
            0
        ) AS adults,

        COALESCE(
            TRY_CONVERT(int, NULLIF(LTRIM(RTRIM(CONVERT(varchar(30),g.NumberOfMinors))), '')),
            0
        ) AS minors,

        ROW_NUMBER() OVER
        (
            PARTITION BY LTRIM(RTRIM(CONVERT(varchar(100),g.reg_id)))
            ORDER BY g.id DESC
        ) AS rn
    FROM dbo.GuestInformationLogTB g
    WHERE LTRIM(RTRIM(CONVERT(varchar(50),g.hotel_id))) = @hotel
),
CurrentGuests AS
(
    SELECT
        s.reg_id,
        s.arrival_date,
        s.departure_date,
        s.room_count,
        lg.phone_norm,
        lg.email_norm,

        CASE
            WHEN ISNULL(s.room_adults,0)
               + ISNULL(s.room_children,0)
               + ISNULL(s.room_infants,0) > 0
            THEN
                ISNULL(s.room_adults,0)
              + ISNULL(s.room_children,0)
              + ISNULL(s.room_infants,0)
            ELSE
                ISNULL(lg.adults,0)
              + ISNULL(lg.minors,0)
        END AS guest_count
    FROM ActiveStays s
    LEFT JOIN LatestGuest lg
        ON lg.reg_id = s.reg_id
       AND lg.rn = 1
),
RepeatFlags AS
(
    SELECT
        cg.reg_id,
        cg.guest_count,
        CASE
            WHEN EXISTS
            (
                SELECT 1
                FROM dbo.GuestInformationLogTB h
                WHERE LTRIM(RTRIM(CONVERT(varchar(50),h.hotel_id))) = @hotel
                  AND LTRIM(RTRIM(CONVERT(varchar(100),h.reg_id))) <> cg.reg_id
                  AND
                  (
                      (
                          cg.phone_norm IS NOT NULL
                          AND NULLIF(
                                REPLACE(
                                    REPLACE(
                                        REPLACE(
                                            REPLACE(
                                                REPLACE(
                                                    LTRIM(RTRIM(CONVERT(varchar(100),h.PhoneNo))),
                                                    ' ',
                                                    ''
                                                ),
                                                '-',
                                                ''
                                            ),
                                            '(',
                                            ''
                                        ),
                                        ')',
                                        ''
                                    ),
                                    '+',
                                    ''
                                ),
                                ''
                              ) = cg.phone_norm
                      )
                      OR
                      (
                          cg.email_norm IS NOT NULL
                          AND NULLIF(
                                LOWER(LTRIM(RTRIM(CONVERT(varchar(250),h.Email)))),
                                ''
                              ) = cg.email_norm
                      )
                  )
                  AND COALESCE(
                        TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(CONVERT(varchar(100),h.ArrivalDate))), ''), 110),
                        TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(CONVERT(varchar(100),h.ArrivalDate))), ''), 103),
                        TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(CONVERT(varchar(100),h.ArrivalDate))), ''), 101),
                        TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(CONVERT(varchar(100),h.ArrivalDate))), ''), 23),
                        TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(CONVERT(varchar(100),h.ArrivalDate))), ''), 100),
                        TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(CONVERT(varchar(100),h.ArrivalDate))), ''))
                      ) < cg.arrival_date
            )
            THEN 1
            ELSE 0
        END AS is_repeat
    FROM CurrentGuests cg
)
SELECT
    COUNT(*) AS ActiveStays,

    COALESCE(
        CAST(
            AVG(
                CONVERT(
                    decimal(9,2),
                    CASE
                        WHEN DATEDIFF(day,arrival_date,departure_date) <= 0 THEN 1
                        ELSE DATEDIFF(day,arrival_date,departure_date)
                    END
                )
            )
            AS decimal(9,2)
        ),
        0
    ) AS AverageStay,

    COALESCE((SELECT SUM(guest_count) FROM CurrentGuests),0) AS InHousePersons,

    COALESCE((SELECT SUM(is_repeat) FROM RepeatFlags),0) AS RepeatStays,

    COALESCE((SELECT COUNT(*) FROM CurrentRooms),0) AS OccupiedRooms
FROM ActiveStays;";

        var result = new DashboardOperationsData();

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand(sql, connection)
        {
            CommandTimeout = 20
        };

        command.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId.Trim();
        command.Parameters.Add("@today", SqlDbType.Date).Value = today.Date;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            var activeStays = ToInt(reader["ActiveStays"]);
            var inHousePersons = ToInt(reader["InHousePersons"]);
            var repeatStays = ToInt(reader["RepeatStays"]);
            var occupiedRooms = ToInt(reader["OccupiedRooms"]);

            // InHouseGuests is the number of PEOPLE currently in house:
            // adults + children + infants.
            result.InHouseGuests = inHousePersons;
            result.AverageStay = Math.Round(ToDecimal(reader["AverageStay"]), 1);
            result.RepeatGuestPercent = Percent(repeatStays, activeStays);
            result.OccupiedRooms = occupiedRooms;
            result.GuestsPerRoom = occupiedRooms <= 0
                ? 0m
                : Math.Round((decimal)inHousePersons / occupiedRooms, 1);

            _logger.LogInformation(
                "Dashboard in-house metrics for hotel {HotelId} on {Date}: people={People}, stays={Stays}, avgStay={AverageStay}, repeats={RepeatStays}/{Stays}, occupiedRooms={OccupiedRooms}.",
                hotelId,
                today.Date,
                inHousePersons,
                activeStays,
                result.AverageStay,
                repeatStays,
                activeStays,
                occupiedRooms);
        }

        return result;
    }

    private async Task<List<DashboardGuestItem>> LoadInHouseGuestsAsync(
        string hotelId,
        DateTime today,
        CancellationToken cancellationToken)
    {
        const string sql = @"
WITH RoomWise AS
(
    SELECT
        p.ID,
        p.reg_id,
        p.hotel_id,
        LTRIM(RTRIM(ISNULL(p.room_no, ''))) AS room_no,
        LTRIM(RTRIM(ISNULL(p.[Type], ''))) AS room_category,
        LTRIM(RTRIM(ISNULL(p.rateplanname, ''))) AS rate_plan,
        COALESCE(TRY_CONVERT(int,p.room_adults),0) AS room_adults,
        COALESCE(TRY_CONVERT(int,p.room_children),0) AS room_children,
        COALESCE(TRY_CONVERT(int,p.room_infants),0) AS room_infants,
        COALESCE(
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(p.ArrivalDate)), ''), 110),
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(p.ArrivalDate)), ''), 103),
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(p.ArrivalDate)), ''), 101),
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(p.ArrivalDate)), ''), 23),
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(p.ArrivalDate)), ''), 100),
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(p.ArrivalDate)), ''))
        ) AS arrival_date,
        COALESCE(
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(p.DepartureDate)), ''), 110),
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(p.DepartureDate)), ''), 103),
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(p.DepartureDate)), ''), 101),
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(p.DepartureDate)), ''), 23),
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(p.DepartureDate)), ''), 100),
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(p.DepartureDate)), ''))
        ) AS departure_date,
        ROW_NUMBER() OVER (PARTITION BY LTRIM(RTRIM(p.room_no)) ORDER BY p.ID DESC) AS rn
    FROM dbo.payments p
    WHERE p.hotel_id = @hotel
      AND LTRIM(RTRIM(ISNULL(p.descr, ''))) = 'Room Rent'
      AND NULLIF(LTRIM(RTRIM(p.room_no)), '') IS NOT NULL
      AND UPPER(LTRIM(RTRIM(p.room_no))) <> 'UNASSIGNED'
      AND LOWER(LTRIM(RTRIM(ISNULL(p.res_status, '')))) IN
          ('check in','check-in','checked in','checked-in')
),
LatestGuest AS
(
    SELECT
        g.reg_id,
        LTRIM(RTRIM(ISNULL(g.GuestName, '') + ' ' + ISNULL(g.LastName, ''))) AS GuestName,
        COALESCE(TRY_CONVERT(int, NULLIF(LTRIM(RTRIM(g.NumberOfAdults)), '')), 0) AS adults,
        COALESCE(TRY_CONVERT(int, NULLIF(LTRIM(RTRIM(g.NumberOfMinors)), '')), 0) AS minors,
        ROW_NUMBER() OVER (PARTITION BY g.reg_id ORDER BY g.id DESC) AS rn
    FROM dbo.GuestInformationLogTB g
    WHERE g.hotel_id = @hotel
),
LatestBalance AS
(
    SELECT
        p.reg_id,
        COALESCE(
            TRY_CONVERT(decimal(18,2), NULLIF(REPLACE(REPLACE(REPLACE(LTRIM(RTRIM(p.remaining_amount)), ',', ''), '£', ''), '$', ''), '')),
            0
        ) AS balance,
        ROW_NUMBER() OVER
        (
            PARTITION BY p.reg_id
            ORDER BY COALESCE(TRY_CONVERT(datetime, p.currentdate), '19000101') DESC, p.id DESC
        ) AS rn
    FROM dbo.PaymentsUpdateTB p
    WHERE p.hotel_id = @hotel
)
SELECT
    rw.reg_id,
    rw.room_no,
    rw.room_category,
    rw.rate_plan,
    rw.arrival_date,
    rw.departure_date,
    ISNULL(g.GuestName, '') AS GuestName,
    CASE
        WHEN ISNULL(rw.room_adults,0)
           + ISNULL(rw.room_children,0)
           + ISNULL(rw.room_infants,0) > 0
        THEN
            ISNULL(rw.room_adults,0)
          + ISNULL(rw.room_children,0)
          + ISNULL(rw.room_infants,0)
        ELSE
            ISNULL(g.adults,0) + ISNULL(g.minors,0)
    END AS Guests,
    ISNULL(b.balance, 0) AS Balance
FROM RoomWise rw
LEFT JOIN LatestGuest g ON g.reg_id = rw.reg_id AND g.rn = 1
LEFT JOIN LatestBalance b ON b.reg_id = rw.reg_id AND b.rn = 1
WHERE rw.rn = 1
  AND rw.arrival_date IS NOT NULL
  AND rw.departure_date IS NOT NULL
  AND rw.arrival_date <= @today
  AND rw.departure_date >= @today
ORDER BY rw.departure_date, TRY_CONVERT(int, rw.room_no), rw.room_no;";

        var result = new List<DashboardGuestItem>();
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 30 };
        command.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
        command.Parameters.Add("@today", SqlDbType.Date).Value = today.Date;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var arrival = ToNullableDate(reader["arrival_date"]) ?? today;
            var departure = ToNullableDate(reader["departure_date"]) ?? today;
            result.Add(new DashboardGuestItem
            {
                RoomNo = Convert.ToString(reader["room_no"])?.Trim() ?? string.Empty,
                GuestName = Convert.ToString(reader["GuestName"])?.Trim() ?? string.Empty,
                ReservationId = Convert.ToString(reader["reg_id"])?.Trim() ?? string.Empty,
                Category = Convert.ToString(reader["room_category"])?.Trim() ?? string.Empty,
                RatePlan = Convert.ToString(reader["rate_plan"])?.Trim() ?? string.Empty,
                ArrivalDate = arrival,
                DepartureDate = departure,
                Nights = Math.Max(1, (departure - arrival).Days),
                Guests = Math.Max(1, ToInt(reader["Guests"])),
                Balance = ToDecimal(reader["Balance"]),
                DueOutToday = departure.Date == today.Date
            });
        }

        return result;
    }

    private async Task<List<DashboardDailyPoint>> LoadOccupancySeriesAsync(
        string hotelId,
        DateTime start,
        DateTime end,
        CancellationToken cancellationToken)
    {
        const string sql = @"
;WITH d AS
(
    SELECT @start AS d
    UNION ALL
    SELECT DATEADD(day, 1, d) FROM d WHERE d < @end
),
inv AS
(
    SELECT COUNT(*) AS Inventory
    FROM dbo.RoomsTB
    WHERE Hotel_id = @hotel
),
ooo AS
(
    SELECT dd.d, COUNT(DISTINCT rt.room_no) AS OutOfInventory
    FROM d dd
    INNER JOIN dbo.RoomsTB rt ON rt.Hotel_id = @hotel
    INNER JOIN dbo.RoomBlocksTB rb
        ON rb.HotelID = rt.Hotel_id
       AND rb.RoomNo = rt.room_no
       AND rb.IsActive = 1
       AND CAST(rb.BlockStartDate AS date) <= dd.d
       AND dd.d < DATEADD(day, 1, CAST(ISNULL(rb.BlockEndDate, '9999-12-31') AS date))
    GROUP BY dd.d
),
p_clean AS
(
    SELECT
        NULLIF(LTRIM(RTRIM(p.reg_id)), '') AS reg_id,
        NULLIF(LTRIM(RTRIM(p.room_no)), '') AS room_no,
        LOWER(LTRIM(RTRIM(ISNULL(p.res_status, '')))) AS res_status,
        COALESCE(
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(p.ArrivalDate)), ''), 110),
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(p.ArrivalDate)), ''), 103),
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(p.ArrivalDate)), ''))
        ) AS arrival_date,
        COALESCE(
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(p.DepartureDate)), ''), 110),
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(p.DepartureDate)), ''), 103),
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(p.DepartureDate)), ''))
        ) AS departure_date,
        COALESCE(
            TRY_CONVERT(decimal(18,2), NULLIF(REPLACE(REPLACE(REPLACE(LTRIM(RTRIM(p.Rate)), ',', ''), '£', ''), '$', ''), '')),
            0
        ) AS room_rate,
        TRY_CONVERT(int, NULLIF(LTRIM(RTRIM(p.Nights)), '')) AS nights_raw
    FROM dbo.payments p
    WHERE p.hotel_id = @hotel
      AND LTRIM(RTRIM(ISNULL(p.descr, ''))) = 'Room Rent'
      AND NULLIF(LTRIM(RTRIM(p.room_no)), '') IS NOT NULL
      AND UPPER(LTRIM(RTRIM(p.room_no))) <> 'UNASSIGNED'
),
room_stays AS
(
    SELECT
        reg_id,
        room_no,
        res_status,
        arrival_date,
        departure_date,
        room_rate,
        COALESCE(NULLIF(nights_raw, 0), NULLIF(DATEDIFF(day, arrival_date, departure_date), 0)) AS stay_nights
    FROM p_clean
    WHERE arrival_date IS NOT NULL
      AND departure_date IS NOT NULL
      AND departure_date > arrival_date
      AND res_status NOT IN ('cancel','cancelled','canceled','no show','noshow','no-show')
),
sold AS
(
    SELECT dd.d,
        COUNT(DISTINCT ISNULL(rs.reg_id, '') + '|' + ISNULL(rs.room_no, '')) AS RoomsSold
    FROM d dd
    INNER JOIN room_stays rs ON rs.arrival_date <= dd.d AND dd.d < rs.departure_date
    GROUP BY dd.d
),
revenue AS
(
    SELECT dd.d,
        SUM(rs.room_rate / NULLIF(rs.stay_nights, 0)) AS AccommodationRevenue
    FROM d dd
    INNER JOIN room_stays rs ON rs.arrival_date <= dd.d AND dd.d < rs.departure_date
    GROUP BY dd.d
)
SELECT
    dd.d AS [Date],
    ISNULL(i.Inventory, 0) AS Inventory,
    ISNULL(o.OutOfInventory, 0) AS OutOfInventory,
    CASE WHEN ISNULL(i.Inventory, 0) - ISNULL(o.OutOfInventory, 0) < 0 THEN 0
         ELSE ISNULL(i.Inventory, 0) - ISNULL(o.OutOfInventory, 0) END AS SellableInventory,
    ISNULL(s.RoomsSold, 0) AS RoomsSold,
    CAST(CASE WHEN ISNULL(i.Inventory,0) - ISNULL(o.OutOfInventory,0) <= 0 THEN 0
         ELSE 100.0 * ISNULL(s.RoomsSold,0) / NULLIF(ISNULL(i.Inventory,0) - ISNULL(o.OutOfInventory,0),0) END AS decimal(9,2)) AS OccupancyPercent,
    CAST(ISNULL(r.AccommodationRevenue,0) AS decimal(18,2)) AS AccommodationRevenue,
    CAST(CASE WHEN ISNULL(s.RoomsSold,0) <= 0 THEN 0
         ELSE ISNULL(r.AccommodationRevenue,0) / NULLIF(s.RoomsSold,0) END AS decimal(18,2)) AS ADR,
    CAST(CASE WHEN ISNULL(i.Inventory,0) - ISNULL(o.OutOfInventory,0) <= 0 THEN 0
         ELSE ISNULL(r.AccommodationRevenue,0) / NULLIF(ISNULL(i.Inventory,0) - ISNULL(o.OutOfInventory,0),0) END AS decimal(18,2)) AS RevPAR
FROM d dd
CROSS JOIN inv i
LEFT JOIN ooo o ON o.d = dd.d
LEFT JOIN sold s ON s.d = dd.d
LEFT JOIN revenue r ON r.d = dd.d
ORDER BY dd.d
OPTION (MAXRECURSION 0);";

        var result = new List<DashboardDailyPoint>();
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 60 };
        command.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
        command.Parameters.Add("@start", SqlDbType.Date).Value = start.Date;
        command.Parameters.Add("@end", SqlDbType.Date).Value = end.Date;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new DashboardDailyPoint
            {
                Date = Convert.ToDateTime(reader["Date"], CultureInfo.InvariantCulture).Date,
                Inventory = ToInt(reader["Inventory"]),
                OutOfInventory = ToInt(reader["OutOfInventory"]),
                SellableInventory = ToInt(reader["SellableInventory"]),
                RoomsSold = ToInt(reader["RoomsSold"]),
                OccupancyPercent = ToDecimal(reader["OccupancyPercent"]),
                AccommodationRevenue = ToDecimal(reader["AccommodationRevenue"]),
                Adr = ToDecimal(reader["ADR"]),
                RevPar = ToDecimal(reader["RevPAR"])
            });
        }
        return result;
    }

    private async Task<DashboardHeadlineData> LoadHeadlineAsync(
        string hotelId,
        DateTime today,
        CancellationToken cancellationToken)
    {
        var monthStart = new DateTime(today.Year, today.Month, 1);
        var lastYearToday = today.AddYears(-1);
        var lastYearMonthStart = new DateTime(lastYearToday.Year, lastYearToday.Month, 1);
        var last30Start = today.AddDays(-29);
        var lastYearLast30Start = lastYearToday.AddDays(-29);

        // These result sets deliberately mirror the active WebForms Dashboard
        // formulas: PaymentsLogTB for revenue, Credit expenses, latest
        // PaymentsUpdateTB balance per reservation, and ArrivalDate-based
        // cancellation/no-show counts.
        const string sql = @"
DECLARE @isClosing bit = ISNULL((
    SELECT TOP (1) ISNULL(isClosing,0)
    FROM dbo.HotelsSignUpTB
    WHERE hotel_id = @hotel
),0);

SELECT
    COALESCE(SUM(CASE WHEN TRY_CONVERT(
        date,
        REPLACE(NULLIF(LTRIM(RTRIM(CONVERT(varchar(100),pl.currentdate))), ''), '  ', ' '),
        100) = @today
        THEN TRY_CONVERT(decimal(18,2),
            NULLIF(REPLACE(REPLACE(LTRIM(RTRIM(CONVERT(varchar(100),pl.paid_amount))), ',', ''), '£', ''), '')
        ) ELSE 0 END),0) AS RevenueToday,
    COALESCE(SUM(CASE WHEN TRY_CONVERT(
        date,
        REPLACE(NULLIF(LTRIM(RTRIM(CONVERT(varchar(100),pl.currentdate))), ''), '  ', ' '),
        100) = @lastYearToday
        THEN TRY_CONVERT(decimal(18,2),
            NULLIF(REPLACE(REPLACE(LTRIM(RTRIM(CONVERT(varchar(100),pl.paid_amount))), ',', ''), '£', ''), '')
        ) ELSE 0 END),0) AS RevenueTodayLastYear,
    COALESCE(SUM(CASE WHEN TRY_CONVERT(
        date,
        REPLACE(NULLIF(LTRIM(RTRIM(CONVERT(varchar(100),pl.currentdate))), ''), '  ', ' '),
        100) BETWEEN @monthStart AND @today
        THEN TRY_CONVERT(decimal(18,2),
            NULLIF(REPLACE(REPLACE(LTRIM(RTRIM(CONVERT(varchar(100),pl.paid_amount))), ',', ''), '£', ''), '')
        ) ELSE 0 END),0) AS MonthRevenue,
    COALESCE(SUM(CASE WHEN TRY_CONVERT(
        date,
        REPLACE(NULLIF(LTRIM(RTRIM(CONVERT(varchar(100),pl.currentdate))), ''), '  ', ' '),
        100) BETWEEN @lastYearMonthStart AND @lastYearToday
        THEN TRY_CONVERT(decimal(18,2),
            NULLIF(REPLACE(REPLACE(LTRIM(RTRIM(CONVERT(varchar(100),pl.paid_amount))), ',', ''), '£', ''), '')
        ) ELSE 0 END),0) AS LastYearMonthRevenue,
    COALESCE(SUM(CASE WHEN TRY_CONVERT(
        date,
        REPLACE(NULLIF(LTRIM(RTRIM(CONVERT(varchar(100),pl.currentdate))), ''), '  ', ' '),
        100) BETWEEN @last30Start AND @today
        THEN TRY_CONVERT(decimal(18,2),
            NULLIF(REPLACE(REPLACE(LTRIM(RTRIM(CONVERT(varchar(100),pl.paid_amount))), ',', ''), '£', ''), '')
        ) ELSE 0 END),0) AS RevenueLast30,
    COALESCE(SUM(CASE WHEN TRY_CONVERT(
        date,
        REPLACE(NULLIF(LTRIM(RTRIM(CONVERT(varchar(100),pl.currentdate))), ''), '  ', ' '),
        100) BETWEEN @lastYearLast30Start AND @lastYearToday
        THEN TRY_CONVERT(decimal(18,2),
            NULLIF(REPLACE(REPLACE(LTRIM(RTRIM(CONVERT(varchar(100),pl.paid_amount))), ',', ''), '£', ''), '')
        ) ELSE 0 END),0) AS RevenueLast30LastYear
FROM dbo.PaymentsLogTB pl
WHERE pl.hotel_id = @hotel
  AND (@isClosing = 0 OR LTRIM(RTRIM(CONVERT(varchar(20),pl.cb_status))) = '2');

SELECT
    COALESCE(SUM(CASE WHEN TRY_CONVERT(date, CONVERT(varchar(100),e.currentdate), 101) = @today
        AND LTRIM(RTRIM(ISNULL(e.transaction_type,''))) = 'Credit'
        THEN TRY_CONVERT(decimal(18,2),e.amount) ELSE 0 END),0) AS ExpensesToday,
    COALESCE(SUM(CASE WHEN TRY_CONVERT(date, CONVERT(varchar(100),e.currentdate), 101) = @lastYearToday
        THEN TRY_CONVERT(decimal(18,2),e.amount) ELSE 0 END),0) AS ExpensesTodayLastYear,
    COALESCE(SUM(CASE WHEN TRY_CONVERT(date, CONVERT(varchar(100),e.currentdate), 101) BETWEEN @monthStart AND @today
        AND LTRIM(RTRIM(ISNULL(e.transaction_type,''))) = 'Credit'
        THEN TRY_CONVERT(decimal(18,2),e.amount) ELSE 0 END),0) AS MonthExpense,
    COALESCE(SUM(CASE WHEN TRY_CONVERT(date, CONVERT(varchar(100),e.currentdate), 101) BETWEEN @lastYearMonthStart AND @lastYearToday
        THEN TRY_CONVERT(decimal(18,2),e.amount) ELSE 0 END),0) AS LastYearMonthExpense,
    COALESCE(SUM(CASE WHEN TRY_CONVERT(date, CONVERT(varchar(100),e.currentdate), 101) BETWEEN @last30Start AND @today
        AND LTRIM(RTRIM(ISNULL(e.transaction_type,''))) = 'Credit'
        THEN TRY_CONVERT(decimal(18,2),e.amount) ELSE 0 END),0) AS ExpenseLast30,
    COALESCE(SUM(CASE WHEN TRY_CONVERT(date, CONVERT(varchar(100),e.currentdate), 101) BETWEEN @lastYearLast30Start AND @lastYearToday
        THEN TRY_CONVERT(decimal(18,2),e.amount) ELSE 0 END),0) AS ExpenseLast30LastYear
FROM dbo.ExpenseDetailTB e
WHERE e.hotel_id = @hotel;

;WITH Unified AS
(
    SELECT
        LTRIM(RTRIM(g.hotel_id)) AS hotel_id,
        LTRIM(RTRIM(g.reg_id)) AS reg_id,
        COALESCE(
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(g.ArrivalDate)), ''), 110),
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(g.ArrivalDate)), ''), 103),
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(g.ArrivalDate)), ''))
        ) AS arr_date,
        COALESCE(
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(g.dept_date)), ''), 110),
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(g.dept_date)), ''), 103),
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(g.dept_date)), ''))
        ) AS dept_date
    FROM dbo.NewReservationsTB g
    WHERE g.hotel_id = @hotel

    UNION ALL

    SELECT
        LTRIM(RTRIM(g.hotel_id)),
        LTRIM(RTRIM(g.reg_id)),
        COALESCE(
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(g.ArrivalDate)), ''), 110),
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(g.ArrivalDate)), ''), 103),
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(g.ArrivalDate)), ''))
        ),
        COALESCE(
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(g.DepartureDate)), ''), 110),
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(g.DepartureDate)), ''), 103),
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(g.DepartureDate)), ''))
        )
    FROM dbo.GuestInformationLogTB g
    WHERE g.hotel_id = @hotel
),
Res AS
(
    SELECT hotel_id, reg_id, MIN(arr_date) AS arr_date, MAX(dept_date) AS dept_date
    FROM Unified
    WHERE NULLIF(reg_id,'') IS NOT NULL
    GROUP BY hotel_id, reg_id
),
LatestRem AS
(
    SELECT
        r.hotel_id,
        r.reg_id,
        TRY_CONVERT(decimal(18,2),
            NULLIF(REPLACE(REPLACE(REPLACE(LTRIM(RTRIM(CONVERT(varchar(100),p.remaining_amount))), ',', ''), '£', ''), '$', ''), '')
        ) AS remaining_dec,
        TRY_CONVERT(date, CONVERT(varchar(100),p.currentdate), 101) AS current_date,
        ROW_NUMBER() OVER
        (
            PARTITION BY LTRIM(RTRIM(CONVERT(varchar(100),p.hotel_id))), LTRIM(RTRIM(p.reg_id))
            ORDER BY p.currentdate DESC, p.id DESC
        ) AS rn
    FROM Res r
    INNER JOIN dbo.PaymentsUpdateTB p
        ON LTRIM(RTRIM(CONVERT(varchar(100),p.hotel_id))) = r.hotel_id
       AND LTRIM(RTRIM(p.reg_id)) = r.reg_id
)
SELECT
    COALESCE(SUM(CASE WHEN r.arr_date >= @today AND r.arr_date <= @today AND lr.remaining_dec > 0
        THEN lr.remaining_dec ELSE 0 END),0) AS TotalReceivable,
    COALESCE(SUM(CASE WHEN r.arr_date >= @today AND r.arr_date <= @today AND lr.remaining_dec > 0
        AND lr.current_date < DATEADD(day,-30,@today)
        THEN lr.remaining_dec ELSE 0 END),0) AS Over30
FROM Res r
LEFT JOIN LatestRem lr
    ON lr.hotel_id = r.hotel_id
   AND lr.reg_id = r.reg_id
   AND lr.rn = 1
WHERE r.hotel_id = @hotel;

SELECT 'cancel' AS ExceptionType, TRY_CONVERT(date, ArrivalDate) AS EventDate
FROM dbo.CancelledReservationsTB
WHERE hotel_id=@hotel
  AND TRY_CONVERT(date,ArrivalDate) >= @last30Start
  AND TRY_CONVERT(date,ArrivalDate) < DATEADD(day,1,@today)
UNION ALL
SELECT 'noshow', TRY_CONVERT(date, ArrivalDate)
FROM dbo.NoShowTB
WHERE hotel_id=@hotel
  AND TRY_CONVERT(date,ArrivalDate) >= @last30Start
  AND TRY_CONVERT(date,ArrivalDate) < DATEADD(day,1,@today);

SELECT COUNT(DISTINCT reg_id) AS Reservations
FROM dbo.payments
WHERE hotel_id=@hotel
  AND LTRIM(RTRIM(ISNULL(descr,'')))='Room Rent'
  AND COALESCE(
        TRY_CONVERT(date,ArrivalDate,110),
        TRY_CONVERT(date,ArrivalDate,103),
        TRY_CONVERT(date,ArrivalDate)
      ) BETWEEN @last30Start AND @today;

;WITH RoomBase AS
(
    SELECT DISTINCT LTRIM(RTRIM(room_no)) AS room_no
    FROM dbo.RoomsTB
    WHERE Hotel_id = @hotel
      AND ISNULL(LTRIM(RTRIM(room_no)), '') <> ''
),
PaymentBase AS
(
    SELECT
        LTRIM(RTRIM(p.room_no)) AS room_no,
        p.reg_id,
        p.hotel_id,
        LOWER(LTRIM(RTRIM(ISNULL(p.res_status, '')))) AS res_status,
        COALESCE(
            TRY_CONVERT(date,p.ArrivalDate),
            TRY_CONVERT(date,p.ArrivalDate,103),
            TRY_CONVERT(date,p.ArrivalDate,110),
            TRY_CONVERT(date,p.ArrivalDate,101),
            TRY_CONVERT(date,p.ArrivalDate,120),
            TRY_CONVERT(date,p.ArrivalDate,126)
        ) AS ArrivalDateParsed,
        COALESCE(
            TRY_CONVERT(date,p.DepartureDate),
            TRY_CONVERT(date,p.DepartureDate,103),
            TRY_CONVERT(date,p.DepartureDate,110),
            TRY_CONVERT(date,p.DepartureDate,101),
            TRY_CONVERT(date,p.DepartureDate,120),
            TRY_CONVERT(date,p.DepartureDate,126)
        ) AS DepartureDateParsed
    FROM dbo.payments p
    WHERE p.hotel_id = @hotel
      AND UPPER(LTRIM(RTRIM(ISNULL(p.room_no, '')))) <> 'UNASSIGNED'
      AND ISNULL(LTRIM(RTRIM(p.room_no)), '') <> ''
      AND LOWER(LTRIM(RTRIM(ISNULL(p.res_status, '')))) IN ('check in','reservation')
),
OccupiedRoomList AS
(
    SELECT DISTINCT pb.room_no
    FROM PaymentBase pb
    INNER JOIN RoomBase rb ON rb.room_no = pb.room_no
    WHERE pb.ArrivalDateParsed IS NOT NULL
      AND pb.DepartureDateParsed IS NOT NULL
      AND pb.ArrivalDateParsed < DATEADD(day,1,@today)
      AND @today < pb.DepartureDateParsed
      AND (
            EXISTS (SELECT 1 FROM dbo.GuestInformationLogTB gi WHERE gi.reg_id=pb.reg_id AND gi.hotel_id=pb.hotel_id)
            OR EXISTS (SELECT 1 FROM dbo.NewReservationsTB nr WHERE nr.reg_id=pb.reg_id AND nr.hotel_id=pb.hotel_id)
          )
),
BlockedRoomList AS
(
    SELECT DISTINCT rbBase.room_no
    FROM RoomBase rbBase
    INNER JOIN dbo.RoomBlocksTB rb
        ON rb.HotelID=@hotel
       AND LTRIM(RTRIM(rb.RoomNo))=rbBase.room_no
       AND rb.IsActive=1
       AND CAST(rb.BlockStartDate AS date) < DATEADD(day,1,@today)
       AND @today < DATEADD(day,1,CAST(ISNULL(rb.BlockEndDate,'9999-12-31') AS date))
),
UnavailableRoomList AS
(
    SELECT room_no FROM OccupiedRoomList
    UNION
    SELECT room_no FROM BlockedRoomList
),
DirtyRooms AS
(
    SELECT COUNT(DISTINCT LTRIM(RTRIM(room_no))) AS DirtyRooms
    FROM dbo.RoomsTB
    WHERE Hotel_id=@hotel
      AND (room_status='NotCleaned' OR room_status='NotClean' OR room_status='CheckOut')
)
SELECT
    (SELECT COUNT(*) FROM RoomBase) AS Total,
    (SELECT COUNT(*) FROM BlockedRoomList) AS blocked,
    (SELECT COUNT(*) FROM OccupiedRoomList) AS occupied,
    CASE WHEN (SELECT COUNT(*) FROM RoomBase)-(SELECT COUNT(*) FROM UnavailableRoomList) < 0
         THEN 0 ELSE (SELECT COUNT(*) FROM RoomBase)-(SELECT COUNT(*) FROM UnavailableRoomList) END AS available,
    ISNULL((SELECT DirtyRooms FROM DirtyRooms),0) AS dirty;";

        var result = new DashboardHeadlineData();
        var rawExceptions = new List<(string Type, DateTime Date)>();
        var reservationCount = 0;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 35 };
        command.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
        command.Parameters.Add("@today", SqlDbType.Date).Value = today.Date;
        command.Parameters.Add("@lastYearToday", SqlDbType.Date).Value = lastYearToday.Date;
        command.Parameters.Add("@monthStart", SqlDbType.Date).Value = monthStart.Date;
        command.Parameters.Add("@lastYearMonthStart", SqlDbType.Date).Value = lastYearMonthStart.Date;
        command.Parameters.Add("@last30Start", SqlDbType.Date).Value = last30Start.Date;
        command.Parameters.Add("@lastYearLast30Start", SqlDbType.Date).Value = lastYearLast30Start.Date;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            result.Financial.RevenueToday = ToDecimal(reader["RevenueToday"]);
            result.Financial.RevenueTodayLastYear = ToDecimal(reader["RevenueTodayLastYear"]);
            result.Financial.MonthRevenue = ToDecimal(reader["MonthRevenue"]);
            result.Financial.LastYearMonthRevenue = ToDecimal(reader["LastYearMonthRevenue"]);
            result.Financial.RevenueLast30 = ToDecimal(reader["RevenueLast30"]);
            result.Financial.RevenueLast30LastYear = ToDecimal(reader["RevenueLast30LastYear"]);
        }

        if (await reader.NextResultAsync(cancellationToken) && await reader.ReadAsync(cancellationToken))
        {
            result.Financial.ExpensesToday = ToDecimal(reader["ExpensesToday"]);
            result.Financial.ExpensesTodayLastYear = ToDecimal(reader["ExpensesTodayLastYear"]);
            result.Financial.MonthExpense = ToDecimal(reader["MonthExpense"]);
            result.Financial.LastYearMonthExpense = ToDecimal(reader["LastYearMonthExpense"]);
            result.Financial.ExpenseLast30 = ToDecimal(reader["ExpenseLast30"]);
            result.Financial.ExpenseLast30LastYear = ToDecimal(reader["ExpenseLast30LastYear"]);
        }

        if (await reader.NextResultAsync(cancellationToken) && await reader.ReadAsync(cancellationToken))
        {
            result.Receivables.Total = ToDecimal(reader["TotalReceivable"]);
            result.Receivables.Over30Days = ToDecimal(reader["Over30"]);
        }

        if (await reader.NextResultAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                if (reader["EventDate"] == DBNull.Value) continue;
                rawExceptions.Add((
                    Convert.ToString(reader["ExceptionType"]) ?? string.Empty,
                    Convert.ToDateTime(reader["EventDate"], CultureInfo.InvariantCulture).Date));
            }
        }

        if (await reader.NextResultAsync(cancellationToken) && await reader.ReadAsync(cancellationToken))
            reservationCount = ToInt(reader["Reservations"]);

        if (await reader.NextResultAsync(cancellationToken) && await reader.ReadAsync(cancellationToken))
        {
            result.Rooms.Total = ToInt(reader["Total"]);
            result.Rooms.Blocked = ToInt(reader["blocked"]);
            result.Rooms.Occupied = ToInt(reader["occupied"]);
            result.Rooms.Available = ToInt(reader["available"]);
            result.Rooms.Dirty = ToInt(reader["dirty"]);
        }

        result.Exceptions = BuildExceptionData(rawExceptions, reservationCount, today);
        return result;
    }

    private async Task<DashboardFinancialSnapshot> LoadFinancialSnapshotAsync(
        string hotelId,
        DateTime today,
        CancellationToken cancellationToken)
    {
        // Keep Revenue and Expense independent. The WebForms Dashboard calculates
        // them independently (GetRevenue / GetExpense), then Profit = Revenue - Expense.
        // Running both aggregates in parallel reduces first-paint time and prevents one
        // legacy data issue from zeroing every financial KPI.
        var revenueTask = SafeLoadAsync(
            () => LoadRevenueSnapshotAsync(hotelId, today, cancellationToken),
            new DashboardFinancialSnapshot(),
            "revenue headline");

        var expenseTask = SafeLoadAsync(
            () => LoadExpenseSnapshotAsync(hotelId, today, cancellationToken),
            new DashboardFinancialSnapshot(),
            "expense headline");

        await Task.WhenAll(revenueTask, expenseTask);

        var revenue = await revenueTask;
        var expense = await expenseTask;

        return new DashboardFinancialSnapshot
        {
            RevenueToday = revenue.RevenueToday,
            RevenueTodayLastYear = revenue.RevenueTodayLastYear,
            MonthRevenue = revenue.MonthRevenue,
            LastYearMonthRevenue = revenue.LastYearMonthRevenue,
            RevenueLast30 = revenue.RevenueLast30,
            RevenueLast30LastYear = revenue.RevenueLast30LastYear,

            ExpensesToday = expense.ExpensesToday,
            ExpensesTodayLastYear = expense.ExpensesTodayLastYear,
            MonthExpense = expense.MonthExpense,
            LastYearMonthExpense = expense.LastYearMonthExpense,
            ExpenseLast30 = expense.ExpenseLast30,
            ExpenseLast30LastYear = expense.ExpenseLast30LastYear
        };
    }

    private async Task<DashboardFinancialSnapshot> LoadRevenueSnapshotAsync(
        string hotelId,
        DateTime today,
        CancellationToken cancellationToken)
    {
        var startDate = today.Date.AddDays(-29);
        var lastYearToday = today.Date.AddYears(-1);
        var lastYearStartDate = lastYearToday.AddDays(-29);
        var monthStart = new DateTime(today.Year, today.Month, 1);
        var lastYearMonthStart = new DateTime(lastYearToday.Year, lastYearToday.Month, 1);

        // Same active WebForms GetRevenue rule:
        // SUM(PaymentsLogTB.paid_amount)
        // + hotel filter
        // + currentdate converted with style 100
        // + cb_status = 2 only when hotel closing is enabled.
        const string sql = @"
DECLARE @isClosing bit = 0;

SELECT TOP (1)
    @isClosing =
        CASE
            WHEN TRY_CONVERT(int, ISNULL(isClosing,0)) = 1 THEN 1
            ELSE 0
        END
FROM dbo.HotelsSignUpTB
WHERE CONVERT(varchar(50),hotel_id) = @hotel;

WITH RevenueRows AS
(
    SELECT
        TRY_CONVERT(
            date,
            REPLACE(NULLIF(LTRIM(RTRIM(CONVERT(varchar(100),pl.currentdate))), ''), '  ', ' '),
            100
        ) AS tx_date,
        TRY_CONVERT(
            decimal(18,2),
            NULLIF(
                REPLACE(
                    REPLACE(LTRIM(RTRIM(CONVERT(varchar(100),pl.paid_amount))), ',', ''),
                    '£',
                    ''
                ),
                ''
            )
        ) AS amount
    FROM dbo.PaymentsLogTB AS pl
    WHERE CONVERT(varchar(50),pl.hotel_id) = @hotel
      AND (@isClosing = 0 OR LTRIM(RTRIM(CONVERT(varchar(20),pl.cb_status))) = '2')
)
SELECT
    COALESCE(SUM(CASE WHEN tx_date = @today THEN amount ELSE 0 END),0) AS RevenueToday,
    COALESCE(SUM(CASE WHEN tx_date = @lastYearToday THEN amount ELSE 0 END),0) AS RevenueTodayLastYear,
    COALESCE(SUM(CASE WHEN tx_date BETWEEN @monthStart AND @today THEN amount ELSE 0 END),0) AS MonthRevenue,
    COALESCE(SUM(CASE WHEN tx_date BETWEEN @lastYearMonthStart AND @lastYearToday THEN amount ELSE 0 END),0) AS LastYearMonthRevenue,
    COALESCE(SUM(CASE WHEN tx_date BETWEEN @startDate AND @today THEN amount ELSE 0 END),0) AS RevenueLast30,
    COALESCE(SUM(CASE WHEN tx_date BETWEEN @lastYearStartDate AND @lastYearToday THEN amount ELSE 0 END),0) AS RevenueLast30LastYear
FROM RevenueRows;";

        var result = new DashboardFinancialSnapshot();

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand(sql, connection)
        {
            CommandTimeout = 20
        };

        command.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId.Trim();
        command.Parameters.Add("@today", SqlDbType.Date).Value = today.Date;
        command.Parameters.Add("@lastYearToday", SqlDbType.Date).Value = lastYearToday;
        command.Parameters.Add("@monthStart", SqlDbType.Date).Value = monthStart.Date;
        command.Parameters.Add("@lastYearMonthStart", SqlDbType.Date).Value = lastYearMonthStart.Date;
        command.Parameters.Add("@startDate", SqlDbType.Date).Value = startDate;
        command.Parameters.Add("@lastYearStartDate", SqlDbType.Date).Value = lastYearStartDate;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            result.RevenueToday = ToDecimal(reader["RevenueToday"]);
            result.RevenueTodayLastYear = ToDecimal(reader["RevenueTodayLastYear"]);
            result.MonthRevenue = ToDecimal(reader["MonthRevenue"]);
            result.LastYearMonthRevenue = ToDecimal(reader["LastYearMonthRevenue"]);
            result.RevenueLast30 = ToDecimal(reader["RevenueLast30"]);
            result.RevenueLast30LastYear = ToDecimal(reader["RevenueLast30LastYear"]);
        }

        return result;
    }

    private async Task<DashboardFinancialSnapshot> LoadExpenseSnapshotAsync(
        string hotelId,
        DateTime today,
        CancellationToken cancellationToken)
    {
        var startDate = today.Date.AddDays(-29);
        var lastYearToday = today.Date.AddYears(-1);
        var lastYearStartDate = lastYearToday.AddDays(-29);
        var monthStart = new DateTime(today.Year, today.Month, 1);
        var lastYearMonthStart = new DateTime(lastYearToday.Year, lastYearToday.Month, 1);

        // Same active WebForms GetExpense rule:
        // SUM(ExpenseDetailTB.amount) for transaction_type = Credit.
        const string sql = @"
WITH ExpenseRows AS
(
    SELECT
        COALESCE(
            TRY_CONVERT(date, CONVERT(varchar(100),e.currentdate), 101),
            TRY_CONVERT(date, CONVERT(varchar(100),e.currentdate), 103),
            TRY_CONVERT(date, CONVERT(varchar(100),e.currentdate), 100),
            TRY_CONVERT(date, CONVERT(varchar(100),e.currentdate))
        ) AS tx_date,
        TRY_CONVERT(decimal(18,2),e.amount) AS amount
    FROM dbo.ExpenseDetailTB AS e
    WHERE CONVERT(varchar(50),e.hotel_id) = @hotel
      AND LTRIM(RTRIM(ISNULL(e.transaction_type,''))) = 'Credit'
)
SELECT
    COALESCE(SUM(CASE WHEN tx_date = @today THEN amount ELSE 0 END),0) AS ExpensesToday,
    COALESCE(SUM(CASE WHEN tx_date = @lastYearToday THEN amount ELSE 0 END),0) AS ExpensesTodayLastYear,
    COALESCE(SUM(CASE WHEN tx_date BETWEEN @monthStart AND @today THEN amount ELSE 0 END),0) AS MonthExpense,
    COALESCE(SUM(CASE WHEN tx_date BETWEEN @lastYearMonthStart AND @lastYearToday THEN amount ELSE 0 END),0) AS LastYearMonthExpense,
    COALESCE(SUM(CASE WHEN tx_date BETWEEN @startDate AND @today THEN amount ELSE 0 END),0) AS ExpenseLast30,
    COALESCE(SUM(CASE WHEN tx_date BETWEEN @lastYearStartDate AND @lastYearToday THEN amount ELSE 0 END),0) AS ExpenseLast30LastYear
FROM ExpenseRows;";

        var result = new DashboardFinancialSnapshot();

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand(sql, connection)
        {
            CommandTimeout = 20
        };

        command.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId.Trim();
        command.Parameters.Add("@today", SqlDbType.Date).Value = today.Date;
        command.Parameters.Add("@lastYearToday", SqlDbType.Date).Value = lastYearToday;
        command.Parameters.Add("@monthStart", SqlDbType.Date).Value = monthStart.Date;
        command.Parameters.Add("@lastYearMonthStart", SqlDbType.Date).Value = lastYearMonthStart.Date;
        command.Parameters.Add("@startDate", SqlDbType.Date).Value = startDate;
        command.Parameters.Add("@lastYearStartDate", SqlDbType.Date).Value = lastYearStartDate;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            result.ExpensesToday = ToDecimal(reader["ExpensesToday"]);
            result.ExpensesTodayLastYear = ToDecimal(reader["ExpensesTodayLastYear"]);
            result.MonthExpense = ToDecimal(reader["MonthExpense"]);
            result.LastYearMonthExpense = ToDecimal(reader["LastYearMonthExpense"]);
            result.ExpenseLast30 = ToDecimal(reader["ExpenseLast30"]);
            result.ExpenseLast30LastYear = ToDecimal(reader["ExpenseLast30LastYear"]);
        }

        return result;
    }

    private async Task<List<DashboardFinancialPoint>> LoadFinancialSeriesAsync(
        string hotelId,
        DateTime today,
        CancellationToken cancellationToken)
    {
        var start = today.AddDays(-29);
        var lyStart = start.AddYears(-1);
        var lyEnd = today.AddYears(-1);

        const string sql = @"
;WITH d AS
(
    SELECT @start AS d
    UNION ALL
    SELECT DATEADD(day, 1, d) FROM d WHERE d < @end
),
rev AS
(
    SELECT x.tx_date, SUM(x.amount) AS amount
    FROM
    (
        SELECT
            COALESCE(
                TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(CONVERT(varchar(100),pl.currentdate))), ''), 100),
                TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(CONVERT(varchar(100),pl.currentdate))), ''), 101),
                TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(CONVERT(varchar(100),pl.currentdate))), ''), 103),
                TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(CONVERT(varchar(100),pl.currentdate))), ''))
            ) AS tx_date,
            COALESCE(
                TRY_CONVERT(decimal(18,2), NULLIF(REPLACE(REPLACE(REPLACE(LTRIM(RTRIM(CONVERT(varchar(100),pl.paid_amount))), ',', ''), '£', ''), '$', ''), '')),
                0
            ) AS amount
        FROM dbo.PaymentsLogTB AS pl
        WHERE pl.hotel_id = @hotel
          AND (
                ISNULL((SELECT TOP (1) ISNULL(h.isClosing,0)
                        FROM dbo.HotelsSignUpTB h
                        WHERE h.hotel_id = @hotel),0) = 0
                OR LTRIM(RTRIM(CONVERT(varchar(20),pl.cb_status))) = '2'
              )
    ) x
    WHERE (x.tx_date BETWEEN @start AND @end)
       OR (x.tx_date BETWEEN @ly_start AND @ly_end)
    GROUP BY x.tx_date
),
exp AS
(
    SELECT x.tx_date, SUM(x.amount) AS amount
    FROM
    (
        SELECT
            COALESCE(
                TRY_CONVERT(date, currentdate, 101),
                TRY_CONVERT(date, currentdate, 103),
                TRY_CONVERT(date, currentdate)
            ) AS tx_date,
            COALESCE(TRY_CONVERT(decimal(18,2), amount), 0) AS amount
        FROM dbo.ExpenseDetailTB
        WHERE hotel_id = @hotel
          AND LTRIM(RTRIM(ISNULL(transaction_type, ''))) = 'Credit'
    ) x
    WHERE (x.tx_date BETWEEN @start AND @end)
       OR (x.tx_date BETWEEN @ly_start AND @ly_end)
    GROUP BY x.tx_date
)
SELECT
    dd.d AS [Date],
    ISNULL(rc.amount,0) AS Revenue,
    ISNULL(ec.amount,0) AS Expense,
    ISNULL(rl.amount,0) AS LastYearRevenue,
    ISNULL(el.amount,0) AS LastYearExpense
FROM d dd
LEFT JOIN rev rc ON rc.tx_date = dd.d
LEFT JOIN exp ec ON ec.tx_date = dd.d
LEFT JOIN rev rl ON rl.tx_date = DATEADD(year,-1,dd.d)
LEFT JOIN exp el ON el.tx_date = DATEADD(year,-1,dd.d)
ORDER BY dd.d
OPTION (MAXRECURSION 100);";

        var result = new List<DashboardFinancialPoint>();
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 45 };
        command.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
        command.Parameters.Add("@start", SqlDbType.Date).Value = start;
        command.Parameters.Add("@end", SqlDbType.Date).Value = today;
        command.Parameters.Add("@ly_start", SqlDbType.Date).Value = lyStart;
        command.Parameters.Add("@ly_end", SqlDbType.Date).Value = lyEnd;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var revenue = ToDecimal(reader["Revenue"]);
            var expense = ToDecimal(reader["Expense"]);
            var lyRevenue = ToDecimal(reader["LastYearRevenue"]);
            var lyExpense = ToDecimal(reader["LastYearExpense"]);
            result.Add(new DashboardFinancialPoint
            {
                Date = Convert.ToDateTime(reader["Date"], CultureInfo.InvariantCulture).Date,
                Revenue = revenue,
                Expense = expense,
                Profit = revenue - expense,
                LastYearRevenue = lyRevenue,
                LastYearProfit = lyRevenue - lyExpense
            });
        }
        return result;
    }

    private async Task<DashboardReceivableData> LoadReceivablesAsync(
        string hotelId,
        DateTime today,
        CancellationToken cancellationToken)
    {
        // Keep this identical to the WebForms GetPendingPay business rule.
        // We run the same formula for two explicit ranges so the card is easy
        // to verify against the old Dashboard:
        //   1) Today's arrivals
        //   2) Arrivals in the last 30 hotel-local days
        //
        // Both requests run in parallel, so the extra clarity does not block
        // the rest of the Dashboard.
        var todayTask = LoadReceivableArrivalRangeAsync(
            hotelId,
            today.Date,
            today.Date,
            cancellationToken);

        var last30Task = LoadReceivableArrivalRangeAsync(
            hotelId,
            today.Date.AddDays(-29),
            today.Date,
            cancellationToken);

        await Task.WhenAll(todayTask, last30Task);

        var todayAmount = await todayTask;
        var last30Amount = await last30Task;

        return new DashboardReceivableData
        {
            Today = todayAmount,
            Last30Arrivals = last30Amount,
            Total = last30Amount,
            Over30Days = 0m
        };
    }

    private async Task<decimal> LoadReceivableArrivalRangeAsync(
        string hotelId,
        DateTime startDate,
        DateTime endDate,
        CancellationToken cancellationToken)
    {
        const string sql = @"
WITH Unified AS
(
    SELECT
        LTRIM(RTRIM(CONVERT(varchar(50),g.hotel_id))) AS hotel_id,
        LTRIM(RTRIM(g.reg_id)) AS reg_id,
        COALESCE(
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(g.ArrivalDate)), ''), 110),
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(g.ArrivalDate)), ''), 103),
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(g.ArrivalDate)), ''))
        ) AS arr_date,
        COALESCE(
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(g.dept_date)), ''), 110),
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(g.dept_date)), ''), 103),
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(g.dept_date)), ''))
        ) AS dept_date
    FROM dbo.NewReservationsTB g
    WHERE CONVERT(varchar(50),g.hotel_id) = @hotel

    UNION ALL

    SELECT
        LTRIM(RTRIM(CONVERT(varchar(50),g.hotel_id))) AS hotel_id,
        LTRIM(RTRIM(g.reg_id)) AS reg_id,
        COALESCE(
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(g.ArrivalDate)), ''), 110),
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(g.ArrivalDate)), ''), 103),
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(g.ArrivalDate)), ''))
        ) AS arr_date,
        COALESCE(
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(g.DepartureDate)), ''), 110),
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(g.DepartureDate)), ''), 103),
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(g.DepartureDate)), ''))
        ) AS dept_date
    FROM dbo.GuestInformationLogTB g
    WHERE CONVERT(varchar(50),g.hotel_id) = @hotel
),
Res AS
(
    SELECT
        hotel_id,
        reg_id,
        MIN(arr_date) AS arr_date,
        MAX(dept_date) AS dept_date
    FROM Unified
    WHERE NULLIF(reg_id,'') IS NOT NULL
    GROUP BY hotel_id, reg_id
),
LatestRem AS
(
    SELECT
        r.hotel_id,
        r.reg_id,
        TRY_CONVERT(
            decimal(18,2),
            NULLIF(
                REPLACE(
                    REPLACE(
                        REPLACE(
                            LTRIM(RTRIM(CONVERT(varchar(100),p.remaining_amount))),
                            ',',
                            ''
                        ),
                        '£',
                        ''
                    ),
                    '$',
                    ''
                ),
                ''
            )
        ) AS remaining_dec,
        ROW_NUMBER() OVER
        (
            PARTITION BY
                LTRIM(RTRIM(CONVERT(varchar(100),p.hotel_id))),
                LTRIM(RTRIM(p.reg_id))
            ORDER BY p.currentdate DESC, p.id DESC
        ) AS rn
    FROM Res r
    INNER JOIN dbo.PaymentsUpdateTB p
        ON LTRIM(RTRIM(CONVERT(varchar(100),p.hotel_id))) = r.hotel_id
       AND LTRIM(RTRIM(p.reg_id)) = r.reg_id
)
SELECT
    COALESCE(SUM(x.remaining_dec),0) AS Pending_Pay
FROM
(
    SELECT
        r.hotel_id,
        r.reg_id,
        lr.remaining_dec
    FROM Res r
    LEFT JOIN LatestRem lr
        ON lr.hotel_id = r.hotel_id
       AND lr.reg_id = r.reg_id
       AND lr.rn = 1
    WHERE r.arr_date >= @startdate
      AND r.arr_date <= @enddate
      AND r.hotel_id = @hotel
) x
WHERE x.remaining_dec IS NOT NULL
  AND x.remaining_dec > CAST(0.00 AS decimal(18,2));";

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand(sql, connection)
        {
            CommandTimeout = 20
        };

        command.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId.Trim();
        command.Parameters.Add("@startdate", SqlDbType.Date).Value = startDate.Date;
        command.Parameters.Add("@enddate", SqlDbType.Date).Value = endDate.Date;

        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return ToDecimal(scalar);
    }

    private async Task<List<DashboardRevenueSourceItem>> LoadRevenueSourcesAsync(
        string hotelId,
        DateTime start,
        DateTime end,
        CancellationToken cancellationToken)
    {
        const string sql = @"
;WITH agency_candidates AS
(
    SELECT
        gl.reg_id,
        CASE
            WHEN NULLIF(LTRIM(RTRIM(gl.agency)), '') IS NULL
              OR REPLACE(LOWER(LTRIM(RTRIM(ISNULL(gl.agency, '')))), ' ', '') IN ('--select--','select')
            THEN 'Walk In' ELSE LTRIM(RTRIM(gl.agency)) END AS AgencyName,
        1 AS Priority
    FROM dbo.GuestInformationLogTB gl
    WHERE gl.hotel_id = @hotel
      AND NULLIF(LTRIM(RTRIM(gl.reg_id)), '') IS NOT NULL
    UNION ALL
    SELECT
        nr.reg_id,
        CASE
            WHEN NULLIF(LTRIM(RTRIM(nr.agency)), '') IS NULL
              OR REPLACE(LOWER(LTRIM(RTRIM(ISNULL(nr.agency, '')))), ' ', '') IN ('--select--','select')
            THEN 'Walk In' ELSE LTRIM(RTRIM(nr.agency)) END,
        2
    FROM dbo.NewReservationsTB nr
    WHERE nr.hotel_id = @hotel
      AND NULLIF(LTRIM(RTRIM(nr.reg_id)), '') IS NOT NULL
),
agency_ranked AS
(
    SELECT reg_id, AgencyName,
        ROW_NUMBER() OVER (PARTITION BY reg_id ORDER BY Priority, AgencyName) AS rn
    FROM agency_candidates
),
room_rent AS
(
    SELECT
        NULLIF(LTRIM(RTRIM(p.reg_id)), '') AS reg_id,
        NULLIF(LTRIM(RTRIM(p.room_no)), '') AS room_no,
        COALESCE(a.AgencyName, 'Walk In') AS AgencyName,
        COALESCE(
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(p.ArrivalDate)), ''), 110),
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(p.ArrivalDate)), ''), 103),
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(p.ArrivalDate)), ''))
        ) AS arrival_date,
        COALESCE(
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(p.DepartureDate)), ''), 110),
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(p.DepartureDate)), ''), 103),
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(p.DepartureDate)), ''))
        ) AS departure_date,
        LOWER(LTRIM(RTRIM(ISNULL(p.res_status,'')))) AS res_status,
        COALESCE(
            TRY_CONVERT(decimal(18,2), NULLIF(REPLACE(REPLACE(REPLACE(LTRIM(RTRIM(p.Rate)), ',', ''), '£', ''), '$', ''), '')),
            0
        ) AS room_rate,
        COALESCE(
            NULLIF(TRY_CONVERT(int, NULLIF(LTRIM(RTRIM(p.Nights)), '')), 0),
            NULLIF(DATEDIFF(day,
                COALESCE(TRY_CONVERT(date,p.ArrivalDate,110),TRY_CONVERT(date,p.ArrivalDate,103),TRY_CONVERT(date,p.ArrivalDate)),
                COALESCE(TRY_CONVERT(date,p.DepartureDate,110),TRY_CONVERT(date,p.DepartureDate,103),TRY_CONVERT(date,p.DepartureDate))), 0)
        ) AS stay_nights
    FROM dbo.payments p
    LEFT JOIN agency_ranked a ON a.reg_id = p.reg_id AND a.rn = 1
    WHERE p.hotel_id = @hotel
      AND LTRIM(RTRIM(ISNULL(p.descr,''))) = 'Room Rent'
      AND NULLIF(LTRIM(RTRIM(p.room_no)), '') IS NOT NULL
),
d AS
(
    SELECT @start AS d
    UNION ALL SELECT DATEADD(day,1,d) FROM d WHERE d < @end
),
daily AS
(
    SELECT
        rr.AgencyName,
        rr.reg_id,
        rr.room_no,
        dd.d,
        rr.room_rate / NULLIF(rr.stay_nights,0) AS DailyRevenue
    FROM d dd
    INNER JOIN room_rent rr ON rr.arrival_date <= dd.d AND dd.d < rr.departure_date
    WHERE rr.res_status NOT IN ('cancel','cancelled','canceled','no show','noshow','no-show')
)
SELECT TOP (10)
    AgencyName,
    COUNT(DISTINCT reg_id) AS Reservations,
    COUNT(DISTINCT ISNULL(reg_id,'') + '|' + ISNULL(room_no,'') + '|' + CONVERT(varchar(10),d,120)) AS RoomNights,
    CAST(SUM(DailyRevenue) AS decimal(18,2)) AS Revenue,
    CAST(CASE WHEN COUNT(DISTINCT ISNULL(reg_id,'') + '|' + ISNULL(room_no,'') + '|' + CONVERT(varchar(10),d,120)) = 0 THEN 0
         ELSE SUM(DailyRevenue) / COUNT(DISTINCT ISNULL(reg_id,'') + '|' + ISNULL(room_no,'') + '|' + CONVERT(varchar(10),d,120)) END AS decimal(18,2)) AS ADR,
    CAST(CASE WHEN SUM(SUM(DailyRevenue)) OVER () = 0 THEN 0
         ELSE 100.0 * SUM(DailyRevenue) / SUM(SUM(DailyRevenue)) OVER () END AS decimal(9,2)) AS SharePercent
FROM daily
GROUP BY AgencyName
ORDER BY Revenue DESC, AgencyName
OPTION (MAXRECURSION 100);";

        var result = new List<DashboardRevenueSourceItem>();
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 60 };
        command.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
        command.Parameters.Add("@start", SqlDbType.Date).Value = start.Date;
        command.Parameters.Add("@end", SqlDbType.Date).Value = end.Date;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new DashboardRevenueSourceItem
            {
                Source = Convert.ToString(reader["AgencyName"])?.Trim() ?? "Walk In",
                Reservations = ToInt(reader["Reservations"]),
                RoomNights = ToInt(reader["RoomNights"]),
                Revenue = ToDecimal(reader["Revenue"]),
                Adr = ToDecimal(reader["ADR"]),
                SharePercent = ToDecimal(reader["SharePercent"])
            });
        }
        return result;
    }

    private async Task<List<DashboardRoomTypeItem>> LoadRoomTypesAsync(
        string hotelId,
        DateTime today,
        CancellationToken cancellationToken)
    {
        const string sql = @"
;WITH d AS
(
    SELECT @today AS d
    UNION ALL SELECT DATEADD(day,1,d) FROM d WHERE d < DATEADD(day,29,@today)
),
inv AS
(
    SELECT LTRIM(RTRIM(ISNULL(room_category,''))) AS category, COUNT(*) AS Rooms
    FROM dbo.RoomsTB
    WHERE Hotel_id = @hotel
      AND NULLIF(LTRIM(RTRIM(room_category)), '') IS NOT NULL
    GROUP BY LTRIM(RTRIM(ISNULL(room_category,'')))
),
ooo AS
(
    SELECT
        LTRIM(RTRIM(ISNULL(rt.room_category,''))) AS category,
        dd.d,
        COUNT(DISTINCT rt.room_no) AS blocked
    FROM d dd
    INNER JOIN dbo.RoomsTB rt ON rt.Hotel_id = @hotel
    INNER JOIN dbo.RoomBlocksTB rb
        ON rb.HotelID = rt.Hotel_id
       AND rb.RoomNo = rt.room_no
       AND rb.IsActive = 1
       AND CAST(rb.BlockStartDate AS date) <= dd.d
       AND dd.d < DATEADD(day,1,CAST(ISNULL(rb.BlockEndDate,'9999-12-31') AS date))
    GROUP BY LTRIM(RTRIM(ISNULL(rt.room_category,''))), dd.d
),
p AS
(
    SELECT
        NULLIF(LTRIM(RTRIM(p.reg_id)), '') AS reg_id,
        NULLIF(LTRIM(RTRIM(p.room_no)), '') AS room_no,
        LTRIM(RTRIM(ISNULL(p.[Type],''))) AS category,
        LOWER(LTRIM(RTRIM(ISNULL(p.res_status,'')))) AS res_status,
        COALESCE(TRY_CONVERT(date,p.ArrivalDate,110),TRY_CONVERT(date,p.ArrivalDate,103),TRY_CONVERT(date,p.ArrivalDate)) AS arrival_date,
        COALESCE(TRY_CONVERT(date,p.DepartureDate,110),TRY_CONVERT(date,p.DepartureDate,103),TRY_CONVERT(date,p.DepartureDate)) AS departure_date,
        COALESCE(TRY_CONVERT(decimal(18,2),NULLIF(REPLACE(REPLACE(REPLACE(LTRIM(RTRIM(p.Rate)),',',''),'£',''),'$',''),'')),0) AS rate,
        COALESCE(NULLIF(TRY_CONVERT(int,p.Nights),0),NULLIF(DATEDIFF(day,
            COALESCE(TRY_CONVERT(date,p.ArrivalDate,110),TRY_CONVERT(date,p.ArrivalDate,103),TRY_CONVERT(date,p.ArrivalDate)),
            COALESCE(TRY_CONVERT(date,p.DepartureDate,110),TRY_CONVERT(date,p.DepartureDate,103),TRY_CONVERT(date,p.DepartureDate))),0)) AS nights
    FROM dbo.payments p
    WHERE p.hotel_id=@hotel
      AND LTRIM(RTRIM(ISNULL(p.descr,'')))='Room Rent'
      AND NULLIF(LTRIM(RTRIM(p.room_no)),'') IS NOT NULL
      AND UPPER(LTRIM(RTRIM(p.room_no))) <> 'UNASSIGNED'
),
daily AS
(
    SELECT
        i.category,
        dd.d,
        i.Rooms,
        CASE WHEN i.Rooms - ISNULL(o.blocked,0) < 0 THEN 0 ELSE i.Rooms - ISNULL(o.blocked,0) END AS sellable,
        COUNT(DISTINCT CASE WHEN p.arrival_date <= dd.d AND dd.d < p.departure_date
             AND p.res_status NOT IN ('cancel','cancelled','canceled','no show','noshow','no-show')
             THEN ISNULL(p.reg_id,'') + '|' + ISNULL(p.room_no,'') END) AS sold,
        SUM(CASE WHEN p.arrival_date <= dd.d AND dd.d < p.departure_date
             AND p.res_status NOT IN ('cancel','cancelled','canceled','no show','noshow','no-show')
             THEN p.rate / NULLIF(p.nights,0) ELSE 0 END) AS revenue
    FROM inv i
    CROSS JOIN d dd
    LEFT JOIN ooo o ON o.category=i.category AND o.d=dd.d
    LEFT JOIN p ON p.category=i.category
    GROUP BY i.category,dd.d,i.Rooms,o.blocked
),
agg AS
(
    SELECT
        category,
        MAX(Rooms) AS Rooms,
        SUM(sellable) AS SellableRoomNights,
        SUM(sold) AS SoldRoomNights,
        SUM(ISNULL(revenue,0)) AS Revenue,
        MAX(CASE WHEN d=@today THEN sellable ELSE 0 END) AS TonightSellable,
        MAX(CASE WHEN d=@today THEN sold ELSE 0 END) AS TonightSold
    FROM daily
    GROUP BY category
)
SELECT
    category, Rooms, SellableRoomNights, SoldRoomNights,
    CAST(ISNULL(Revenue,0) AS decimal(18,2)) AS Revenue,
    CAST(CASE WHEN TonightSellable<=0 THEN 0 ELSE 100.0*TonightSold/TonightSellable END AS decimal(9,2)) AS OccupancyTonight,
    CAST(CASE WHEN SellableRoomNights<=0 THEN 0 ELSE 100.0*SoldRoomNights/SellableRoomNights END AS decimal(9,2)) AS OccupancyNext30,
    CAST(CASE WHEN SoldRoomNights<=0 THEN 0 ELSE Revenue/SoldRoomNights END AS decimal(18,2)) AS ADR,
    CAST(CASE WHEN SellableRoomNights<=0 THEN 0 ELSE Revenue/SellableRoomNights END AS decimal(18,2)) AS RevPAR
FROM agg
ORDER BY category
OPTION (MAXRECURSION 100);";

        var result = new List<DashboardRoomTypeItem>();
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 60 };
        command.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
        command.Parameters.Add("@today", SqlDbType.Date).Value = today.Date;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var next30 = ToDecimal(reader["OccupancyNext30"]);
            result.Add(new DashboardRoomTypeItem
            {
                Category = Convert.ToString(reader["category"])?.Trim() ?? string.Empty,
                Rooms = ToInt(reader["Rooms"]),
                OccupancyTonight = ToDecimal(reader["OccupancyTonight"]),
                OccupancyNext30 = next30,
                Adr = ToDecimal(reader["ADR"]),
                RevPar = ToDecimal(reader["RevPAR"]),
                Revenue = ToDecimal(reader["Revenue"]),
                Demand = DemandLabel(next30),
                HasPaceVsLastYear = false
            });
        }
        return result;
    }

    private async Task<DashboardExceptionData> LoadExceptionsAsync(
        string hotelId,
        DateTime today,
        CancellationToken cancellationToken)
    {
        var start = today.AddDays(-29);
        const string sql = @"
SELECT 'cancel' AS ExceptionType, TRY_CONVERT(date, ArrivalDate) AS EventDate
FROM dbo.CancelledReservationsTB
WHERE hotel_id=@hotel
  AND TRY_CONVERT(date,ArrivalDate) BETWEEN @start AND @today
UNION ALL
SELECT 'noshow', TRY_CONVERT(date, ArrivalDate)
FROM dbo.NoShowTB
WHERE hotel_id=@hotel
  AND TRY_CONVERT(date,ArrivalDate) BETWEEN @start AND @today;

SELECT COUNT(DISTINCT reg_id) AS Reservations
FROM dbo.payments
WHERE hotel_id=@hotel
  AND LTRIM(RTRIM(ISNULL(descr,'')))='Room Rent'
  AND COALESCE(TRY_CONVERT(date,ArrivalDate,110),TRY_CONVERT(date,ArrivalDate,103),TRY_CONVERT(date,ArrivalDate)) BETWEEN @start AND @today;";

        var raw = new List<(string Type, DateTime Date)>();
        var reservations = 0;
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 30 };
        command.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
        command.Parameters.Add("@start", SqlDbType.Date).Value = start;
        command.Parameters.Add("@today", SqlDbType.Date).Value = today;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (reader["EventDate"] == DBNull.Value) continue;
            raw.Add((Convert.ToString(reader["ExceptionType"]) ?? string.Empty,
                Convert.ToDateTime(reader["EventDate"], CultureInfo.InvariantCulture).Date));
        }
        if (await reader.NextResultAsync(cancellationToken) && await reader.ReadAsync(cancellationToken))
            reservations = ToInt(reader["Reservations"]);

        var monthStart = new DateTime(today.Year, today.Month, 1);
        var cancels = raw.Where(x => x.Type == "cancel").ToList();
        var noshows = raw.Where(x => x.Type == "noshow").ToList();
        var series = new List<DashboardExceptionPoint>();
        for (var week = 0; week < 5; week++)
        {
            var weekStart = start.AddDays(week * 7);
            var weekEnd = weekStart.AddDays(6);
            series.Add(new DashboardExceptionPoint
            {
                Label = weekStart.ToString("dd MMM", CultureInfo.InvariantCulture),
                Cancellations = cancels.Count(x => x.Date >= weekStart && x.Date <= weekEnd),
                NoShows = noshows.Count(x => x.Date >= weekStart && x.Date <= weekEnd)
            });
        }

        var cancelCount = cancels.Count;
        var noShowCount = noshows.Count;
        var baseCount = Math.Max(1, reservations + cancelCount + noShowCount);
        return new DashboardExceptionData
        {
            CancellationsToday = cancels.Count(x => x.Date == today),
            CancellationsMonthToDate = cancels.Count(x => x.Date >= monthStart),
            CancellationsLast30 = cancelCount,
            NoShowsToday = noshows.Count(x => x.Date == today),
            NoShowsMonthToDate = noshows.Count(x => x.Date >= monthStart),
            NoShowsLast30 = noShowCount,
            CancellationRateLast30 = Percent(cancelCount, baseCount),
            Series = series,
            Sources = new List<DashboardExceptionSourceItem>
            {
                new() { Source = "Cancellations", Count = cancelCount, RatePercent = Percent(cancelCount, baseCount) },
                new() { Source = "No-shows", Count = noShowCount, RatePercent = Percent(noShowCount, baseCount) }
            }
        };
    }

    private async Task<List<DashboardCurrentRate>> LoadCurrentRatesAsync(
        string hotelId,
        DateTime today,
        CancellationToken cancellationToken)
    {
        const string sql = @"
WITH ranked AS
(
    SELECT
        LTRIM(RTRIM(ISNULL(cp.category,''))) AS CategoryName,
        LTRIM(RTRIM(ISNULL(cp.planname,''))) AS PlanName,
        CONVERT(varchar(50),cp.localplanid) AS PlanId,
        COALESCE(
            TRY_CONVERT(decimal(18,2), dr.rate),
            TRY_CONVERT(decimal(18,2), cp.rate),
            TRY_CONVERT(decimal(18,2), cp.baserate),
            0
        ) AS CurrentRate,
        ROW_NUMBER() OVER
        (
            PARTITION BY LTRIM(RTRIM(ISNULL(cp.category,'')))
            ORDER BY
                CASE WHEN NULLIF(LTRIM(RTRIM(ISNULL(cp.parent_planid,''))),'') IS NULL THEN 0 ELSE 1 END,
                ISNULL(p.orderid,999999),
                cp.planname
        ) AS rn
    FROM dbo.category_plan cp
    INNER JOIN dbo.plans p
        ON p.hotel_id=cp.hotel_id
       AND p.localplanid=cp.localplanid
    LEFT JOIN dbo.datesrates dr
        ON dr.hotel_id=cp.hotel_id
       AND dr.category_id=cp.category_id
       AND CONVERT(varchar(50),dr.planid)=CONVERT(varchar(50),cp.localplanid)
       AND dr.[date]=@today
    WHERE cp.hotel_id=@hotel
      AND NULLIF(LTRIM(RTRIM(cp.category)),'') IS NOT NULL
)
SELECT CategoryName,PlanName,PlanId,CurrentRate
FROM ranked
WHERE rn=1
ORDER BY CategoryName;";

        var result = new List<DashboardCurrentRate>();
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 30 };
        command.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
        command.Parameters.Add("@today", SqlDbType.Date).Value = today.Date;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new DashboardCurrentRate(
                Convert.ToString(reader["CategoryName"])?.Trim() ?? string.Empty,
                Convert.ToString(reader["PlanName"])?.Trim() ?? string.Empty,
                ToDecimal(reader["CurrentRate"])));
        }
        return result;
    }

    private static DashboardExceptionData BuildExceptionData(
        IReadOnlyList<(string Type, DateTime Date)> raw,
        int reservations,
        DateTime today)
    {
        var start = today.AddDays(-29);
        var monthStart = new DateTime(today.Year, today.Month, 1);
        var cancels = raw.Where(x => string.Equals(x.Type, "cancel", StringComparison.OrdinalIgnoreCase)).ToList();
        var noshows = raw.Where(x => string.Equals(x.Type, "noshow", StringComparison.OrdinalIgnoreCase)).ToList();
        var series = new List<DashboardExceptionPoint>();

        for (var week = 0; week < 5; week++)
        {
            var weekStart = start.AddDays(week * 7);
            var weekEnd = weekStart.AddDays(6);
            series.Add(new DashboardExceptionPoint
            {
                Label = weekStart.ToString("dd MMM", CultureInfo.InvariantCulture),
                Cancellations = cancels.Count(x => x.Date >= weekStart && x.Date <= weekEnd),
                NoShows = noshows.Count(x => x.Date >= weekStart && x.Date <= weekEnd)
            });
        }

        var cancelCount = cancels.Count;
        var noShowCount = noshows.Count;
        var baseCount = Math.Max(1, reservations + cancelCount + noShowCount);
        return new DashboardExceptionData
        {
            CancellationsToday = cancels.Count(x => x.Date == today.Date),
            CancellationsMonthToDate = cancels.Count(x => x.Date >= monthStart),
            CancellationsLast30 = cancelCount,
            NoShowsToday = noshows.Count(x => x.Date == today.Date),
            NoShowsMonthToDate = noshows.Count(x => x.Date >= monthStart),
            NoShowsLast30 = noShowCount,
            CancellationRateLast30 = Percent(cancelCount, baseCount),
            Series = series,
            Sources = new List<DashboardExceptionSourceItem>
            {
                new() { Source = "Cancellations", Count = cancelCount, RatePercent = Percent(cancelCount, baseCount) },
                new() { Source = "No-shows", Count = noShowCount, RatePercent = Percent(noShowCount, baseCount) }
            }
        };
    }

    private static void ApplyRoomTypeContribution(List<DashboardRoomTypeItem> rows)
    {
        var total = rows.Sum(x => x.Revenue);
        foreach (var row in rows)
            row.ContributionPercent = total <= 0m ? 0m : Math.Round(row.Revenue * 100m / total, 1);
    }

    private static List<DashboardPriceRecommendation> BuildPriceRecommendations(
        IReadOnlyList<DashboardCurrentRate> rates,
        IReadOnlyList<DashboardRoomTypeItem> roomTypes)
    {
        var types = roomTypes.ToDictionary(x => x.Category, StringComparer.OrdinalIgnoreCase);
        var result = new List<DashboardPriceRecommendation>();
        foreach (var rate in rates.Take(6))
        {
            types.TryGetValue(rate.Category, out var roomType);
            var occupancy = roomType?.OccupancyNext30 ?? 0m;
            decimal change = occupancy switch
            {
                >= 85m => 12m,
                >= 75m => 8m,
                >= 65m => 4m,
                < 35m => -10m,
                < 50m => -6m,
                _ => 0m
            };
            var recommended = rate.Rate <= 0m
                ? 0m
                : Math.Round(rate.Rate * (1m + change / 100m), 2, MidpointRounding.AwayFromZero);
            var demand = DemandLabel(occupancy);
            var reason = change > 0
                ? $"{demand} demand · {occupancy:0.#}% sold for the next 30 days"
                : change < 0
                    ? $"{demand} demand · {occupancy:0.#}% sold for the next 30 days"
                    : $"Stable demand · {occupancy:0.#}% sold for the next 30 days";

            result.Add(new DashboardPriceRecommendation
            {
                Category = rate.Category,
                PlanName = rate.PlanName,
                CurrentRate = rate.Rate,
                RecommendedRate = recommended,
                ChangePercent = change,
                OccupancyNext30 = occupancy,
                Demand = demand,
                Reason = reason
            });
        }
        return result;
    }

    private static List<DashboardDecisionItem> BuildDecisions(DashboardViewModel model)
    {
        var result = new List<DashboardDecisionItem>();
        var weak = model.RoomTypes.OrderBy(x => x.OccupancyNext30).FirstOrDefault();
        var strong = model.RoomTypes.OrderByDescending(x => x.OccupancyNext30).FirstOrDefault();
        var source = model.RevenueSources.OrderByDescending(x => x.SharePercent).FirstOrDefault();

        if (weak is not null && weak.OccupancyNext30 < 55m)
        {
            result.Add(new DashboardDecisionItem
            {
                Tone = "warning",
                Title = $"{weak.Category} needs demand support",
                Detail = $"Only {weak.OccupancyNext30:0.#}% is on the books for the next 30 days. Review price and direct-channel offers."
            });
        }
        if (strong is not null && strong.OccupancyNext30 >= 75m)
        {
            result.Add(new DashboardDecisionItem
            {
                Tone = "positive",
                Title = $"{strong.Category} is pacing strongly",
                Detail = $"{strong.OccupancyNext30:0.#}% is already sold for the next 30 days. Consider protecting the remaining inventory with a higher rate."
            });
        }
        if (model.ReceivablesOver30Days > 0m)
        {
            result.Add(new DashboardDecisionItem
            {
                Tone = "danger",
                Title = "Overdue receivables need attention",
                Detail = $"{model.CurrencySymbol}{model.ReceivablesOver30Days:N2} of the outstanding balance is older than 30 days."
            });
        }
        if (model.CancellationsLast30 > 0)
        {
            result.Add(new DashboardDecisionItem
            {
                Tone = "warning",
                Title = $"{model.CancellationsLast30} cancellations in the last 30 days",
                Detail = $"The cancellation rate is {model.CancellationRateLast30:0.#}%. Review cancellation terms on the highest-risk channels."
            });
        }
        if (source is not null)
        {
            result.Add(new DashboardDecisionItem
            {
                Tone = "info",
                Title = $"{source.Source} leads revenue on the books",
                Detail = $"It represents {source.SharePercent:0.#}% of the next 30 days' accommodation revenue at an ADR of {model.CurrencySymbol}{source.Adr:N2}."
            });
        }

        if (result.Count == 0)
        {
            result.Add(new DashboardDecisionItem
            {
                Tone = "positive",
                Title = "No urgent revenue actions",
                Detail = "Current occupancy, pricing and receivables do not show a high-priority exception."
            });
        }
        return result.Take(5).ToList();
    }

    private async Task<T> CachedAsync<T>(
        string key,
        TimeSpan lifetime,
        Func<Task<T>> factory)
    {
        if (_cache.TryGetValue(key, out object? cachedValue) && cachedValue is T cached)
            return cached;

        var loaded = await factory();
        _cache.Set(key, loaded, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = lifetime
        });
        return loaded;
    }

    private async Task<T> SafeLoadAsync<T>(Func<Task<T>> loader, T fallback, string operation)
    {
        try
        {
            return await loader();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Dashboard {Operation} query failed.", operation);
            return fallback;
        }
    }

    private static decimal WeightedOccupancy(IEnumerable<DashboardDailyPoint> rows, DateTime start, DateTime end)
    {
        var selected = rows.Where(x => x.Date.Date >= start.Date && x.Date.Date <= end.Date).ToList();
        var sellable = selected.Sum(x => x.SellableInventory);
        var sold = selected.Sum(x => x.RoomsSold);
        return Percent(sold, sellable);
    }

    private static decimal Percent(decimal numerator, decimal denominator)
        => denominator <= 0m ? 0m : Math.Round(numerator * 100m / denominator, 1);

    private static string DemandLabel(decimal occupancy)
        => occupancy >= 75m ? "High" : occupancy < 50m ? "Low" : "Normal";

    private static int ToInt(object value)
        => value == DBNull.Value ? 0 : Convert.ToInt32(value, CultureInfo.InvariantCulture);

    private static decimal ToDecimal(object value)
    {
        if (value == DBNull.Value || value is null) return 0m;
        if (value is decimal d) return d;
        return decimal.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0m;
    }

    private static DateTime? ToNullableDate(object value)
        => value == DBNull.Value || value is null ? null : Convert.ToDateTime(value, CultureInfo.InvariantCulture).Date;

    private static string Clean(string? value, int maxLength)
    {
        var text = (value ?? string.Empty).Trim();
        return text.Length <= maxLength ? text : text[..maxLength];
    }

    private sealed record DashboardHotelInfo(string Name, string CurrencySymbol);
    private sealed record DashboardCurrentRate(string Category, string PlanName, decimal Rate);

    private sealed class DashboardOperationsData
    {
        public int Arrivals { get; set; }
        public int Departures { get; set; }
        public int InHouseGuests { get; set; }
        public int OccupiedRooms { get; set; }
        public decimal AverageStay { get; set; }
        public decimal RepeatGuestPercent { get; set; }
        public decimal GuestsPerRoom { get; set; }
    }

    private sealed class DashboardHeadlineData
    {
        public DashboardFinancialSnapshot Financial { get; set; } = new();
        public DashboardReceivableData Receivables { get; set; } = new();
        public DashboardExceptionData Exceptions { get; set; } = new();
        public DashboardRoomSummaryData Rooms { get; set; } = new();
    }

    private sealed class DashboardRoomSummaryData
    {
        public int Total { get; set; }
        public int Available { get; set; }
        public int Occupied { get; set; }
        public int Blocked { get; set; }
        public int Dirty { get; set; }
    }

    private sealed class DashboardFinancialSnapshot
    {
        public decimal RevenueToday { get; set; }
        public decimal RevenueTodayLastYear { get; set; }
        public decimal ExpensesToday { get; set; }
        public decimal ExpensesTodayLastYear { get; set; }
        public decimal MonthRevenue { get; set; }
        public decimal MonthExpense { get; set; }
        public decimal LastYearMonthRevenue { get; set; }
        public decimal LastYearMonthExpense { get; set; }
        public decimal RevenueLast30 { get; set; }
        public decimal RevenueLast30LastYear { get; set; }
        public decimal ExpenseLast30 { get; set; }
        public decimal ExpenseLast30LastYear { get; set; }
    }

    private sealed class DashboardReceivableData
    {
        public decimal Today { get; set; }
        public decimal Last30Arrivals { get; set; }

        // Compatibility field for older internal dashboard code.
        public decimal Total { get; set; }

        public decimal Over30Days { get; set; }
    }

    private sealed class DashboardExceptionData
    {
        public int CancellationsToday { get; set; }
        public int CancellationsMonthToDate { get; set; }
        public int CancellationsLast30 { get; set; }
        public int NoShowsToday { get; set; }
        public int NoShowsMonthToDate { get; set; }
        public int NoShowsLast30 { get; set; }
        public decimal CancellationRateLast30 { get; set; }
        public List<DashboardExceptionPoint> Series { get; set; } = new();
        public List<DashboardExceptionSourceItem> Sources { get; set; } = new();
    }
}
