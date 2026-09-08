using System.Data;
using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Data.SqlClient;
using Orapmshms.Models;

namespace Orapmshms.Services;

public sealed class YourHotelsService : IYourHotelsService
{
    private const string OpenHotelDashboardAction = "OpenHotelDashboard";
    private const double DonutCircumference = 339.3;

    private readonly string _connectionString;
    private readonly IHotelClock _hotelClock;
    private readonly ILegacyUrlSigner _urlSigner;
    private readonly IConfiguration _configuration;

    public YourHotelsService(
        IConfiguration configuration,
        IHotelClock hotelClock,
        ILegacyUrlSigner urlSigner)
    {
        _configuration = configuration;
        _connectionString = configuration.GetConnectionString("con")
            ?? throw new InvalidOperationException("ConnectionStrings:con is missing from configuration.");
        _hotelClock = hotelClock;
        _urlSigner = urlSigner;
    }

    public async Task<YourHotelsPageViewModel> BuildPageAsync(
        string userId,
        int? requestedYear,
        CancellationToken cancellationToken = default)
    {
        userId = Clean(userId, 100);
        if (string.IsNullOrWhiteSpace(userId))
            throw new UnauthorizedAccessException("User context is missing.");

        var currentYear = _hotelClock.GetHotelToday().Year;
        var selectedYear = requestedYear is >= 1900 and <= 2050 ? requestedYear.Value : currentYear;

        var model = new YourHotelsPageViewModel
        {
            SelectedYear = selectedYear,
            Years = BuildYearList(currentYear),
            OccupancyLabels = new[] { "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec" },
            BookingSourceLabels = new[]
            {
                "January", "February", "March", "April", "May", "June",
                "July", "August", "September", "October", "November", "December"
            }
        };

        var assignedHotelIds = await GetAssignedHotelIdsForUserAsync(userId, cancellationToken);

        // Web Forms checks OpenHotelDashboard per assigned hotel, not once globally.
        // Keep the same permission result on every card so the UI and server-side click
        // validation remain consistent.
        model.Hotels = await LoadHotelCardsAsync(userId, false, cancellationToken);
        foreach (var hotel in model.Hotels)
        {
            hotel.CanOpenDashboard = await HasYourHotelsActionAsync(
                userId,
                hotel.HotelId,
                OpenHotelDashboardAction,
                cancellationToken);
        }

        var canOpenAnyDashboard = model.Hotels.Any(h => h.CanOpenDashboard);
        model.OccupancyDatasets = await LoadOccupancyDatasetsAsync(userId, selectedYear, cancellationToken);

        var bookingChart = await LoadBookingSourceChartAsync(
            userId,
            selectedYear,
            assignedHotelIds,
            canOpenAnyDashboard,
            cancellationToken);

        model.BookingSourceDatasets = bookingChart.Datasets;
        model.BookingSourceFilter = bookingChart.Sources;

        return model;
    }

    public async Task<OpenHotelResult> OpenHotelAsync(
        string userId,
        string hotelId,
        CancellationToken cancellationToken = default)
    {
        userId = Clean(userId, 100);
        hotelId = Clean(hotelId, 100);

        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(hotelId))
            return Fail("You do not have permission for this hotel.");

        if (!await IsHotelAssignedToUserAsync(userId, hotelId, cancellationToken))
            return Fail("You do not have permission for this hotel.");

        if (!await HasYourHotelsActionAsync(userId, hotelId, OpenHotelDashboardAction, cancellationToken))
            return Fail("You do not have permission to open this hotel dashboard.");

        var account = await GetOpenHotelAccountAsync(userId, hotelId, cancellationToken);
        if (account is null)
            return Fail("User does not exist.");

        if (!string.Equals(account.ActiveStatus, "ACTIVE", StringComparison.OrdinalIgnoreCase))
            return Fail("This account is expired.");

        var query = new Dictionary<string, string?>
        {
            ["UD"] = ToBase64(account.UserId),
            ["UN"] = ToBase64(account.UserName),
            ["hd"] = ToBase64(account.HotelId),
            ["rl"] = ToBase64(account.Role),
            ["hn"] = ToBase64(account.Email),
            ["hr"] = ToBase64(account.HotelRole)
        };

        // Dashboard is now converted to ASP.NET Core MVC.
        // The selected hotel context is written to session by YourHotelsController,
        // so no sensitive hotel/user context needs to remain in the browser URL.
        var redirectUrl = "/Dashboard";

