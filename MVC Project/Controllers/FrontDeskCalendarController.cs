using Microsoft.AspNetCore.DataProtection;
using Orapmshms.Services.Restaurant;
using Microsoft.Data.SqlClient;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Orapmshms.Models;
using Orapmshms.Services;
using Orapmshms.Services.AvailabilityJobs;
using Orapmshms.Services.Logging;

namespace Orapmshms.Controllers;

[Route("Calendar")]
public sealed class FrontDeskCalendarController : Controller
{
    private readonly IFrontDeskCalendarService _calendar;
    private readonly IAppLogger _logger;
    private readonly FrontDeskCalendarDbLogger _dbLogger;
    private readonly IDataProtector _invoiceShareProtector;
    private readonly IDataProtector _payNowShareProtector;
    private readonly RestaurantGuestLinkTokens _restaurantGuestLinks;
    private readonly string _connectionString;
    private readonly string? _payNowPublicBaseUrl;

    private const string InvoiceSharePurpose = "ORAPMS.InvoiceShare.v1";
    private const string PayNowSharePurpose = "ORAPMS.PayNowShare.v1";

    // Program.cs stays unchanged.
    // Build the calendar service here using dependencies that the existing PMS
    // already exposes to controllers. CheckInService is constructed explicitly,
    // so ICheckInService does not need a DI registration.
    public FrontDeskCalendarController(
        IConfiguration configuration,
        IHotelClock hotelClock,
        IHttpClientFactory httpClientFactory,
        IAvailabilityAutoUpdateQueue availabilityQueue,
        IAvailabilityChannelSyncQueue channelQueue,
        IMemoryCache cache,
        ILoggerFactory loggerFactory,
        IDataProtectionProvider dataProtectionProvider)
    {
        _logger = new AppLogger(loggerFactory.CreateLogger<AppLogger>());
        _invoiceShareProtector = dataProtectionProvider.CreateProtector(InvoiceSharePurpose);
        _payNowShareProtector = dataProtectionProvider.CreateProtector(PayNowSharePurpose);
        _restaurantGuestLinks = new RestaurantGuestLinkTokens(dataProtectionProvider);
        _connectionString = configuration.GetConnectionString("con")
            ?? throw new InvalidOperationException("ConnectionStrings:con is missing.");

        // Optional public site origin used by PayNowController as well. When the
        // Calendar runs behind a proxy, the internal Request.Host may not be
        // reachable by guests. Keep the previous Request origin as the default.
        var configuredPayNowBase = configuration["PayNow:PublicBaseUrl"]?.Trim().TrimEnd('/');
        if (!string.IsNullOrWhiteSpace(configuredPayNowBase))
        {
            if (!Uri.TryCreate(configuredPayNowBase, UriKind.Absolute, out var publicUri) ||
                (publicUri.Scheme != Uri.UriSchemeHttps &&
                 !(publicUri.Scheme == Uri.UriSchemeHttp && publicUri.IsLoopback)))
                throw new InvalidOperationException(
                    "PayNow:PublicBaseUrl must be a valid HTTPS URL (HTTP only for localhost).");

            _payNowPublicBaseUrl = configuredPayNowBase;
        }
        var connectionString = _connectionString;
        _dbLogger = new FrontDeskCalendarDbLogger(connectionString, hotelClock, _logger);

        ICheckInService checkInService = new CheckInService(
            configuration,
            hotelClock,
            httpClientFactory,
            availabilityQueue,
            loggerFactory.CreateLogger<CheckInService>());

        IYieldManagementService yieldManagementService = new YieldManagementService(
            configuration,
            hotelClock,
            channelQueue,
            loggerFactory.CreateLogger<YieldManagementService>());

        _calendar = new FrontDeskCalendarService(
            configuration,
            hotelClock,
            availabilityQueue,
            checkInService,
            yieldManagementService,
            httpClientFactory,
            cache,
            _logger,
            _dbLogger);
    }

