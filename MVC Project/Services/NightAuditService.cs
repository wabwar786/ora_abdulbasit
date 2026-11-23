using System;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Orapmshms.Models;

namespace Orapmshms.Services;

/// <summary>
/// Hotel-day audit: reads actual operational and ledger records without posting invoices,
/// editing payments or running night-close jobs. The only write is dated Online Ratings.
/// Works without a Program.cs registration (the controller constructs it directly).
/// </summary>
public sealed class NightAuditService
{
    private readonly string _connectionString;
    private readonly RoomWiseRevenueService _revenue;
    private readonly DashboardService _dashboard;

    public NightAuditService(IConfiguration config, DashboardService dashboard)
    {
        _connectionString = config.GetConnectionString("con")
            ?? throw new InvalidOperationException("Missing connection string 'con'.");
        _revenue = new RoomWiseRevenueService(config);
        _dashboard = dashboard;
    }

    private static SqlParameter Hotel(string hotel) => new("@hotel", SqlDbType.VarChar, 50) { Value = hotel };
    private static SqlParameter Day(DateTime date) => new("@day", SqlDbType.Date) { Value = date.Date };
    private static decimal MoneyValue(object? o)
    {
        if (o is null or DBNull) return 0;
        if (o is decimal d) return d;
        var v = Convert.ToString(o, CultureInfo.InvariantCulture) ?? "";
        v = v.Replace(",", "").Replace("£", "").Replace("$", "")
            .Replace("Rs.", "", StringComparison.OrdinalIgnoreCase).Trim();
        return decimal.TryParse(v, NumberStyles.Number | NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture, out var result) ? result : 0;
    }
    private static string S(SqlDataReader r, string name) => r[name] is DBNull ? "" : Convert.ToString(r[name])?.Trim() ?? "";
    private static decimal M(SqlDataReader r, string name) => MoneyValue(r[name]);
    private static int I(SqlDataReader r, string name) => r[name] is DBNull ? 0 : Convert.ToInt32(r[name], CultureInfo.InvariantCulture);

    public async Task<NightAuditViewModel> LoadAsync(string hotelId, DateTime reportDay,
        DateTime hotelToday, CancellationToken ct = default)
    {
        var model = new NightAuditViewModel { HotelId = hotelId, ReportDate = reportDay.Date };
        // Use the same trusted room-night, posted-extras and SAVED-GST logic as the
        // current Room-wise Revenue Report. No configured tax rates are used here.
        var revenue = new RoomWiseRevenueViewModel { From = reportDay.Date, To = reportDay.Date };
        await _revenue.LoadAsync(hotelId, revenue, ct);
        model.HotelName = revenue.HotelName;
        model.CurrencySign = revenue.CurrencySign;
        model.UnallocatedGross = revenue.ExcludedGross;

        // Keep extra-only / checkout-day rows as well as occupied room-night rows.
        // RoomWiseRevenueService has already grouped same-day extras once on one
        // saved room for the reservation; do not query/sum those extras again.
        foreach (var r in revenue.Rows
                     .Where(x => x.RoomNights > 0 || x.ExtraRevenue != 0 || x.ExtraVat != 0
                               || x.FnbRevenue != 0 || x.FnbVat != 0)
                     .OrderBy(x => x.RoomNo).ThenBy(x => x.ReservationNo))
        {
            model.Accommodation.Add(new NightAuditAccommodationLine
            {
                ReservationNo = r.ReservationNo, GuestName = r.GuestName,
                RoomNo = r.RoomNo, ArrivalDate = r.ArrivalDate,
                BookingSource = r.SourceName,
                RoomNet = r.RoomRevenue, RoomTax = r.RoomVat,
                ExtraNet = r.ExtraRevenue, ExtraTax = r.ExtraVat,
                FoodNet = r.FnbRevenue, FoodTax = r.FnbVat
            });
        }
        model.RoomsOccupied = revenue.Rows.Where(x => x.RoomNights > 0).Select(x => x.RoomNo)
            .Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Count();

        await using var cn = new SqlConnection(_connectionString);
        await cn.OpenAsync(ct);
        await ReadReservationMethodsAndBalances(cn, hotelId, model, ct);
        await ReadRefundableSecurity(cn, hotelId, reportDay.Date, model, ct);
        await ReadInventoryAndExpected(hotelId, reportDay.Date, hotelToday.Date, model, ct);
        // F&B comes from the same date-filtered RoomWiseRevenue rows shown above.
        // Do not independently add ReadFoodOrders() amounts to nightly totals.
        await ReadPaymentsAndRefunds(cn, hotelId, reportDay.Date, model, ct);
        await ReadDiscount(cn, hotelId, reportDay.Date, model, ct);
        await ReadNoShows(cn, hotelId, reportDay.Date, model, ct);
        await LoadRatings(cn, hotelId, model, ct);
        return model;
    }

