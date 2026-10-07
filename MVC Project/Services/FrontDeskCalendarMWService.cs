using System.Data;
using Microsoft.Data.SqlClient;
using Orapmshms.Models;
using Orapmshms.Services.AvailabilityJobs;

namespace Orapmshms.Services;

/// <summary>
/// Month-wise calendar adapter. It deliberately reuses the proven daily
/// FrontDeskCalendarService in bounded chunks instead of changing its 60-day
/// loading contract. This keeps the existing /Calendar implementation isolated.
/// </summary>
public sealed class FrontDeskCalendarMWService : IFrontDeskCalendarMWService
{
    private const int ChunkDays = 60;
    private const int MaxRangeDays = 550; // covers the legacy Council 15-month window from the current month.

    private readonly IFrontDeskCalendarService _calendar;
    private readonly IHotelClock _hotelClock;
    private readonly string _connectionString;
    private readonly IAvailabilityAutoUpdateQueue _availabilityQueue;

    public FrontDeskCalendarMWService(
        IConfiguration configuration,
        IHotelClock hotelClock,
        IFrontDeskCalendarService calendar,
        IAvailabilityAutoUpdateQueue availabilityQueue)
    {
        _calendar = calendar;
        _hotelClock = hotelClock;
        _availabilityQueue = availabilityQueue;
        _connectionString = configuration.GetConnectionString("con")
            ?? throw new InvalidOperationException("ConnectionStrings:con is missing.");
    }

    public async Task<FrontDeskCalendarMWPageViewModel> GetPageAsync(
        string hotelId, string hotelName, string userId, string userName, string role,
        DateTime? start, DateTime? end, CancellationToken ct = default)
    {
        var today = _hotelClock.GetHotelToday(hotelId).Date;
        var startDate = (start ?? new DateTime(today.Year, today.Month, 1)).Date;
        // Default month-wise window: 14 complete month columns beginning with
        // the current/start month. This keeps the MW view dense but still gives
        // roughly a full year plus two months of forward visibility.
        var defaultEnd = new DateTime(startDate.Year, startDate.Month, 1)
            .AddMonths(14)
            .AddDays(-1);
        var endDate = (end ?? defaultEnd).Date;
        if (endDate < startDate) endDate = startDate;
        if ((endDate - startDate).TotalDays > MaxRangeDays) endDate = startDate.AddDays(MaxRangeDays);

        var basePage = await _calendar.GetPageAsync(
            hotelId, hotelName, userId, userName, role, startDate, ct);

        return new FrontDeskCalendarMWPageViewModel
        {
            HotelId = basePage.HotelId,
            HotelName = basePage.HotelName,
            UserId = basePage.UserId,
            UserName = basePage.UserName,
            Role = basePage.Role,
            CurrencySymbol = basePage.CurrencySymbol,
            HotelToday = basePage.HotelToday,
            StartDate = startDate,
            EndDate = endDate,
            CanOnlineCardPayment = basePage.CanOnlineCardPayment,
            CanPdqPayment = basePage.CanPdqPayment,
            Permissions = basePage.Permissions
        };
    }

    public async Task<FrontDeskCalendarMWPayload> GetCalendarAsync(
        string hotelId, string userId, string userName, string role,
        DateTime start, DateTime end, CancellationToken ct = default)
    {
        start = start.Date;
        end = end.Date;
        if (end < start) throw new ArgumentException("End date must be on or after start date.");
        if ((end - start).TotalDays > MaxRangeDays)
            throw new ArgumentException($"Month-wise calendar range cannot exceed {MaxRangeDays + 1} days.");

        var ranges = new List<(DateTime Start, int Days)>();
        for (var cursor = start; cursor <= end;)
        {
            var remaining = (end - cursor).Days + 1;
            var days = Math.Min(ChunkDays, remaining);
            ranges.Add((cursor, days));
            cursor = cursor.AddDays(days);
        }

        using var gate = new SemaphoreSlim(3, 3);
        var tasks = ranges.Select(async range =>
        {
            await gate.WaitAsync(ct);
            try
            {
                return await _calendar.GetCalendarAsync(
                    hotelId, userId, userName, role, range.Start, range.Days, ct);
            }
            finally
            {
                gate.Release();
            }
        }).ToArray();

        var parts = await Task.WhenAll(tasks);
        var first = parts.FirstOrDefault() ?? new FrontDeskCalendarPayload
        {
            StartDate = start,
            EndDate = end,
            HotelToday = _hotelClock.GetHotelToday(hotelId),
            CurrencySymbol = "£"
        };

        var bookings = parts
            .SelectMany(x => x.Bookings ?? Array.Empty<FrontDeskBookingDto>())
            .GroupBy(BookingKey, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(x => x.CategoryName)
            .ThenBy(x => x.RoomNo)
            .ThenBy(x => x.Arrival)
            .ToList();

        // WebForms MW parity: PaymentsUpdateTB stores paid amount at reservation level,
        // but the month-wise calendar allocates that amount across individual payment rows.
        // Do this only in the MW adapter so the working daily calendar remains untouched.
        await ApplyAllocatedPaymentAmountsAsync(hotelId, bookings, ct);

        var blocks = parts
            .SelectMany(x => x.Blocks ?? Array.Empty<FrontDeskBlockDto>())
            .GroupBy(BlockKey, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(x => x.CategoryName)
            .ThenBy(x => x.RoomNo)
            .ThenBy(x => x.StartDate)
            .ToList();

        var rooms = parts
            .SelectMany(x => x.Rooms ?? Array.Empty<FrontDeskRoomDto>())
            .GroupBy(x => $"{x.CategoryId}|{x.RoomNo}", StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        var assignments = await LoadCouncilAssignmentsAsync(hotelId, start, end, ct);

        return new FrontDeskCalendarMWPayload
        {
            StartDate = start,
            EndDate = end,
            HotelToday = first.HotelToday,
            CurrencySymbol = first.CurrencySymbol,
            Months = BuildMonths(start, end),
            Categories = first.Categories ?? Array.Empty<FrontDeskCategoryDto>(),
            Rooms = rooms,
            Bookings = bookings,
            Blocks = blocks,
            CouncilAssignments = assignments,
            Permissions = first.Permissions ?? new FrontDeskCalendarPermissions()
        };
    }

    private static string BookingKey(FrontDeskBookingDto x) =>
        $"{x.PaymentId}|{x.RegId}|{x.CategoryId}|{x.RoomNo}|{x.Arrival:yyyyMMdd}|{x.Departure:yyyyMMdd}";

    private static string BlockKey(FrontDeskBlockDto x) =>
        x.BlockId > 0
            ? x.BlockId.ToString()
            : $"{x.RoomNo}|{x.CategoryId}|{x.StartDate:yyyyMMdd}|{x.EndDate:yyyyMMdd}|{x.Reason}";

    private static IReadOnlyList<FrontDeskCalendarMWMonthDto> BuildMonths(DateTime start, DateTime end)
    {
        var result = new List<FrontDeskCalendarMWMonthDto>();
        var cursor = new DateTime(start.Year, start.Month, 1);
        while (cursor <= end)
        {
            var monthEnd = cursor.AddMonths(1).AddDays(-1);
            result.Add(new FrontDeskCalendarMWMonthDto
            {
                Label = cursor.ToString("MMM yyyy", System.Globalization.CultureInfo.InvariantCulture),
                Month = cursor.ToString("MMMM", System.Globalization.CultureInfo.InvariantCulture),
                Year = cursor.ToString("yy", System.Globalization.CultureInfo.InvariantCulture),
                StartDate = cursor < start ? start : cursor,
                EndDate = monthEnd > end ? end : monthEnd
            });
            cursor = cursor.AddMonths(1);
        }
        return result;
    }

    private async Task<IReadOnlyList<FrontDeskCouncilRoomAssignmentDto>> LoadCouncilAssignmentsAsync(
        string hotelId, DateTime start, DateTime end, CancellationToken ct)
    {
        const string sql = @"
SET NOCOUNT ON;
SELECT
    ISNULL(CONVERT(varchar(100),ura.RoomNo),'') AS RoomNo,
    ISNULL(CONVERT(varchar(100),ura.UserId),'') AS UserId,
    parsed.FromDate,
    parsed.ToDate
FROM dbo.UserRoomAccess ura WITH (READPAST)
INNER JOIN dbo.Hms_accounts ha WITH (READPAST)
    ON CONVERT(varchar(100),ha.user_id)=CONVERT(varchar(100),ura.UserId)
OUTER APPLY
(
    SELECT
        COALESCE(
            TRY_CONVERT(date,ura.FromDate,23),
            TRY_CONVERT(date,ura.FromDate,103),
            TRY_CONVERT(date,ura.FromDate,110),
            TRY_CONVERT(date,ura.FromDate,101),
            TRY_CONVERT(date,ura.FromDate)
        ) AS FromDate,
        COALESCE(
            TRY_CONVERT(date,ura.ToDate,23),
            TRY_CONVERT(date,ura.ToDate,103),
            TRY_CONVERT(date,ura.ToDate,110),
            TRY_CONVERT(date,ura.ToDate,101),
            TRY_CONVERT(date,ura.ToDate)
        ) AS ToDate
) parsed
WHERE CONVERT(varchar(100),ura.HotelId)=@hotel
  AND LOWER(LTRIM(RTRIM(ISNULL(ha.role,''))))='council'
  AND ISNULL(parsed.FromDate,CONVERT(date,'19000101',112)) <= @end
  AND ISNULL(parsed.ToDate,CONVERT(date,'99991231',112)) >= @start
ORDER BY ura.RoomNo, parsed.FromDate, parsed.ToDate;";

        var result = new List<FrontDeskCouncilRoomAssignmentDto>();
        await using var cn = new SqlConnection(_connectionString);
        await cn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, cn);
        cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 100).Value = hotelId;
        cmd.Parameters.Add("@start", SqlDbType.Date).Value = start.Date;
        cmd.Parameters.Add("@end", SqlDbType.Date).Value = end.Date;
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            result.Add(new FrontDeskCouncilRoomAssignmentDto
            {
                RoomNo = Convert.ToString(rd["RoomNo"]) ?? string.Empty,
                CouncilUserId = Convert.ToString(rd["UserId"]) ?? string.Empty,
                FromDate = rd["FromDate"] == DBNull.Value ? null : Convert.ToDateTime(rd["FromDate"]).Date,
                ToDate = rd["ToDate"] == DBNull.Value ? null : Convert.ToDateTime(rd["ToDate"]).Date
            });
        }
        return result;
    }

