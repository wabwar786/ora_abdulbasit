using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using System.Data;
using System.Globalization;
using System.Text;
using Orapmshms.Models;
using Orapmshms.Services;

namespace Orapmshms.Controllers;

[Route("BreakfastGuestOccupancyReport")]
public sealed class BreakfastGuestOccupancyReportController : Controller
{
    private readonly string _connectionString;
    private readonly IHotelClock _hotelClock;
    private readonly ILogger<BreakfastGuestOccupancyReportController> _logger;
    public BreakfastGuestOccupancyReportController(IConfiguration config, IHotelClock clock,
        ILogger<BreakfastGuestOccupancyReportController> logger)
    {
        _connectionString = config.GetConnectionString("con") ?? throw new InvalidOperationException("Missing con connection string");
        _hotelClock = clock;
        _logger = logger;
    }

    private string HotelId => HttpContext.Session.GetString("hotel")?.Trim() ?? "";
    private string UserId => HttpContext.Session.GetString("UserId")?.Trim() ?? "";
    private string UserName => HttpContext.Session.GetString("UserName")?.Trim() ?? "";
    private bool SignedIn => HotelId.Length > 0 && UserId.Length > 0;

    [HttpGet("")]
    [HttpGet("Index")]
    [HttpGet("/BreakfastGuestOccupancyReport.aspx")]
    public async Task<IActionResult> Index(DateTime? date, CancellationToken ct)
    {
        if (!SignedIn) return RedirectToAction("Index", "LoginHMS");
        try
        {
            var model = await LoadAsync(date ?? _hotelClock.GetHotelToday(HotelId), ct);
            Response.Headers["Cache-Control"] = "no-store";
            return View("~/Views/BreakfastGuestOccupancyReport/Index.cshtml", model);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Breakfast occupancy report failed for hotel {HotelId}", HotelId);
            ViewBag.Error = "Unable to load breakfast occupancy. Please verify the selected date and retry.";
            return View("~/Views/BreakfastGuestOccupancyReport/Index.cshtml", new BreakfastGuestOccupancyViewModel
            { HotelName = HttpContext.Session.GetString("HotelName") ?? "Hotel", ReportDate = date ?? _hotelClock.GetHotelToday(HotelId) });
        }
    }

    // A print-only view (Layout=null) avoids printing the PMS sidebar/header.
    [HttpGet("Print")]
    public async Task<IActionResult> Print(DateTime? date, CancellationToken ct)
    {
        if (!SignedIn) return RedirectToAction("Index", "LoginHMS");
        try
        {
            var model = await LoadAsync(date ?? _hotelClock.GetHotelToday(HotelId), ct);
            Response.Headers["Cache-Control"] = "no-store";
            return View("~/Views/BreakfastGuestOccupancyReport/Print.cshtml", model);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Breakfast occupancy print failed for hotel {HotelId}", HotelId);
            return RedirectToAction(nameof(Index), new { date });
        }
    }

    [HttpGet("Export")]
    public async Task<IActionResult> Export(DateTime? date, CancellationToken ct)
    {
        if (!SignedIn) return Unauthorized();
        var model = await LoadAsync(date ?? _hotelClock.GetHotelToday(HotelId), ct);
        // Spreadsheet formula injection protection: prefix formula-looking fields.
        static string Cell(string? input)
        {
            var str = input ?? "";
            if (str.Length > 0 && "=+-@".Contains(str[0])) str = "'" + str;
            return "\"" + str.Replace("\"", "\"\"") + "\"";
        }
        var sb = new StringBuilder("No,Reservation,Guest,Room,Adults,Children,Infants,Category,Rate Plan,Arrival,Departure\r\n");
        for (var i = 0; i < model.Rows.Count; i++)
        {
            var r = model.Rows[i];
            sb.AppendLine(string.Join(",", new [] {
                (i + 1).ToString(CultureInfo.InvariantCulture), Cell(r.RegId), Cell(r.GuestName), Cell(r.RoomNo),
                r.Adults.ToString(CultureInfo.InvariantCulture), r.Children.ToString(CultureInfo.InvariantCulture),
                r.Infants.ToString(CultureInfo.InvariantCulture), Cell(r.RoomCategory), Cell(r.RatePlanName),
                Cell(r.ArrivalDate?.ToString("yyyy-MM-dd")), Cell(r.DepartureDate?.ToString("yyyy-MM-dd"))
            }));
        }
        return File(new UTF8Encoding(true).GetBytes(sb.ToString()), "text/csv",
            $"BreakfastOccupancy_{model.ReportDate:yyyyMMdd}.csv");
    }