        return new OpenHotelResult(true, string.Empty, redirectUrl, account);
    }

    private async Task<List<HotelCardViewModel>> LoadHotelCardsAsync(
        string userId,
        bool canOpenDashboard,
        CancellationToken cancellationToken)
    {
        var result = new List<HotelCardViewModel>();
        var hotelToday = _hotelClock.GetHotelToday();
        var hotelTomorrow = hotelToday.AddDays(1);

        const string sql = @"
DECLARE @arr date = @Today;
DECLARE @dep date = @Tomorrow;

WITH UserHotels AS (
    SELECT uha.userid, h.hotel_id, h.name, h.logo, h.city, h.address
    FROM dbo.UserHotelAccess uha
    INNER JOIN dbo.HotelsSignUpTB h ON h.hotel_id = uha.HotelId
    WHERE uha.userid = @UserId
),
TotalRooms AS (
    SELECT rt.Hotel_id AS hotel_id, COUNT(*) AS total_rooms
    FROM dbo.RoomsTB rt
    GROUP BY rt.Hotel_id
),
TodayReservations AS (
    SELECT p.hotel_id, COUNT(*) AS today_reservations
    FROM dbo.Payments p
    WHERE p.res_status = 'reservation'
      AND p.descr = 'Room Rent'
      AND ISNULL(p.room_no, '') <> 'UNASSIGNED'
      AND p.ArrivalDate = @Today
      AND
      (
          EXISTS
          (
              SELECT 1
              FROM NewReservationsTB nr
              WHERE nr.reg_id = p.reg_id
          )
          OR EXISTS
          (
              SELECT 1
              FROM GuestInformationLogTB gil
              WHERE gil.reg_id = p.reg_id
          )
      )
    GROUP BY p.hotel_id
),
TodayCheckIns AS (
    SELECT g.hotel_id, COUNT(*) AS today_checkins
    FROM dbo.Payments g
    WHERE g.res_status = 'check in'
      AND TRY_CONVERT(date, g.ArrivalDate, 110) = @Today
    GROUP BY g.hotel_id
),
FreeRoomsToday AS (
    SELECT uh.hotel_id, COUNT(*) AS free_rooms_today
    FROM UserHotels uh
    INNER JOIN dbo.RoomsTB rt ON rt.Hotel_id = uh.hotel_id
    LEFT JOIN dbo.RoomBlocksTB rb
        ON rb.HotelID = rt.Hotel_id
       AND rb.RoomNo = rt.room_no
       AND rb.IsActive = 1
       AND (
            CAST(rb.BlockStartDate AS DATE) < @dep
            AND @arr < DATEADD(DAY, 1, CAST(ISNULL(rb.BlockEndDate,'9999-12-31') AS DATE))
       )
    WHERE rb.BlockID IS NULL

      AND NOT EXISTS (
        SELECT 1
        FROM dbo.payments p
        INNER JOIN dbo.GuestInformationLogTB gi
            ON gi.reg_id = p.reg_id AND gi.hotel_id = p.hotel_id
        WHERE p.hotel_id = rt.Hotel_id
          AND p.room_no = rt.room_no
          AND p.res_status IN ('check in','reservation')
          AND TRY_CONVERT(date, p.ArrivalDate, 110) < @dep
          AND @arr < TRY_CONVERT(date, p.DepartureDate, 110)
      )

      AND NOT EXISTS (
        SELECT 1
        FROM dbo.payments p
        INNER JOIN dbo.NewReservationsTB nr
            ON nr.reg_id = p.reg_id AND nr.hotel_id = p.hotel_id
        WHERE p.hotel_id = rt.Hotel_id
          AND p.room_no = rt.room_no
          AND p.res_status IN ('check in','reservation')
          AND TRY_CONVERT(date, p.ArrivalDate, 110) < @dep
          AND @arr < TRY_CONVERT(date, p.DepartureDate, 110)
      )

    GROUP BY uh.hotel_id
)
SELECT
    uh.hotel_id AS HotelId,
    uh.name AS DisplayName,
    ISNULL(NULLIF(LTRIM(RTRIM(uh.logo)), ''), 'https://via.placeholder.com/80?text=Logo') AS ImageUrl,
    CASE
        WHEN ISNULL(NULLIF(LTRIM(RTRIM(uh.city)), ''), '') <> '' THEN uh.city
        WHEN ISNULL(NULLIF(LTRIM(RTRIM(uh.address)), ''), '') <> '' THEN uh.address
        ELSE 'Open dashboard'
    END AS CityInfo,
    ISNULL(tr.today_reservations, 0) AS TodayReservations,
    ISNULL(tc.today_checkins, 0) AS TodayCheckIns,
    ISNULL(tr.today_reservations, 0) + ISNULL(tc.today_checkins, 0) AS ExpectedCheckIns,
    ISNULL(tot.total_rooms, 0) AS TotalRooms,
    ISNULL(fr.free_rooms_today, 0) AS FreeRoomsToday
FROM UserHotels uh
LEFT JOIN TotalRooms tot ON tot.hotel_id = uh.hotel_id
LEFT JOIN TodayReservations tr ON tr.hotel_id = uh.hotel_id
LEFT JOIN TodayCheckIns tc ON tc.hotel_id = uh.hotel_id
LEFT JOIN FreeRoomsToday fr ON fr.hotel_id = uh.hotel_id
ORDER BY uh.name;";

        await using var connection = new SqlConnection(_connectionString);
        await using var cmd = new SqlCommand(sql, connection);
        cmd.Parameters.Add("@UserId", SqlDbType.VarChar, 100).Value = userId;
        cmd.Parameters.Add("@Today", SqlDbType.Date).Value = hotelToday;
        cmd.Parameters.Add("@Tomorrow", SqlDbType.Date).Value = hotelTomorrow;

        await connection.OpenAsync(cancellationToken);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var todayCheckIns = ReadInt(reader, "TodayCheckIns");
            var expectedCheckIns = ReadInt(reader, "ExpectedCheckIns");
            var freeRooms = ReadInt(reader, "FreeRoomsToday");
            var totalRooms = ReadInt(reader, "TotalRooms");

            var checkPct = expectedCheckIns > 0
                ? (int)Math.Round(todayCheckIns * 100.0 / expectedCheckIns, 0, MidpointRounding.AwayFromZero)
                : 0;
            var freePct = totalRooms > 0
                ? (int)Math.Round(freeRooms * 100.0 / totalRooms, 0, MidpointRounding.AwayFromZero)
                : 0;

            checkPct = Math.Clamp(checkPct, 0, 100);
            freePct = Math.Clamp(freePct, 0, 100);

            result.Add(new HotelCardViewModel
            {
                HotelId = ReadString(reader, "HotelId"),
                DisplayName = ReadString(reader, "DisplayName"),
                ImageUrl = ReadString(reader, "ImageUrl"),
                CityInfo = ReadString(reader, "CityInfo"),
                TodayReservations = ReadInt(reader, "TodayReservations"),
                TodayCheckIns = todayCheckIns,
                ExpectedCheckIns = expectedCheckIns,
                TotalRooms = totalRooms,
                FreeRoomsToday = freeRooms,
                CheckInDashOffset = (DonutCircumference * (1.0 - checkPct / 100.0)).ToString("0.0", CultureInfo.InvariantCulture),
                FreeDashOffset = (DonutCircumference * (1.0 - freePct / 100.0)).ToString("0.0", CultureInfo.InvariantCulture),
                CanOpenDashboard = canOpenDashboard
            });
        }

        return result;
    }

    private async Task<List<OccupancyChartDataset>> LoadOccupancyDatasetsAsync(
        string userId,
        int year,
        CancellationToken cancellationToken)
    {
        const string sql = @"
DECLARE @Year int = @Y;

WITH UserHotels AS (
    SELECT h.hotel_id, h.name AS hotel_name
    FROM dbo.UserHotelAccess uha
    INNER JOIN dbo.HotelsSignUpTB h ON h.hotel_id = uha.HotelId
    WHERE uha.userid = @UserId
),
Months AS (
    SELECT
        1 AS m,
        DATEFROMPARTS(@Year, 1, 1) AS MonthStart,
        DATEADD(month, 1, DATEFROMPARTS(@Year, 1, 1)) AS MonthEnd
    UNION ALL
    SELECT
        m + 1,
        DATEADD(month, 1, MonthStart),
        DATEADD(month, 2, MonthStart)
    FROM Months
    WHERE m < 12
),
Inv AS (
    SELECT rt.Hotel_id AS hotel_id, COUNT(*) AS inventory
    FROM dbo.RoomsTB rt
    INNER JOIN UserHotels uh ON uh.hotel_id = rt.Hotel_id
    GROUP BY rt.Hotel_id
),
RawStays AS (
    SELECT DISTINCT
        p.hotel_id,
        LTRIM(RTRIM(p.room_no)) AS room_no,
        COALESCE(
            TRY_CONVERT(date, p.ArrivalDate, 110),
            TRY_CONVERT(date, p.ArrivalDate, 101),
            TRY_CONVERT(date, p.ArrivalDate, 103)
        ) AS arr,
        COALESCE(
            TRY_CONVERT(date, p.DepartureDate, 110),
            TRY_CONVERT(date, p.DepartureDate, 101),
            TRY_CONVERT(date, p.DepartureDate, 103)
        ) AS dep
    FROM dbo.payments p
    INNER JOIN dbo.GuestInformationLogTB gi
        ON gi.reg_id = p.reg_id
       AND gi.hotel_id = p.hotel_id
    INNER JOIN UserHotels uh ON uh.hotel_id = p.hotel_id
    WHERE p.descr = 'Room Rent'
      AND gi.res_status IN ('check in','reservation','check out')
      AND p.ArrivalDate IS NOT NULL
      AND p.DepartureDate IS NOT NULL
      AND LTRIM(RTRIM(ISNULL(p.room_no, ''))) <> ''

    UNION

    SELECT DISTINCT
        p.hotel_id,
        LTRIM(RTRIM(p.room_no)) AS room_no,
        COALESCE(
            TRY_CONVERT(date, p.ArrivalDate, 110),
            TRY_CONVERT(date, p.ArrivalDate, 101),
            TRY_CONVERT(date, p.ArrivalDate, 103)
        ) AS arr,
        COALESCE(
            TRY_CONVERT(date, p.DepartureDate, 110),
            TRY_CONVERT(date, p.DepartureDate, 101),
            TRY_CONVERT(date, p.DepartureDate, 103)
        ) AS dep
    FROM dbo.payments p
    INNER JOIN dbo.NewReservationsTB nr
        ON nr.reg_id = p.reg_id
       AND nr.hotel_id = p.hotel_id
    INNER JOIN UserHotels uh ON uh.hotel_id = p.hotel_id
    WHERE p.descr = 'Room Rent'
      AND nr.res_status IN ('check in','reservation','check out')
      AND p.ArrivalDate IS NOT NULL
      AND p.DepartureDate IS NOT NULL
      AND LTRIM(RTRIM(ISNULL(p.room_no, ''))) <> ''
),
ValidStays AS (
    SELECT hotel_id, room_no, arr, dep
    FROM RawStays
    WHERE arr IS NOT NULL
      AND dep IS NOT NULL
      AND dep > arr
      AND arr < DATEFROMPARTS(@Year + 1, 1, 1)
      AND dep > DATEFROMPARTS(@Year, 1, 1)
),
OccMonthly AS (
    SELECT
        s.hotel_id,
        m.m,
        SUM(
            CASE
                WHEN s.arr < m.MonthEnd AND s.dep > m.MonthStart THEN
                    DATEDIFF(
                        day,
                        CASE WHEN s.arr > m.MonthStart THEN s.arr ELSE m.MonthStart END,
                        CASE WHEN s.dep < m.MonthEnd THEN s.dep ELSE m.MonthEnd END
                    )
                ELSE 0
            END
        ) AS occ_room_nights
    FROM ValidStays s
    INNER JOIN Months m
        ON s.arr < m.MonthEnd
       AND s.dep > m.MonthStart
    GROUP BY s.hotel_id, m.m
),
DaysInMonth AS (
    SELECT
        m,
        DAY(EOMONTH(DATEFROMPARTS(@Year, m, 1))) AS days_in_month
    FROM Months
)
SELECT
    uh.hotel_id,
    uh.hotel_name,
    m.m AS MonthNo,
    CAST(
        CASE
            WHEN ISNULL(inv.inventory, 0) = 0 THEN 0
            ELSE (ISNULL(om.occ_room_nights, 0) * 100.0)
                 / (inv.inventory * dim.days_in_month)
        END
        AS decimal(10,2)
    ) AS OccPct
FROM UserHotels uh
CROSS JOIN Months m
LEFT JOIN Inv inv ON inv.hotel_id = uh.hotel_id
LEFT JOIN DaysInMonth dim ON dim.m = m.m
LEFT JOIN OccMonthly om ON om.hotel_id = uh.hotel_id AND om.m = m.m
ORDER BY uh.hotel_name, m.m
OPTION (MAXRECURSION 20);";

        var hotelMap = new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase);
        var nameMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();

        await using var connection = new SqlConnection(_connectionString);
        await using var cmd = new SqlCommand(sql, connection) { CommandTimeout = 120 };
        cmd.Parameters.Add("@UserId", SqlDbType.VarChar, 100).Value = userId;
        cmd.Parameters.Add("@Y", SqlDbType.Int).Value = year;

        await connection.OpenAsync(cancellationToken);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var hotelId = ReadString(reader, "hotel_id");
            if (string.IsNullOrWhiteSpace(hotelId))
                continue;

            if (!hotelMap.TryGetValue(hotelId, out var values))
            {
                values = new double[12];
                hotelMap[hotelId] = values;
                nameMap[hotelId] = ReadString(reader, "hotel_name");
                order.Add(hotelId);
            }

            var monthNo = ReadInt(reader, "MonthNo");
            if (monthNo is >= 1 and <= 12)
                values[monthNo - 1] = ReadDouble(reader, "OccPct");
        }

        var result = new List<OccupancyChartDataset>();
        for (var index = 0; index < order.Count; index++)
        {
            var hotelId = order[index];
            var color = GetPaletteColor(index);
            result.Add(new OccupancyChartDataset
            {
                Label = string.IsNullOrWhiteSpace(nameMap[hotelId]) ? hotelId : nameMap[hotelId],
                Data = hotelMap[hotelId],
                BorderColor = color,
                BackgroundColor = color
            });
        }

        return result;
    }

    private async Task<BookingSourceChartResult> LoadBookingSourceChartAsync(
        string userId,
        int year,
        IReadOnlyList<string> assignedHotelIds,
        bool canSeeAllSources,
        CancellationToken cancellationToken)
    {
        var result = new BookingSourceChartResult();
        if (assignedHotelIds.Count == 0)
        {
            if (!canSeeAllSources)
            {
                result.Sources.Add("BNBUK");
                result.Sources.Add("GoogleHotelARI");
            }
            return result;
        }

        var sourcesCteSql = canSeeAllSources
            ? @"
Sources AS (
    SELECT DISTINCT BookingSource
    FROM BookingSources
    WHERE LTRIM(RTRIM(ISNULL(BookingSource, ''))) <> ''
)"
            : @"
Sources AS (
    SELECT CAST('BNBUK' AS varchar(150)) AS BookingSource
    UNION ALL
    SELECT CAST('GoogleHotelARI' AS varchar(150)) AS BookingSource
)";

        var sql = @"
DECLARE @StartDate date = DATEFROMPARTS(@Y, 1, 1);
DECLARE @EndDate   date = DATEFROMPARTS(@Y + 1, 1, 1);

WITH UserHotels AS (
    SELECT h.hotel_id, h.name AS hotel_name
    FROM dbo.UserHotelAccess uha
    INNER JOIN dbo.HotelsSignUpTB h ON h.hotel_id = uha.HotelId
    WHERE uha.userid = @UserId
),
Months AS (
    SELECT 1 AS MonthNo
    UNION ALL SELECT MonthNo + 1 FROM Months WHERE MonthNo < 12
),
RoomRent AS (
    SELECT DISTINCT
        p.hotel_id,
        LTRIM(RTRIM(p.reg_id)) AS BookingKey,
        COALESCE(
            TRY_CONVERT(date, p.ArrivalDate, 101),
            TRY_CONVERT(date, p.ArrivalDate, 110),
            TRY_CONVERT(date, p.ArrivalDate, 103)
        ) AS ArrDate
    FROM dbo.payments p
    INNER JOIN UserHotels uh ON uh.hotel_id = p.hotel_id
    WHERE p.descr = 'Room Rent'
      AND p.ArrivalDate IS NOT NULL
      AND LTRIM(RTRIM(ISNULL(p.reg_id, ''))) <> ''
),
FilteredRoomRent AS (
    SELECT hotel_id, BookingKey, ArrDate
    FROM RoomRent
    WHERE ArrDate IS NOT NULL
      AND ArrDate >= @StartDate
      AND ArrDate < @EndDate
),
RawBookingSources AS (
    SELECT
        rr.hotel_id,
        rr.BookingKey,
        MONTH(rr.ArrDate) AS MonthNo,
        ISNULL(NULLIF(LTRIM(RTRIM(gi.Agency)), ''), 'Walk In') AS RawSource
    FROM FilteredRoomRent rr
    INNER JOIN dbo.GuestInformationLogTB gi
        ON gi.reg_id = rr.BookingKey
       AND gi.hotel_id = rr.hotel_id

    UNION

    SELECT
        rr.hotel_id,
        rr.BookingKey,
        MONTH(rr.ArrDate) AS MonthNo,
        ISNULL(NULLIF(LTRIM(RTRIM(nr.Agency)), ''), 'Walk In') AS RawSource
    FROM FilteredRoomRent rr
    INNER JOIN dbo.NewReservationsTB nr
        ON nr.reg_id = rr.BookingKey
       AND nr.hotel_id = rr.hotel_id
),
NormalizedSources AS (
    SELECT
        hotel_id,
        BookingKey,
        MonthNo,
        ISNULL(NULLIF(LTRIM(RTRIM(RawSource)), ''), 'Walk In') AS CleanSource,
        LOWER(
            REPLACE(
            REPLACE(
            REPLACE(
            REPLACE(
            REPLACE(ISNULL(NULLIF(LTRIM(RTRIM(RawSource)), ''), 'Walk In'), '.', ''),
            ' ', ''),
            '-', ''),
            '_', ''),
            '/', '')
        ) AS SourceKey
    FROM RawBookingSources
),
BookingSources AS (
    SELECT
        hotel_id,
        BookingKey,
        MonthNo,
        CASE
            WHEN SourceKey IN (
                'bnbuk',
                'bnbukcom',
                'bnbukcouk',
                'wwwbnbukcouk',
                'bnbukwebsite',
                'bnbukweb',
                'bnbukdirect',
                'bnbukbooking',
                'bookingbnbuk',
                'bnbukota',
                'bnbook',
                'bnbookuk'
            ) THEN 'BNBUK'

            WHEN SourceKey IN (
                'googlehotelari',
                'googlehotelapi',
                'googlehotelads',
                'googlehotel',
                'googlehotels',
                'googleari',
                'googleapi',
                'googletravel',
                'google'
            ) THEN 'GoogleHotelARI'

            ELSE CleanSource
        END AS BookingSource
    FROM NormalizedSources
),
" + sourcesCteSql + @",
FinalCounts AS (
    SELECT
        bs.hotel_id,
        bs.MonthNo,
        bs.BookingSource,
        COUNT(DISTINCT bs.BookingKey) AS BookingCount
    FROM BookingSources bs
    WHERE LTRIM(RTRIM(ISNULL(bs.BookingSource, ''))) <> ''
    GROUP BY bs.hotel_id, bs.MonthNo, bs.BookingSource
)
SELECT
    uh.hotel_id,
    uh.hotel_name,
    m.MonthNo,
    s.BookingSource,
    ISNULL(fc.BookingCount, 0) AS BookingCount
FROM UserHotels uh
CROSS JOIN Months m
CROSS JOIN Sources s
LEFT JOIN FinalCounts fc
       ON fc.hotel_id = uh.hotel_id
      AND fc.MonthNo = m.MonthNo
      AND fc.BookingSource = s.BookingSource
ORDER BY
    CASE
        WHEN s.BookingSource = 'BNBUK' THEN 0
        WHEN s.BookingSource = 'GoogleHotelARI' THEN 1
        WHEN s.BookingSource IN ('BookingCom','Booking.com','Booking Com') THEN 2
        WHEN s.BookingSource = 'Walk In' THEN 98
        ELSE 50
    END,
    s.BookingSource,
    uh.hotel_name,
    m.MonthNo
OPTION (MAXRECURSION 20);";

        var sourceSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sourceOrder = new List<string>();
        var hotelSourceMap = new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase);
        var hotelNameMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var hotelColorIndexMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        await using var connection = new SqlConnection(_connectionString);
        await using var cmd = new SqlCommand(sql, connection) { CommandTimeout = 120 };
        cmd.Parameters.Add("@UserId", SqlDbType.VarChar, 100).Value = userId;
        cmd.Parameters.Add("@Y", SqlDbType.Int).Value = year;

        await connection.OpenAsync(cancellationToken);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var hotelId = ReadString(reader, "hotel_id");
            var hotelName = ReadString(reader, "hotel_name");
            var source = ReadString(reader, "BookingSource");
            var monthNo = ReadInt(reader, "MonthNo");
            var count = ReadDouble(reader, "BookingCount");

            if (string.IsNullOrWhiteSpace(hotelId) || string.IsNullOrWhiteSpace(source))
                continue;
            if (string.IsNullOrWhiteSpace(hotelName))
                hotelName = hotelId;

            if (!hotelNameMap.ContainsKey(hotelId))
            {
                hotelNameMap[hotelId] = hotelName;
                hotelColorIndexMap[hotelId] = hotelColorIndexMap.Count;
            }

            if (sourceSeen.Add(source))
                sourceOrder.Add(source);

            var key = hotelId + "||" + source;
            if (!hotelSourceMap.TryGetValue(key, out var values))
            {
                values = new double[12];
                hotelSourceMap[key] = values;
            }

            if (monthNo is >= 1 and <= 12)
                values[monthNo - 1] = count;
        }

        if (!canSeeAllSources)
        {
            if (sourceSeen.Add("BNBUK"))
                sourceOrder.Add("BNBUK");
            if (sourceSeen.Add("GoogleHotelARI"))
                sourceOrder.Add("GoogleHotelARI");
        }

        sourceOrder.Sort((a, b) =>
        {
            var rankCompare = GetSourceSortRank(a).CompareTo(GetSourceSortRank(b));
            return rankCompare != 0 ? rankCompare : string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
        });
        result.Sources.AddRange(sourceOrder);

        // Preserve the source page's dataset order: SQL output is source -> hotel -> month,
        // and Dictionary insertion order is retained by modern .NET.
        foreach (var entry in hotelSourceMap)
        {
            var parts = entry.Key.Split(new[] { "||" }, StringSplitOptions.None);
            var hotelId = parts.Length > 0 ? parts[0] : string.Empty;
            var source = parts.Length > 1 ? parts[1] : string.Empty;
            var hotelName = hotelNameMap.TryGetValue(hotelId, out var mappedName) ? mappedName : hotelId;
            var colorIndex = hotelColorIndexMap.TryGetValue(hotelId, out var mappedIndex) ? mappedIndex : 0;
            var color = GetPaletteColor(colorIndex);

            result.Datasets.Add(new BookingSourceChartDataset
            {
                Label = hotelName,
                HotelName = hotelName,
                SourceName = source,
                Data = entry.Value,
                BackgroundColor = HexToRgba(color, 0.82),
                BorderColor = color
            });
        }

        return result;
    }

    private async Task<List<string>> GetAssignedHotelIdsForUserAsync(
        string userId,
        CancellationToken cancellationToken)
    {
        var result = new List<string>();

        const string sql = @"
SELECT DISTINCT CAST(HotelId AS varchar(100)) AS HotelId
FROM dbo.UserHotelAccess
WHERE userid = @UserId
  AND LTRIM(RTRIM(ISNULL(CAST(HotelId AS varchar(100)), ''))) <> ''
ORDER BY CAST(HotelId AS varchar(100));";

        await using var connection = new SqlConnection(_connectionString);
        await using var cmd = new SqlCommand(sql, connection);
        cmd.Parameters.Add("@UserId", SqlDbType.VarChar, 100).Value = userId;

        await connection.OpenAsync(cancellationToken);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var hotelId = ReadString(reader, "HotelId");
            if (!string.IsNullOrWhiteSpace(hotelId))
                result.Add(hotelId);
        }

        return result;
    }

    private async Task<bool> IsHotelAssignedToUserAsync(
        string userId,
        string hotelId,
        CancellationToken cancellationToken)
    {
        const string sql = @"
SELECT COUNT(1)
FROM dbo.UserHotelAccess
WHERE userid = @UserId
  AND HotelId = @HotelId;";

        await using var connection = new SqlConnection(_connectionString);
        await using var cmd = new SqlCommand(sql, connection);
        cmd.Parameters.Add("@UserId", SqlDbType.VarChar, 100).Value = userId;
        cmd.Parameters.Add("@HotelId", SqlDbType.VarChar, 100).Value = hotelId;
        await connection.OpenAsync(cancellationToken);
        var value = await cmd.ExecuteScalarAsync(cancellationToken);
        return value is not null && value != DBNull.Value && Convert.ToInt32(value, CultureInfo.InvariantCulture) > 0;
    }

    private async Task<bool> HasYourHotelsActionAsync(
        string userId,
        string hotelId,
        string actionId,
        CancellationToken cancellationToken)
    {
        userId = Clean(userId, 100);
        hotelId = Clean(hotelId, 100);
        actionId = Clean(actionId, 200);

        if (string.IsNullOrWhiteSpace(userId) ||
            string.IsNullOrWhiteSpace(hotelId) ||
            string.IsNullOrWhiteSpace(actionId))
        {
            return false;
        }

        const string menuSql = @"
SELECT TOP 1 menu_id
FROM dbo.AddMenuTB
WHERE ISNULL(page_name,'') = @page_name
  AND ISNULL([show],1) = 1
ORDER BY menu_id;";

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var menuCommand = new SqlCommand(menuSql, connection);
        menuCommand.Parameters.Add("@page_name", SqlDbType.NVarChar, 200).Value = "YourHotels";
        var menuObject = await menuCommand.ExecuteScalarAsync(cancellationToken);

        // Same PermissionHelper behavior: when the page is not registered, do not
        // break the existing flow. UserHotelAccess still protects the hotel itself.
        if (menuObject is null || menuObject == DBNull.Value ||
            Convert.ToInt32(menuObject, CultureInfo.InvariantCulture) <= 0)
        {
            return true;
        }

        var menuId = Convert.ToInt32(menuObject, CultureInfo.InvariantCulture);

        // Port of the supplied PermissionHelper hotel/user precedence.
        const string actionSql = @"
SELECT TOP (1)
    ISNULL(resolved.is_allowed, 0)
FROM dbo.PageActionsTB pa
OUTER APPLY
(
    SELECT TOP (1)
        uap.is_allowed
    FROM dbo.UserActionPermissionsTB uap
    WHERE uap.action_id = pa.action_id
      AND uap.menuid = pa.menuid
      AND ISNULL(uap.is_active, 0) = 1
      AND
      (
          (
              CONVERT(varchar(50), uap.hotel_id) = @hotel_id
              AND LOWER(LTRIM(RTRIM(ISNULL(uap.permission_for, '')))) = 'hotel'
          )
          OR
          (
              CONVERT(varchar(50), uap.user_id) = @user_id
              AND CONVERT(varchar(50), uap.hotel_id) IN (@hotel_id, '-1')
          )
      )
    ORDER BY
        CASE
            WHEN CONVERT(varchar(50), uap.hotel_id) = @hotel_id
             AND CONVERT(varchar(50), uap.user_id) = @user_id
             AND LOWER(LTRIM(RTRIM(ISNULL(uap.permission_for, '')))) <> 'hotel'
                THEN 0
            WHEN CONVERT(varchar(50), uap.hotel_id) = @hotel_id
             AND LOWER(LTRIM(RTRIM(ISNULL(uap.permission_for, '')))) = 'hotel'
                THEN 1
            WHEN CONVERT(varchar(50), uap.hotel_id) = @hotel_id
             AND CONVERT(varchar(50), uap.user_id) = @user_id
                THEN 2
            WHEN CONVERT(varchar(50), uap.hotel_id) = '-1'
             AND CONVERT(varchar(50), uap.user_id) = @user_id
                THEN 3
            ELSE 4
        END,
        ISNULL(uap.updated_date, uap.created_date) DESC,
        uap.permission_id DESC
) resolved
WHERE pa.menuid = @menuid
  AND pa.action_name = @action_name
  AND ISNULL(pa.is_active, 0) = 1;";

        await using var actionCommand = new SqlCommand(actionSql, connection);
        actionCommand.Parameters.Add("@hotel_id", SqlDbType.VarChar, 100).Value = hotelId;
        actionCommand.Parameters.Add("@user_id", SqlDbType.VarChar, 100).Value = userId;
        actionCommand.Parameters.Add("@menuid", SqlDbType.Int).Value = menuId;
        actionCommand.Parameters.Add("@action_name", SqlDbType.NVarChar, 200).Value = actionId;

        var actionObject = await actionCommand.ExecuteScalarAsync(cancellationToken);

        // PermissionHelper.HasAction allows an action that is not registered for the page.
        if (actionObject is null || actionObject == DBNull.Value)
            return true;

        return Convert.ToBoolean(actionObject, CultureInfo.InvariantCulture);
    }

    private async Task<OpenHotelAccount?> GetOpenHotelAccountAsync(
        string userId,
        string hotelId,
        CancellationToken cancellationToken)
    {
        const string sql = @"
SELECT TOP 1 email, user_id, username, role, hotel_role, activestatus
FROM dbo.Hms_accounts
WHERE user_id = @user_id;";

        await using var connection = new SqlConnection(_connectionString);
        await using var cmd = new SqlCommand(sql, connection);
        cmd.Parameters.Add("@user_id", SqlDbType.VarChar, 100).Value = userId;
        await connection.OpenAsync(cancellationToken);
        await using var reader = await cmd.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        return new OpenHotelAccount
        {
            Email = ReadString(reader, "email"),
            UserId = ReadString(reader, "user_id"),
            UserName = ReadString(reader, "username"),
            HotelId = hotelId,
            Role = ReadString(reader, "role"),
            HotelRole = ReadString(reader, "hotel_role"),
            ActiveStatus = ReadString(reader, "activestatus")
        };
    }

    private static List<int> BuildYearList(int currentYear)
    {
        var years = new List<int>();
        for (var year = currentYear - 6; year <= 2050; year++)
            years.Add(year);
        return years;
    }

    private static int GetSourceSortRank(string source)
    {
        if (string.Equals(source, "BNBUK", StringComparison.OrdinalIgnoreCase)) return 0;
        if (string.Equals(source, "GoogleHotelARI", StringComparison.OrdinalIgnoreCase)) return 1;
        if (string.Equals(source, "BookingCom", StringComparison.OrdinalIgnoreCase)) return 2;
        if (string.Equals(source, "Booking.com", StringComparison.OrdinalIgnoreCase)) return 2;
        if (string.Equals(source, "Booking Com", StringComparison.OrdinalIgnoreCase)) return 2;
        if (string.Equals(source, "Walk In", StringComparison.OrdinalIgnoreCase)) return 98;
        return 50;
    }

    private static string GetPaletteColor(int index)
    {
        string[] colors =
        {
            "#ff7a00", "#5aa9ff", "#22c55e", "#a855f7", "#ef4444",
            "#06b6d4", "#f59e0b", "#14b8a6", "#6366f1", "#e11d48"
        };
        return colors[index % colors.Length];
    }

    private static string HexToRgba(string hex, double alpha)
    {
        if (string.IsNullOrWhiteSpace(hex))
            hex = "#64748B";

        hex = hex.Trim().TrimStart('#');
        if (hex.Length != 6)
            return "rgba(100,116,139," + alpha.ToString("0.##", CultureInfo.InvariantCulture) + ")";

        var r = int.Parse(hex[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var g = int.Parse(hex.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var b = int.Parse(hex.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return $"rgba({r},{g},{b},{alpha.ToString("0.##", CultureInfo.InvariantCulture)})";
    }

    private static string ToBase64(string value)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(value ?? string.Empty));

    private static string Clean(string? value, int maxLength)
    {
        value = value?.Trim() ?? string.Empty;
        return maxLength > 0 && value.Length > maxLength ? value[..maxLength] : value;
    }

    private static string ReadString(SqlDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? string.Empty : Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static int ReadInt(SqlDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? 0 : Convert.ToInt32(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
    }

    private static double ReadDouble(SqlDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? 0d : Convert.ToDouble(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
    }

    private static OpenHotelResult Fail(string message) => new(false, message);

    private sealed class BookingSourceChartResult
    {
        public List<BookingSourceChartDataset> Datasets { get; } = new();
        public List<string> Sources { get; } = new();
    }
}
