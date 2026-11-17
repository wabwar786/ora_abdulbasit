using System;
using System.Data;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Orapmshms.Models;

namespace Orapmshms.Services;

/// <summary>
/// Read-only room-level revenue using dbo.payments actual saved tax columns.
/// It does not change reservations, tax settings, payments, or reporting data.
/// Standalone service, instantiated by its MVC controller (no Program.cs edit).
/// </summary>
public sealed class RoomWiseRevenueService
{
    private readonly string _connectionString;

    public RoomWiseRevenueService(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("con")
            ?? throw new InvalidOperationException("Connection string 'con' is missing.");
    }

    // Room revenue and saved room taxes are allocated across occupied nights
    // [arrival, departure). Extras and saved extra taxes are recognised only
    // on payments.currentdate, even when the room has no occupied night that day.
    // All extras for one reservation/posting date are summed on one saved room.
    // Room selection uses the reservation's complete room history, regardless of
    // when each room record was created, occupied, checked out or recorded.
    // Extras with no room anywhere on their reservation require review;
    // no GST/Bed amounts are estimated from configured tax rates.
    private const string ReportSql = @"
;WITH SourceCandidates AS
(
    SELECT hotel_id,
           NULLIF(LTRIM(RTRIM(CONVERT(varchar(50),reg_id))), '') AS RegId,
           CASE WHEN NULLIF(LTRIM(RTRIM(CONVERT(nvarchar(150),agency))),'') IS NULL
                  OR LOWER(REPLACE(LTRIM(RTRIM(CONVERT(nvarchar(150),agency))),' ','')) IN ('--select--','select')
                THEN 'Walk In' ELSE LTRIM(RTRIM(CONVERT(nvarchar(150),agency))) END AS SourceName,
           CASE WHEN NULLIF(LTRIM(RTRIM(CONVERT(nvarchar(150),agency))),'') IS NULL
                  OR LOWER(REPLACE(LTRIM(RTRIM(CONVERT(nvarchar(150),agency))),' ','')) IN ('--select--','select')
                THEN 1 ELSE 0 END AS DefaultSource,
           1 AS TablePriority
    FROM dbo.GuestInformationLogTB WHERE hotel_id=@hotel
    UNION ALL
    SELECT hotel_id,
           NULLIF(LTRIM(RTRIM(CONVERT(varchar(50),reg_id))), ''),
           CASE WHEN NULLIF(LTRIM(RTRIM(CONVERT(nvarchar(150),agency))),'') IS NULL
                  OR LOWER(REPLACE(LTRIM(RTRIM(CONVERT(nvarchar(150),agency))),' ','')) IN ('--select--','select')
                THEN 'Walk In' ELSE LTRIM(RTRIM(CONVERT(nvarchar(150),agency))) END,
           CASE WHEN NULLIF(LTRIM(RTRIM(CONVERT(nvarchar(150),agency))),'') IS NULL
                  OR LOWER(REPLACE(LTRIM(RTRIM(CONVERT(nvarchar(150),agency))),' ','')) IN ('--select--','select')
                THEN 1 ELSE 0 END,
           2
    FROM dbo.NewReservationsTB WHERE hotel_id=@hotel
), SourceRanked AS
(
    SELECT *, ROW_NUMBER() OVER
        (PARTITION BY hotel_id,RegId ORDER BY DefaultSource, TablePriority, SourceName) AS rn
    FROM SourceCandidates WHERE RegId IS NOT NULL
), SourceOne AS
(
    SELECT hotel_id,RegId,SourceName FROM SourceRanked WHERE rn=1
), GuestCandidates AS
(
    SELECT hotel_id,
           NULLIF(LTRIM(RTRIM(CONVERT(varchar(50),reg_id))), '') AS RegId,
           NULLIF(LTRIM(RTRIM(CONCAT(ISNULL(GuestName,''),' ',ISNULL(LastName,'')))), '') AS GuestName,
           1 AS Priority
    FROM dbo.GuestInformationLogTB WHERE hotel_id=@hotel
    UNION ALL
    SELECT hotel_id,
           NULLIF(LTRIM(RTRIM(CONVERT(varchar(50),reg_id))), ''),
           NULLIF(LTRIM(RTRIM(CONCAT(ISNULL(GuestName,''),' ',ISNULL(LastName,'')))), ''),
           2
    FROM dbo.NewReservationsTB WHERE hotel_id=@hotel
), GuestRanked AS
(
    SELECT *, ROW_NUMBER() OVER
      (PARTITION BY hotel_id,RegId
       ORDER BY CASE WHEN GuestName IS NULL THEN 1 ELSE 0 END,Priority,GuestName) AS rn
    FROM GuestCandidates WHERE RegId IS NOT NULL
), GuestOne AS
(
    SELECT hotel_id,RegId,GuestName FROM GuestRanked WHERE rn=1
), Raw AS
(
    SELECT p.ID,p.hotel_id,
        NULLIF(LTRIM(RTRIM(CONVERT(varchar(50),p.reg_id))), '') AS RegId,
        ISNULL(NULLIF(LTRIM(RTRIM(CONVERT(varchar(100),p.room_no))),''),'') AS RoomNo,
        ISNULL(NULLIF(LTRIM(RTRIM(CONVERT(varchar(150),p.descr))),''),'') AS Descr,
        ISNULL(NULLIF(LTRIM(RTRIM(CONVERT(varchar(120),p.[Type]))),''),'') AS ChargeType,
        LOWER(LTRIM(RTRIM(ISNULL(CONVERT(varchar(50),p.res_status),'')))) AS StayStatus,
        COALESCE(NULLIF(LTRIM(RTRIM(CONVERT(nvarchar(200),p.guestname))),''),g.GuestName,'') AS GuestName,
        COALESCE(s.SourceName,'Walk In') AS SourceName,
        COALESCE(TRY_CONVERT(date,p.ArrivalDate,110),TRY_CONVERT(date,p.ArrivalDate,23),
                 TRY_CONVERT(date,p.ArrivalDate,103),TRY_CONVERT(date,p.ArrivalDate)) AS ArrivalDt,
        COALESCE(TRY_CONVERT(date,p.DepartureDate,110),TRY_CONVERT(date,p.DepartureDate,23),
                 TRY_CONVERT(date,p.DepartureDate,103),TRY_CONVERT(date,p.DepartureDate)) AS DepartureDt,
        COALESCE(TRY_CONVERT(date,p.currentdate,103),TRY_CONVERT(date,p.currentdate,110),
                 TRY_CONVERT(date,p.currentdate)) AS PostedDt,
        COALESCE(TRY_CONVERT(decimal(18,2),REPLACE(REPLACE(REPLACE(REPLACE(
            CONVERT(nvarchar(80),p.Rate),',',''),N'£',''),N'$',''),N'Rs.','')),0) AS RateAmt,
        COALESCE(TRY_CONVERT(decimal(18,2),REPLACE(REPLACE(REPLACE(REPLACE(
            CONVERT(nvarchar(80),p.discount),',',''),N'£',''),N'$',''),N'Rs.','')),0) AS DiscountAmt,
        COALESCE(TRY_CONVERT(decimal(18,2),REPLACE(REPLACE(REPLACE(REPLACE(
            CONVERT(nvarchar(80),p.GST),',',''),N'£',''),N'$',''),N'Rs.','')),0) AS GstAmt,
        COALESCE(TRY_CONVERT(decimal(18,2),REPLACE(REPLACE(REPLACE(REPLACE(
            CONVERT(nvarchar(80),p.Bed),',',''),N'£',''),N'$',''),N'Rs.','')),0) AS BedAmt,
        COALESCE(TRY_CONVERT(decimal(18,2),REPLACE(REPLACE(REPLACE(REPLACE(
            CONVERT(nvarchar(80),p.totalamount),',',''),N'£',''),N'$',''),N'Rs.','')),0) AS TotalAmt,
        COALESCE(TRY_CONVERT(decimal(18,2),REPLACE(REPLACE(REPLACE(REPLACE(
            CONVERT(nvarchar(80),p.Charge),',',''),N'£',''),N'$',''),N'Rs.','')),0) AS ChargeAmt
    FROM dbo.payments p
    LEFT JOIN GuestOne g ON g.hotel_id=p.hotel_id
                        AND g.RegId=NULLIF(LTRIM(RTRIM(CONVERT(varchar(50),p.reg_id))), '')
    LEFT JOIN SourceOne s ON s.hotel_id=p.hotel_id
                         AND s.RegId=NULLIF(LTRIM(RTRIM(CONVERT(varchar(50),p.reg_id))), '')
    WHERE p.hotel_id=@hotel AND (@source='' OR COALESCE(s.SourceName,'Walk In')=@source)
), RoomCandidates AS
(
    SELECT r.*,DATEDIFF(day,r.ArrivalDt,r.DepartureDt) AS ActualNights
    FROM Raw r
    WHERE LOWER(r.Descr)='room rent'
      AND r.RegId IS NOT NULL
      AND r.ArrivalDt IS NOT NULL AND r.DepartureDt IS NOT NULL
      AND r.DepartureDt>r.ArrivalDt
      AND r.ArrivalDt<=@to AND r.DepartureDt>@from
      AND r.StayStatus NOT IN ('cancel','cancelled','canceled','no show','noshow','no-show')
), ValidRooms AS
(
    SELECT * FROM RoomCandidates
    WHERE RoomNo<>'' AND UPPER(RoomNo) NOT IN
      ('UNASSIGNED','(UNASSIGNED ROOM)','UNKNOWN','N/A','NA','-')
), DaySeries AS
(
    SELECT @from AS StayDate
    UNION ALL SELECT DATEADD(day,1,StayDate) FROM DaySeries WHERE StayDate<@to
), NightsAllocated AS
(
    SELECT rr.RegId,rr.RoomNo,rr.GuestName,rr.SourceName,ds.StayDate,
        rr.ArrivalDt,rr.DepartureDt,
        CAST(ROUND((rr.RateAmt-rr.DiscountAmt)*idx.DayIndex/rr.ActualNights,2)
           - ROUND((rr.RateAmt-rr.DiscountAmt)*(idx.DayIndex-1)/rr.ActualNights,2) AS decimal(18,2)) AS RoomRevenue,
        CAST(ROUND(rr.GstAmt*idx.DayIndex/rr.ActualNights,2)
           - ROUND(rr.GstAmt*(idx.DayIndex-1)/rr.ActualNights,2) AS decimal(18,2)) AS RoomVat,
        CAST(ROUND(rr.BedAmt*idx.DayIndex/rr.ActualNights,2)
           - ROUND(rr.BedAmt*(idx.DayIndex-1)/rr.ActualNights,2) AS decimal(18,2)) AS BedTax
    FROM ValidRooms rr
    INNER JOIN DaySeries ds ON rr.ArrivalDt<=ds.StayDate AND ds.StayDate<rr.DepartureDt
    CROSS APPLY (SELECT DATEDIFF(day,rr.ArrivalDt,ds.StayDate)+1 AS DayIndex) idx
), RoomTotals AS
(
    -- One row per occupied night: a single-date report shows only that night.
    SELECT RegId,RoomNo,SourceName,StayDate AS RevenueDate,
           MAX(GuestName) AS GuestName,
           MIN(ArrivalDt) AS ArrivalDt,MAX(DepartureDt) AS DepartureDt,
           COUNT(DISTINCT StayDate) AS RoomNights,
           CAST(SUM(RoomRevenue) AS decimal(18,2)) AS RoomRevenue,
           CAST(SUM(RoomVat) AS decimal(18,2)) AS RoomVat,
           CAST(SUM(BedTax) AS decimal(18,2)) AS BedTax
    FROM NightsAllocated
    GROUP BY RegId,RoomNo,SourceName,StayDate
), KnownRooms AS
(
    -- Select from ALL saved room charges on the reservation, with no date
    -- filter on room creation, stay dates or checkout. This supports extras
    -- posted long after departure as well as extras posted before check-in.
    SELECT DISTINCT RegId,RoomNo
    FROM Raw
    WHERE LOWER(Descr)='room rent' AND RegId IS NOT NULL
      AND RoomNo<>'' AND RoomNo NOT LIKE '%,%'
      AND UPPER(RoomNo) NOT IN
          ('UNASSIGNED','(UNASSIGNED ROOM)','UNKNOWN','N/A','NA','-')
), ExtraCandidates AS
(
    SELECT r.*,
           CASE WHEN r.TotalAmt=0 AND r.ChargeAmt<>0
                THEN r.ChargeAmt ELSE r.TotalAmt END AS ExtraGross
    FROM Raw r
    WHERE LOWER(r.Descr)<>'room rent'
      AND LOWER(r.Descr) NOT IN ('refund','room security','security deposit','security refund')
      AND LOWER(r.ChargeType) NOT IN ('refund','discount offers')
      AND r.RegId IS NOT NULL
      -- Extras are recognised ONLY on their actual posting date.
      AND r.PostedDt BETWEEN @from AND @to
      AND r.StayStatus NOT IN ('cancel','cancelled','canceled','no show','noshow','no-show')
      AND (r.TotalAmt<>0 OR r.ChargeAmt<>0 OR r.GstAmt<>0 OR r.BedAmt<>0)
), ExtraDaily AS
(
    -- Sum all extra entries ONCE per reservation and posting date, irrespective
    -- of each extra's saved room_no. This avoids double-counting or dropping
    -- room-less extras when the booking contains multiple rooms.
    SELECT RegId,PostedDt AS RevenueDate,
           MAX(GuestName) AS GuestName,MAX(SourceName) AS SourceName,
           CAST(SUM(ExtraGross-GstAmt-BedAmt) AS decimal(18,2)) AS ExtraRevenue,
           CAST(SUM(GstAmt) AS decimal(18,2)) AS ExtraVat,
           CAST(SUM(BedAmt) AS decimal(18,2)) AS ExtraBedTax,
           CAST(SUM(ExtraGross) AS decimal(18,2)) AS ExtraGrossTotal
    FROM ExtraCandidates
    GROUP BY RegId,PostedDt
), ExtraMapped AS
(
    SELECT d.*,
           target.RoomNo AS MappedRoomNo
    FROM ExtraDaily d
    OUTER APPLY
    (
        -- Select ONE room belonging to this reservation, independent of
        -- its creation, arrival, departure, check-in or checkout date.
        -- If an extra explicitly references a valid room in the reservation,
        -- prefer it. Otherwise choose a deterministic saved room number.
        -- No condition compares a room's dates with the extra posting date.
        -- All same-day extras are already summed in ExtraDaily, so their
        -- combined amount is assigned exactly once to this selected room.
        SELECT TOP (1) kr.RoomNo
        FROM KnownRooms kr
        WHERE kr.RegId=d.RegId
        ORDER BY
            CASE WHEN EXISTS
              (SELECT 1 FROM ExtraCandidates ec
               WHERE ec.RegId=d.RegId AND ec.PostedDt=d.RevenueDate
                 AND ec.RoomNo=kr.RoomNo)
                 THEN 0 ELSE 1 END,
            CASE WHEN TRY_CONVERT(int,kr.RoomNo) IS NULL THEN 1 ELSE 0 END,
            TRY_CONVERT(int,kr.RoomNo), kr.RoomNo
    ) target
), ExtraTotals AS
(
    SELECT RegId,MappedRoomNo AS RoomNo,RevenueDate,GuestName,SourceName,
           ExtraRevenue,ExtraVat,ExtraBedTax
    FROM ExtraMapped
    WHERE MappedRoomNo IS NOT NULL
), ExtraTotalsWithStay AS
(
    -- An extra can be entered after departure. Find the corresponding room stay
    -- using this reservation + selected room. Dates here are used only to
    -- DISPLAY the stay period, never to determine whether an extra is included.
    SELECT et.*, stay.ArrivalDt,stay.DepartureDt
    FROM ExtraTotals et
    OUTER APPLY
    (
        SELECT TOP (1) rr.ArrivalDt,rr.DepartureDt
        FROM Raw rr
        WHERE rr.RegId=et.RegId AND rr.RoomNo=et.RoomNo
          AND LOWER(rr.Descr)='room rent'
          AND rr.ArrivalDt IS NOT NULL AND rr.DepartureDt>rr.ArrivalDt
        ORDER BY CASE
            WHEN et.RevenueDate>=rr.ArrivalDt AND et.RevenueDate<=rr.DepartureDt THEN 0
            WHEN rr.ArrivalDt<=et.RevenueDate THEN 1
            ELSE 2 END,
            ABS(DATEDIFF(day,rr.ArrivalDt,et.RevenueDate)),rr.ID DESC
    ) stay
), RoomResults AS
(
    -- FULL JOIN is intentional: a room can have an extra posted on a day when
    -- it has zero room nights. Do not discard that legitimate posted revenue.
    SELECT COALESCE(rt.RegId,et.RegId) AS RegId,
           COALESCE(rt.RoomNo,et.RoomNo) AS RoomNo,
           COALESCE(rt.RevenueDate,et.RevenueDate) AS RevenueDate,
           COALESCE(rt.ArrivalDt,et.ArrivalDt) AS ArrivalDt,
           COALESCE(rt.DepartureDt,et.DepartureDt) AS DepartureDt,
           COALESCE(rt.GuestName,et.GuestName,'') AS GuestName,
           COALESCE(rt.SourceName,et.SourceName,'Walk In') AS SourceName,
           ISNULL(rt.RoomNights,0) AS RoomNights,
           CAST(ISNULL(rt.RoomRevenue,0) AS decimal(18,2)) AS RoomRevenue,
           CAST(ISNULL(rt.RoomVat,0)+ISNULL(rt.BedTax,0) AS decimal(18,2)) AS RoomVat,
           CAST(ISNULL(et.ExtraRevenue,0) AS decimal(18,2)) AS ExtraRevenue,
           CAST(ISNULL(et.ExtraVat,0)+ISNULL(et.ExtraBedTax,0) AS decimal(18,2)) AS ExtraVat
    FROM RoomTotals rt
    FULL OUTER JOIN ExtraTotalsWithStay et
      ON et.RegId=rt.RegId AND et.RoomNo=rt.RoomNo
     AND et.RevenueDate=rt.RevenueDate
), BadRoomDaily AS
(
    SELECT rr.RegId,rr.RoomNo,rr.GuestName,rr.SourceName,
           rr.ArrivalDt,rr.DepartureDt,ds.StayDate AS RevenueDate,
      CAST(ROUND((rr.RateAmt-rr.DiscountAmt+rr.GstAmt+rr.BedAmt)*idx.DayIndex/rr.ActualNights,2)
         - ROUND((rr.RateAmt-rr.DiscountAmt+rr.GstAmt+rr.BedAmt)*(idx.DayIndex-1)/rr.ActualNights,2) AS decimal(18,2)) AS ExcludedGross
    FROM RoomCandidates rr
    INNER JOIN DaySeries ds ON rr.ArrivalDt<=ds.StayDate AND ds.StayDate<rr.DepartureDt
    CROSS APPLY (SELECT DATEDIFF(day,rr.ArrivalDt,ds.StayDate)+1 AS DayIndex) idx
    WHERE rr.RoomNo='' OR UPPER(rr.RoomNo) IN
      ('UNASSIGNED','(UNASSIGNED ROOM)','UNKNOWN','N/A','NA','-')
), IssueRooms AS
(
    SELECT RegId,CASE WHEN RoomNo='' THEN '(No Room)' ELSE RoomNo END AS RoomNo,
           RevenueDate,MIN(ArrivalDt) AS ArrivalDt,MAX(DepartureDt) AS DepartureDt,
           MAX(GuestName) AS GuestName,MAX(SourceName) AS SourceName,
           CAST('Room Rent without a valid room number' AS nvarchar(160)) AS IssueReason,
           CAST(SUM(ExcludedGross) AS decimal(18,2)) AS ExcludedGross
    FROM BadRoomDaily GROUP BY RegId,RoomNo,RevenueDate
), IssueExtras AS
(
    SELECT e.RegId,CAST('(No Room)' AS varchar(100)) AS RoomNo,
           e.RevenueDate,stay.ArrivalDt,stay.DepartureDt,
           e.GuestName,e.SourceName,
           CAST('Extra has no saved room under this reservation' AS nvarchar(160)) AS IssueReason,
           e.ExtraGrossTotal AS ExcludedGross
    FROM ExtraMapped e
    OUTER APPLY
    (
        SELECT MIN(r.ArrivalDt) AS ArrivalDt,MAX(r.DepartureDt) AS DepartureDt
        FROM Raw r WHERE r.RegId=e.RegId AND LOWER(r.Descr)='room rent'
    ) stay
    WHERE e.MappedRoomNo IS NULL
), Feed AS
(
    SELECT 0 AS SortOrder,CAST('DATA' AS varchar(10)) AS Kind,
           CAST('' AS nvarchar(160)) AS IssueReason,
           RegId,RoomNo,GuestName,SourceName,RevenueDate,ArrivalDt,DepartureDt,
           RoomNights,RoomRevenue,RoomVat,
           ExtraRevenue,ExtraVat,CAST(0 AS decimal(18,2)) AS ExcludedGross
    FROM RoomResults
    UNION ALL
    SELECT 1,'ISSUE',IssueReason,RegId,RoomNo,GuestName,SourceName,RevenueDate,
           ArrivalDt,DepartureDt,0,CAST(0 AS decimal(18,2)),CAST(0 AS decimal(18,2)),
           CAST(0 AS decimal(18,2)),CAST(0 AS decimal(18,2)),ExcludedGross
    FROM IssueRooms
    UNION ALL
    SELECT 1,'ISSUE',IssueReason,RegId,RoomNo,GuestName,SourceName,RevenueDate,
           ArrivalDt,DepartureDt,0,CAST(0 AS decimal(18,2)),CAST(0 AS decimal(18,2)),
           CAST(0 AS decimal(18,2)),CAST(0 AS decimal(18,2)),ExcludedGross
    FROM IssueExtras
)
SELECT Kind,IssueReason,RegId,RoomNo,GuestName,SourceName,RevenueDate,ArrivalDt,DepartureDt,RoomNights,
       RoomRevenue,RoomVat,ExtraRevenue,ExtraVat,ExcludedGross
FROM Feed
WHERE @search='' OR RegId LIKE @searchLike OR RoomNo LIKE @searchLike
   OR GuestName LIKE @searchLike
ORDER BY SortOrder,RevenueDate,RoomNo,RegId
OPTION (MAXRECURSION 366);
";