    private static async Task ReadReservationMethodsAndBalances(SqlConnection cn, string hotel,
        NightAuditViewModel model, CancellationToken ct)
    {
        // GuestInformationLogTB takes precedence; use the latest payment update
        // as the current balance. Never multiply reservation-level unpaid amounts
        // when a reservation has two or more occupied rooms.
        const string sql = @"
;WITH Methods AS
(
 SELECT hotel_id,CONVERT(varchar(60),reg_id) RegId,
        NULLIF(LTRIM(RTRIM(CONVERT(nvarchar(100),payment_method))), '') Method,
        1 Priority, ID RowId FROM dbo.GuestInformationLogTB WHERE hotel_id=@hotel
 UNION ALL
 SELECT hotel_id,CONVERT(varchar(60),reg_id),
        NULLIF(LTRIM(RTRIM(CONVERT(nvarchar(100),payment_method))), ''),
        2, ID FROM dbo.NewReservationsTB WHERE hotel_id=@hotel
 UNION ALL
 SELECT hotel_id,CONVERT(varchar(60),reg_id),
        NULLIF(LTRIM(RTRIM(CONVERT(nvarchar(100),payment_method))), ''),
        3, id FROM dbo.PaymentsUpdateTB WHERE hotel_id=@hotel
), RankedMethods AS
(
 SELECT RegId,Method,ROW_NUMBER() OVER(PARTITION BY RegId
        ORDER BY CASE WHEN Method IS NULL THEN 1 ELSE 0 END,Priority,RowId DESC) rn
 FROM Methods
), RankedBalances AS
(
 SELECT CONVERT(varchar(60),reg_id) RegId,
        TRY_CONVERT(decimal(18,2),remaining_amount) Remaining,
        ROW_NUMBER() OVER(PARTITION BY reg_id ORDER BY id DESC) rn
 FROM dbo.PaymentsUpdateTB WHERE hotel_id=@hotel
)
SELECT DISTINCT m.RegId,m.Method,b.Remaining
FROM RankedMethods m LEFT JOIN RankedBalances b ON b.RegId=m.RegId AND b.rn=1
WHERE m.rn=1;";
        var map = new System.Collections.Generic.Dictionary<string, (string method, decimal? balance)>(StringComparer.OrdinalIgnoreCase);
        await using (var cmd = new SqlCommand(sql, cn) { CommandTimeout = 60 })
        {
            cmd.Parameters.Add(Hotel(hotel));
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                var id = S(rd, "RegId");
                if (id.Length > 0)
                    map[id] = (S(rd, "Method"), rd["Remaining"] is DBNull ? null : M(rd, "Remaining"));
            }
        }
        var marked = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in model.Accommodation)
        {
            if (!map.TryGetValue(row.ReservationNo, out var data)) continue;
            row.PaymentMethod = data.method;
            if (marked.Add(row.ReservationNo)) row.Unpaid = data.balance;
        }
        model.UnpaidBookings = model.Accommodation.Where(x => x.Unpaid > 0).Sum(x => x.Unpaid ?? 0);
        // A checkout-day extra-only row is not another occupied complimentary room.
        model.Complimentary = model.Accommodation.Count(x => x.RoomAmount != 0 && (
            x.PaymentMethod.Contains("complement", StringComparison.OrdinalIgnoreCase) ||
            x.PaymentMethod.Contains("compliment", StringComparison.OrdinalIgnoreCase) ||
            x.PaymentMethod.Contains("house use", StringComparison.OrdinalIgnoreCase)));
    }

    private static async Task ReadRefundableSecurity(SqlConnection cn, string hotel, DateTime day,
        NightAuditViewModel model, CancellationToken ct)
    {
        // Exactly the same balance convention used by CheckInService:
        // positive deposits less negative refunds/deductions in RoomSecurityTB.
        // It is keyed by reservation, not room, and MUST NOT be treated as
        // revenue or duplicated across multiple rooms of one reservation.
        if (model.Accommodation.Count == 0) return;
        var balances = new System.Collections.Generic.Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        await using (var cmd = new SqlCommand(@"
;WITH SecurityRows AS
(
 SELECT CONVERT(varchar(60),reg_id) AS RegId,
        COALESCE(TRY_CONVERT(decimal(18,2),security),0) AS Amount,
        COALESCE(TRY_CONVERT(datetime,currentdate,121),
                 TRY_CONVERT(datetime,currentdate,120),
                 TRY_CONVERT(datetime,currentdate,103),
                 TRY_CONVERT(datetime,currentdate,109),
                 TRY_CONVERT(datetime,currentdate,100),
                 TRY_CONVERT(datetime,currentdate,0)) AS RecordedAt
 FROM dbo.RoomSecurityTB WHERE hotel_id=@hotel
)
SELECT RegId,SUM(Amount) AS Balance FROM SecurityRows
WHERE CONVERT(date,RecordedAt)<=@day
GROUP BY RegId;", cn) { CommandTimeout = 45 })
        {
            cmd.Parameters.Add(Hotel(hotel));
            cmd.Parameters.Add(Day(day));
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                var reg = S(rd, "RegId");
                if (reg.Length > 0) balances[reg] = Math.Max(0, M(rd, "Balance"));
            }
        }
        var assigned = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in model.Accommodation
            .Where(x => x.RoomAmount != 0)
            .OrderBy(x => x.ReservationNo, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.RoomNo, StringComparer.OrdinalIgnoreCase))
        {
            if (assigned.Add(row.ReservationNo) &&
                balances.TryGetValue(row.ReservationNo, out var balance))
                row.RefundableSecurity = balance;
        }
    }

    private async Task ReadInventoryAndExpected(string hotel, DateTime reportDay,
        DateTime hotelToday, NightAuditViewModel model, CancellationToken ct)
    {
        // Physical rooms, active blocks, arrivals and in-house occupancy are now
        // calculated by DashboardService's EXACT SAME queries for each hotel-day.
        // Audit-night SOLD room-nights are still from RoomWiseRevenueService; do
        // not confuse them with currently checked-in guests or future bookings.
        var auditRoomsTask = _dashboard.GetRoomStatusAsync(hotel, reportDay, ct);
        var nextDay = reportDay.AddDays(1);
        var nextRoomsTask = _dashboard.GetRoomStatusAsync(hotel, nextDay, ct);
        var nextStaysTask = _dashboard.GetReservedRoomStaysAsync(hotel, nextDay, ct);
        var arrivalsTask = _dashboard.GetStatisticDetailsAsync(hotel, "checkin", nextDay, ct);
        var checkoutsTask = _dashboard.GetStatisticDetailsAsync(hotel, "checkout", nextDay, ct);
        await Task.WhenAll(auditRoomsTask, nextRoomsTask, nextStaysTask, arrivalsTask, checkoutsTask);
        var auditRooms = await auditRoomsTask;
        var nextRooms = await nextRoomsTask;
        var nextNightRooms = DashboardService.CollectOvernightRoomNumbers(nextRooms, await nextStaysTask, nextDay);
        model.OutOfServiceRooms = auditRooms.Count(r => r.State == "ooo");
        model.TotalRoomsAvailable = auditRooms.Count - model.OutOfServiceRooms.Value;
        model.AvailableRoomsReportDay = auditRooms.Count(r => r.State is "vacant" or "dirty" or "checkout");
        model.ActualInHouseReportDay = auditRooms.Count(r => r.State == "checkin");
        model.ArrivalsNextDay = (await arrivalsTask).Rows.Count;
        model.CompletedCheckoutsNextDay = (await checkoutsTask).Rows.Count;
        model.DeparturesNextDay = nextRooms.Count(r => r.State == "checkin" && r.DepartureDate?.Date == nextDay);
        model.InHouseNextDay = nextRooms.Count(r => r.State == "checkin");
        // ACTUAL occupancy is checked-in room-nights ONLY. Do not forecast
        // occupancy using outstanding reservations, or retain guests whose
        // scheduled departure is on the next day itself.
        model.ActualOccupiedNextDay = nextNightRooms.CheckedIn.Count;
        model.SellableNextDay = nextRooms.Count(r => r.State != "ooo");
        model.BlockedNextDay = nextRooms.Count(r => r.State == "ooo");
        model.AvailableNextDay = Math.Max(0, model.SellableNextDay - nextNightRooms.Booked.Count);
        // Available next night still excludes future reserved rooms; actual
        // occupancy excludes them because they have not checked in.
    }

    private static async Task ReadFoodOrders(SqlConnection cn, string hotel, DateTime day,
        NightAuditViewModel model, CancellationToken ct)
    {
        // Restaurant posts have descr=Extras and PaymentMethod=Extra/Extras.
        // Group by source row, not reservation; each voucher is shown once.
        // No inference of VAT from a tax rate.
        await using var cmd = new SqlCommand(@"
;WITH E AS
(
 SELECT p.ID,p.reg_id,ISNULL(NULLIF(LTRIM(RTRIM(CONVERT(varchar(80),p.room_no))),''),'') RoomNo,
        ISNULL(NULLIF(LTRIM(RTRIM(CONVERT(varchar(80),p.VoucherNumber))),''),'') VoucherNo,
        ISNULL(NULLIF(LTRIM(RTRIM(CONVERT(varchar(80),p.payment_status))),''),'') [Status],
        COALESCE(TRY_CONVERT(date,p.currentdate,103),TRY_CONVERT(date,p.currentdate,110),
                 TRY_CONVERT(date,p.currentdate)) Posted,
        COALESCE(TRY_CONVERT(decimal(18,2),p.totalamount),0) TotalAmt,
        COALESCE(TRY_CONVERT(decimal(18,2),p.Charge),0) ChargeAmt,
        COALESCE(TRY_CONVERT(decimal(18,2),p.GST),0) Tax,
        COALESCE(TRY_CONVERT(decimal(18,2),p.Bed),0) BedTax
 FROM dbo.payments p WHERE p.hotel_id=@hotel
   AND LOWER(LTRIM(RTRIM(CONVERT(varchar(100),p.descr))))='extras'
   AND LOWER(LTRIM(RTRIM(ISNULL(CONVERT(varchar(80),p.PaymentMethod),'')))) IN ('extra','extras')
), F AS
(
 SELECT e.*,CASE WHEN TotalAmt=0 AND ChargeAmt<>0 THEN ChargeAmt ELSE TotalAmt END Gross
 FROM E e WHERE Posted=@day
)
SELECT f.reg_id,COALESCE(NULLIF(f.RoomNo,''),room.RoomNo,NULLIF(f.VoucherNo,''),'Unassigned') RoomOrBill,
       CASE WHEN LOWER(f.[Status]) IN ('paid','posted','cash','card') THEN f.[Status]
            WHEN LOWER(f.[Status])='unpaid' THEN 'Posted to folio'
            ELSE 'Posted to folio' END PaymentMode,
       f.Gross-f.Tax-f.BedTax BeforeTax,f.Tax+f.BedTax Tax,f.[Status]
FROM F f
OUTER APPLY
(
 SELECT TOP(1) CONVERT(varchar(100),p.room_no) RoomNo
 FROM dbo.payments p
 WHERE p.hotel_id=@hotel AND p.reg_id=f.reg_id
   AND LOWER(LTRIM(RTRIM(CONVERT(varchar(100),p.descr))))='room rent'
   AND NULLIF(LTRIM(RTRIM(CONVERT(varchar(100),p.room_no))),'') IS NOT NULL
 ORDER BY p.ID
) room
ORDER BY RoomOrBill,f.ID;", cn) { CommandTimeout = 60 };
        cmd.Parameters.Add(Hotel(hotel)); cmd.Parameters.Add(Day(day));
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            model.FoodOrders.Add(new NightAuditOrderLine
            {
                ReservationNo = S(rd, "reg_id"), RoomOrBill = S(rd, "RoomOrBill"),
                PaymentMode = S(rd, "PaymentMode"), BeforeTax = M(rd, "BeforeTax"), Tax = M(rd, "Tax")
            });
            if (S(rd, "Status").Equals("unpaid", StringComparison.OrdinalIgnoreCase))
                model.UnpaidFood += M(rd, "BeforeTax") + M(rd, "Tax");
        }
    }

    private static async Task ReadPaymentsAndRefunds(SqlConnection cn, string hotel, DateTime day,
        NightAuditViewModel model, CancellationToken ct)
    {
        // Shared ledger SQL is also used by Dashboard top cards and drill-downs.
        // The receipts list is gross-positive; refunds are separately disclosed.
        // NET RECEIVED = gross receipts - refunds, exactly Dashboard RevenueToday.
        var sql = HotelPaymentLedgerSql.Prefix + @"
SELECT method AS Method,
       CAST(SUM(CASE WHEN amount>0 THEN amount ELSE 0 END) AS decimal(18,2)) AS Received,
       CAST(SUM(CASE WHEN amount<0 THEN ABS(amount) ELSE 0 END) AS decimal(18,2)) AS Refunded,
       SUM(CASE WHEN amount<0 THEN 1 ELSE 0 END) AS RefundCount
FROM PaymentLedger WHERE tx_date=@day GROUP BY method ORDER BY method;";
        await using var cmd = new SqlCommand(sql, cn) { CommandTimeout = 60 };
        cmd.Parameters.Add(Hotel(hotel)); cmd.Parameters.Add(Day(day));
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            var method = S(rd, "Method");
            var received = M(rd, "Received");
            if (received != 0) model.Received.Add(new NightAuditPaymentLine { Method = method, Amount = received });
            var refunded = M(rd, "Refunded");
            var count = I(rd, "RefundCount");
            if (method.Contains("cash", StringComparison.OrdinalIgnoreCase))
            { model.RefundCash += refunded; model.RefundCashCount += count; }
            else { model.RefundCard += refunded; model.RefundCardCount += count; }
        }
    }

    private static async Task ReadDiscount(SqlConnection cn, string hotel, DateTime day,
        NightAuditViewModel model, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(@"
;WITH R AS
(
 SELECT COALESCE(TRY_CONVERT(date,ArrivalDate,110),TRY_CONVERT(date,ArrivalDate,103),
                 TRY_CONVERT(date,ArrivalDate)) Arr,
        COALESCE(TRY_CONVERT(date,DepartureDate,110),TRY_CONVERT(date,DepartureDate,103),
                 TRY_CONVERT(date,DepartureDate)) Dep,
        COALESCE(TRY_CONVERT(decimal(18,2),discount),0) Disc
 FROM dbo.payments WHERE hotel_id=@hotel
   AND LOWER(LTRIM(RTRIM(CONVERT(varchar(100),descr))))='room rent'
   AND LOWER(ISNULL(CONVERT(varchar(80),res_status),'')) NOT IN
          ('cancel','cancelled','canceled','no show','noshow','no-show')
)
SELECT ISNULL(SUM(CASE WHEN Arr<=@day AND Dep>@day AND DATEDIFF(day,Arr,Dep)>0
       THEN ROUND(Disc*(DATEDIFF(day,Arr,@day)+1)/DATEDIFF(day,Arr,Dep),2)
          - ROUND(Disc*DATEDIFF(day,Arr,@day)/DATEDIFF(day,Arr,Dep),2)
       ELSE 0 END),0) DiscountForDay FROM R;", cn) { CommandTimeout = 60 };
        cmd.Parameters.Add(Hotel(hotel)); cmd.Parameters.Add(Day(day));
        model.BookingDiscount = MoneyValue(await cmd.ExecuteScalarAsync(ct));
    }

    private static async Task ReadNoShows(SqlConnection cn, string hotel, DateTime day,
        NightAuditViewModel model, CancellationToken ct)
    {
        // NoShowTB copies reservation dates, not the actual no-show event date.
        // Count dated audit actions only; never invent a no-show timestamp.
        await using var cmd = new SqlCommand(@"
SELECT COUNT(DISTINCT CONVERT(varchar(60),reg_id)) FROM dbo.Reserv_log
WHERE hotel_id=@hotel
  AND (LOWER(ISNULL(CONVERT(varchar(120),status),'')) LIKE '%no show%'
       OR LOWER(ISNULL(CONVERT(varchar(120),status),'')) LIKE '%noshow%')
  AND COALESCE(TRY_CONVERT(date,createdat),TRY_CONVERT(date,[date],23),
               TRY_CONVERT(date,[date],103))=@day;", cn) { CommandTimeout = 30 };
        cmd.Parameters.Add(Hotel(hotel)); cmd.Parameters.Add(Day(day));
        model.NoShows = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct) ?? 0, CultureInfo.InvariantCulture);
    }

    private static async Task LoadRatings(SqlConnection cn, string hotel, NightAuditViewModel model,
        CancellationToken ct)
    {
        await using (var installed = new SqlCommand(
            "SELECT CASE WHEN OBJECT_ID(N'dbo.NightAuditRatingsTB',N'U') IS NULL THEN 0 ELSE 1 END", cn))
            model.RatingsTableReady = Convert.ToInt32(await installed.ExecuteScalarAsync(ct) ?? 0) == 1;
        if (!model.RatingsTableReady)
        {
            model.Info = "Online Ratings storage is not installed. Run Sql/NightAuditRatings_Install.sql first.";
            return;
        }
        await using var cmd = new SqlCommand(@"
SELECT booking_rating,google_rating,trip_rating,agoda_rating,updated_at_utc,updated_by
FROM dbo.NightAuditRatingsTB WHERE hotel_id=@hotel AND report_date=@day;", cn);
        cmd.Parameters.Add(Hotel(hotel)); cmd.Parameters.Add(Day(model.ReportDate));
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (await rd.ReadAsync(ct))
        {
            model.Ratings.Booking = rd["booking_rating"] is DBNull ? null : M(rd, "booking_rating");
            model.Ratings.Google = rd["google_rating"] is DBNull ? null : M(rd, "google_rating");
            model.Ratings.Trip = rd["trip_rating"] is DBNull ? null : M(rd, "trip_rating");
            model.Ratings.Agoda = rd["agoda_rating"] is DBNull ? null : M(rd, "agoda_rating");
            model.Ratings.LastSavedAtUtc = rd["updated_at_utc"] is DBNull ? null : Convert.ToDateTime(rd["updated_at_utc"]);
            model.Ratings.LastSavedBy = S(rd, "updated_by");
        }
    }

    public async Task SaveRatingsAsync(string hotel, NightAuditRatingInput input, string user,
        CancellationToken ct = default)
    {
        // Server-side validation is mandatory. Null means not yet recorded.
        if (input.ReportDate.Year < 2000 || input.ReportDate.Year > 2100)
            throw new ArgumentException("Invalid report date.");
        static bool InRange(decimal? x, decimal max) => !x.HasValue || (x >= 0 && x <= max);
        if (!InRange(input.Booking, 10) || !InRange(input.Google, 5) ||
            !InRange(input.Trip, 10) || !InRange(input.Agoda, 10))
            throw new ArgumentException("Booking/Trip/Agoda must be 0–10 and Google 0–5.");

        await using var cn = new SqlConnection(_connectionString);
        await cn.OpenAsync(ct);
        await using var tx = (SqlTransaction)await cn.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        try
        {
            await using var cmd = new SqlCommand(@"
IF OBJECT_ID(N'dbo.NightAuditRatingsTB',N'U') IS NULL
    THROW 51050, 'NightAuditRatingsTB is missing. Run NightAuditRatings_Install.sql.', 1;
UPDATE dbo.NightAuditRatingsTB WITH (UPDLOCK,SERIALIZABLE)
 SET booking_rating=@booking,google_rating=@google,trip_rating=@trip,agoda_rating=@agoda,
     updated_at_utc=SYSUTCDATETIME(),updated_by=@user
 WHERE hotel_id=@hotel AND report_date=@day;
IF @@ROWCOUNT=0
    INSERT INTO dbo.NightAuditRatingsTB
      (hotel_id,report_date,booking_rating,google_rating,trip_rating,agoda_rating,updated_by)
    VALUES (@hotel,@day,@booking,@google,@trip,@agoda,@user);", cn, tx);
            cmd.Parameters.Add(Hotel(hotel)); cmd.Parameters.Add(Day(input.ReportDate));
            cmd.Parameters.Add("@booking", SqlDbType.Decimal).Value = (object?)input.Booking ?? DBNull.Value;
            cmd.Parameters.Add("@google", SqlDbType.Decimal).Value = (object?)input.Google ?? DBNull.Value;
            cmd.Parameters.Add("@trip", SqlDbType.Decimal).Value = (object?)input.Trip ?? DBNull.Value;
            cmd.Parameters.Add("@agoda", SqlDbType.Decimal).Value = (object?)input.Agoda ?? DBNull.Value;
            cmd.Parameters.Add("@user", SqlDbType.NVarChar, 100).Value = user.Length > 100 ? user[..100] : user;
            foreach (var name in new[] { "@booking", "@google", "@trip", "@agoda" })
            { cmd.Parameters[name].Precision = 4; cmd.Parameters[name].Scale = 2; }
            await cmd.ExecuteNonQueryAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch
        {
            try { await tx.RollbackAsync(CancellationToken.None); } catch { /* original exception retained */ }
            throw;
        }
    }
}