    private async Task<BreakfastGuestOccupancyViewModel> LoadAsync(DateTime date, CancellationToken ct)
    {
        var model = new BreakfastGuestOccupancyViewModel
        {
            ReportDate = date.Date,
            UserName = UserName,
            HotelName = HttpContext.Session.GetString("HotelName") ?? "Hotel"
        };
        await using var cn = new SqlConnection(_connectionString);
        await cn.OpenAsync(ct);
        await using (var cmd = new SqlCommand(@"
SELECT TOP (1) ISNULL(name,'') AS name, ISNULL(logo,'') AS logo
FROM dbo.HotelsSignUpTB WHERE hotel_id=@hotel;", cn))
        {
            cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = HotelId;
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                model.HotelName = Convert.ToString(reader["name"]) ?? model.HotelName;
                model.HotelLogo = Convert.ToString(reader["logo"]) ?? "";
            }
        }
        // SQL is ported from BreakfastGuestOccupancyReport.aspx.cs: room occupancy
        // first; guest-level fallback; unassigned checked-in legacy rows.
        await using var report = new SqlCommand(OccupancySql, cn) { CommandTimeout = 60 };
        report.Parameters.Add("@hotelId", SqlDbType.VarChar, 50).Value = HotelId;
        report.Parameters.Add("@today", SqlDbType.Date).Value = date.Date;
        await using var rd = await report.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            static string Text(SqlDataReader r, string name) => r[name] == DBNull.Value ? "" : Convert.ToString(r[name]) ?? "";
            static int Count(SqlDataReader r, string name) => r[name] == DBNull.Value ? 0 : Convert.ToInt32(r[name], CultureInfo.InvariantCulture);
            static DateTime? Date(SqlDataReader r, string name) => r[name] == DBNull.Value ? null : Convert.ToDateTime(r[name], CultureInfo.InvariantCulture);
            model.Rows.Add(new BreakfastGuestOccupancyRow {
                PaymentId = rd["payment_id"] == DBNull.Value ? null : Convert.ToInt32(rd["payment_id"], CultureInfo.InvariantCulture),
                RegId = Text(rd, "reg_id"), GuestName = Text(rd, "GuestName"), RoomNo = Text(rd, "room_no"),
                Adults = Count(rd, "Adults"), Children = Count(rd, "Children"), Infants = Count(rd, "Infants"),
                RoomCategory = Text(rd, "room_category"), RatePlanName = Text(rd, "rateplan_name"),
                ArrivalDate = Date(rd, "ArrivalDate"), DepartureDate = Date(rd, "DepartureDate")
            });
        }
        return model;
    }