    // Matches the source-precedence logic used in the report query.
    private const string SourcesSql = @"
;WITH Candidates AS
(
 SELECT hotel_id,NULLIF(LTRIM(RTRIM(CONVERT(varchar(50),reg_id))), '') AS RegId,
        CASE WHEN NULLIF(LTRIM(RTRIM(CONVERT(nvarchar(150),agency))),'') IS NULL
                   OR LOWER(REPLACE(LTRIM(RTRIM(CONVERT(nvarchar(150),agency))),' ','')) IN ('--select--','select')
             THEN 'Walk In' ELSE LTRIM(RTRIM(CONVERT(nvarchar(150),agency))) END AS SourceName,
        CASE WHEN NULLIF(LTRIM(RTRIM(CONVERT(nvarchar(150),agency))),'') IS NULL
                   OR LOWER(REPLACE(LTRIM(RTRIM(CONVERT(nvarchar(150),agency))),' ','')) IN ('--select--','select')
             THEN 1 ELSE 0 END AS DefaultSource,1 AS TablePriority
 FROM dbo.GuestInformationLogTB WHERE hotel_id=@hotel
 UNION ALL
 SELECT hotel_id,NULLIF(LTRIM(RTRIM(CONVERT(varchar(50),reg_id))), ''),
        CASE WHEN NULLIF(LTRIM(RTRIM(CONVERT(nvarchar(150),agency))),'') IS NULL
                   OR LOWER(REPLACE(LTRIM(RTRIM(CONVERT(nvarchar(150),agency))),' ','')) IN ('--select--','select')
             THEN 'Walk In' ELSE LTRIM(RTRIM(CONVERT(nvarchar(150),agency))) END,
        CASE WHEN NULLIF(LTRIM(RTRIM(CONVERT(nvarchar(150),agency))),'') IS NULL
                   OR LOWER(REPLACE(LTRIM(RTRIM(CONVERT(nvarchar(150),agency))),' ','')) IN ('--select--','select')
             THEN 1 ELSE 0 END,2
 FROM dbo.NewReservationsTB WHERE hotel_id=@hotel
), Ranked AS
(
 SELECT *,ROW_NUMBER() OVER(PARTITION BY hotel_id,RegId ORDER BY DefaultSource,TablePriority,SourceName) AS rn
 FROM Candidates WHERE RegId IS NOT NULL
)
SELECT DISTINCT SourceName FROM Ranked WHERE rn=1
ORDER BY SourceName;";