    [HttpGet("")]
    [HttpGet("Index")]
    [HttpGet("/FrontDeskCalender")]
    [HttpGet("/FrontDeskCalender.aspx")]
    [HttpGet("/FrontDeskCalendar")]
    [HttpGet("/FrontDeskCalendar.aspx")]
    public async Task<IActionResult> Index(DateTime? start, CancellationToken cancellationToken)
    {
        if (!HasSession()) return RedirectToAction("Index", "LoginHMS");

        // Property-level calendar mode switch. Keep /Calendar as the single
        // navigation entry point: properties with Monthwise enabled go straight
        // to the isolated MVC Month-Wise Calendar, while all other properties
        // continue to use this existing daily Calendar unchanged.
        if (await IsMonthWiseCalendarEnabledAsync(SessionValue("hotel"), cancellationToken))
            return RedirectToAction("Index", "FrontDeskCalendarMW");

        try
        {
            var model = await _calendar.GetPageAsync(
                SessionValue("hotel"),
                SessionValue("HotelName"),
                SessionValue("UserId"),
                SessionValue("UserName"),
                SessionValue("Role"),
                start,
                cancellationToken);
            try
            {
                // Matches FrontDeskCalender.aspx: HotelsSignUpTB.isResturant controls access.
                await using (var cn = new SqlConnection(_connectionString))
                await using (var cmd = new SqlCommand(@"SELECT TOP (1) ISNULL(isResturant,0) FROM dbo.HotelsSignUpTB WHERE hotel_id=@hotel", cn))
                {
                    cmd.Parameters.AddWithValue("@hotel", SessionValue("hotel"));
                    await cn.OpenAsync(cancellationToken);
                    var enabled = await cmd.ExecuteScalarAsync(cancellationToken);
                    model.IsRestaurantEnabled = enabled != null && enabled != DBNull.Value &&
                        (string.Equals(Convert.ToString(enabled), "True", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(Convert.ToString(enabled), "1", StringComparison.OrdinalIgnoreCase));
                }
            }
            catch (SqlException ex)
            {
                // The optional Restaurant action must never prevent the Calendar from loading.
                _logger.Error(ex, "Cannot read restaurant-enabled setting for current hotel.");
                model.IsRestaurantEnabled = false;
            }
            return View(model);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Front desk calendar page load failed.");
            await _dbLogger.LogExceptionAsync(ex, "Front Desk Calendar", "Index", SessionValue("hotel"), SessionValue("UserId"), SessionValue("UserName"), Ip(), cancellationToken);
            return View(new FrontDeskCalendarPageViewModel
            {
                HotelId = SessionValue("hotel"),
                HotelName = SessionValue("HotelName"),
                UserId = SessionValue("UserId"),
                UserName = SessionValue("UserName"),
                Role = SessionValue("Role"),
                HotelToday = DateTime.Today,
                StartDate = start?.Date ?? DateTime.Today,
                ViewDays = 20
            });
        }
    }

    private async Task<bool> IsMonthWiseCalendarEnabledAsync(
        string hotelId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(hotelId))
            return false;

        try
        {
            await using var connection = new SqlConnection(_connectionString);
            await using var command = new SqlCommand(@"
SELECT TOP (1) ISNULL(monthwise, 0)
FROM dbo.HotelsSignUpTB
WHERE hotel_id = @hotel_id;", connection);

            command.Parameters.AddWithValue("@hotel_id", hotelId.Trim());

            await connection.OpenAsync(cancellationToken);
            object? value = await command.ExecuteScalarAsync(cancellationToken);

            return value != null &&
                   value != DBNull.Value &&
                   Convert.ToInt32(value) == 1;
        }
        catch (Exception ex)
        {
            // Do not let a mode-flag lookup disturb the proven daily Calendar.
            // If this lookup fails, simply continue with the existing Calendar.
            _logger.Error(ex, "Unable to read the Monthwise Calendar flag for hotel {HotelId}.", hotelId);
            return false;
        }
    }

    [HttpGet("Data")]
    public async Task<IActionResult> Data(DateTime? start, int days = 20, CancellationToken cancellationToken = default)
    {
        if (!HasSession()) return Unauthorized(new { ok = false, message = "Your login session has expired." });
        days = Math.Clamp(days, 7, 45);
        try
        {
            var model = await _calendar.GetCalendarAsync(
                SessionValue("hotel"), SessionValue("UserId"), SessionValue("UserName"), SessionValue("Role"),
                (start ?? DateTime.Today).Date, days, cancellationToken);
            Response.Headers.CacheControl = "no-store, no-cache";
            return Json(new { ok = true, data = model });
        }
        catch (OperationCanceledException) { return new EmptyResult(); }
        catch (Exception ex)
        {
            _logger.Error(ex, "Calendar data load failed.");
            await _dbLogger.LogExceptionAsync(ex, "Front Desk Calendar", "Data", SessionValue("hotel"), SessionValue("UserId"), SessionValue("UserName"), Ip(), cancellationToken);
            return StatusCode(500, new { ok = false, message = "Unable to load the Front Desk Calendar." });
        }
    }

    // Daily Calendar only. Room choices are calculated for the entire stored
    // reservation stay. Never trust dates passed by the browser for availability.
    // A fresh availability check under SQL locks still runs during final save.
    [HttpGet("UnassignedAssignmentOptions")]
    public async Task<IActionResult> UnassignedAssignmentOptions(string regId, CancellationToken ct)
    {
        if (!HasSession())
            return Unauthorized(new { ok = false, message = "Your login session has expired. Sign in again." });
        regId = (regId ?? string.Empty).Trim();
        if (regId.Length == 0 || regId.Length > 100)
            return BadRequest(new { ok = false, message = "A valid reservation ID is required." });

        try
        {
            await using var cn = new SqlConnection(_connectionString);
            await cn.OpenAsync(ct);
            DateTime arrival;
            DateTime departure;
            await using (var stay = new SqlCommand(@"
SELECT TOP (1) x.arrival,x.departure
FROM (
  SELECT 1 AS priority,ID AS rowId,
    COALESCE(TRY_CONVERT(date,ArrivalDate,110),TRY_CONVERT(date,ArrivalDate,23),
      TRY_CONVERT(date,ArrivalDate,103),TRY_CONVERT(date,ArrivalDate)) AS arrival,
    COALESCE(TRY_CONVERT(date,DepartureDate,110),TRY_CONVERT(date,DepartureDate,23),
      TRY_CONVERT(date,DepartureDate,103),TRY_CONVERT(date,DepartureDate)) AS departure
  FROM dbo.GuestInformationLogTB
  WHERE hotel_id=@hotel AND reg_id=@reg
  UNION ALL
  SELECT 2,ID,
    COALESCE(TRY_CONVERT(date,ArrivalDate,110),TRY_CONVERT(date,ArrivalDate,23),
      TRY_CONVERT(date,ArrivalDate,103),TRY_CONVERT(date,ArrivalDate)),
    COALESCE(TRY_CONVERT(date,dept_date,110),TRY_CONVERT(date,dept_date,23),
      TRY_CONVERT(date,dept_date,103),TRY_CONVERT(date,dept_date))
  FROM dbo.NewReservationsTB
  WHERE hotel_id=@hotel AND reg_id=@reg
) x ORDER BY x.priority,x.rowId DESC;", cn))
            {
                stay.CommandTimeout = 15;
                stay.Parameters.Add("@hotel", System.Data.SqlDbType.VarChar, 50).Value = SessionValue("hotel");
                stay.Parameters.Add("@reg", System.Data.SqlDbType.VarChar, 100).Value = regId;
                await using var reader = await stay.ExecuteReaderAsync(ct);
                if (!await reader.ReadAsync(ct) || reader.IsDBNull(0) || reader.IsDBNull(1))
                    return NotFound(new { ok = false, message = "Reservation dates were not found. Refresh the calendar." });
                arrival = reader.GetDateTime(0).Date;
                departure = reader.GetDateTime(1).Date;
            }
            if (departure <= arrival || (departure - arrival).TotalDays > 730)
                return BadRequest(new { ok = false, message = "The reservation has invalid stay dates." });

            var rooms = new List<object>();
            var plans = new List<object>();
            await using (var cmd = new SqlCommand(@"
SET NOCOUNT ON;
/* Only physical rooms that can be occupied across the entire stay. */
SELECT DISTINCT
  LTRIM(RTRIM(r.room_no)) AS room_no,
  CONVERT(varchar(100),cr.localcategoryid) AS category_id,
  LTRIM(RTRIM(cr.description)) AS category_name
FROM dbo.RoomsTB r
INNER JOIN dbo.create_room cr
  ON cr.hotel_id=r.Hotel_id
 AND LTRIM(RTRIM(cr.description))=LTRIM(RTRIM(r.room_category))
 AND LTRIM(RTRIM(ISNULL(cr.category,'')))='Room Rent'
WHERE r.Hotel_id=@hotel
  AND NULLIF(LTRIM(RTRIM(ISNULL(r.room_no,''))),'') IS NOT NULL
  AND LTRIM(RTRIM(ISNULL(r.room_no,'')))<>'UNASSIGNED'
  AND NULLIF(LTRIM(RTRIM(CONVERT(varchar(100),cr.localcategoryid))),'') IS NOT NULL
  AND LOWER(LTRIM(RTRIM(ISNULL(r.room_status,''))))<>'blocked'
  AND (
       NOT EXISTS(SELECT 1 FROM dbo.Hms_accounts ha
                  WHERE CONVERT(varchar(100),ha.user_id)=@user
                    AND LOWER(LTRIM(RTRIM(ISNULL(ha.role,''))))='council')
       OR EXISTS(SELECT 1 FROM dbo.UserRoomAccess ura
                 WHERE ura.HotelId=@hotel AND ura.RoomNo=r.room_no
                   AND CONVERT(varchar(100),ura.UserId)=@user)
      )
  AND NOT EXISTS (
    SELECT 1 FROM dbo.payments p
    WHERE p.hotel_id=@hotel
      AND LTRIM(RTRIM(ISNULL(p.room_no,'')))=LTRIM(RTRIM(r.room_no))
      AND LTRIM(RTRIM(ISNULL(p.descr,'')))='Room Rent'
      AND LOWER(LTRIM(RTRIM(ISNULL(p.res_status,'')))) IN
          ('reservation','check in','checkin','checked in','provisional','tentative')
      AND COALESCE(TRY_CONVERT(date,p.ArrivalDate,110),TRY_CONVERT(date,p.ArrivalDate,23),
                   TRY_CONVERT(date,p.ArrivalDate,103),TRY_CONVERT(date,p.ArrivalDate)) < @departure
      AND @arrival < COALESCE(TRY_CONVERT(date,p.DepartureDate,110),TRY_CONVERT(date,p.DepartureDate,23),
                              TRY_CONVERT(date,p.DepartureDate,103),TRY_CONVERT(date,p.DepartureDate))
  )
  AND NOT EXISTS (
    SELECT 1 FROM dbo.RoomBlocksTB rb
    WHERE rb.HotelID=@hotel AND rb.RoomNo=r.room_no AND ISNULL(rb.IsActive,0)=1
      AND COALESCE(TRY_CONVERT(date,rb.BlockStartDate,110),TRY_CONVERT(date,rb.BlockStartDate,23),
                   TRY_CONVERT(date,rb.BlockStartDate,103),TRY_CONVERT(date,rb.BlockStartDate)) < @departure
      AND @arrival <= COALESCE(TRY_CONVERT(date,rb.BlockEndDate,110),TRY_CONVERT(date,rb.BlockEndDate,23),
                               TRY_CONVERT(date,rb.BlockEndDate,103),TRY_CONVERT(date,rb.BlockEndDate))
  )
ORDER BY category_name,room_no;
/* Rate plans are returned as JSON by this Calendar endpoint, never HTML. */
SELECT DISTINCT
  LTRIM(RTRIM(CONVERT(varchar(100),localplanid))) AS plan_id,
  ISNULL(planname,'') AS plan_name,
  LTRIM(RTRIM(ISNULL(category,''))) AS category_name,
  LTRIM(RTRIM(ISNULL(CONVERT(varchar(100),category_id),''))) AS category_id
FROM dbo.category_plan
WHERE hotel_id=@hotel
  AND NULLIF(LTRIM(RTRIM(CONVERT(varchar(100),localplanid))),'') IS NOT NULL
ORDER BY category_name,plan_name,plan_id;", cn))
            {
                cmd.CommandTimeout = 15;
                cmd.Parameters.Add("@hotel", System.Data.SqlDbType.VarChar, 50).Value = SessionValue("hotel");
                cmd.Parameters.Add("@user", System.Data.SqlDbType.VarChar, 100).Value = SessionValue("UserId");
                cmd.Parameters.Add("@arrival", System.Data.SqlDbType.Date).Value = arrival;
                cmd.Parameters.Add("@departure", System.Data.SqlDbType.Date).Value = departure;
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    rooms.Add(new
                    {
                        roomNo = reader.GetString(0),
                        categoryId = reader.GetString(1),
                        categoryName = reader.GetString(2)
                    });
                }
                if (await reader.NextResultAsync(ct))
                {
                    while (await reader.ReadAsync(ct))
                    {
                        plans.Add(new
                        {
                            value = reader.GetString(0),
                            text = reader.GetString(1),
                            categoryName = reader.GetString(2),
                            categoryId = reader.GetString(3)
                        });
                    }
                }
            }
            Response.Headers.CacheControl = "private, no-store";
            return Json(new
            {
                ok = true,
                data = new
                {
                    arrival = arrival.ToString("yyyy-MM-dd"),
                    departure = departure.ToString("yyyy-MM-dd"),
                    rooms,
                    plans
                }
            });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return new EmptyResult(); }
        catch (Exception ex)
        {
            _logger.Error(ex, "Unable to load available rooms/rate plans for the daily calendar.");
            return StatusCode(500, new
            {
                ok = false,
                message = "Unable to load room availability or rate plans. Check server logs."
            });
        }
    }

    [HttpGet("BookingDetails")]
    public async Task<IActionResult> BookingDetails(string regId, int paymentId, CancellationToken cancellationToken)
    {
        if (!HasSession()) return Unauthorized(new { ok = false, message = "Your login session has expired." });
        try
        {
            var row = await _calendar.GetBookingDetailsAsync(SessionValue("hotel"), regId ?? "", paymentId, cancellationToken);
            return row == null ? NotFound(new { ok = false, message = "Booking details were not found." }) : Json(new { ok = true, data = row });
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Calendar booking details failed for {RegId}/{PaymentId}.", regId, paymentId);
            var errorRef = await _dbLogger.LogExceptionAsync(ex, "Front Desk Calendar", "Booking Details", SessionValue("hotel"), SessionValue("UserId"), SessionValue("UserName"), Ip(), cancellationToken);
            return StatusCode(500, new { ok = false, message = $"Unable to load reservation details. Ref: {errorRef}" });
        }
    }

    [HttpGet("GuestHistory")]
    public async Task<IActionResult> GuestHistory(string regId, CancellationToken cancellationToken)
    {
        if (!HasSession()) return Unauthorized(new { ok = false, message = "Your login session has expired." });
        try
        {
            var row = await _calendar.GetGuestHistoryAsync(SessionValue("hotel"), regId ?? "", cancellationToken);
            return row == null ? NotFound(new { ok = false, message = "Guest history was not found." }) : Json(new { ok = true, data = row });
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Calendar guest history failed for {RegId}.", regId);
            var errorRef = await _dbLogger.LogExceptionAsync(ex, "Front Desk Calendar", "Guest History", SessionValue("hotel"), SessionValue("UserId"), SessionValue("UserName"), Ip(), cancellationToken);
            return StatusCode(500, new { ok = false, message = $"Unable to load guest history. Ref: {errorRef}" });
        }
    }

    [HttpGet("GuestHistoryPage")]
    public async Task<IActionResult> GuestHistoryPage(string regId, CancellationToken cancellationToken)
    {
        if (!HasSession()) return RedirectToAction("Index", "LoginHMS");
        var row = await _calendar.GetGuestHistoryAsync(SessionValue("hotel"), regId ?? string.Empty, cancellationToken);
        if (row == null) return NotFound("Guest history was not found.");
        return View("GuestHistory", row);
    }

    [HttpPost("SaveNote"), ValidateAntiForgeryToken]
    public Task<IActionResult> SaveNote([FromBody] FrontDeskNoteRequest request, CancellationToken ct) => Mutate(
        () => _calendar.SaveNoteAsync(SessionValue("hotel"), SessionValue("UserId"), SessionValue("UserName"), Ip(), request, ct));

    [HttpPost("MarkRoomClean"), ValidateAntiForgeryToken]
    public Task<IActionResult> MarkRoomClean([FromBody] FrontDeskRoomCleanRequest request, CancellationToken ct) => Mutate(
        () => _calendar.MarkRoomCleanAsync(SessionValue("hotel"), SessionValue("UserId"), SessionValue("UserName"), Ip(), request, ct));

    [HttpPost("BlockRoom"), ValidateAntiForgeryToken]
    public Task<IActionResult> BlockRoom([FromBody] FrontDeskRoomBlockRequest request, CancellationToken ct) => Mutate(
        () => _calendar.BlockRoomAsync(SessionValue("hotel"), SessionValue("HotelName"), SessionValue("UserId"), SessionValue("UserName"), Ip(), request, ct));

    [HttpPost("UpdateBlock"), ValidateAntiForgeryToken]
    public Task<IActionResult> UpdateBlock([FromBody] FrontDeskRoomBlockUpdateRequest request, CancellationToken ct) => Mutate(
        () => _calendar.UpdateBlockAsync(SessionValue("hotel"), SessionValue("HotelName"), SessionValue("UserId"), SessionValue("UserName"), Ip(), request, ct));

    [HttpPost("RemoveBlock"), ValidateAntiForgeryToken]
    public Task<IActionResult> RemoveBlock([FromBody] FrontDeskBlockActionRequest request, CancellationToken ct) => Mutate(
        () => _calendar.RemoveBlockAsync(SessionValue("hotel"), SessionValue("HotelName"), SessionValue("UserId"), SessionValue("UserName"), Ip(), request, ct));

    [HttpPost("ActivateRoom"), ValidateAntiForgeryToken]
    public Task<IActionResult> ActivateRoom([FromBody] FrontDeskBlockActionRequest request, CancellationToken ct) => Mutate(
        () => _calendar.ActivateRoomFromTodayAsync(SessionValue("hotel"), SessionValue("HotelName"), SessionValue("UserId"), SessionValue("UserName"), Ip(), request, ct));

    [HttpPost("MoveBooking"), ValidateAntiForgeryToken]
    public Task<IActionResult> MoveBooking([FromBody] FrontDeskBookingMoveRequest request, CancellationToken ct) => Mutate(
        () => _calendar.MoveBookingAsync(SessionValue("hotel"), SessionValue("HotelName"), SessionValue("UserId"), SessionValue("UserName"), Ip(), request, ct));

    [HttpPost("UpdateRoomOccupancy"), ValidateAntiForgeryToken]
    public Task<IActionResult> UpdateRoomOccupancy([FromBody] FrontDeskRoomOccupancyRequest request, CancellationToken ct) => Mutate(
        () => _calendar.UpdateRoomOccupancyAsync(SessionValue("hotel"), SessionValue("UserId"), SessionValue("UserName"), Ip(), request, ct));

    [HttpPost("DeleteRoomSegment"), ValidateAntiForgeryToken]
    public Task<IActionResult> DeleteRoomSegment([FromBody] FrontDeskDeleteRoomSegmentRequest request, CancellationToken ct) => Mutate(
        () => _calendar.DeleteRoomSegmentAsync(SessionValue("hotel"), SessionValue("HotelName"),
            SessionValue("UserId"), SessionValue("UserName"), Ip(), request.RegId, request.PaymentId, ct));

    [HttpPost("ResizeBooking"), ValidateAntiForgeryToken]
    public Task<IActionResult> ResizeBooking([FromBody] FrontDeskBookingResizeRequest request, CancellationToken ct) => Mutate(
        () => _calendar.ResizeBookingAsync(SessionValue("hotel"), SessionValue("HotelName"), SessionValue("UserId"), SessionValue("UserName"), Ip(), request, ct));

    [HttpPost("ConfirmReservation"), ValidateAntiForgeryToken]
    public Task<IActionResult> ConfirmReservation([FromBody] FrontDeskConfirmReservationRequest request, CancellationToken ct) => Mutate(
        () => _calendar.ConfirmReservationAsync(SessionValue("hotel"), SessionValue("UserId"), SessionValue("UserName"), Ip(), request, ct));

    [HttpPost("DirectCheckIn"), ValidateAntiForgeryToken]
    public Task<IActionResult> DirectCheckIn([FromBody] FrontDeskDirectCheckInRequest request, CancellationToken ct) => Mutate(
        () => _calendar.DirectCheckInAsync(SessionValue("hotel"), SessionValue("HotelName"), SessionValue("UserId"), SessionValue("UserName"), Ip(), request, ct));

    [HttpPost("PrepareEmail"), ValidateAntiForgeryToken]
    public async Task<IActionResult> PrepareEmail(
        [FromBody] FrontDeskEmailPrepareRequest request,
        CancellationToken ct)
    {
        if (!HasSession())
            return Unauthorized(new { ok = false, message = "Your login session has expired." });

        if (!ModelState.IsValid)
            return BadRequest(new { ok = false, message = "Please check the entered values." });

        try
        {
            FrontDeskOperationResult result = await _calendar.PrepareEmailComposerAsync(
                SessionValue("hotel"),
                SessionValue("UserId"),
                BaseUrl(),
                request,
                ct);

            if (result.Success && result.Data is FrontDeskEmailComposerDto data)
            {
                string shareUrl = BuildInvoiceShareUrl(
                    SessionValue("hotel"),
                    data.RegId);

                string oldInvoiceUrl = data.InvoiceUrl ?? string.Empty;
                string oldInvoicePdfUrl = data.InvoicePdfUrl ?? string.Empty;
                string oldPaymentUrl = data.PaymentUrl ?? string.Empty;

                data.InvoiceUrl = shareUrl;
                data.InvoicePdfUrl = shareUrl;

                if (oldInvoiceUrl.Length > 0)
                    data.InvoiceMessage = (data.InvoiceMessage ?? string.Empty)
                        .Replace(oldInvoiceUrl, shareUrl, StringComparison.Ordinal);

                if (oldInvoicePdfUrl.Length > 0)
                    data.InvoicePdfMessage = (data.InvoicePdfMessage ?? string.Empty)
                        .Replace(oldInvoicePdfUrl, shareUrl, StringComparison.Ordinal);

                // The calendar service keeps producing the established PayNow values.
                // Protect only the customer-facing URL before it leaves the controller.
                if (oldPaymentUrl.Length > 0)
                {
                    string securePaymentUrl = BuildPayNowShareUrl(oldPaymentUrl);
                    data.PaymentUrl = securePaymentUrl;

                    // The PayNow URL is used in more than the Payment Link tab.
                    // In particular, Invoice Link also includes a payment link when
                    // an outstanding balance exists. Replace it everywhere before
                    // the composer DTO is returned to the browser.
                    data.PaymentMessage = (data.PaymentMessage ?? string.Empty)
                        .Replace(oldPaymentUrl, securePaymentUrl, StringComparison.Ordinal);
                    data.InvoiceMessage = (data.InvoiceMessage ?? string.Empty)
                        .Replace(oldPaymentUrl, securePaymentUrl, StringComparison.Ordinal);
                    data.InvoicePdfMessage = (data.InvoicePdfMessage ?? string.Empty)
                        .Replace(oldPaymentUrl, securePaymentUrl, StringComparison.Ordinal);
                    data.GenericMessage = (data.GenericMessage ?? string.Empty)
                        .Replace(oldPaymentUrl, securePaymentUrl, StringComparison.Ordinal);
                }
            }

            return Json(new { ok = result.Success, message = result.Message, data = result.Data });
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Front desk calendar email preparation failed.");
            var errorRef = await _dbLogger.LogExceptionAsync(
                ex,
                "Front Desk Calendar",
                "Prepare Email",
                SessionValue("hotel"),
                SessionValue("UserId"),
                SessionValue("UserName"),
                Ip(),
                ct);

            return StatusCode(500, new
            {
                ok = false,
                message = $"The email could not be prepared. Ref: {errorRef}"
            });
        }
    }

    [HttpPost("SendEmail"), ValidateAntiForgeryToken]
    public Task<IActionResult> SendEmail([FromBody] FrontDeskEmailSendRequest request, CancellationToken ct) => Mutate(
        () => _calendar.SendEmailAsync(SessionValue("hotel"), SessionValue("UserId"), SessionValue("UserName"), Ip(), request, ct));

    [HttpPost("NoShow"), ValidateAntiForgeryToken]
    public Task<IActionResult> NoShow([FromBody] FrontDeskNoShowRequest request, CancellationToken ct) => Mutate(
        () => _calendar.NoShowAsync(SessionValue("hotel"), SessionValue("HotelName"), SessionValue("UserId"), SessionValue("UserName"), Ip(), request, ct));

    [HttpPost("CancelReservation"), ValidateAntiForgeryToken]
    public Task<IActionResult> CancelReservation([FromBody] FrontDeskCancelReservationRequest request, CancellationToken ct) => Mutate(
        () => _calendar.CancelReservationAsync(SessionValue("hotel"), SessionValue("HotelName"), SessionValue("UserId"), SessionValue("UserName"), Ip(), request, ct));

    private async Task<IActionResult> Mutate(Func<Task<FrontDeskOperationResult>> action)
    {
        if (!HasSession()) return Unauthorized(new { ok = false, message = "Your login session has expired." });
        if (!ModelState.IsValid) return BadRequest(new { ok = false, message = "Please check the entered values." });
        try
        {
            var result = await action();
            return Json(new { ok = result.Success, message = result.Message, data = result.Data });
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Front desk calendar action failed.");
            var errorRef = await _dbLogger.LogExceptionAsync(ex, "Front Desk Calendar", "Calendar Action", SessionValue("hotel"), SessionValue("UserId"), SessionValue("UserName"), Ip());
            return StatusCode(500, new { ok = false, message = $"The calendar action could not be completed. Ref: {errorRef}" });
        }
    }

    // Protect reservation/room identifiers before placing them in a browser URL.
    // Existing Calendar anti-forgery token and user session are required.
    [HttpPost("RestaurantAccessUrl"), ValidateAntiForgeryToken]
    public async Task<IActionResult> RestaurantAccessUrl([FromBody] RestaurantCalendarLinkRequest? request, CancellationToken ct)
    {
        if (!HasSession() || string.IsNullOrWhiteSpace(SessionValue("UserId")))
            return Unauthorized(new { message = "Your PMS session has expired." });
        if (SessionValue("Role").Contains("Council", StringComparison.OrdinalIgnoreCase))
            return Forbid();
        var regId = request?.RegId?.Trim() ?? "";
        var roomNo = request?.RoomNo?.Trim() ?? "";
        if (regId.Length is < 1 or > 100 || roomNo.Length is < 1 or > 100 ||
            string.Equals(roomNo, "UNASSIGNED", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { message = "Select a checked-in guest with an assigned room." });
        try
        {
            await using var cn = new SqlConnection(_connectionString);
            await using var cmd = new SqlCommand(@"SELECT TOP (1) ISNULL(isResturant,0)
                FROM dbo.HotelsSignUpTB WHERE hotel_id=@hotel", cn);
            cmd.Parameters.AddWithValue("@hotel", SessionValue("hotel"));
            await cn.OpenAsync(ct);
            var enabled = await cmd.ExecuteScalarAsync(ct);
            if (enabled is null || enabled == DBNull.Value ||
                !(string.Equals(Convert.ToString(enabled), "True", StringComparison.OrdinalIgnoreCase) ||
                  string.Equals(Convert.ToString(enabled), "1", StringComparison.OrdinalIgnoreCase)))
                return BadRequest(new { message = "Restaurant is not enabled for this hotel." });

            // Encrypt and authenticate the parameters; Home still verifies room/check-in in SQL.
            var guestToken = _restaurantGuestLinks.Create(SessionValue("hotel"), SessionValue("UserId"), regId, roomNo);
            var link = Url.Action("Home", "Restaurant", new { guestToken });
            if (string.IsNullOrWhiteSpace(link))
                throw new InvalidOperationException("The Restaurant Home route could not be resolved.");
            return Json(new { url = link });
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Cannot generate protected Restaurant guest link.");
            return StatusCode(500, new { message = "Unable to open Restaurant. Please try again." });
        }
    }

    private string BuildInvoiceShareUrl(string hotelId, string regId)
    {
        string token = _invoiceShareProtector.Protect(
            (hotelId ?? string.Empty).Trim() + "\n" +
            (regId ?? string.Empty).Trim());

        return BaseUrl().TrimEnd('/') +
               "/InvoiceRecieving/i/" +
               Uri.EscapeDataString(token);
    }

    private string BuildPayNowShareUrl(string existingUrl)
    {
        if (string.IsNullOrWhiteSpace(existingUrl))
            return string.Empty;

        try
        {
            var uri = new Uri(existingUrl, UriKind.Absolute);
            string payload = uri.Query.TrimStart('?');
            if (payload.Length == 0)
                return existingUrl;

            string token = _payNowShareProtector.Protect(payload);

            // Put the protected token in the query string, not a URL path
            // segment. HTTP.sys/IIS can reject long path segments before MVC
            // receives the request (400 Bad Request - Invalid URL).
            return (_payNowPublicBaseUrl ?? BaseUrl().TrimEnd('/')) +
                   "/PayNow/p?t=" +
                   Uri.EscapeDataString(token);
        }
        catch
        {
            // Never break email preparation because URL protection failed.
            // Existing behavior remains available as a safe fallback.
            return existingUrl;
        }
    }

    private bool HasSession() => !string.IsNullOrWhiteSpace(SessionValue("hotel"));
    private string SessionValue(string key) => HttpContext.Session.GetString(key) ?? string.Empty;
    private string BaseUrl() => $"{Request.Scheme}://{Request.Host}{Request.PathBase}";
    private string Ip() => HttpContext.Connection.RemoteIpAddress?.ToString() ?? string.Empty;
}