    private const string OccupancySql = @"
WITH PaymentRoomRows AS
(
    SELECT
        p.ID AS payment_id,
        p.reg_id,
        p.hotel_id,
        NULLIF(LTRIM(RTRIM(p.room_no)), '') AS room_no,
        NULLIF(LTRIM(RTRIM(p.[Type])), '') AS room_category,
        NULLIF(LTRIM(RTRIM(p.rateplanname)), '') AS rateplan_name,
        NULLIF(LTRIM(RTRIM(p.guestname)), '') AS room_guest_name,
        ISNULL(p.room_adults, 0) AS room_adults,
        ISNULL(p.room_children, 0) AS room_children,
        ISNULL(p.room_infants, 0) AS room_infants,
        COALESCE(
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(p.ArrivalDate)), ''), 110),
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(p.ArrivalDate)), ''), 103),
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(p.ArrivalDate)), ''))
        ) AS ArrivalDate,
        COALESCE(
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(p.DepartureDate)), ''), 110),
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(p.DepartureDate)), ''), 103),
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(p.DepartureDate)), ''))
        ) AS DepartureDate,
        LOWER(LTRIM(RTRIM(ISNULL(p.res_status,'')))) AS payment_status
    FROM dbo.payments p
    WHERE p.hotel_id = @hotelId
      AND LTRIM(RTRIM(ISNULL(p.descr,''))) = 'Room Rent'
      AND LOWER(LTRIM(RTRIM(ISNULL(p.res_status,'')))) IN ('check in','check-in','checked in','checked-in')
),
PaymentBased AS
(
    SELECT
        pr.payment_id,
        pr.reg_id,
        COALESCE(
            NULLIF(LTRIM(RTRIM(pr.room_guest_name)), ''),
            NULLIF(LTRIM(RTRIM(CONCAT(ISNULL(gi.GuestName,''), ' ', ISNULL(gi.LastName,'')))), ''),
            NULLIF(LTRIM(RTRIM(CONCAT(ISNULL(nr.GuestName,''), ' ', ISNULL(nr.LastName,'')))), ''),
            'Guest'
        ) AS GuestName,
        ISNULL(pr.room_no, 'UNASSIGNED') AS room_no,
        CASE
            WHEN pr.room_adults + pr.room_children + pr.room_infants > 0 THEN pr.room_adults
            ELSE COALESCE(TRY_CONVERT(int, gi.NumberOfAdults), TRY_CONVERT(int, nr.number_of_adult), 0)
        END AS Adults,
        CASE
            WHEN pr.room_adults + pr.room_children + pr.room_infants > 0 THEN pr.room_children
            ELSE COALESCE(TRY_CONVERT(int, gi.NumberOfMinors), TRY_CONVERT(int, nr.number_of_minor), 0)
        END AS Children,
        CASE
            WHEN pr.room_adults + pr.room_children + pr.room_infants > 0 THEN pr.room_infants
            ELSE 0
        END AS Infants,
        ISNULL(pr.room_category,
               NULLIF(LTRIM(RTRIM(COALESCE(gi.room_category, nr.room_category, ''))), '')) AS room_category,
        ISNULL(pr.rateplan_name, NULLIF(LTRIM(RTRIM(ISNULL(nr.rateplanname,''))), '')) AS rateplan_name,
        COALESCE(pr.ArrivalDate,
                 TRY_CONVERT(date, gi.ArrivalDate, 110), TRY_CONVERT(date, gi.ArrivalDate, 103),
                 TRY_CONVERT(date, nr.ArrivalDate, 110), TRY_CONVERT(date, nr.ArrivalDate, 103)) AS ArrivalDate,
        COALESCE(pr.DepartureDate,
                 TRY_CONVERT(date, gi.DepartureDate, 110), TRY_CONVERT(date, gi.DepartureDate, 103),
                 TRY_CONVERT(date, nr.dept_date, 110), TRY_CONVERT(date, nr.dept_date, 103)) AS DepartureDate
    FROM PaymentRoomRows pr
    LEFT JOIN dbo.GuestInformationLogTB gi
      ON gi.hotel_id = pr.hotel_id AND gi.reg_id = pr.reg_id
    LEFT JOIN dbo.NewReservationsTB nr
      ON nr.hotel_id = pr.hotel_id AND nr.reg_id = pr.reg_id
    WHERE COALESCE(pr.ArrivalDate,
                   TRY_CONVERT(date, gi.ArrivalDate, 110), TRY_CONVERT(date, gi.ArrivalDate, 103),
                   TRY_CONVERT(date, nr.ArrivalDate, 110), TRY_CONVERT(date, nr.ArrivalDate, 103)) <= @today
      AND COALESCE(pr.DepartureDate,
                   TRY_CONVERT(date, gi.DepartureDate, 110), TRY_CONVERT(date, gi.DepartureDate, 103),
                   TRY_CONVERT(date, nr.dept_date, 110), TRY_CONVERT(date, nr.dept_date, 103)) >= @today
),
GuestLogFallback AS
(
    SELECT
        NULL AS payment_id,
        gi.reg_id,
        NULLIF(LTRIM(RTRIM(CONCAT(ISNULL(gi.GuestName,''), ' ', ISNULL(gi.LastName,'')))), '') AS GuestName,
        'UNASSIGNED' AS room_no,
        ISNULL(TRY_CONVERT(int, gi.NumberOfAdults), 0) AS Adults,
        ISNULL(TRY_CONVERT(int, gi.NumberOfMinors), 0) AS Children,
        0 AS Infants,
        ISNULL(NULLIF(LTRIM(RTRIM(gi.room_category)), ''), '') AS room_category,
        ISNULL(NULLIF(LTRIM(RTRIM(nr.rateplanname)), ''), '') AS rateplan_name,
        COALESCE(TRY_CONVERT(date, gi.ArrivalDate, 110), TRY_CONVERT(date, gi.ArrivalDate, 103), TRY_CONVERT(date, gi.ArrivalDate)) AS ArrivalDate,
        COALESCE(TRY_CONVERT(date, gi.DepartureDate, 110), TRY_CONVERT(date, gi.DepartureDate, 103), TRY_CONVERT(date, gi.DepartureDate)) AS DepartureDate
    FROM dbo.GuestInformationLogTB gi
    LEFT JOIN dbo.NewReservationsTB nr
      ON nr.hotel_id = gi.hotel_id AND nr.reg_id = gi.reg_id
    WHERE gi.hotel_id = @hotelId
      AND LOWER(LTRIM(RTRIM(ISNULL(gi.res_status,'')))) IN ('check in','check-in','checked in','checked-in')
      AND COALESCE(TRY_CONVERT(date, gi.ArrivalDate, 110), TRY_CONVERT(date, gi.ArrivalDate, 103), TRY_CONVERT(date, gi.ArrivalDate)) <= @today
      AND COALESCE(TRY_CONVERT(date, gi.DepartureDate, 110), TRY_CONVERT(date, gi.DepartureDate, 103), TRY_CONVERT(date, gi.DepartureDate)) >= @today
      AND NOT EXISTS
      (
          SELECT 1 FROM PaymentRoomRows px
          WHERE px.hotel_id = gi.hotel_id AND px.reg_id = gi.reg_id
      )
),
ReservationFallback AS
(
    SELECT
        NULL AS payment_id,
        nr.reg_id,
        NULLIF(LTRIM(RTRIM(CONCAT(ISNULL(nr.GuestName,''), ' ', ISNULL(nr.LastName,'')))), '') AS GuestName,
        ISNULL(NULLIF(LTRIM(RTRIM(nr.room_no)), ''), 'UNASSIGNED') AS room_no,
        ISNULL(TRY_CONVERT(int, nr.number_of_adult), 0) AS Adults,
        ISNULL(TRY_CONVERT(int, nr.number_of_minor), 0) AS Children,
        0 AS Infants,
        ISNULL(NULLIF(LTRIM(RTRIM(nr.room_category)), ''), '') AS room_category,
        ISNULL(NULLIF(LTRIM(RTRIM(nr.rateplanname)), ''), '') AS rateplan_name,
        COALESCE(TRY_CONVERT(date, nr.ArrivalDate, 110), TRY_CONVERT(date, nr.ArrivalDate, 103), TRY_CONVERT(date, nr.ArrivalDate)) AS ArrivalDate,
        COALESCE(TRY_CONVERT(date, nr.dept_date, 110), TRY_CONVERT(date, nr.dept_date, 103), TRY_CONVERT(date, nr.dept_date)) AS DepartureDate
    FROM dbo.NewReservationsTB nr
    WHERE nr.hotel_id = @hotelId
      AND LOWER(LTRIM(RTRIM(ISNULL(nr.res_status,'')))) IN ('check in','check-in','checked in','checked-in')
      AND COALESCE(TRY_CONVERT(date, nr.ArrivalDate, 110), TRY_CONVERT(date, nr.ArrivalDate, 103), TRY_CONVERT(date, nr.ArrivalDate)) <= @today
      AND COALESCE(TRY_CONVERT(date, nr.dept_date, 110), TRY_CONVERT(date, nr.dept_date, 103), TRY_CONVERT(date, nr.dept_date)) >= @today
      AND NOT EXISTS
      (
          SELECT 1 FROM PaymentRoomRows px
          WHERE px.hotel_id = nr.hotel_id AND px.reg_id = nr.reg_id
      )
      AND NOT EXISTS
      (
          SELECT 1 FROM dbo.GuestInformationLogTB gx
          WHERE gx.hotel_id = nr.hotel_id AND gx.reg_id = nr.reg_id
            AND LOWER(LTRIM(RTRIM(ISNULL(gx.res_status,'')))) IN ('check in','check-in','checked in','checked-in')
      )
),
AllInHouse AS
(
    SELECT * FROM PaymentBased
    UNION ALL
    SELECT * FROM GuestLogFallback
    UNION ALL
    SELECT * FROM ReservationFallback
)
SELECT
    payment_id,
    reg_id,
    ISNULL(GuestName,'Guest') AS GuestName,
    ISNULL(room_no,'UNASSIGNED') AS room_no,
    ISNULL(Adults,0) AS Adults,
    ISNULL(Children,0) AS Children,
    ISNULL(Infants,0) AS Infants,
    ISNULL(room_category,'') AS room_category,
    ISNULL(rateplan_name,'') AS rateplan_name,
    ArrivalDate,
    DepartureDate
FROM AllInHouse
ORDER BY
    CASE WHEN TRY_CONVERT(int, room_no) IS NULL THEN 1 ELSE 0 END,
    TRY_CONVERT(int, room_no),
    room_no,
    ArrivalDate,
    reg_id;";
}