    public async Task LoadAsync(string hotelId, RoomWiseRevenueViewModel model, CancellationToken ct)
    {
        await using var cn = new SqlConnection(_connectionString);
        await cn.OpenAsync(ct);

        await using (var identity = new SqlCommand(@"
SELECT TOP (1) ISNULL(CONVERT(nvarchar(150),name),'') AS HotelName,
       COALESCE(NULLIF(LTRIM(RTRIM(CONVERT(nvarchar(20),currency_sign))),''),
                NULLIF(LTRIM(RTRIM(CONVERT(nvarchar(20),currency))),''),'') AS CurrencySign
FROM dbo.HotelsSignUpTB WHERE hotel_id=@hotel;", cn))
        {
            identity.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
            await using var reader = await identity.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                model.HotelName = reader.IsDBNull(0) ? "" : reader.GetString(0);
                model.CurrencySign = reader.IsDBNull(1) ? "" : reader.GetString(1);
                model.CurrencySign = model.CurrencySign.ToUpperInvariant() switch
                {
                    "GBP" => "£", "PKR" => "Rs.", "USD" => "$", "EUR" => "€",
                    _ => model.CurrencySign
                };
            }
        }

        await using (var sourceCmd = new SqlCommand(SourcesSql, cn) { CommandTimeout = 60 })
        {
            sourceCmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
            await using var sourceReader = await sourceCmd.ExecuteReaderAsync(ct);
            while (await sourceReader.ReadAsync(ct))
            {
                var name = sourceReader.IsDBNull(0) ? "" : sourceReader.GetString(0);
                if (!string.IsNullOrWhiteSpace(name)) model.Sources.Add(name);
            }
        }

        await using var cmd = new SqlCommand(ReportSql, cn) { CommandTimeout = 120 };
        cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
        cmd.Parameters.Add("@from", SqlDbType.Date).Value = model.From.Date;
        cmd.Parameters.Add("@to", SqlDbType.Date).Value = model.To.Date;
        cmd.Parameters.Add("@search", SqlDbType.NVarChar, 100).Value = model.Search;
        cmd.Parameters.Add("@source", SqlDbType.NVarChar, 150).Value = model.Source;
        var s = model.Search.Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]");
        cmd.Parameters.Add("@searchLike", SqlDbType.NVarChar, 220).Value = "%" + s + "%";

        await using var rows = await cmd.ExecuteReaderAsync(ct);
        while (await rows.ReadAsync(ct))
        {
            if (GetText(rows, "Kind") == "ISSUE")
            {
                model.Issues.Add(new RoomWiseRevenueIssue
                {
                    ReservationNo = GetText(rows, "RegId"),
                    RoomNo = GetText(rows, "RoomNo"),
                    GuestName = GetText(rows, "GuestName"),
                    SourceName = GetText(rows, "SourceName"),
                    RevenueDate = GetDate(rows, "RevenueDate"),
                    ArrivalDate = GetOptionalDate(rows, "ArrivalDt"),
                    DepartureDate = GetOptionalDate(rows, "DepartureDt"),
                    Reason = GetText(rows, "IssueReason"),
                    ExcludedGross = GetMoney(rows, "ExcludedGross")
                });
                continue;
            }
            model.Rows.Add(new RoomWiseRevenueRow
            {
                ReservationNo = GetText(rows, "RegId"),
                RoomNo = GetText(rows, "RoomNo"),
                GuestName = GetText(rows, "GuestName"),
                SourceName = GetText(rows, "SourceName"),
                RevenueDate = GetDate(rows, "RevenueDate"),
                ArrivalDate = GetOptionalDate(rows, "ArrivalDt"),
                DepartureDate = GetOptionalDate(rows, "DepartureDt"),
                RoomNights = GetInt(rows, "RoomNights"),
                RoomRevenue = GetMoney(rows, "RoomRevenue"),
                RoomVat = GetMoney(rows, "RoomVat"),
                ExtraRevenue = GetMoney(rows, "ExtraRevenue"),
                ExtraVat = GetMoney(rows, "ExtraVat")
            });
        }
    }

    private static string GetText(SqlDataReader reader, string col)
        => reader[col] == DBNull.Value ? "" : Convert.ToString(reader[col], CultureInfo.InvariantCulture) ?? "";

    private static DateTime GetDate(SqlDataReader reader, string col)
        => reader[col] == DBNull.Value ? DateTime.MinValue : Convert.ToDateTime(reader[col], CultureInfo.InvariantCulture);

    private static DateTime? GetOptionalDate(SqlDataReader reader, string col)
        => reader[col] == DBNull.Value ? null : Convert.ToDateTime(reader[col], CultureInfo.InvariantCulture);

    private static int GetInt(SqlDataReader reader, string col)
        => reader[col] == DBNull.Value ? 0 : Convert.ToInt32(reader[col], CultureInfo.InvariantCulture);

    private static decimal GetMoney(SqlDataReader reader, string col)
        => reader[col] == DBNull.Value ? 0m : Convert.ToDecimal(reader[col], CultureInfo.InvariantCulture);
}