    public async Task<FrontDeskOperationResult> CheckResizeAvailabilityAsync(
        string hotelId,
        FrontDeskCalendarMWResizeCheckRequest request,
        CancellationToken ct = default)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.RegId))
            return FrontDeskOperationResult.Fail("Reservation ID is required.");

        var regId = NormalizeRegId(request.RegId);
        var arrival = request.Arrival.Date;
        var departure = request.NewDeparture.Date;
        if (departure <= arrival)
            return FrontDeskOperationResult.Fail("New departure must be after arrival.");

        const string sql = @"
SET NOCOUNT ON;
;WITH TargetRooms AS
(
    SELECT DISTINCT
        LTRIM(RTRIM(ISNULL(room_no,''))) AS room_no
    FROM dbo.payments WITH (READPAST)
    WHERE CONVERT(varchar(100),hotel_id)=@hotel
      AND CONVERT(varchar(100),reg_id)=@reg
      AND LTRIM(RTRIM(ISNULL(descr,'')))='Room Rent'
      AND LOWER(LTRIM(RTRIM(ISNULL(res_status,'')))) IN ('check in','reservation','provisional','booked','confirmed')
      AND LTRIM(RTRIM(ISNULL(room_no,'')))<>''
      AND UPPER(LTRIM(RTRIM(ISNULL(room_no,''))))<>'UNASSIGNED'
)
SELECT TOP (1) t.room_no
FROM TargetRooms t
WHERE
    EXISTS
    (
        SELECT 1
        FROM dbo.payments p WITH (READPAST)
        CROSS APPLY
        (
            SELECT
                COALESCE(
                    TRY_CONVERT(date,p.ArrivalDate,110), TRY_CONVERT(date,p.ArrivalDate,23),
                    TRY_CONVERT(date,p.ArrivalDate,101), TRY_CONVERT(date,p.ArrivalDate,103),
                    TRY_CONVERT(date,p.ArrivalDate)
                ) AS arr,
                COALESCE(
                    TRY_CONVERT(date,p.DepartureDate,110), TRY_CONVERT(date,p.DepartureDate,23),
                    TRY_CONVERT(date,p.DepartureDate,101), TRY_CONVERT(date,p.DepartureDate,103),
                    TRY_CONVERT(date,p.DepartureDate)
                ) AS dep
        ) d
        WHERE CONVERT(varchar(100),p.hotel_id)=@hotel
          AND CONVERT(varchar(100),p.reg_id)<>@reg
          AND LTRIM(RTRIM(ISNULL(p.descr,'')))='Room Rent'
          AND LTRIM(RTRIM(ISNULL(p.room_no,'')))=t.room_no
          AND LOWER(LTRIM(RTRIM(ISNULL(p.res_status,'')))) IN ('check in','reservation','provisional','booked','confirmed')
          AND d.arr IS NOT NULL AND d.dep IS NOT NULL
          AND d.arr < @departure
          AND d.dep > @arrival
    )
    OR EXISTS
    (
        SELECT 1
        FROM dbo.RoomBlocksTB b WITH (READPAST)
        CROSS APPLY
        (
            SELECT
                COALESCE(
                    TRY_CONVERT(date,b.BlockStartDate,110), TRY_CONVERT(date,b.BlockStartDate,23),
                    TRY_CONVERT(date,b.BlockStartDate,101), TRY_CONVERT(date,b.BlockStartDate,103),
                    TRY_CONVERT(date,b.BlockStartDate)
                ) AS bs,
                COALESCE(
                    TRY_CONVERT(date,b.BlockEndDate,110), TRY_CONVERT(date,b.BlockEndDate,23),
                    TRY_CONVERT(date,b.BlockEndDate,101), TRY_CONVERT(date,b.BlockEndDate,103),
                    TRY_CONVERT(date,b.BlockEndDate)
                ) AS be
        ) d
        WHERE CONVERT(varchar(100),b.HotelID)=@hotel
          AND LTRIM(RTRIM(ISNULL(b.RoomNo,'')))=t.room_no
          AND ISNULL(b.IsActive,1)=1
          AND d.bs IS NOT NULL AND d.be IS NOT NULL
          AND d.bs < @departure
          AND d.be >= @arrival
    );";

        await using var cn = new SqlConnection(_connectionString);
        await cn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, cn);
        cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 100).Value = hotelId;
        cmd.Parameters.Add("@reg", SqlDbType.VarChar, 100).Value = regId;
        cmd.Parameters.Add("@arrival", SqlDbType.Date).Value = arrival;
        cmd.Parameters.Add("@departure", SqlDbType.Date).Value = departure;
        var overlap = Convert.ToString(await cmd.ExecuteScalarAsync(ct))?.Trim() ?? string.Empty;

        return string.IsNullOrWhiteSpace(overlap)
            ? FrontDeskOperationResult.Ok("Rooms are available for the selected month range.")
            : FrontDeskOperationResult.Fail(
                $"Room no {overlap} is not available for the full stay.",
                new { overlappedRoomNo = overlap });
    }

    public async Task<IReadOnlyList<FrontDeskCalendarMWPlanRateDto>> GetResizePlansAsync(
        string hotelId,
        string regId,
        DateTime arrival,
        DateTime oldDeparture,
        CancellationToken ct = default)
    {
        regId = NormalizeRegId(regId);
        arrival = arrival.Date;
        oldDeparture = oldDeparture.Date;
        if (string.IsNullOrWhiteSpace(regId) || oldDeparture <= arrival)
            return Array.Empty<FrontDeskCalendarMWPlanRateDto>();

        var oldStayMonths = MonthDiffCeil(arrival, oldDeparture);
        if (oldStayMonths <= 0) oldStayMonths = 1;

        const string sql = @"
;WITH p AS
(
    SELECT
        NULLIF(LTRIM(RTRIM(p.rateplan)), '') AS planId,
        NULLIF(LTRIM(RTRIM(p.[Type])), '') AS roomType,
        TRY_CONVERT(decimal(18,2), NULLIF(LTRIM(RTRIM(CONVERT(varchar(100),p.totalamount))), '')) AS totalAmountDec,
        p.hotel_id,
        p.reg_id
    FROM dbo.payments p WITH (READPAST)
    WHERE CONVERT(varchar(100),p.hotel_id)=@hotel
      AND CONVERT(varchar(100),p.reg_id)=@reg
      AND LTRIM(RTRIM(ISNULL(p.descr,'')))='Room Rent'
      AND LOWER(LTRIM(RTRIM(ISNULL(p.res_status,'')))) IN ('check in','reservation','provisional','booked','confirmed')
)
SELECT
    p.planId,
    p.roomType,
    ISNULL(CONVERT(varchar(100),cr.localcategoryid),'') AS categoryLocalId,
    CAST(COALESCE(SUM(p.totalAmountDec),0) AS decimal(18,2)) AS oldTotal
FROM p
LEFT JOIN dbo.create_room cr WITH (READPAST)
    ON CONVERT(varchar(100),cr.hotel_id)=CONVERT(varchar(100),p.hotel_id)
   AND LTRIM(RTRIM(ISNULL(cr.description,'')))=p.roomType
   AND LTRIM(RTRIM(ISNULL(cr.category,'')))='Room Rent'
WHERE p.planId IS NOT NULL
  AND p.roomType IS NOT NULL
GROUP BY p.planId,p.roomType,cr.localcategoryid
ORDER BY p.planId,p.roomType;";

        var result = new List<FrontDeskCalendarMWPlanRateDto>();
        await using var cn = new SqlConnection(_connectionString);
        await cn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, cn);
        cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 100).Value = hotelId;
        cmd.Parameters.Add("@reg", SqlDbType.VarChar, 100).Value = regId;
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            var oldTotal = rd["oldTotal"] == DBNull.Value ? 0m : Convert.ToDecimal(rd["oldTotal"]);
            var oldRate = Math.Round(oldTotal / oldStayMonths, 2, MidpointRounding.AwayFromZero);
            var planId = Convert.ToString(rd["planId"])?.Trim() ?? string.Empty;
            result.Add(new FrontDeskCalendarMWPlanRateDto
            {
                PlanId = planId,
                PlanName = planId,
                RoomType = Convert.ToString(rd["roomType"])?.Trim() ?? string.Empty,
                CategoryLocalId = Convert.ToString(rd["categoryLocalId"])?.Trim() ?? string.Empty,
                OldRate = oldRate,
                NewRate = oldRate
            });
        }
        return result;
    }

    public async Task<FrontDeskOperationResult> ResizeBookingAsync(
        string hotelId,
        string hotelName,
        string userId,
        string userName,
        string ip,
        FrontDeskCalendarMWResizeRequest request,
        CancellationToken ct = default)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.RegId))
            return FrontDeskOperationResult.Fail("Invalid month-wise resize request.");

        var regId = NormalizeRegId(request.RegId);
        var arrival = request.Arrival.Date;
        var oldDeparture = request.OldDeparture.Date;
        var newDeparture = request.NewDeparture.Date;
        if (arrival == default || oldDeparture == default || newDeparture == default)
            return FrontDeskOperationResult.Fail("Valid reservation dates are required.");
        if (oldDeparture <= arrival || newDeparture <= arrival)
            return FrontDeskOperationResult.Fail("New departure must be after arrival.");
        if (newDeparture == oldDeparture)
            return FrontDeskOperationResult.Ok("Reservation dates are unchanged.");

        var availability = await CheckResizeAvailabilityAsync(
            hotelId,
            new FrontDeskCalendarMWResizeCheckRequest
            {
                RegId = regId,
                Arrival = arrival,
                NewDeparture = newDeparture
            },
            ct);
        if (!availability.Success) return availability;

        var plans = (request.Plans ?? Array.Empty<FrontDeskCalendarMWPlanRateDto>())
            .Where(x => x != null)
            .Select(x => new FrontDeskCalendarMWPlanRateDto
            {
                PlanId = (x.PlanId ?? string.Empty).Trim(),
                PlanName = (x.PlanName ?? string.Empty).Trim(),
                RoomType = (x.RoomType ?? string.Empty).Trim(),
                CategoryLocalId = (x.CategoryLocalId ?? string.Empty).Trim(),
                OldRate = x.OldRate,
                NewRate = Math.Max(0m, x.NewRate)
            })
            .ToList();
        if (plans.Count == 0)
            return FrontDeskOperationResult.Fail("No monthly rate plans were supplied.");

        var isExtend = newDeparture > oldDeparture;
        var isShrink = newDeparture < oldDeparture;
        var oldTotalMonths = MonthDiffCeil(arrival, oldDeparture);
        var newTotalMonths = MonthDiffCeil(arrival, newDeparture);
        if (oldTotalMonths <= 0 || newTotalMonths <= 0)
            return FrontDeskOperationResult.Fail("Unable to calculate the month-wise stay length.");

        await using var cn = new SqlConnection(_connectionString);
        await cn.OpenAsync(ct);
        await using var tx = (SqlTransaction)await cn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

        try
        {
            if (isExtend)
            {
                var extMonths = MonthDiffCeil(oldDeparture, newDeparture);
                if (extMonths <= 0) return FrontDeskOperationResult.Fail("Invalid extension month count.");

                await UpdateMasterMonthDatesAsync(cn, tx, hotelId, regId, arrival, oldDeparture, newDeparture, newTotalMonths, true, ct);

                foreach (var plan in plans)
                {
                    if (string.IsNullOrWhiteSpace(plan.PlanId) || string.IsNullOrWhiteSpace(plan.CategoryLocalId) || plan.NewRate <= 0m)
                        continue;

                    var d = oldDeparture;
                    for (var i = 0; i < extMonths; i++)
                    {
                        await using var rateCmd = new SqlCommand(@"
IF EXISTS
(
    SELECT 1 FROM dbo.NewReservationRate
    WHERE CONVERT(varchar(100),reg_id)=@reg
      AND CONVERT(varchar(100),hotel_id)=@hotel
      AND LTRIM(RTRIM(ISNULL(plan_name,'')))=@plan
      AND CONVERT(varchar(100),category_id)=@category
      AND TRY_CONVERT(date,rate_date)=@rateDate
)
    UPDATE dbo.NewReservationRate
       SET rate=@rate
     WHERE CONVERT(varchar(100),reg_id)=@reg
       AND CONVERT(varchar(100),hotel_id)=@hotel
       AND LTRIM(RTRIM(ISNULL(plan_name,'')))=@plan
       AND CONVERT(varchar(100),category_id)=@category
       AND TRY_CONVERT(date,rate_date)=@rateDate;
ELSE
    INSERT INTO dbo.NewReservationRate(reg_id,hotel_id,plan_name,category_id,rate_date,rate)
    VALUES(@reg,@hotel,@plan,@category,@rateDate,@rate);", cn, tx);
                        rateCmd.Parameters.Add("@reg", SqlDbType.VarChar, 100).Value = regId;
                        rateCmd.Parameters.Add("@hotel", SqlDbType.VarChar, 100).Value = hotelId;
                        rateCmd.Parameters.Add("@plan", SqlDbType.VarChar, 100).Value = plan.PlanId;
                        rateCmd.Parameters.Add("@category", SqlDbType.VarChar, 100).Value = plan.CategoryLocalId;
                        rateCmd.Parameters.Add("@rateDate", SqlDbType.Date).Value = d.Date;
                        rateCmd.Parameters.Add("@rate", SqlDbType.Decimal).Value = plan.NewRate;
                        await rateCmd.ExecuteNonQueryAsync(ct);
                        d = AddMonthsSafe(d, 1);
                    }
                }

                var rows = await LoadPaymentRowsAsync(cn, tx, hotelId, regId, ct);
                foreach (var row in rows)
                {
                    var newNights = Math.Max(1, row.OldNights + extMonths);

                    if (row.Description.Equals("Extras", StringComparison.OrdinalIgnoreCase))
                    {
                        await using var extras = new SqlCommand(
                            "UPDATE dbo.payments SET Nights=@n WHERE ID=@id AND CONVERT(varchar(100),hotel_id)=@hotel AND CONVERT(varchar(100),reg_id)=@reg;",
                            cn, tx);
                        extras.Parameters.Add("@n", SqlDbType.Int).Value = newNights;
                        extras.Parameters.Add("@id", SqlDbType.Int).Value = row.Id;
                        extras.Parameters.Add("@hotel", SqlDbType.VarChar, 100).Value = hotelId;
                        extras.Parameters.Add("@reg", SqlDbType.VarChar, 100).Value = regId;
                        await extras.ExecuteNonQueryAsync(ct);
                        continue;
                    }

                    if (!row.Description.Equals("Room Rent", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var selectedMonthlyRate = ResolvePlanRate(plans, row.PlanId, row.RoomType, row.CategoryLocalId, row.OldNights > 0 ? row.Rate / row.OldNights : 0m);
                    var addBase = Math.Round(selectedMonthlyRate * extMonths, 2, MidpointRounding.AwayFromZero);
                    var newBaseTotal = Math.Round(row.Rate + addBase, 2, MidpointRounding.AwayFromZero);
                    var gstPerUnit = row.OldNights > 0 ? row.Gst / row.OldNights : 0m;
                    var bedPerUnit = row.OldNights > 0 ? row.Bed / row.OldNights : 0m;
                    var newGst = row.Gst > 0m ? Math.Round(gstPerUnit * newNights, 2, MidpointRounding.AwayFromZero) : 0m;
                    var newBed = row.Bed > 0m ? Math.Round(bedPerUnit * newNights, 2, MidpointRounding.AwayFromZero) : 0m;
                    var newTotal = Math.Round(newBaseTotal + newGst + newBed, 2, MidpointRounding.AwayFromZero);

                    await UpdateMonthPaymentRowAsync(cn, tx, hotelId, regId, row.Id, newDeparture, newNights, newBaseTotal, newGst, newBed, newTotal, ct);
                }

                try
                {
                    await using var logCmd = new SqlCommand(@"
INSERT INTO dbo.extendreservationlogs
(reg_id,old_departuredate,new_departuredate,hotel_id,user_id,systemname,ipaddress)
VALUES(@reg,@old,@new,@hotel,@user,@system,@ip);", cn, tx);
                    logCmd.Parameters.Add("@reg", SqlDbType.VarChar, 100).Value = regId;
                    logCmd.Parameters.Add("@old", SqlDbType.VarChar, 20).Value = LegacyDate(oldDeparture);
                    logCmd.Parameters.Add("@new", SqlDbType.VarChar, 20).Value = LegacyDate(newDeparture);
                    logCmd.Parameters.Add("@hotel", SqlDbType.VarChar, 100).Value = hotelId;
                    logCmd.Parameters.Add("@user", SqlDbType.VarChar, 100).Value = userId ?? string.Empty;
                    logCmd.Parameters.Add("@system", SqlDbType.VarChar, 100).Value = Environment.MachineName;
                    logCmd.Parameters.Add("@ip", SqlDbType.VarChar, 100).Value = ip ?? string.Empty;
                    await logCmd.ExecuteNonQueryAsync(ct);
                }
                catch (SqlException)
                {
                    // The legacy log table is not present in every property schema.
                    // The reservation change itself should remain functional.
                }
            }
            else if (isShrink)
            {
                await UpdateMasterMonthDatesAsync(cn, tx, hotelId, regId, arrival, oldDeparture, newDeparture, newTotalMonths, false, ct);

                var rows = (await LoadPaymentRowsAsync(cn, tx, hotelId, regId, ct))
                    .Where(x => x.Description.Equals("Room Rent", StringComparison.OrdinalIgnoreCase) && IsActiveReservationStatus(x.ResStatus))
                    .ToList();

                foreach (var row in rows)
                {
                    var perGst = row.OldNights > 0 ? row.Gst / row.OldNights : 0m;
                    var perBed = row.OldNights > 0 ? row.Bed / row.OldNights : 0m;
                    var fallbackRate = row.OldNights > 0 ? row.Rate / row.OldNights : 0m;
                    var selectedMonthlyRate = ResolvePlanRate(plans, row.PlanId, row.RoomType, row.CategoryLocalId, fallbackRate);
                    var newBaseTotal = Math.Round(selectedMonthlyRate * newTotalMonths, 2, MidpointRounding.AwayFromZero);
                    var newGst = Math.Round(perGst * newTotalMonths, 2, MidpointRounding.AwayFromZero);
                    var newBed = Math.Round(perBed * newTotalMonths, 2, MidpointRounding.AwayFromZero);
                    var newTotal = Math.Round(newBaseTotal + newGst + newBed, 2, MidpointRounding.AwayFromZero);

                    await UpdateMonthPaymentRowAsync(cn, tx, hotelId, regId, row.Id, newDeparture, newTotalMonths, newBaseTotal, newGst, newBed, newTotal, ct);
                }
            }

            await RefreshPaymentSummaryAsync(cn, tx, hotelId, regId, userId, userName, ip, ct);
            await tx.CommitAsync(ct);

            _availabilityQueue.Queue(new AvailabilityAutoUpdateJob(
                hotelId,
                hotelName ?? string.Empty,
                userId ?? string.Empty,
                userName ?? string.Empty,
                ip ?? string.Empty,
                arrival,
                oldDeparture > newDeparture ? oldDeparture : newDeparture,
                "0"));

            return FrontDeskOperationResult.Ok(
                isExtend ? "Reservation extended successfully." : "Reservation shortened successfully.",
                new
                {
                    arrival,
                    oldDeparture,
                    newDeparture,
                    oldMonths = oldTotalMonths,
                    newMonths = newTotalMonths,
                    changedMonths = Math.Abs(newTotalMonths - oldTotalMonths),
                    direction = isExtend ? "extend" : "shrink"
                });
        }
        catch (Exception ex)
        {
            try { await tx.RollbackAsync(ct); } catch { }
            return FrontDeskOperationResult.Fail("Unable to change reservation months. " + ex.Message);
        }
    }


    public async Task<FrontDeskOperationResult> SwapBookingAsync(
        string hotelId,
        string userId,
        string userName,
        string ip,
        FrontDeskCalendarMWSwapRequest request,
        CancellationToken ct = default)
    {
        if (request == null)
            return FrontDeskOperationResult.Fail("Invalid swap request.");

        var sourceReg = NormalizeRegId(request.SourceRegId);
        var targetReg = NormalizeRegId(request.TargetRegId);
        if (string.IsNullOrWhiteSpace(sourceReg) || string.IsNullOrWhiteSpace(targetReg))
            return FrontDeskOperationResult.Fail("Both reservations are required for a swap.");
        if (request.SourcePaymentId <= 0 || request.TargetPaymentId <= 0)
            return FrontDeskOperationResult.Fail("The selected room rows are missing their payment IDs.");
        if (request.SourcePaymentId == request.TargetPaymentId)
            return FrontDeskOperationResult.Ok("Room assignment is unchanged.");

        await using var cn = new SqlConnection(_connectionString);
        await cn.OpenAsync(ct);
        await using var tx = (SqlTransaction)await cn.BeginTransactionAsync(IsolationLevel.Serializable, ct);

        try
        {
            var source = await LoadSwapRowAsync(cn, tx, hotelId, sourceReg, request.SourcePaymentId, ct);
            var target = await LoadSwapRowAsync(cn, tx, hotelId, targetReg, request.TargetPaymentId, ct);
            if (source == null || target == null)
                return FrontDeskOperationResult.Fail("One of the selected room assignments no longer exists.");

            if (string.IsNullOrWhiteSpace(source.RoomNo) || string.IsNullOrWhiteSpace(target.RoomNo) ||
                source.RoomNo.Equals("UNASSIGNED", StringComparison.OrdinalIgnoreCase) ||
                target.RoomNo.Equals("UNASSIGNED", StringComparison.OrdinalIgnoreCase))
                return FrontDeskOperationResult.Fail("Occupied-room swap requires two assigned rooms.");

            // Legacy MW rule: an occupied target is a SWAP only when both stays have
            // exactly the same arrival and departure dates.
            if (source.Arrival != target.Arrival || source.Departure != target.Departure)
                return FrontDeskOperationResult.Fail(
                    "Cannot swap these rooms because both reservations must have the same check-in and check-out dates.");

            if (source.RoomNo.Equals(target.RoomNo, StringComparison.OrdinalIgnoreCase))
                return FrontDeskOperationResult.Ok("Room assignment is unchanged.");

            var sourceCatText = string.IsNullOrWhiteSpace(source.RoomType)
                ? (request.SourceCategoryName ?? string.Empty).Trim()
                : source.RoomType;
            var targetCatText = string.IsNullOrWhiteSpace(target.RoomType)
                ? (request.TargetCategoryName ?? string.Empty).Trim()
                : target.RoomType;

            var sourceCatLocal = await ResolveLocalCategoryIdAsync(
                cn, tx, hotelId, sourceCatText, request.SourceCategoryId, ct);
            var targetCatLocal = await ResolveLocalCategoryIdAsync(
                cn, tx, hotelId, targetCatText, request.TargetCategoryId, ct);

            var tempRoom = "__TMP__" + Guid.NewGuid().ToString("N")[..12];

            await using (var aToTemp = new SqlCommand(@"
UPDATE dbo.payments
   SET room_no=@tmp
 WHERE CONVERT(varchar(100),hotel_id)=@hotel
   AND CONVERT(varchar(100),reg_id)=@reg
   AND descr='Room Rent'
   AND LTRIM(RTRIM(ISNULL(room_no,'')))=@room;", cn, tx))
            {
                aToTemp.Parameters.Add("@tmp", SqlDbType.VarChar, 100).Value = tempRoom;
                aToTemp.Parameters.Add("@hotel", SqlDbType.VarChar, 100).Value = hotelId;
                aToTemp.Parameters.Add("@reg", SqlDbType.VarChar, 100).Value = sourceReg;
                aToTemp.Parameters.Add("@room", SqlDbType.VarChar, 100).Value = source.RoomNo;
                if (await aToTemp.ExecuteNonQueryAsync(ct) <= 0)
                    throw new InvalidOperationException("The source room assignment changed before the swap could be saved.");
            }

            await using (var bToA = new SqlCommand(@"
UPDATE dbo.payments
   SET room_no=@roomA,
       [Type]=@catA
 WHERE CONVERT(varchar(100),hotel_id)=@hotel
   AND CONVERT(varchar(100),reg_id)=@reg
   AND descr='Room Rent'
   AND LTRIM(RTRIM(ISNULL(room_no,'')))=@roomB;", cn, tx))
            {
                bToA.Parameters.Add("@roomA", SqlDbType.VarChar, 100).Value = source.RoomNo;
                bToA.Parameters.Add("@catA", SqlDbType.VarChar, 200).Value = sourceCatText;
                bToA.Parameters.Add("@hotel", SqlDbType.VarChar, 100).Value = hotelId;
                bToA.Parameters.Add("@reg", SqlDbType.VarChar, 100).Value = targetReg;
                bToA.Parameters.Add("@roomB", SqlDbType.VarChar, 100).Value = target.RoomNo;
                if (await bToA.ExecuteNonQueryAsync(ct) <= 0)
                    throw new InvalidOperationException("The target room assignment changed before the swap could be saved.");
            }

            await using (var tempToB = new SqlCommand(@"
UPDATE dbo.payments
   SET room_no=@roomB,
       [Type]=@catB
 WHERE CONVERT(varchar(100),hotel_id)=@hotel
   AND CONVERT(varchar(100),reg_id)=@reg
   AND descr='Room Rent'
   AND room_no=@tmp;", cn, tx))
            {
                tempToB.Parameters.Add("@roomB", SqlDbType.VarChar, 100).Value = target.RoomNo;
                tempToB.Parameters.Add("@catB", SqlDbType.VarChar, 200).Value = targetCatText;
                tempToB.Parameters.Add("@hotel", SqlDbType.VarChar, 100).Value = hotelId;
                tempToB.Parameters.Add("@reg", SqlDbType.VarChar, 100).Value = sourceReg;
                tempToB.Parameters.Add("@tmp", SqlDbType.VarChar, 100).Value = tempRoom;
                if (await tempToB.ExecuteNonQueryAsync(ct) <= 0)
                    throw new InvalidOperationException("The source room assignment could not be completed.");
            }

            // Same behavior as the WebForms MW swap: when categories differ,
            // move the reservation-rate category references too.  For two room rows
            // inside the same reservation there is no per-room NRR key, so leave it
            // unchanged rather than applying two conflicting whole-reg updates.
            if (!sourceReg.Equals(targetReg, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(sourceCatLocal, targetCatLocal, StringComparison.OrdinalIgnoreCase))
            {
                await UpdateReservationRateCategoryAsync(cn, tx, hotelId, sourceReg, targetCatLocal, ct);
                await UpdateReservationRateCategoryAsync(cn, tx, hotelId, targetReg, sourceCatLocal, ct);
            }

            await SetRoomOccupiedAsync(cn, tx, hotelId, source.RoomNo, ct);
            await SetRoomOccupiedAsync(cn, tx, hotelId, target.RoomNo, ct);

            await tx.CommitAsync(ct);

            foreach (var cat in new[] { sourceCatLocal, targetCatLocal }
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                _availabilityQueue.Queue(new AvailabilityAutoUpdateJob(
                    hotelId,
                    string.Empty,
                    userId ?? string.Empty,
                    userName ?? string.Empty,
                    ip ?? string.Empty,
                    source.Arrival,
                    source.Departure,
                    cat));
            }

            return FrontDeskOperationResult.Ok("Rooms swapped successfully.", new
            {
                sourceRegId = sourceReg,
                targetRegId = targetReg,
                sourceOldRoom = source.RoomNo,
                sourceNewRoom = target.RoomNo,
                targetOldRoom = target.RoomNo,
                targetNewRoom = source.RoomNo
            });
        }
        catch (Exception ex)
        {
            try { await tx.RollbackAsync(ct); } catch { }
            return FrontDeskOperationResult.Fail("Unable to swap the selected rooms. " + ex.Message);
        }
    }

    private async Task<SwapRow?> LoadSwapRowAsync(
        SqlConnection cn,
        SqlTransaction tx,
        string hotelId,
        string regId,
        long paymentId,
        CancellationToken ct)
    {
        await using var cmd = new SqlCommand(@"
SELECT TOP (1)
    ID,
    LTRIM(RTRIM(ISNULL(room_no,''))) AS room_no,
    LTRIM(RTRIM(ISNULL([Type],''))) AS room_type,
    TRY_CONVERT(date,ArrivalDate,110) AS arrival,
    TRY_CONVERT(date,DepartureDate,110) AS departure
FROM dbo.payments WITH (UPDLOCK,HOLDLOCK)
WHERE ID=@id
  AND CONVERT(varchar(100),hotel_id)=@hotel
  AND CONVERT(varchar(100),reg_id)=@reg
  AND descr='Room Rent';", cn, tx);
        cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = paymentId;
        cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 100).Value = hotelId;
        cmd.Parameters.Add("@reg", SqlDbType.VarChar, 100).Value = regId;

        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct)) return null;
        if (rd["arrival"] == DBNull.Value || rd["departure"] == DBNull.Value) return null;

        return new SwapRow
        {
            PaymentId = Convert.ToInt64(rd["ID"]),
            RoomNo = Convert.ToString(rd["room_no"])?.Trim() ?? string.Empty,
            RoomType = Convert.ToString(rd["room_type"])?.Trim() ?? string.Empty,
            Arrival = Convert.ToDateTime(rd["arrival"]).Date,
            Departure = Convert.ToDateTime(rd["departure"]).Date
        };
    }

    private async Task<string> ResolveLocalCategoryIdAsync(
        SqlConnection cn,
        SqlTransaction tx,
        string hotelId,
        string categoryName,
        string fallback,
        CancellationToken ct)
    {
        await using var cmd = new SqlCommand(@"
SELECT TOP (1) CONVERT(varchar(100),localcategoryid)
FROM dbo.create_room
WHERE CONVERT(varchar(100),hotel_id)=@hotel
  AND LTRIM(RTRIM(ISNULL(description,'')))=@category
  AND (category='Room Rent' OR category IS NULL OR LTRIM(RTRIM(category))='');", cn, tx);
        cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 100).Value = hotelId;
        cmd.Parameters.Add("@category", SqlDbType.VarChar, 200).Value = categoryName ?? string.Empty;
        var value = await cmd.ExecuteScalarAsync(ct);
        var resolved = Convert.ToString(value)?.Trim() ?? string.Empty;
        return string.IsNullOrWhiteSpace(resolved) ? (fallback ?? string.Empty).Trim() : resolved;
    }

    private static async Task UpdateReservationRateCategoryAsync(
        SqlConnection cn,
        SqlTransaction tx,
        string hotelId,
        string regId,
        string categoryId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(categoryId)) return;
        await using var cmd = new SqlCommand(@"
UPDATE dbo.NewReservationRate
   SET category_id=@category
 WHERE CONVERT(varchar(100),hotel_id)=@hotel
   AND CONVERT(varchar(100),reg_id)=@reg;", cn, tx);
        cmd.Parameters.Add("@category", SqlDbType.VarChar, 100).Value = categoryId;
        cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 100).Value = hotelId;
        cmd.Parameters.Add("@reg", SqlDbType.VarChar, 100).Value = regId;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task SetRoomOccupiedAsync(
        SqlConnection cn,
        SqlTransaction tx,
        string hotelId,
        string roomNo,
        CancellationToken ct)
    {
        await using var cmd = new SqlCommand(@"
UPDATE dbo.RoomsTB
   SET room_status='Occupied'
 WHERE CONVERT(varchar(100),Hotel_id)=@hotel
   AND LTRIM(RTRIM(ISNULL(room_no,'')))=@room;", cn, tx);
        cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 100).Value = hotelId;
        cmd.Parameters.Add("@room", SqlDbType.VarChar, 100).Value = roomNo;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task ApplyAllocatedPaymentAmountsAsync(
        string hotelId,
        List<FrontDeskBookingDto> bookings,
        CancellationToken ct)
    {
        if (bookings == null || bookings.Count == 0) return;

        var paymentIds = bookings.Where(x => x.PaymentId > 0).Select(x => x.PaymentId).Distinct().ToArray();
        var rows = new Dictionary<int, PaymentAllocationRow>();

        const int batchSize = 400;
        for (var offset = 0; offset < paymentIds.Length; offset += batchSize)
        {
            var batch = paymentIds.Skip(offset).Take(batchSize).ToArray();
            if (batch.Length == 0) continue;

            var parameterNames = batch.Select((_, i) => "@id" + i).ToArray();
            var sql = $@"
SELECT
    p.ID,
    ISNULL(CONVERT(varchar(100),p.reg_id),'') AS reg_id,
    ISNULL(TRY_CONVERT(decimal(18,2),NULLIF(LTRIM(RTRIM(CONVERT(varchar(100),p.totalamount))),'')),0) AS line_total,
    ISNULL(pu.paid_amount,0) AS paid_amount
FROM dbo.payments p WITH (READPAST)
OUTER APPLY
(
    SELECT TOP (1)
        ISNULL(TRY_CONVERT(decimal(18,2),NULLIF(LTRIM(RTRIM(CONVERT(varchar(100),u.paid_amount))),'')),0) AS paid_amount
    FROM dbo.PaymentsUpdateTB u WITH (READPAST)
    WHERE CONVERT(varchar(100),u.hotel_id)=CONVERT(varchar(100),p.hotel_id)
      AND CONVERT(varchar(100),u.reg_id)=CONVERT(varchar(100),p.reg_id)
    ORDER BY TRY_CONVERT(datetime,u.currentdate) DESC,u.id DESC
) pu
WHERE CONVERT(varchar(100),p.hotel_id)=@hotel
  AND p.ID IN ({string.Join(",", parameterNames)});";

            await using var cn = new SqlConnection(_connectionString);
            await cn.OpenAsync(ct);
            await using var cmd = new SqlCommand(sql, cn);
            cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 100).Value = hotelId;
            for (var i = 0; i < batch.Length; i++)
                cmd.Parameters.Add(parameterNames[i], SqlDbType.Int).Value = batch[i];

            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                var id = Convert.ToInt32(rd["ID"]);
                rows[id] = new PaymentAllocationRow(
                    Convert.ToString(rd["reg_id"]) ?? string.Empty,
                    rd["line_total"] == DBNull.Value ? 0m : Convert.ToDecimal(rd["line_total"]),
                    rd["paid_amount"] == DBNull.Value ? 0m : Convert.ToDecimal(rd["paid_amount"]));
            }
        }

        foreach (var group in bookings.GroupBy(x => NormalizeRegId(x.RegId), StringComparer.OrdinalIgnoreCase))
        {
            var totalPaidForReg = group
                .Select(x => rows.TryGetValue(x.PaymentId, out var row) ? row.PaidAmount : x.Paid)
                .DefaultIfEmpty(0m)
                .Max();
            var usedPaid = 0m;

            foreach (var booking in group.OrderBy(x => x.Arrival).ThenBy(x => x.Departure).ThenBy(x => x.PaymentId))
            {
                var lineTotal = rows.TryGetValue(booking.PaymentId, out var row) ? row.LineTotal : booking.Total;
                lineTotal = Math.Max(0m, lineTotal);
                var paidForLine = Math.Max(0m, Math.Min(lineTotal, totalPaidForReg - usedPaid));

                booking.Total = lineTotal;
                booking.Paid = paidForLine;
                booking.Balance = Math.Max(0m, lineTotal - paidForLine);
                booking.PaymentStatus = PaymentStatus(lineTotal, paidForLine);

                usedPaid += lineTotal;
            }
        }
    }

    private async Task<List<MonthPaymentRow>> LoadPaymentRowsAsync(
        SqlConnection cn,
        SqlTransaction tx,
        string hotelId,
        string regId,
        CancellationToken ct)
    {
        const string sql = @"
SELECT
    p.ID,
    ISNULL(CONVERT(varchar(100),p.rateplan),'') AS rateplan,
    ISNULL(CONVERT(varchar(150),p.[Type]),'') AS roomType,
    ISNULL(CONVERT(varchar(100),cr.localcategoryid),'') AS categoryLocalId,
    ISNULL(TRY_CONVERT(decimal(18,2),NULLIF(LTRIM(RTRIM(CONVERT(varchar(100),p.rate))),'')),0) AS rate,
    ISNULL(TRY_CONVERT(decimal(18,2),NULLIF(LTRIM(RTRIM(CONVERT(varchar(100),p.GST))),'')),0) AS GST,
    ISNULL(TRY_CONVERT(decimal(18,2),NULLIF(LTRIM(RTRIM(CONVERT(varchar(100),p.Bed))),'')),0) AS Bed,
    ISNULL(TRY_CONVERT(int,p.Nights),0) AS Nights,
    ISNULL(CONVERT(varchar(100),p.descr),'') AS descr,
    ISNULL(CONVERT(varchar(100),p.res_status),'') AS res_status
FROM dbo.payments p WITH (UPDLOCK,ROWLOCK)
LEFT JOIN dbo.create_room cr WITH (READPAST)
    ON CONVERT(varchar(100),cr.hotel_id)=CONVERT(varchar(100),p.hotel_id)
   AND LTRIM(RTRIM(ISNULL(cr.description,'')))=LTRIM(RTRIM(ISNULL(p.[Type],'')))
   AND LTRIM(RTRIM(ISNULL(cr.category,'')))='Room Rent'
WHERE CONVERT(varchar(100),p.hotel_id)=@hotel
  AND CONVERT(varchar(100),p.reg_id)=@reg
ORDER BY p.ID;";

        var result = new List<MonthPaymentRow>();
        await using var cmd = new SqlCommand(sql, cn, tx);
        cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 100).Value = hotelId;
        cmd.Parameters.Add("@reg", SqlDbType.VarChar, 100).Value = regId;
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            result.Add(new MonthPaymentRow
            {
                Id = Convert.ToInt32(rd["ID"]),
                PlanId = Convert.ToString(rd["rateplan"])?.Trim() ?? string.Empty,
                RoomType = Convert.ToString(rd["roomType"])?.Trim() ?? string.Empty,
                CategoryLocalId = Convert.ToString(rd["categoryLocalId"])?.Trim() ?? string.Empty,
                Rate = rd["rate"] == DBNull.Value ? 0m : Convert.ToDecimal(rd["rate"]),
                Gst = rd["GST"] == DBNull.Value ? 0m : Convert.ToDecimal(rd["GST"]),
                Bed = rd["Bed"] == DBNull.Value ? 0m : Convert.ToDecimal(rd["Bed"]),
                OldNights = rd["Nights"] == DBNull.Value ? 0 : Convert.ToInt32(rd["Nights"]),
                Description = Convert.ToString(rd["descr"])?.Trim() ?? string.Empty,
                ResStatus = Convert.ToString(rd["res_status"])?.Trim() ?? string.Empty
            });
        }
        return result;
    }

    private static decimal ResolvePlanRate(
        IReadOnlyList<FrontDeskCalendarMWPlanRateDto> plans,
        string planId,
        string roomType,
        string categoryLocalId,
        decimal fallback)
    {
        var match = plans.FirstOrDefault(x =>
            string.Equals((x.PlanId ?? string.Empty).Trim(), (planId ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase) &&
            ((!string.IsNullOrWhiteSpace(categoryLocalId) &&
              string.Equals((x.CategoryLocalId ?? string.Empty).Trim(), categoryLocalId.Trim(), StringComparison.OrdinalIgnoreCase)) ||
             string.Equals((x.RoomType ?? string.Empty).Trim(), (roomType ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase)));

        return match != null && match.NewRate > 0m ? match.NewRate : Math.Max(0m, fallback);
    }

    private static async Task UpdateMonthPaymentRowAsync(
        SqlConnection cn,
        SqlTransaction tx,
        string hotelId,
        string regId,
        int paymentId,
        DateTime newDeparture,
        int newNights,
        decimal newRate,
        decimal newGst,
        decimal newBed,
        decimal newTotal,
        CancellationToken ct)
    {
        await using var cmd = new SqlCommand(@"
UPDATE dbo.payments
SET DepartureDate=@departure,
    rate=@rate,
    charge=@rate,
    Nights=@nights,
    GST=@gst,
    Bed=@bed,
    totalamount=@total
WHERE ID=@id
  AND CONVERT(varchar(100),hotel_id)=@hotel
  AND CONVERT(varchar(100),reg_id)=@reg;", cn, tx);
        cmd.Parameters.Add("@departure", SqlDbType.VarChar, 20).Value = LegacyDate(newDeparture);
        cmd.Parameters.Add("@rate", SqlDbType.Decimal).Value = newRate;
        cmd.Parameters.Add("@nights", SqlDbType.Int).Value = newNights;
        cmd.Parameters.Add("@gst", SqlDbType.Decimal).Value = newGst;
        cmd.Parameters.Add("@bed", SqlDbType.Decimal).Value = newBed;
        cmd.Parameters.Add("@total", SqlDbType.Decimal).Value = newTotal;
        cmd.Parameters.Add("@id", SqlDbType.Int).Value = paymentId;
        cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 100).Value = hotelId;
        cmd.Parameters.Add("@reg", SqlDbType.VarChar, 100).Value = regId;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task UpdateMasterMonthDatesAsync(
        SqlConnection cn,
        SqlTransaction tx,
        string hotelId,
        string regId,
        DateTime arrival,
        DateTime oldDeparture,
        DateTime newDeparture,
        int newMonths,
        bool extended,
        CancellationToken ct)
    {
        await using (var cmd = new SqlCommand(@"
UPDATE dbo.GuestInformationLogTB
SET DepartureDate=@dep,
    totalnights=CONVERT(varchar(20),@months),
    extended=@extended,
    previousdate=CASE WHEN @extended=1 THEN @oldDep ELSE previousdate END
WHERE CONVERT(varchar(100),reg_id)=@reg
  AND CONVERT(varchar(100),hotel_id)=@hotel;", cn, tx))
        {
            cmd.Parameters.Add("@dep", SqlDbType.VarChar, 20).Value = LegacyDate(newDeparture);
            cmd.Parameters.Add("@months", SqlDbType.Int).Value = newMonths;
            cmd.Parameters.Add("@extended", SqlDbType.Int).Value = extended ? 1 : 0;
            cmd.Parameters.Add("@oldDep", SqlDbType.VarChar, 20).Value = LegacyDate(oldDeparture);
            cmd.Parameters.Add("@reg", SqlDbType.VarChar, 100).Value = regId;
            cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 100).Value = hotelId;
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await using (var cmd = new SqlCommand(@"
UPDATE dbo.NewReservationsTB
SET ArrivalDate=@arr,
    dept_date=@dep,
    totalnights=CONVERT(varchar(20),@months)
WHERE CONVERT(varchar(100),reg_id)=@reg
  AND CONVERT(varchar(100),hotel_id)=@hotel;", cn, tx))
        {
            cmd.Parameters.Add("@arr", SqlDbType.VarChar, 20).Value = LegacyDate(arrival);
            cmd.Parameters.Add("@dep", SqlDbType.VarChar, 20).Value = LegacyDate(newDeparture);
            cmd.Parameters.Add("@months", SqlDbType.Int).Value = newMonths;
            cmd.Parameters.Add("@reg", SqlDbType.VarChar, 100).Value = regId;
            cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 100).Value = hotelId;
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task RefreshPaymentSummaryAsync(
        SqlConnection cn,
        SqlTransaction tx,
        string hotelId,
        string regId,
        string userId,
        string userName,
        string ip,
        CancellationToken ct)
    {
        decimal grandTotal;
        await using (var sum = new SqlCommand(@"
SELECT ISNULL(SUM(TRY_CONVERT(decimal(18,2),NULLIF(LTRIM(RTRIM(CONVERT(varchar(100),totalamount))),''))),0)
FROM dbo.payments
WHERE CONVERT(varchar(100),reg_id)=@reg
  AND CONVERT(varchar(100),hotel_id)=@hotel;", cn, tx))
        {
            sum.Parameters.Add("@reg", SqlDbType.VarChar, 100).Value = regId;
            sum.Parameters.Add("@hotel", SqlDbType.VarChar, 100).Value = hotelId;
            grandTotal = Convert.ToDecimal(await sum.ExecuteScalarAsync(ct));
        }

        decimal paid = 0m;
        decimal security = 0m;
        int latestId = 0;
        await using (var get = new SqlCommand(@"
SELECT TOP (1)
    id,
    ISNULL(TRY_CONVERT(decimal(18,2),NULLIF(LTRIM(RTRIM(CONVERT(varchar(100),paid_amount))),'')),0) AS paid,
    ISNULL(TRY_CONVERT(decimal(18,2),NULLIF(LTRIM(RTRIM(CONVERT(varchar(100),room_security))),'')),0) AS security
FROM dbo.PaymentsUpdateTB WITH (UPDLOCK,ROWLOCK)
WHERE CONVERT(varchar(100),reg_id)=@reg
  AND CONVERT(varchar(100),hotel_id)=@hotel
ORDER BY TRY_CONVERT(datetime,currentdate) DESC,id DESC;", cn, tx))
        {
            get.Parameters.Add("@reg", SqlDbType.VarChar, 100).Value = regId;
            get.Parameters.Add("@hotel", SqlDbType.VarChar, 100).Value = hotelId;
            await using var rd = await get.ExecuteReaderAsync(ct);
            if (await rd.ReadAsync(ct))
            {
                latestId = Convert.ToInt32(rd["id"]);
                paid = Convert.ToDecimal(rd["paid"]);
                security = Convert.ToDecimal(rd["security"]);
            }
        }

        if (latestId <= 0) return;
        var payable = grandTotal + security;
        var remaining = Math.Max(0m, payable - paid);

        await using var upd = new SqlCommand(@"
UPDATE dbo.PaymentsUpdateTB
SET grand_total=@grand,
    payable=@payable,
    remaining_amount=@remaining,
    user_id=@user,
    systemUser=@systemUser,
    systemName=@systemName,
    ipAddress=@ip
WHERE id=@id;", cn, tx);
        upd.Parameters.Add("@grand", SqlDbType.Decimal).Value = grandTotal;
        upd.Parameters.Add("@payable", SqlDbType.Decimal).Value = payable;
        upd.Parameters.Add("@remaining", SqlDbType.Decimal).Value = remaining;
        upd.Parameters.Add("@user", SqlDbType.VarChar, 100).Value = userId ?? string.Empty;
        upd.Parameters.Add("@systemUser", SqlDbType.VarChar, 100).Value = userName ?? string.Empty;
        upd.Parameters.Add("@systemName", SqlDbType.VarChar, 100).Value = Environment.MachineName;
        upd.Parameters.Add("@ip", SqlDbType.VarChar, 100).Value = ip ?? string.Empty;
        upd.Parameters.Add("@id", SqlDbType.Int).Value = latestId;
        await upd.ExecuteNonQueryAsync(ct);
    }

    private static string PaymentStatus(decimal total, decimal paid)
    {
        if (total <= 0m) return string.Empty;
        if (paid <= 0m) return "Not Paid";
        if (paid + 0.005m >= total) return "Fully Paid";
        return "Partially Paid";
    }

    private static string NormalizeRegId(string value)
    {
        var regId = (value ?? string.Empty).Trim();
        if (regId.EndsWith("_R", StringComparison.OrdinalIgnoreCase))
            regId = regId[..^2].Trim();
        return regId;
    }

    private static int MonthDiffCeil(DateTime start, DateTime end)
    {
        start = start.Date;
        end = end.Date;
        if (end <= start) return 0;
        var months = ((end.Year - start.Year) * 12) + end.Month - start.Month;
        var anchor = AddMonthsSafe(start, months);
        if (anchor < end) months++;
        return Math.Max(months, 1);
    }

    private static DateTime AddMonthsSafe(DateTime date, int months)
    {
        var first = new DateTime(date.Year, date.Month, 1).AddMonths(months);
        return new DateTime(first.Year, first.Month, Math.Min(date.Day, DateTime.DaysInMonth(first.Year, first.Month)));
    }

    private static string LegacyDate(DateTime date) => date.ToString("MM-dd-yyyy", System.Globalization.CultureInfo.InvariantCulture);

    private static bool IsActiveReservationStatus(string value)
    {
        var s = (value ?? string.Empty).Trim().ToLowerInvariant();
        return s is "check in" or "checkin" or "reservation" or "provisional" or "booked" or "confirmed";
    }

    private sealed record PaymentAllocationRow(string RegId, decimal LineTotal, decimal PaidAmount);


    private sealed class SwapRow
    {
        public long PaymentId { get; init; }
        public string RoomNo { get; init; } = string.Empty;
        public string RoomType { get; init; } = string.Empty;
        public DateTime Arrival { get; init; }
        public DateTime Departure { get; init; }
    }

    private sealed class MonthPaymentRow
    {
        public int Id { get; set; }
        public string PlanId { get; set; } = string.Empty;
        public string RoomType { get; set; } = string.Empty;
        public string CategoryLocalId { get; set; } = string.Empty;
        public decimal Rate { get; set; }
        public decimal Gst { get; set; }
        public decimal Bed { get; set; }
        public int OldNights { get; set; }
        public string Description { get; set; } = string.Empty;
        public string ResStatus { get; set; } = string.Empty;
    }

}
