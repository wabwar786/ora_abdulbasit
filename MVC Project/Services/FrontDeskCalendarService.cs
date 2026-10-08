using System.Data;
using System.Globalization;
using System.Net;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Caching.Memory;
using Orapmshms.Models;
using Orapmshms.Services.Logging;
using Orapmshms.Services.AvailabilityJobs;

namespace Orapmshms.Services;

/// <summary>
/// Fast ASP.NET Core conversion of FrontDeskCalender.aspx.
///
/// Performance rules:
/// - the visible calendar is loaded in one SQL round-trip with multiple result sets;
/// - no SQL is executed per cell, per room or per day;
/// - booking drawer/history data is loaded only when the user opens it;
/// - permission metadata is cached briefly per hotel/user;
/// - heavy availability + Channex recalculation is queued after writes and never blocks the UI request.
/// </summary>
public sealed class FrontDeskCalendarService : IFrontDeskCalendarService
{
    private readonly string _connectionString;
    private readonly IHotelClock _hotelClock;
    private readonly IAvailabilityAutoUpdateQueue _availabilityQueue;
    private readonly ICheckInService _checkInService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMemoryCache _cache;
    private readonly IAppLogger _logger;
    private readonly FrontDeskCalendarDbLogger _dbLogger;

    private static readonly string[] DateFormats =
    {
        "MM-dd-yyyy", "yyyy-MM-dd", "dd/MM/yyyy", "dd-MM-yyyy", "M/d/yyyy", "M-d-yyyy"
    };

    public FrontDeskCalendarService(
        IConfiguration configuration,
        IHotelClock hotelClock,
        IAvailabilityAutoUpdateQueue availabilityQueue,
        ICheckInService checkInService,
        IHttpClientFactory httpClientFactory,
        IMemoryCache cache,
        IAppLogger logger,
        FrontDeskCalendarDbLogger? dbLogger = null)
    {
        _connectionString = configuration.GetConnectionString("con")
            ?? throw new InvalidOperationException("ConnectionStrings:con is missing.");
        _hotelClock = hotelClock;
        _availabilityQueue = availabilityQueue;
        _checkInService = checkInService;
        _httpClientFactory = httpClientFactory;
        _cache = cache;
        _logger = logger;
        _dbLogger = dbLogger ?? new FrontDeskCalendarDbLogger(_connectionString, _hotelClock, _logger);
    }

    public async Task<FrontDeskCalendarPageViewModel> GetPageAsync(
        string hotelId, string hotelName, string userId, string userName, string role,
        DateTime? start, CancellationToken ct = default)
    {
        EnsureSession(hotelId, userId);
        var today = _hotelClock.GetHotelToday(hotelId);
        var startDate = (start ?? today).Date;
        var permissionsTask = GetPermissionsAsync(hotelId, userId, role, ct);
        var currencyTask = GetCurrencySymbolAsync(hotelId, ct);
        var paymentCapabilitiesTask = GetPaymentCapabilitiesAsync(hotelId, ct);
        await Task.WhenAll(permissionsTask, currencyTask, paymentCapabilitiesTask);

        var permissions = await permissionsTask;
        var paymentCapabilities = await paymentCapabilitiesTask;

        return new FrontDeskCalendarPageViewModel
        {
            HotelId = hotelId,
            HotelName = hotelName,
            UserId = userId,
            UserName = userName,
            Role = role,
            CurrencySymbol = await currencyTask,
            HotelToday = today,
            StartDate = startDate,
            ViewDays = 20,
            // Same idea as the old WebForms calendar/check-in screens:
            // action permission AND a configured provider are both required.
            CanOnlineCardPayment = permissions.CanOnlineCardPayment && paymentCapabilities.StripeConfigured,
            CanPdqPayment = permissions.CanPdqPayment && (paymentCapabilities.StripeConfigured || paymentCapabilities.CloverConfigured),
            Permissions = permissions
        };
    }

    public async Task<FrontDeskCalendarPayload> GetCalendarAsync(
        string hotelId, string userId, string userName, string role,
        DateTime start, int days, CancellationToken ct = default)
    {
        EnsureSession(hotelId, userId);
        days = Math.Clamp(days <= 0 ? 20 : days, 1, 60);
        start = start.Date;
        var end = start.AddDays(days - 1);
        var hotelToday = _hotelClock.GetHotelToday(hotelId);
        var isCouncil = role.Equals("Council", StringComparison.OrdinalIgnoreCase);

        var categories = new List<FrontDeskCategoryDto>();
        var rooms = new List<FrontDeskRoomDto>();
        var bookings = new List<FrontDeskBookingDto>();
        var blocks = new List<FrontDeskBlockDto>();

        const string sql = @"
SET NOCOUNT ON;

/* 1) Categories */
SELECT
    CONVERT(varchar(100),ISNULL(cr.localcategoryid,'')) AS localcategoryid,
    ISNULL(cr.description,'') AS description,
    ISNULL(TRY_CONVERT(decimal(18,2),cr.rate),0) AS base_rate,
    ISNULL(TRY_CONVERT(int,cr.no_of_rooms),0) AS no_of_rooms,
    ISNULL(s.Occupied,0) AS Occupied,
    ISNULL(s.Dirty,0) AS Dirty,
    ISNULL(s.Available,0) AS Available,
    ISNULL(s.NotBooked,0) AS NotBooked,
    ISNULL(s.Blocked,0) AS Blocked
FROM dbo.create_room cr WITH (READPAST)
OUTER APPLY
(
    SELECT
        COUNT(CASE WHEN r.room_status='Occupied' THEN 1 END) AS Occupied,
        COUNT(CASE WHEN r.room_status IN ('NotClean','CheckOut') THEN 1 END) AS Dirty,
        COUNT(CASE WHEN r.room_status='Available' THEN 1 END) AS Available,
        COUNT(CASE WHEN r.room_status='Not Booked' THEN 1 END) AS NotBooked,
        COUNT(CASE WHEN r.room_status='Blocked' THEN 1 END) AS Blocked
    FROM dbo.RoomsTB r WITH (READPAST)
    WHERE r.Hotel_id=@hotel AND r.room_category=cr.description
) s
WHERE cr.hotel_id=@hotel AND LTRIM(RTRIM(ISNULL(cr.category,'')))='Room Rent'
ORDER BY ISNULL(cr.orderid,999999), cr.description;

/* 2) Physical rooms */
SELECT
    ISNULL(r.room_no,'') AS room_no,
    ISNULL(r.room_category,'') AS room_category,
    ISNULL(CONVERT(varchar(100),cr.localcategoryid),'') AS localcategoryid,
    ISNULL(r.room_status,'') AS room_status,
    CASE WHEN ISNULL(ca.CouncilAssignmentCount,0) > 0 THEN 1 ELSE 0 END AS IsAssignedToCouncil,
    ca.CouncilAssignmentStartDate,
    ca.CouncilAssignmentEndDate
FROM dbo.RoomsTB r WITH (READPAST)
LEFT JOIN dbo.create_room cr WITH (READPAST)
    ON cr.hotel_id=r.Hotel_id AND cr.description=r.room_category
   AND LTRIM(RTRIM(ISNULL(cr.category,'')))='Room Rent'
OUTER APPLY
(
    SELECT
        COUNT_BIG(1) AS CouncilAssignmentCount,
        MIN(TRY_CONVERT(date,ura2.FromDate)) AS CouncilAssignmentStartDate,
        MAX(TRY_CONVERT(date,ura2.ToDate)) AS CouncilAssignmentEndDate
    FROM dbo.UserRoomAccess ura2 WITH (READPAST)
    INNER JOIN dbo.Hms_accounts ha WITH (READPAST) ON ha.user_id=ura2.UserId
    WHERE ura2.HotelId=r.Hotel_id
      AND ura2.RoomNo=r.room_no
      AND LOWER(LTRIM(RTRIM(ISNULL(ha.role,''))))='council'
) ca
WHERE r.Hotel_id=@hotel
  AND (@isCouncil=0 OR EXISTS
      (SELECT 1 FROM dbo.UserRoomAccess ura WITH (READPAST)
       WHERE ura.HotelId=r.Hotel_id AND ura.RoomNo=r.room_no AND CONVERT(varchar(100),ura.UserId)=@userId))
ORDER BY ISNULL(cr.orderid,999999), r.room_category, TRY_CONVERT(int,r.room_no), r.room_no;

/* 3) Visible booking bars. payments is authoritative for room-level stays. */
;WITH LatestGuest AS
(
    SELECT *, ROW_NUMBER() OVER(PARTITION BY hotel_id,reg_id ORDER BY id DESC) AS rn
    FROM dbo.GuestInformationLogTB WITH (READPAST)
    WHERE hotel_id=@hotel
),
LatestReservation AS
(
    SELECT *, ROW_NUMBER() OVER(PARTITION BY hotel_id,reg_id ORDER BY id DESC) AS rn
    FROM dbo.NewReservationsTB WITH (READPAST)
    WHERE hotel_id=@hotel
),
PaymentRows AS
(
    SELECT
        p.ID AS PaymentId,
        CONVERT(varchar(100),p.reg_id) AS reg_id,
        ISNULL(CONVERT(varchar(100),g.visit_id),'') AS visit_id,
        ISNULL(LTRIM(RTRIM(p.room_no)),'') AS room_no,
        ISNULL(p.[Type],'') AS room_category,
        ISNULL(CONVERT(varchar(100),cr.localcategoryid),'') AS localcategoryid,
        COALESCE(TRY_CONVERT(date,p.ArrivalDate,110),TRY_CONVERT(date,p.ArrivalDate,23),TRY_CONVERT(date,p.ArrivalDate,103),TRY_CONVERT(date,p.ArrivalDate)) AS ArrivalDate,
        COALESCE(TRY_CONVERT(date,p.DepartureDate,110),TRY_CONVERT(date,p.DepartureDate,23),TRY_CONVERT(date,p.DepartureDate,103),TRY_CONVERT(date,p.DepartureDate)) AS DepartureDate,
        ISNULL(p.res_status,'') AS res_status,
        ISNULL(p.rateplanname,'') AS planname,
        ISNULL(TRY_CONVERT(decimal(18,2),p.rate),0) AS row_rate,
        ISNULL(TRY_CONVERT(decimal(18,2),p.GST),0) AS row_gst,
        ISNULL(TRY_CONVERT(decimal(18,2),p.Bed),0) AS row_bed,
        ISNULL(p.guestname,'') AS room_guest_name,
        ISNULL(p.room_adults,0) AS room_adults,
        ISNULL(p.room_children,0) AS room_children,
        ISNULL(p.room_infants,0) AS room_infants,
        ISNULL(CONVERT(varchar(100),g.visit_id),'') AS master_visit_id,
        ISNULL(g.Bookid,n.Bookid) AS Bookid,
        LTRIM(RTRIM(CONCAT(ISNULL(g.GuestName,n.GuestName),' ',ISNULL(g.LastName,n.LastName)))) AS master_name,
        ISNULL(g.PhoneNo,n.PhoneNo) AS PhoneNo,
        ISNULL(g.Agency,n.Agency) AS Agency,
        ISNULL(g.booking_id,n.booking_id) AS booking_id,
        ISNULL(g.is_virtual,n.is_virtual) AS is_virtual,
        ISNULL(g.createdRole,n.createdRole) AS createdRole,
        ISNULL(CONVERT(varchar(100),g.user_id),CONVERT(varchar(100),n.user_id)) AS created_user_id,
        COALESCE(CONVERT(varchar(50),g.expirydate),CONVERT(varchar(50),n.expirydate),'') AS expirydate,
        ISNULL(TRY_CONVERT(int,g.NumberOfAdults),ISNULL(TRY_CONVERT(int,n.number_of_adult),0)) AS master_adults,
        ISNULL(TRY_CONVERT(int,g.NumberOfMinors),ISNULL(TRY_CONVERT(int,n.number_of_minor),0)) AS master_children,
        ISNULL(TRY_CONVERT(decimal(18,2),u.grand_total),0) AS grand_total,
        ISNULL(TRY_CONVERT(decimal(18,2),u.paid_amount),0) AS paid_amount,
        ISNULL(TRY_CONVERT(decimal(18,2),u.remaining_amount),0) AS remaining_amount,
        ISNULL(TRY_CONVERT(decimal(18,2),u.discount),0) AS discount
    FROM dbo.payments p WITH (READPAST)
    LEFT JOIN LatestGuest g ON g.hotel_id=p.hotel_id AND g.reg_id=p.reg_id AND g.rn=1
    LEFT JOIN LatestReservation n ON n.hotel_id=p.hotel_id AND n.reg_id=p.reg_id AND n.rn=1
    LEFT JOIN dbo.create_room cr WITH (READPAST)
        ON cr.hotel_id=p.hotel_id AND cr.description=p.[Type]
       AND LTRIM(RTRIM(ISNULL(cr.category,'')))='Room Rent'
    OUTER APPLY
    (
        SELECT TOP (1) pu.grand_total,pu.paid_amount,pu.remaining_amount,pu.discount
        FROM dbo.PaymentsUpdateTB pu WITH (READPAST)
        WHERE pu.hotel_id=p.hotel_id AND pu.reg_id=p.reg_id
        ORDER BY pu.id DESC
    ) u
    WHERE p.hotel_id=@hotel
      -- A payment row can supply room-level details only while the reservation
      -- still exists in one of the two master reservation tables.  This keeps
      -- orphan payment rows from rendering ghost bookings on either calendar.
      AND (g.reg_id IS NOT NULL OR n.reg_id IS NOT NULL)
      AND LTRIM(RTRIM(ISNULL(p.descr,'')))='Room Rent'
      AND LOWER(LTRIM(RTRIM(ISNULL(p.res_status,'')))) IN ('reservation','check in','check out','provisional','tentative')
      AND COALESCE(TRY_CONVERT(date,p.ArrivalDate,110),TRY_CONVERT(date,p.ArrivalDate,23),TRY_CONVERT(date,p.ArrivalDate,103),TRY_CONVERT(date,p.ArrivalDate)) <= @viewEnd
      AND COALESCE(TRY_CONVERT(date,p.DepartureDate,110),TRY_CONVERT(date,p.DepartureDate,23),TRY_CONVERT(date,p.DepartureDate,103),TRY_CONVERT(date,p.DepartureDate)) >= @viewStart
      AND
      (
          @isCouncil=0
          OR UPPER(LTRIM(RTRIM(ISNULL(p.room_no,''))))='UNASSIGNED'
          OR EXISTS (SELECT 1 FROM dbo.UserRoomAccess ura WITH (READPAST)
                     WHERE ura.HotelId=p.hotel_id AND ura.RoomNo=p.room_no AND CONVERT(varchar(100),ura.UserId)=@userId)
      )
)
SELECT * FROM PaymentRows
WHERE ArrivalDate IS NOT NULL AND DepartureDate IS NOT NULL
ORDER BY ArrivalDate, DepartureDate, PaymentId;

/* 4) Active room blocks. End date is stored inclusive. */
SELECT
    b.BlockID,
    ISNULL(b.RoomNo,'') AS RoomNo,
    ISNULL(CONVERT(varchar(100),cr.localcategoryid),'') AS localcategoryid,
    ISNULL(r.room_category,cr.description) AS room_category,
    COALESCE(TRY_CONVERT(date,b.BlockStartDate,110),TRY_CONVERT(date,b.BlockStartDate,23),TRY_CONVERT(date,b.BlockStartDate)) AS BlockStartDate,
    COALESCE(TRY_CONVERT(date,b.BlockEndDate,110),TRY_CONVERT(date,b.BlockEndDate,23),TRY_CONVERT(date,b.BlockEndDate)) AS BlockEndDate,
    ISNULL(b.Reason,'') AS Reason,
    CAST('' AS varchar(500)) AS Note
FROM dbo.RoomBlocksTB b WITH (READPAST)
LEFT JOIN dbo.RoomsTB r WITH (READPAST) ON r.Hotel_id=b.HotelID AND r.room_no=b.RoomNo
LEFT JOIN dbo.create_room cr WITH (READPAST)
    ON cr.hotel_id=b.HotelID AND cr.description=r.room_category
   AND LTRIM(RTRIM(ISNULL(cr.category,'')))='Room Rent'
WHERE b.HotelID=@hotel AND ISNULL(b.IsActive,0)=1
  AND COALESCE(TRY_CONVERT(date,b.BlockStartDate,110),TRY_CONVERT(date,b.BlockStartDate,23),TRY_CONVERT(date,b.BlockStartDate)) <= @viewEnd
  AND COALESCE(TRY_CONVERT(date,b.BlockEndDate,110),TRY_CONVERT(date,b.BlockEndDate,23),TRY_CONVERT(date,b.BlockEndDate)) >= @viewStart
ORDER BY BlockStartDate, BlockID;";

        await using var cn = new SqlConnection(_connectionString);
        await cn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, cn) { CommandTimeout = 25 };
        cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
        cmd.Parameters.Add("@userId", SqlDbType.VarChar, 100).Value = userId;
        cmd.Parameters.Add("@isCouncil", SqlDbType.Bit).Value = isCouncil;
        cmd.Parameters.Add("@viewStart", SqlDbType.Date).Value = start;
        cmd.Parameters.Add("@viewEnd", SqlDbType.Date).Value = end;

        await using var rd = await cmd.ExecuteReaderAsync(ct);

        while (await rd.ReadAsync(ct))
        {
            categories.Add(new FrontDeskCategoryDto
            {
                Id = S(rd, "localcategoryid"),
                Name = S(rd, "description"),
                BaseRate = M(rd, "base_rate"),
                TotalRooms = I(rd, "no_of_rooms"),
                Occupied = I(rd, "Occupied"),
                Dirty = I(rd, "Dirty"),
                Available = I(rd, "Available"),
                NotBooked = I(rd, "NotBooked"),
                Blocked = I(rd, "Blocked")
            });
        }

        if (await rd.NextResultAsync(ct))
        {
            while (await rd.ReadAsync(ct))
            {
                var raw = S(rd, "room_status");
                var condition = raw.Equals("NotClean", StringComparison.OrdinalIgnoreCase) ||
                                raw.Equals("CheckOut", StringComparison.OrdinalIgnoreCase)
                    ? "Dirty"
                    : raw.Equals("Blocked", StringComparison.OrdinalIgnoreCase) ? "Blocked" : "Clean";

                rooms.Add(new FrontDeskRoomDto
                {
                    RoomNo = S(rd, "room_no"),
                    CategoryName = S(rd, "room_category"),
                    CategoryId = S(rd, "localcategoryid"),
                    Condition = condition,
                    DirtyDate = condition == "Dirty" ? hotelToday : null,
                    IsAssignedToCouncil = B(rd, "IsAssignedToCouncil"),
                    CouncilAssignmentStartDate = D(rd, "CouncilAssignmentStartDate"),
                    CouncilAssignmentEndDate = D(rd, "CouncilAssignmentEndDate")
                });
            }
        }

        var permissions = await GetPermissionsAsync(hotelId, userId, role, ct);

        if (await rd.NextResultAsync(ct))
        {
            while (await rd.ReadAsync(ct))
            {
                var arrival = D(rd, "ArrivalDate");
                var departure = D(rd, "DepartureDate");
                if (!arrival.HasValue || !departure.HasValue || departure <= arrival) continue;

                var regId = S(rd, "reg_id");
                var paymentId = I(rd, "PaymentId");
                var roomNo = S(rd, "room_no");
                var roomGuest = S(rd, "room_guest_name");
                var masterName = S(rd, "master_name");
                var statusRaw = S(rd, "res_status");
                var statusCode = LegacyStatusCode(statusRaw);
                if (statusCode == "P")
                {
                    var expiryRaw = S(rd, "expirydate");
                    if (!string.IsNullOrWhiteSpace(expiryRaw) &&
                        TryParseLegacyDate(expiryRaw, out var expiryDate) &&
                        expiryDate.Date < hotelToday.Date)
                    {
                        continue;
                    }
                }

                var total = M(rd, "grand_total");
                var paid = M(rd, "paid_amount");
                var balance = M(rd, "remaining_amount");
                var adults = I(rd, "room_adults");
                var children = I(rd, "room_children");
                var infants = I(rd, "room_infants");
                if (adults + children + infants <= 0)
                {
                    adults = I(rd, "master_adults");
                    children = I(rd, "master_children");
                }

                bookings.Add(new FrontDeskBookingDto
                {
                    Id = paymentId > 0 ? $"P{paymentId}" : $"R{regId}",
                    RegId = regId,
                    PaymentId = paymentId,
                    VisitId = FirstNonEmpty(S(rd, "visit_id"), S(rd, "master_visit_id")),
                    BookId = S(rd, "Bookid"),
                    // A guest name saved against this specific room/payment row takes priority.
                    // Only fall back to the reservation/base-table guest when no room guest was set.
                    GuestName = string.IsNullOrWhiteSpace(roomGuest) ? masterName : roomGuest,
                    Phone = S(rd, "PhoneNo"),
                    RoomNo = roomNo.Equals("UNASSIGNED", StringComparison.OrdinalIgnoreCase) ? string.Empty : roomNo,
                    CategoryName = S(rd, "room_category"),
                    CategoryId = S(rd, "localcategoryid"),
                    Arrival = arrival.Value,
                    Departure = departure.Value,
                    Status = DisplayStatus(statusRaw, arrival.Value, hotelToday),
                    StatusCode = statusCode,
                    Source = S(rd, "Agency"),
                    PlanName = S(rd, "planname"),
                    Rate = M(rd, "row_rate"),
                    Gst = M(rd, "row_gst"),
                    Bed = M(rd, "row_bed"),
                    Total = total,
                    Paid = paid,
                    Balance = balance,
                    Discount = M(rd, "discount"),
                    PaymentStatus = LegacyPaymentStatus(total, paid, balance),
                    ChannexBookingId = S(rd, "booking_id"),
                    IsVirtualCard = B(rd, "is_virtual"),
                    ShowAutoPay = B(rd, "is_virtual") && !string.IsNullOrWhiteSpace(S(rd, "booking_id")) &&
                                  !S(rd, "booking_id").Equals("N/A", StringComparison.OrdinalIgnoreCase) && balance > 0.005m,
                    CreatedRole = S(rd, "createdRole"),
                    CreatedByUserId = S(rd, "created_user_id"),
                    Adults = adults,
                    Children = children,
                    Infants = infants,
                    CanDrag = permissions.CanDragDrop && !IsCancelledStatus(statusRaw),
                    CanResize = permissions.CanExtendShrink && !IsClosedStatus(statusRaw)
                });
            }
        }

        if (await rd.NextResultAsync(ct))
        {
            while (await rd.ReadAsync(ct))
            {
                var blockStart = D(rd, "BlockStartDate");
                var blockEnd = D(rd, "BlockEndDate");
                if (!blockStart.HasValue || !blockEnd.HasValue) continue;
                var reason = S(rd, "Reason");
                var note = S(rd, "Note");
                blocks.Add(new FrontDeskBlockDto
                {
                    BlockId = I(rd, "BlockID"),
                    RoomNo = S(rd, "RoomNo"),
                    CategoryId = S(rd, "localcategoryid"),
                    CategoryName = S(rd, "room_category"),
                    StartDate = blockStart.Value.Date,
                    EndDate = blockEnd.Value.Date.AddDays(1),
                    Reason = reason,
                    Kind = reason.Contains("maintenance", StringComparison.OrdinalIgnoreCase) ||
                           note.Contains("maintenance", StringComparison.OrdinalIgnoreCase)
                        ? "Maintenance" : "Room Block"
                });
            }
        }

        // Match the legacy WebForms calendar indicators without putting either
        // lookup into the main calendar SQL. Keeping them isolated means an old
        // or optional indicator table can never stop the calendar itself loading.
        // Both lookups are reservation-level, so every room bar for the same
        // reservation receives the same marker.
        var visibleRegIds = bookings
            .Select(x => NormalizeRegId(x.RegId))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (visibleRegIds.Count > 0)
        {
            var noteRegIds = await LoadNotebookIndicatorRegIdsAsync(hotelId, visibleRegIds, ct);
            var roomChangedRegIds = await LoadRoomChangedIndicatorRegIdsAsync(hotelId, visibleRegIds, ct);

            foreach (var booking in bookings)
            {
                var normalizedRegId = NormalizeRegId(booking.RegId);
                booking.HasNote = noteRegIds.Contains(normalizedRegId);
                booking.HasRoomChange = roomChangedRegIds.Contains(normalizedRegId);
            }
        }

        // UNASSIGNED is a virtual lane per category. It is intentionally not returned as a physical room.
        return new FrontDeskCalendarPayload
        {
            StartDate = start,
            EndDate = end,
            HotelToday = hotelToday,
            ViewDays = days,
            CurrencySymbol = await GetCurrencySymbolAsync(hotelId, ct),
            Categories = categories,
            Rooms = rooms,
            Bookings = bookings,
            Blocks = blocks,
            Permissions = permissions
        };
    }

    public async Task<FrontDeskBookingDetailsDto?> GetBookingDetailsAsync(
        string hotelId, string regId, int paymentId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(regId)) return null;

        const string sql = @"
SET NOCOUNT ON;
;WITH G AS
(
    SELECT TOP (1) * FROM dbo.GuestInformationLogTB WITH (READPAST)
    WHERE hotel_id=@hotel AND reg_id=@reg ORDER BY id DESC
),
N AS
(
    SELECT TOP (1) * FROM dbo.NewReservationsTB WITH (READPAST)
    WHERE hotel_id=@hotel AND reg_id=@reg ORDER BY id DESC
)
SELECT TOP (1)
    p.ID AS PaymentId,
    p.reg_id,
    ISNULL(CONVERT(varchar(100),G.visit_id),'') AS visit_id,
    CASE
        WHEN NULLIF(LTRIM(RTRIM(ISNULL(p.guestname,''))), '') IS NOT NULL
            THEN LTRIM(RTRIM(p.guestname))
        ELSE LTRIM(RTRIM(CONCAT(ISNULL(G.GuestName,N.GuestName),' ',ISNULL(G.LastName,N.LastName))))
    END AS guest_name,
    ISNULL(G.PhoneNo,N.PhoneNo) AS PhoneNo,
    ISNULL(G.Email,N.Email) AS Email,
    ISNULL(G.Bookid,N.Bookid) AS Bookid,
    ISNULL(p.room_no,'') AS room_no,
    ISNULL(p.[Type],'') AS room_category,
    ISNULL(CONVERT(varchar(100),cr.localcategoryid),'') AS localcategoryid,
    ISNULL(p.res_status,'') AS res_status,
    COALESCE(TRY_CONVERT(date,p.ArrivalDate,110),TRY_CONVERT(date,p.ArrivalDate,23),TRY_CONVERT(date,p.ArrivalDate,103),TRY_CONVERT(date,p.ArrivalDate)) AS ArrivalDate,
    COALESCE(TRY_CONVERT(date,p.DepartureDate,110),TRY_CONVERT(date,p.DepartureDate,23),TRY_CONVERT(date,p.DepartureDate,103),TRY_CONVERT(date,p.DepartureDate)) AS DepartureDate,
    ISNULL(p.rateplanname,'') AS planname,
    ISNULL(G.Agency,N.Agency) AS Agency,
    CASE WHEN ISNULL(p.room_adults,0)+ISNULL(p.room_children,0)+ISNULL(p.room_infants,0)>0 THEN ISNULL(p.room_adults,0)
         ELSE ISNULL(TRY_CONVERT(int,G.NumberOfAdults),ISNULL(TRY_CONVERT(int,N.number_of_adult),0)) END AS adults,
    CASE WHEN ISNULL(p.room_adults,0)+ISNULL(p.room_children,0)+ISNULL(p.room_infants,0)>0 THEN ISNULL(p.room_children,0)
         ELSE ISNULL(TRY_CONVERT(int,G.NumberOfMinors),ISNULL(TRY_CONVERT(int,N.number_of_minor),0)) END AS children,
    ISNULL(p.room_infants,0) AS infants,
    ISNULL(TRY_CONVERT(decimal(18,2),U.grand_total),0) AS total,
    ISNULL(TRY_CONVERT(decimal(18,2),U.discount),0) AS discount,
    ISNULL(TRY_CONVERT(decimal(18,2),U.paid_amount),0) AS paid,
    ISNULL(TRY_CONVERT(decimal(18,2),U.remaining_amount),0) AS balance,
    ISNULL(Sec.security_balance,0) AS room_security,
    ISNULL(G.notes,N.notes) AS notes,
    ISNULL(G.booking_id,N.booking_id) AS booking_id,
    ISNULL(G.is_virtual,N.is_virtual) AS is_virtual,
    ISNULL(G.createdat,N.createdat) AS created_at,
    ISNULL(GN.description,'') AS fdo_notes
FROM dbo.payments p WITH (READPAST)
LEFT JOIN G ON 1=1
LEFT JOIN N ON 1=1
LEFT JOIN dbo.create_room cr WITH (READPAST)
  ON cr.hotel_id=p.hotel_id AND cr.description=p.[Type] AND LTRIM(RTRIM(ISNULL(cr.category,'')))='Room Rent'
OUTER APPLY
(
    SELECT TOP (1) pu.grand_total,pu.discount,pu.paid_amount,pu.remaining_amount
    FROM dbo.PaymentsUpdateTB pu WITH (READPAST)
    WHERE pu.hotel_id=p.hotel_id AND pu.reg_id=p.reg_id ORDER BY pu.id DESC
) U
OUTER APPLY
(
    SELECT CAST(ISNULL(SUM(ISNULL(TRY_CONVERT(decimal(18,2),rs.security),0)),0) AS decimal(18,2)) AS security_balance
    FROM dbo.RoomSecurityTB rs WITH (READPAST)
    WHERE rs.hotel_id=p.hotel_id AND rs.reg_id=p.reg_id
) Sec
LEFT JOIN dbo.GuestNoteBook GN WITH (READPAST) ON GN.hotel_id=p.hotel_id AND GN.reg_id=p.reg_id
WHERE p.hotel_id=@hotel AND p.reg_id=@reg AND LTRIM(RTRIM(ISNULL(p.descr,'')))='Room Rent'
  AND (@paymentId<=0 OR p.ID=@paymentId)
ORDER BY CASE WHEN p.ID=@paymentId THEN 0 ELSE 1 END,p.ID DESC;

SELECT *
FROM dbo.PaymentsLogTB WITH (READPAST)
WHERE hotel_id=@hotel AND reg_id=@reg
ORDER BY id DESC;";

        await using var cn = new SqlConnection(_connectionString);
        await cn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, cn) { CommandTimeout = 15 };
        cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
        cmd.Parameters.Add("@reg", SqlDbType.VarChar, 100).Value = NormalizeRegId(regId);
        cmd.Parameters.Add("@paymentId", SqlDbType.Int).Value = paymentId;
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct)) return null;

        var arrival = D(rd, "ArrivalDate") ?? _hotelClock.GetHotelToday(hotelId);
        var departure = D(rd, "DepartureDate") ?? arrival.AddDays(1);
        var details = new FrontDeskBookingDetailsDto
        {
            RegId = S(rd, "reg_id"),
            PaymentId = I(rd, "PaymentId"),
            VisitId = S(rd, "visit_id"),
            GuestName = S(rd, "guest_name"),
            Phone = S(rd, "PhoneNo"),
            Email = S(rd, "Email"),
            BookingNo = S(rd, "Bookid"),
            RoomNo = S(rd, "room_no").Equals("UNASSIGNED", StringComparison.OrdinalIgnoreCase) ? string.Empty : S(rd, "room_no"),
            CategoryName = S(rd, "room_category"),
            CategoryId = S(rd, "localcategoryid"),
            Status = DisplayStatus(S(rd, "res_status"), arrival, _hotelClock.GetHotelToday(hotelId)),
            CreatedAt = DTime(rd, "created_at"),
            Arrival = arrival,
            Departure = departure,
            Nights = Math.Max(1, (departure.Date - arrival.Date).Days),
            Adults = I(rd, "adults"),
            Children = I(rd, "children"),
            Infants = I(rd, "infants"),
            Source = S(rd, "Agency"),
            PlanName = S(rd, "planname"),
            Total = M(rd, "total"),
            Discount = M(rd, "discount"),
            Paid = M(rd, "paid"),
            Balance = M(rd, "balance"),
            RoomSecurity = Math.Max(0m, M(rd, "room_security")),
            Notes = S(rd, "notes"),
            FrontDeskNotes = S(rd, "fdo_notes"),
            ChannexBookingId = S(rd, "booking_id"),
            IsVirtualCard = B(rd, "is_virtual")
        };
        details.ShowChannexChat = !string.IsNullOrWhiteSpace(details.ChannexBookingId) &&
                                  !details.ChannexBookingId.Equals("N/A", StringComparison.OrdinalIgnoreCase);
        // Match the WebForms Auto Payment visibility rule as closely as the
        // MVC detail query can: a real booking id, a virtual card, and money due.
        details.ShowAutoPay = details.ShowChannexChat && details.IsVirtualCard && details.Balance > 0.005m;

        var allLogs = new List<FrontDeskPaymentLogDto>();
        if (await rd.NextResultAsync(ct))
        {
            while (await rd.ReadAsync(ct))
            {
                var paymentIdText = S(rd, "PaymentId");
                var chargeIdText = S(rd, "chargeid");
                allLogs.Add(new FrontDeskPaymentLogDto
                {
                    Id = I(rd, "id"),
                    Date = DTime(rd, "currentdate"),
                    Amount = M(rd, "paid_amount"),
                    Method = S(rd, "payment_method"),
                    Reference = !string.IsNullOrWhiteSpace(paymentIdText) ? paymentIdText : chargeIdText,
                    ReceiptUrl = S(rd, "receipturl"),
                    PaymentId = paymentIdText,
                    ChargeId = chargeIdText,
                    RefundId = S(rd, "RefundId"),
                    ExternalRefundId = S(rd, "externalrefundid")
                });
            }
        }

        // Match the Check-In payment log rules: refund rows are negative and are
        // linked to the original payment by externalrefundid whenever available.
        var refundRows = allLogs.Where(x => x.Amount < 0m).ToArray();
        foreach (var row in allLogs)
        {
            if (row.Amount <= 0m)
            {
                row.CanRefund = false;
                row.RemainingRefundable = 0m;
                continue;
            }

            // Every positive payment can have a refundable balance. Provider-backed
            // card/PDQ payments are refunded through their provider by CheckInService;
            // manual methods such as Cash, Bank Transfer, Cheque, etc. are refunded
            // locally in the PMS and therefore do not require PaymentId/chargeid.
            var targetId = row.Id.ToString(CultureInfo.InvariantCulture);
            var hasExplicitLink = false;
            decimal alreadyRefunded = 0m;

            foreach (var refund in refundRows)
            {
                if (refund.Id == row.Id) continue;
                if (!string.Equals(
                        (refund.ExternalRefundId ?? string.Empty).Trim(),
                        targetId,
                        StringComparison.OrdinalIgnoreCase))
                    continue;

                hasExplicitLink = true;
                alreadyRefunded += Math.Abs(refund.Amount);
            }

            if (!hasExplicitLink)
            {
                alreadyRefunded = 0m;
                foreach (var refund in refundRows)
                {
                    if (refund.Id == row.Id) continue;
                    // Legacy fallback is safe only for provider-backed payments.
                    // Manual payments (Cash, Bank Transfer, Cheque, etc.) do not
                    // have PaymentId/ChargeId, so a generic RefundId must NOT be
                    // allowed to consume every manual payment on the reservation.
                    // New/manual refunds are linked exactly through externalrefundid.
                    var matches =
                        (!string.IsNullOrWhiteSpace(row.PaymentId) &&
                         string.Equals(refund.PaymentId, row.PaymentId, StringComparison.OrdinalIgnoreCase)) ||
                        (!string.IsNullOrWhiteSpace(row.ChargeId) &&
                         string.Equals(refund.ChargeId, row.ChargeId, StringComparison.OrdinalIgnoreCase));

                    if (matches) alreadyRefunded += Math.Abs(refund.Amount);
                }
            }

            row.RemainingRefundable =
                Math.Max(0m, Math.Abs(row.Amount) - alreadyRefunded);
            row.CanRefund = row.RemainingRefundable > 0.005m;
        }

        // Keep the drawer compact while refund calculations still consider the
        // complete reservation payment history.
        details.Payments = allLogs.Take(30).ToList();
        return details;
    }

    public async Task<FrontDeskGuestHistoryDto?> GetGuestHistoryAsync(
        string hotelId, string regId, CancellationToken ct = default)
    {
        const string sql = @"
DECLARE @email varchar(250),@phone varchar(100),@name varchar(300);
SELECT TOP (1)
    @email=ISNULL(Email,''),@phone=ISNULL(PhoneNo,''),
    @name=LTRIM(RTRIM(CONCAT(ISNULL(GuestName,''),' ',ISNULL(LastName,''))))
FROM dbo.GuestInformationLogTB WITH (READPAST)
WHERE hotel_id=@hotel AND reg_id=@reg ORDER BY id DESC;
IF ISNULL(@name,'')=''
SELECT TOP (1)
    @email=ISNULL(Email,''),@phone=ISNULL(PhoneNo,''),
    @name=LTRIM(RTRIM(CONCAT(ISNULL(GuestName,''),' ',ISNULL(LastName,''))))
FROM dbo.NewReservationsTB WITH (READPAST)
WHERE hotel_id=@hotel AND reg_id=@reg ORDER BY id DESC;
SELECT ISNULL(@name,'') guest_name,ISNULL(@email,'') email,ISNULL(@phone,'') phone;
SELECT TOP (50)
    p.reg_id,p.room_no,p.res_status,
    COALESCE(TRY_CONVERT(date,p.ArrivalDate,110),TRY_CONVERT(date,p.ArrivalDate,23),TRY_CONVERT(date,p.ArrivalDate)) ArrivalDate,
    COALESCE(TRY_CONVERT(date,p.DepartureDate,110),TRY_CONVERT(date,p.DepartureDate,23),TRY_CONVERT(date,p.DepartureDate)) DepartureDate
FROM dbo.payments p WITH (READPAST)
LEFT JOIN dbo.GuestInformationLogTB g WITH (READPAST) ON g.hotel_id=p.hotel_id AND g.reg_id=p.reg_id
LEFT JOIN dbo.NewReservationsTB n WITH (READPAST) ON n.hotel_id=p.hotel_id AND n.reg_id=p.reg_id
WHERE p.hotel_id=@hotel AND LTRIM(RTRIM(ISNULL(p.descr,'')))='Room Rent'
  AND ((@email<>'' AND (g.Email=@email OR n.Email=@email)) OR (@phone<>'' AND (g.PhoneNo=@phone OR n.PhoneNo=@phone)) OR p.reg_id=@reg)
ORDER BY COALESCE(TRY_CONVERT(date,p.ArrivalDate,110),TRY_CONVERT(date,p.ArrivalDate,23),TRY_CONVERT(date,p.ArrivalDate)) DESC,p.ID DESC;";

        await using var cn = new SqlConnection(_connectionString);
        await cn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, cn) { CommandTimeout = 15 };
        cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
        cmd.Parameters.Add("@reg", SqlDbType.VarChar, 100).Value = NormalizeRegId(regId);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct)) return null;
        var result = new FrontDeskGuestHistoryDto
        {
            GuestName = S(rd, "guest_name"), Email = S(rd, "email"), Phone = S(rd, "phone")
        };
        var stays = new List<FrontDeskGuestStayDto>();
        if (await rd.NextResultAsync(ct))
        {
            while (await rd.ReadAsync(ct))
            {
                stays.Add(new FrontDeskGuestStayDto
                {
                    RegId = S(rd, "reg_id"), RoomNo = S(rd, "room_no"), Status = S(rd, "res_status"),
                    Arrival = D(rd, "ArrivalDate"), Departure = D(rd, "DepartureDate")
                });
            }
        }
        result.Stays = stays;
        return result;
    }

    public async Task<FrontDeskOperationResult> SaveNoteAsync(
        string hotelId, string userId, string userName, string ip,
        FrontDeskNoteRequest request, CancellationToken ct = default)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.RegId)) return FrontDeskOperationResult.Fail("Reservation ID is required.");
        await using var cn = new SqlConnection(_connectionString);
        await cn.OpenAsync(ct);
        await using var tx = (SqlTransaction)await cn.BeginTransactionAsync(ct);
        try
        {
            await using (var cmd = new SqlCommand(@"
UPDATE dbo.GuestNoteBook SET description=@notes
WHERE hotel_id=@hotel AND reg_id=@reg;
IF @@ROWCOUNT=0
INSERT INTO dbo.GuestNoteBook(reg_id,description,hotel_id) VALUES(@reg,@notes,@hotel);", cn, tx))
            {
                cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
                cmd.Parameters.Add("@reg", SqlDbType.VarChar, 100).Value = NormalizeRegId(request.RegId);
                cmd.Parameters.Add("@notes", SqlDbType.VarChar, 4000).Value = (request.Notes ?? string.Empty).Trim();
                await cmd.ExecuteNonQueryAsync(ct);
            }
            await InsertLogAsync(cn, tx, hotelId, userId, userName, ip, request.RegId, "NOTEBOOK", "Front desk notebook updated.", ct);
            await tx.CommitAsync(ct);
            return FrontDeskOperationResult.Ok("Notebook saved.");
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(ct);
            _logger.Error(ex, "Calendar notebook save failed for {RegId}.", request.RegId);
            await _dbLogger.LogExceptionAsync(ex, "Front Desk Calendar", "Save Note", hotelId, userId, userName, ip, ct);
            return FrontDeskOperationResult.Fail("Unable to save notebook. " + ex.Message);
        }
    }

    public async Task<FrontDeskOperationResult> MarkRoomCleanAsync(
        string hotelId, string userId, string userName, string ip,
        FrontDeskRoomCleanRequest request, CancellationToken ct = default)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.RoomNo)) return FrontDeskOperationResult.Fail("Room number is required.");
        await using var cn = new SqlConnection(_connectionString);
        await cn.OpenAsync(ct);
        await using var cmd = new SqlCommand(@"
UPDATE dbo.RoomsTB
SET room_status='Available'
WHERE Hotel_id=@hotel AND room_no=@room AND room_status IN ('NotClean','CheckOut','Dirty');", cn);
        cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
        cmd.Parameters.Add("@room", SqlDbType.VarChar, 50).Value = request.RoomNo.Trim();
        var changed = await cmd.ExecuteNonQueryAsync(ct);
        if (changed == 0) return FrontDeskOperationResult.Fail("Room is not currently marked dirty.");
        await InsertLogAsync(cn, null, hotelId, userId, userName, ip, string.Empty, "ROOM CLEAN", $"Room {request.RoomNo} marked Available.", ct);
        return FrontDeskOperationResult.Ok($"Room {request.RoomNo} is now available.");
    }

    public Task<FrontDeskOperationResult> BlockRoomAsync(
        string hotelId, string hotelName, string userId, string userName, string ip,
        FrontDeskRoomBlockRequest request, CancellationToken ct = default)
        => SaveBlockInternalAsync(hotelId, hotelName, userId, userName, ip, request, null, ct);

    public Task<FrontDeskOperationResult> UpdateBlockAsync(
        string hotelId, string hotelName, string userId, string userName, string ip,
        FrontDeskRoomBlockUpdateRequest request, CancellationToken ct = default)
        => SaveBlockInternalAsync(hotelId, hotelName, userId, userName, ip, request, request.BlockId, ct);

    public async Task<FrontDeskOperationResult> RemoveBlockAsync(
        string hotelId, string hotelName, string userId, string userName, string ip,
        FrontDeskBlockActionRequest request, CancellationToken ct = default)
    {
        if (request == null || request.BlockId <= 0) return FrontDeskOperationResult.Fail("Invalid room block.");
        await using var cn = new SqlConnection(_connectionString);
        await cn.OpenAsync(ct);
        DateTime? start = null, end = null;
        await using var tx = (SqlTransaction)await cn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        try
        {
            await using (var read = new SqlCommand(@"
SELECT TOP (1)
 COALESCE(TRY_CONVERT(date,BlockStartDate,110),TRY_CONVERT(date,BlockStartDate,23),TRY_CONVERT(date,BlockStartDate)) S,
 COALESCE(TRY_CONVERT(date,BlockEndDate,110),TRY_CONVERT(date,BlockEndDate,23),TRY_CONVERT(date,BlockEndDate)) E
FROM dbo.RoomBlocksTB WITH (UPDLOCK,HOLDLOCK)
WHERE HotelID=@hotel AND BlockID=@id AND ISNULL(IsActive,0)=1;", cn, tx))
            {
                read.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
                read.Parameters.Add("@id", SqlDbType.Int).Value = request.BlockId;
                await using var rd = await read.ExecuteReaderAsync(ct);
                if (await rd.ReadAsync(ct)) { start = D(rd, "S"); end = D(rd, "E"); }
            }
            if (!start.HasValue || !end.HasValue) return FrontDeskOperationResult.Fail("Room block was not found.");
            await using (var cmd = new SqlCommand("UPDATE dbo.RoomBlocksTB SET IsActive=0 WHERE HotelID=@hotel AND BlockID=@id;", cn, tx))
            {
                cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
                cmd.Parameters.Add("@id", SqlDbType.Int).Value = request.BlockId;
                await cmd.ExecuteNonQueryAsync(ct);
            }
            await InsertLogAsync(cn, tx, hotelId, userId, userName, ip, string.Empty, "UNBLOCK ROOM", $"Room {request.RoomNo}, BlockID {request.BlockId}.", ct);
            await tx.CommitAsync(ct);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(ct);
            _logger.Error(ex, "Unable to remove room block {BlockId}.", request.BlockId);
            await _dbLogger.LogExceptionAsync(ex, "Front Desk Calendar", "Remove Room Block", hotelId, userId, userName, ip, ct);
            return FrontDeskOperationResult.Fail("Unable to remove room block. " + ex.Message);
        }
        QueueAvailability(hotelId, hotelName, userId, userName, ip, start!.Value, end!.Value, request.CategoryId);
        return FrontDeskOperationResult.Ok("Room block removed.");
    }

    public async Task<FrontDeskOperationResult> ActivateRoomFromTodayAsync(
        string hotelId, string hotelName, string userId, string userName, string ip,
        FrontDeskBlockActionRequest request, CancellationToken ct = default)
    {
        if (request == null || request.BlockId <= 0) return FrontDeskOperationResult.Fail("Invalid room block.");
        var today = _hotelClock.GetHotelToday(hotelId);
        await using var cn = new SqlConnection(_connectionString);
        await cn.OpenAsync(ct);
        DateTime? oldEnd = null;
        await using var tx = (SqlTransaction)await cn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        try
        {
            await using (var read = new SqlCommand(@"
SELECT TOP (1) COALESCE(TRY_CONVERT(date,BlockEndDate,110),TRY_CONVERT(date,BlockEndDate,23),TRY_CONVERT(date,BlockEndDate)) E
FROM dbo.RoomBlocksTB WITH (UPDLOCK,HOLDLOCK)
WHERE HotelID=@hotel AND BlockID=@id AND ISNULL(IsActive,0)=1;", cn, tx))
            {
                read.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
                read.Parameters.Add("@id", SqlDbType.Int).Value = request.BlockId;
                var oldEndValue = await read.ExecuteScalarAsync(ct);
                if (oldEndValue is DateTime oldEndDate)
                    oldEnd = oldEndDate.Date;
                else if (oldEndValue != null && oldEndValue != DBNull.Value && DateTime.TryParse(Convert.ToString(oldEndValue, CultureInfo.InvariantCulture), out var parsedOldEnd))
                    oldEnd = parsedOldEnd.Date;
            }
            if (!oldEnd.HasValue) return FrontDeskOperationResult.Fail("Room block was not found.");
            if (oldEnd.Value.Date < today) return FrontDeskOperationResult.Ok("Room is already active.");
            await using (var cmd = new SqlCommand(@"
UPDATE dbo.RoomBlocksTB SET BlockEndDate=@end,IsActive=0 WHERE HotelID=@hotel AND BlockID=@id;", cn, tx))
            {
                cmd.Parameters.Add("@end", SqlDbType.VarChar, 20).Value = today.AddDays(-1).ToString("MM-dd-yyyy", CultureInfo.InvariantCulture);
                cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
                cmd.Parameters.Add("@id", SqlDbType.Int).Value = request.BlockId;
                await cmd.ExecuteNonQueryAsync(ct);
            }
            await InsertLogAsync(cn, tx, hotelId, userId, userName, ip, string.Empty, "ACTIVATE ROOM", $"Room {request.RoomNo} activated from {today:yyyy-MM-dd}.", ct);
            await tx.CommitAsync(ct);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(ct);
            _logger.Error(ex, "Unable to activate room for block {BlockId}.", request.BlockId);
            await _dbLogger.LogExceptionAsync(ex, "Front Desk Calendar", "Activate Room", hotelId, userId, userName, ip, ct);
            return FrontDeskOperationResult.Fail("Unable to activate room. " + ex.Message);
        }
        QueueAvailability(hotelId, hotelName, userId, userName, ip, today, oldEnd!.Value, request.CategoryId);
        return FrontDeskOperationResult.Ok("Room activated from today.");
    }

    public async Task<FrontDeskOperationResult> MoveBookingAsync(
        string hotelId, string hotelName, string userId, string userName, string ip,
        FrontDeskBookingMoveRequest request, CancellationToken ct = default)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.RegId) || request.PaymentId <= 0)
            return FrontDeskOperationResult.Fail("Invalid reservation move request.");
        if (request.NewArrival == default) return FrontDeskOperationResult.Fail("A valid new arrival date is required.");

        await using var cn = new SqlConnection(_connectionString);
        await cn.OpenAsync(ct);
        // WebForms room moves run under normal READ COMMITTED semantics. Serializable +
        // READPAST elsewhere in the same connection triggers SQL Server error 4132.
        await using var tx = (SqlTransaction)await cn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        BookingRow? current;
        try
        {
            current = await GetBookingRowForUpdateAsync(cn, tx, hotelId, request.RegId, request.PaymentId, ct);
            if (current == null) return FrontDeskOperationResult.Fail("Reservation room row was not found.");

            var newRoom = string.IsNullOrWhiteSpace(request.NewRoomNo) ? "UNASSIGNED" : request.NewRoomNo.Trim();
            var isCheckedOut = IsCheckedOutStatus(current.Status);

            // Checked-out rows remain historical stays, but the room assignment can still
            // be corrected by drag/drop (including Unassigned). Preserve dates, amounts and
            // checkout status. For a real destination room, also verify that the historical
            // date span does not overlap another booking/block for that room.
            if (isCheckedOut)
            {
                var checkedOutTargetCategoryId = current.CategoryId;
                var checkedOutTargetCategoryName = current.CategoryName;

                if (!newRoom.Equals("UNASSIGNED", StringComparison.OrdinalIgnoreCase))
                {
                    var roomInfo = await GetRoomInfoAsync(cn, tx, hotelId, newRoom, ct);
                    if (roomInfo == null)
                        return FrontDeskOperationResult.Fail("Destination room does not exist.");

                    checkedOutTargetCategoryName = roomInfo.Value.CategoryName;
                    checkedOutTargetCategoryId = roomInfo.Value.CategoryId;

                    if (!await IsRoomFreeAsync(cn, tx, hotelId, newRoom,
                            current.Arrival.Date, current.Departure.Date, request.PaymentId, ct))
                    {
                        return FrontDeskOperationResult.Fail("Destination room is not available for the checked-out stay dates.");
                    }
                }

                if (string.Equals(current.RoomNo ?? string.Empty, newRoom, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(current.CategoryId ?? string.Empty, checkedOutTargetCategoryId ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                {
                    await tx.CommitAsync(ct);
                    return FrontDeskOperationResult.Ok("Checked-out room assignment is unchanged.");
                }

                await using (var moveClosed = new SqlCommand(@"
UPDATE dbo.payments
SET room_no=@room,[Type]=@type,category_id=@category
WHERE hotel_id=@hotel AND reg_id=@reg AND ID=@id
  AND LTRIM(RTRIM(ISNULL(descr,'')))='Room Rent'
  AND LOWER(LTRIM(RTRIM(ISNULL(res_status,'')))) IN ('check out','checkout','checked out');", cn, tx))
                {
                    moveClosed.Parameters.Add("@room", SqlDbType.VarChar, 50).Value = newRoom;
                    moveClosed.Parameters.Add("@type", SqlDbType.VarChar, 150).Value = checkedOutTargetCategoryName;
                    moveClosed.Parameters.Add("@category", SqlDbType.VarChar, 100).Value = checkedOutTargetCategoryId;
                    moveClosed.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
                    moveClosed.Parameters.Add("@reg", SqlDbType.VarChar, 100).Value = NormalizeRegId(request.RegId);
                    moveClosed.Parameters.Add("@id", SqlDbType.Int).Value = request.PaymentId;
                    if (await moveClosed.ExecuteNonQueryAsync(ct) != 1)
                        throw new InvalidOperationException("The checked-out room changed before the move was saved. Refresh and try again.");
                }

                await InsertRoomChangeLogAsync(cn, tx, hotelId, request.RegId,
                    current.CategoryId, checkedOutTargetCategoryId, current.CategoryName, checkedOutTargetCategoryName,
                    current.RoomNo, newRoom, userId, userName, ip,
                    $"Checked-out room assignment corrected by drag/drop; PaymentId={request.PaymentId}.", ct);
                await InsertLogAsync(cn, tx, hotelId, userId, userName, ip, request.RegId,
                    newRoom.Equals("UNASSIGNED", StringComparison.OrdinalIgnoreCase)
                        ? "CALENDAR MOVE TO UNASSIGNED"
                        : "CALENDAR MOVE CHECKED OUT",
                    $"Checked-out room {current.RoomNo} -> {newRoom}; category {current.CategoryName} -> {checkedOutTargetCategoryName}; PaymentId={request.PaymentId}.", ct);

                await tx.CommitAsync(ct);

                // Availability refresh is still useful for historical reports/calendars. It
                // does not alter the checked-out status or current RoomsTB occupancy state.
                QueueAvailability(hotelId, hotelName, userId, userName, ip,
                    current.Arrival.Date, current.Departure.Date, current.CategoryId);
                if (!current.CategoryId.Equals(checkedOutTargetCategoryId, StringComparison.OrdinalIgnoreCase))
                    QueueAvailability(hotelId, hotelName, userId, userName, ip,
                        current.Arrival.Date, current.Departure.Date, checkedOutTargetCategoryId);

                return FrontDeskOperationResult.Ok(
                    newRoom.Equals("UNASSIGNED", StringComparison.OrdinalIgnoreCase)
                        ? "Checked-out guest moved to Unassigned."
                        : "Checked-out room assignment updated.",
                    new
                    {
                        roomNo = newRoom.Equals("UNASSIGNED", StringComparison.OrdinalIgnoreCase) ? string.Empty : newRoom,
                        categoryId = checkedOutTargetCategoryId,
                        categoryName = checkedOutTargetCategoryName,
                        arrival = current.Arrival,
                        departure = current.Departure
                    });
            }

            if (IsClosedStatus(current.Status))
                return FrontDeskOperationResult.Fail("A cancelled stay cannot be moved.");

            var nights = Math.Max(1, (current.Departure.Date - current.Arrival.Date).Days);
            var newArrival = request.NewArrival.Date;
            var newDeparture = newArrival.AddDays(nights);
            var targetCategoryId = request.TargetCategoryId.Trim();
            var targetCategoryName = request.TargetCategoryName.Trim();

            if (!newRoom.Equals("UNASSIGNED", StringComparison.OrdinalIgnoreCase))
            {
                var roomInfo = await GetRoomInfoAsync(cn, tx, hotelId, newRoom, ct);
                if (roomInfo == null) return FrontDeskOperationResult.Fail("Destination room does not exist.");
                targetCategoryName = roomInfo.Value.CategoryName;
                targetCategoryId = roomInfo.Value.CategoryId;
                if (!await IsRoomFreeAsync(cn, tx, hotelId, newRoom, newArrival, newDeparture, request.PaymentId, ct))
                    return FrontDeskOperationResult.Fail("Destination room is no longer available for those dates.");
            }

            var categoryChanged = !current.CategoryName.Equals(targetCategoryName, StringComparison.OrdinalIgnoreCase);
            if (categoryChanged && (!request.NightlyRateOverride.HasValue || request.NightlyRateOverride.Value < 0))
            {
                var suggested = await GetCategoryRateAsync(cn, tx, hotelId, targetCategoryId, targetCategoryName, newArrival, ct);
                return FrontDeskOperationResult.Fail("A nightly rate is required because the room category changed.", new
                {
                    requiresRate = true,
                    suggestedRate = suggested,
                    oldCategory = current.CategoryName,
                    newCategory = targetCategoryName
                });
            }

            decimal newBase = current.Rate;
            decimal newGst = current.Gst;
            decimal newBed = current.Bed;
            decimal newTotal = current.Total;
            if (categoryChanged && request.NightlyRateOverride.HasValue)
            {
                var oldBase = Math.Max(0m, current.Rate);
                var gstRatio = oldBase > 0 ? current.Gst / oldBase : 0m;
                var bedRatio = oldBase > 0 ? current.Bed / oldBase : 0m;
                newBase = Math.Round(request.NightlyRateOverride.Value * nights, 2, MidpointRounding.AwayFromZero);
                newGst = Math.Round(newBase * gstRatio, 2, MidpointRounding.AwayFromZero);
                newBed = Math.Round(newBase * bedRatio, 2, MidpointRounding.AwayFromZero);
                newTotal = newBase + newGst + newBed;
            }

            await using (var cmd = new SqlCommand(@"
UPDATE dbo.payments
SET room_no=@room,[Type]=@type,category_id=@category,
    ArrivalDate=@arrival,DepartureDate=@departure,
    rate=@rate,charge=@rate,Nights=@nights,GST=@gst,Bed=@bed,totalamount=@total
WHERE hotel_id=@hotel AND reg_id=@reg AND ID=@id AND LTRIM(RTRIM(ISNULL(descr,'')))='Room Rent';", cn, tx))
            {
                cmd.Parameters.Add("@room", SqlDbType.VarChar, 50).Value = newRoom;
                cmd.Parameters.Add("@type", SqlDbType.VarChar, 150).Value = targetCategoryName;
                cmd.Parameters.Add("@category", SqlDbType.VarChar, 100).Value = targetCategoryId;
                cmd.Parameters.Add("@arrival", SqlDbType.VarChar, 20).Value = LegacyDate(newArrival);
                cmd.Parameters.Add("@departure", SqlDbType.VarChar, 20).Value = LegacyDate(newDeparture);
                cmd.Parameters.Add("@rate", SqlDbType.Decimal).Value = newBase;
                cmd.Parameters.Add("@nights", SqlDbType.Decimal).Value = nights;
                cmd.Parameters.Add("@gst", SqlDbType.Decimal).Value = newGst;
                cmd.Parameters.Add("@bed", SqlDbType.Decimal).Value = newBed;
                cmd.Parameters.Add("@total", SqlDbType.Decimal).Value = newTotal;
                cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
                cmd.Parameters.Add("@reg", SqlDbType.VarChar, 100).Value = NormalizeRegId(request.RegId);
                cmd.Parameters.Add("@id", SqlDbType.Int).Value = request.PaymentId;
                if (await cmd.ExecuteNonQueryAsync(ct) != 1)
                    throw new InvalidOperationException("The reservation changed before the move was saved. Refresh and try again.");
            }

            await UpdateMasterDatesWhenSingleRoomAsync(cn, tx, hotelId, request.RegId, newArrival, newDeparture, ct);
            await InsertRoomChangeLogAsync(cn, tx, hotelId, request.RegId, current.CategoryId, targetCategoryId,
                current.CategoryName, targetCategoryName, current.RoomNo, newRoom, userId, userName, ip,
                $"Drag/drop PaymentId={request.PaymentId}; {current.Arrival:yyyy-MM-dd}-{current.Departure:yyyy-MM-dd} -> {newArrival:yyyy-MM-dd}-{newDeparture:yyyy-MM-dd}", ct);
            await InsertLogAsync(cn, tx, hotelId, userId, userName, ip, request.RegId, "CALENDAR MOVE",
                $"Room {current.RoomNo} -> {newRoom}; category {current.CategoryName} -> {targetCategoryName}.", ct);
            await tx.CommitAsync(ct);

            var min = current.Arrival < newArrival ? current.Arrival : newArrival;
            var max = current.Departure > newDeparture ? current.Departure : newDeparture;
            QueueAvailability(hotelId, hotelName, userId, userName, ip, min, max, current.CategoryId);
            if (!current.CategoryId.Equals(targetCategoryId, StringComparison.OrdinalIgnoreCase))
                QueueAvailability(hotelId, hotelName, userId, userName, ip, min, max, targetCategoryId);

            return FrontDeskOperationResult.Ok("Reservation moved successfully.", new
            {
                roomNo = newRoom.Equals("UNASSIGNED", StringComparison.OrdinalIgnoreCase) ? string.Empty : newRoom,
                categoryId = targetCategoryId,
                categoryName = targetCategoryName,
                arrival = newArrival,
                departure = newDeparture
            });
        }
        catch (Exception ex)
        {
            try { await tx.RollbackAsync(ct); } catch { }
            _logger.Error(ex, "Calendar move failed for {RegId}/{PaymentId}.", request.RegId, request.PaymentId);
            await _dbLogger.LogExceptionAsync(ex, "Front Desk Calendar", "Move Reservation", hotelId, userId, userName, ip, ct);
            return FrontDeskOperationResult.Fail("Unable to move reservation. " + ex.Message);
        }
    }

    public async Task<FrontDeskOperationResult> ResizeBookingAsync(
        string hotelId, string hotelName, string userId, string userName, string ip,
        FrontDeskBookingResizeRequest request, CancellationToken ct = default)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.RegId) || request.PaymentId <= 0)
            return FrontDeskOperationResult.Fail("Invalid reservation resize request.");
        if (request.NewArrival == default || request.NewDeparture == default)
            return FrontDeskOperationResult.Fail("Valid reservation dates are required.");

        await using var cn = new SqlConnection(_connectionString);
        await cn.OpenAsync(ct);
        await using var tx = (SqlTransaction)await cn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

        try
        {
            var current = await GetBookingRowForUpdateAsync(cn, tx, hotelId, request.RegId, request.PaymentId, ct);
            if (current == null) return FrontDeskOperationResult.Fail("Reservation room row was not found.");
            if (IsClosedStatus(current.Status)) return FrontDeskOperationResult.Fail("A checked-out/cancelled stay cannot be changed.");

            var newArrival = request.NewArrival.Date;
            var newDeparture = request.NewDeparture.Date;

            if (newArrival != current.Arrival.Date)
                return FrontDeskOperationResult.Fail("Calendar resize can change the departure date only.");
            if (newDeparture <= newArrival)
                return FrontDeskOperationResult.Fail("A stay must be at least one night.");

            var isExtend = newDeparture > current.Departure.Date;
            var isShrink = newDeparture < current.Departure.Date;
            if (!isExtend && !isShrink)
                return FrontDeskOperationResult.Ok("Reservation dates are unchanged.");

            if (!current.RoomNo.Equals("UNASSIGNED", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(current.RoomNo) &&
                !await IsRoomFreeForReservationAsync(cn, tx, hotelId, current.RoomNo, newArrival, newDeparture, request.RegId, ct))
            {
                return FrontDeskOperationResult.Fail("Room is not available for the selected dates.");
            }

            var oldNights = Math.Max(1, (current.Departure.Date - current.Arrival.Date).Days);
            var currentPerNight = oldNights > 0
                ? Math.Round(current.Rate / oldNights, 2, MidpointRounding.AwayFromZero)
                : 0m;
            var chosenPerNight = request.NightlyRateOverride.HasValue
                ? Math.Max(0m, request.NightlyRateOverride.Value)
                : currentPerNight;

            decimal resultTotal;

            if (isExtend)
            {
                // WebForms parity:
                // - do NOT modify the original Room Rent payment row;
                // - create one new Room Rent row for the newly-added nights;
                // - apply the editable rate only to those added nights;
                // - tax on the added nights is optional and comes from the existing per-night tax history.
                string roomType = current.CategoryName;
                string roomNo = current.RoomNo;
                string planId = string.Empty;
                string planName = string.Empty;
                DateTime rowArrival = current.Arrival.Date;
                DateTime rowOldDeparture = current.Departure.Date;
                decimal oldBase = current.Rate;
                decimal oldGst = current.Gst;
                decimal oldBed = current.Bed;

                await using (var parent = new SqlCommand(@"
SELECT TOP (1)
    ID,[Type],room_no,ArrivalDate,DepartureDate,
    ISNULL(TRY_CONVERT(decimal(18,2),rate),0) rate,
    ISNULL(TRY_CONVERT(decimal(18,2),GST),0) GST,
    ISNULL(TRY_CONVERT(decimal(18,2),Bed),0) Bed,
    ISNULL(rateplan,'') rateplan,
    ISNULL(rateplanname,'') rateplanname
FROM dbo.payments WITH (UPDLOCK,ROWLOCK)
WHERE hotel_id=@hotel
  AND reg_id=@reg
  AND ID=@id
  AND LTRIM(RTRIM(ISNULL(descr,'')))='Room Rent';", cn, tx))
                {
                    parent.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
                    parent.Parameters.Add("@reg", SqlDbType.VarChar, 100).Value = NormalizeRegId(request.RegId);
                    parent.Parameters.Add("@id", SqlDbType.Int).Value = request.PaymentId;

                    await using var rd = await parent.ExecuteReaderAsync(ct);
                    if (!await rd.ReadAsync(ct))
                        return FrontDeskOperationResult.Fail("Selected Room Rent payment row was not found.");

                    roomType = S(rd, "Type");
                    roomNo = S(rd, "room_no");
                    planId = S(rd, "rateplan");
                    planName = S(rd, "rateplanname");
                    oldBase = M(rd, "rate");
                    oldGst = M(rd, "GST");
                    oldBed = M(rd, "Bed");

                    var parsedArrival = ParseLegacyDateValue(rd["ArrivalDate"]);
                    var parsedDeparture = ParseLegacyDateValue(rd["DepartureDate"]);
                    if (parsedArrival.HasValue) rowArrival = parsedArrival.Value.Date;
                    if (parsedDeparture.HasValue) rowOldDeparture = parsedDeparture.Value.Date;
                }

                var oldRowNights = Math.Max(1, (rowOldDeparture - rowArrival).Days);
                var addedNights = (newDeparture - rowOldDeparture).Days;
                if (addedNights <= 0)
                    return FrontDeskOperationResult.Fail("New departure must be after this room's current departure date.");

                if (!request.NightlyRateOverride.HasValue)
                    chosenPerNight = oldRowNights > 0
                        ? Math.Round(oldBase / oldRowNights, 2, MidpointRounding.AwayFromZero)
                        : 0m;

                decimal gstPerNight = oldGst > 0m && oldRowNights > 0
                    ? Math.Round(oldGst / oldRowNights, 4, MidpointRounding.AwayFromZero)
                    : await GetHistoricalTaxPerNightAsync(cn, tx, hotelId, request.RegId, roomNo, roomType, planId, "GST", ct);

                decimal bedPerNight = oldBed > 0m && oldRowNights > 0
                    ? Math.Round(oldBed / oldRowNights, 4, MidpointRounding.AwayFromZero)
                    : await GetHistoricalTaxPerNightAsync(cn, tx, hotelId, request.RegId, roomNo, roomType, planId, "Bed", ct);

                var extensionBase = Math.Round(chosenPerNight * addedNights, 2, MidpointRounding.AwayFromZero);
                var extensionGst = request.IncludeTaxOnExtension
                    ? Math.Round(gstPerNight * addedNights, 2, MidpointRounding.AwayFromZero)
                    : 0m;
                var extensionBed = request.IncludeTaxOnExtension
                    ? Math.Round(bedPerNight * addedNights, 2, MidpointRounding.AwayFromZero)
                    : 0m;
                var extensionTotal = Math.Round(extensionBase + extensionGst + extensionBed, 2, MidpointRounding.AwayFromZero);

                await using (var dup = new SqlCommand(@"
SELECT COUNT(1)
FROM dbo.payments
WHERE hotel_id=@hotel
  AND reg_id=@reg
  AND LTRIM(RTRIM(ISNULL(descr,'')))='Room Rent'
  AND LTRIM(RTRIM(ISNULL(room_no,'')))=@room
  AND LTRIM(RTRIM(ISNULL([Type],'')))=@type
  AND LTRIM(RTRIM(ISNULL(rateplan,'')))=@plan
  AND COALESCE(
        TRY_CONVERT(date,ArrivalDate,110),
        TRY_CONVERT(date,ArrivalDate,23),
        TRY_CONVERT(date,ArrivalDate,101),
        TRY_CONVERT(date,ArrivalDate,103),
        TRY_CONVERT(date,ArrivalDate)
      )=@arr
  AND COALESCE(
        TRY_CONVERT(date,DepartureDate,110),
        TRY_CONVERT(date,DepartureDate,23),
        TRY_CONVERT(date,DepartureDate,101),
        TRY_CONVERT(date,DepartureDate,103),
        TRY_CONVERT(date,DepartureDate)
      )=@dep;", cn, tx))
                {
                    dup.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
                    dup.Parameters.Add("@reg", SqlDbType.VarChar, 100).Value = NormalizeRegId(request.RegId);
                    dup.Parameters.Add("@room", SqlDbType.VarChar, 50).Value = roomNo;
                    dup.Parameters.Add("@type", SqlDbType.VarChar, 150).Value = roomType;
                    dup.Parameters.Add("@plan", SqlDbType.VarChar, 100).Value = planId;
                    dup.Parameters.Add("@arr", SqlDbType.Date).Value = rowOldDeparture;
                    dup.Parameters.Add("@dep", SqlDbType.Date).Value = newDeparture;
                    if (Convert.ToInt32(await dup.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture) > 0)
                        return FrontDeskOperationResult.Fail("Extension row already exists for the selected dates. Please refresh the calendar.");
                }

                // The production schemas used by ORA PMS are not identical.  Keep the
                // extension insert to columns that are already required by the calendar,
                // instead of depending on optional legacy columns such as visit_id.
                await using (var ins = new SqlCommand(@"
INSERT INTO dbo.payments
(
    [Type],room_no,ArrivalDate,DepartureDate,NumberOfRoom,
    rate,charge,Nights,totalamount,reg_id,hotel_id,
    payment_status,currentdate,descr,res_status,
    rateplan,rateplanname,GST,Bed,category_id
)
SELECT
    [Type],room_no,@arrDate,@depDate,ISNULL(NumberOfRoom,1),
    @rate,@charge,@nights,@total,reg_id,hotel_id,
    payment_status,@currentdate,descr,res_status,
    rateplan,rateplanname,@gst,@bed,category_id
FROM dbo.payments
WHERE ID=@parentId
  AND hotel_id=@hotel
  AND reg_id=@reg;", cn, tx))
                {
                    ins.Parameters.Add("@arrDate", SqlDbType.VarChar, 20).Value = LegacyDate(rowOldDeparture);
                    ins.Parameters.Add("@depDate", SqlDbType.VarChar, 20).Value = LegacyDate(newDeparture);
                    ins.Parameters.Add("@rate", SqlDbType.Decimal).Value = extensionBase;
                    ins.Parameters.Add("@charge", SqlDbType.Decimal).Value = extensionBase;
                    ins.Parameters.Add("@nights", SqlDbType.Decimal).Value = addedNights;
                    ins.Parameters.Add("@total", SqlDbType.Decimal).Value = extensionTotal;
                    ins.Parameters.Add("@gst", SqlDbType.Decimal).Value = extensionGst;
                    ins.Parameters.Add("@bed", SqlDbType.Decimal).Value = extensionBed;
                    ins.Parameters.Add("@currentdate", SqlDbType.DateTime).Value = _hotelClock.GetHotelNow(hotelId);
                    ins.Parameters.Add("@parentId", SqlDbType.Int).Value = request.PaymentId;
                    ins.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
                    ins.Parameters.Add("@reg", SqlDbType.VarChar, 100).Value = NormalizeRegId(request.RegId);

                    if (await ins.ExecuteNonQueryAsync(ct) <= 0)
                        throw new InvalidOperationException("Failed to insert the extension Room Rent row.");
                }

                // Match WebForms: add nightly rate history only for the newly-added dates.
                if (!string.IsNullOrWhiteSpace(planId) &&
                    !string.IsNullOrWhiteSpace(current.CategoryId) &&
                    chosenPerNight > 0m)
                {
                    for (var d = rowOldDeparture; d < newDeparture; d = d.AddDays(1))
                    {
                        await using var rateCmd = new SqlCommand(@"
IF NOT EXISTS
(
    SELECT 1
    FROM dbo.NewReservationRate
    WHERE reg_id=@reg AND hotel_id=@hotel
      AND plan_name=@plan AND category_id=@category AND rate_date=@rateDate
)
BEGIN
    INSERT INTO dbo.NewReservationRate(reg_id,hotel_id,plan_name,category_id,rate_date,rate)
    VALUES(@reg,@hotel,@plan,@category,@rateDate,@rate);
END
ELSE
BEGIN
    UPDATE dbo.NewReservationRate
       SET rate=@rate
     WHERE reg_id=@reg AND hotel_id=@hotel
       AND plan_name=@plan AND category_id=@category AND rate_date=@rateDate;
END;", cn, tx);
                        rateCmd.Parameters.Add("@reg", SqlDbType.VarChar, 100).Value = NormalizeRegId(request.RegId);
                        rateCmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
                        rateCmd.Parameters.Add("@plan", SqlDbType.VarChar, 100).Value = planId;
                        rateCmd.Parameters.Add("@category", SqlDbType.VarChar, 100).Value = current.CategoryId;
                        rateCmd.Parameters.Add("@rateDate", SqlDbType.Date).Value = d.Date;
                        rateCmd.Parameters.Add("@rate", SqlDbType.Decimal).Value = chosenPerNight;
                        await rateCmd.ExecuteNonQueryAsync(ct);
                    }
                }

                await SyncMasterDatesFromPaymentsAsync(cn, tx, hotelId, request.RegId, ct);
                await RefreshPaymentSummaryAsync(cn, tx, hotelId, request.RegId, ct);
                await InsertLogAsync(cn, tx, hotelId, userId, userName, ip, request.RegId,
                    "EXTEND RESERVATION",
                    $"PaymentId {request.PaymentId}: added {addedNights} night(s), {rowOldDeparture:yyyy-MM-dd} -> {newDeparture:yyyy-MM-dd}; rate/night={chosenPerNight:0.00}; tax={(request.IncludeTaxOnExtension ? "Yes" : "No")}.", ct);

                resultTotal = extensionTotal;
            }
            else
            {
                // WebForms shrink flow updates the selected Room Rent row to the reduced stay.
                var newNights = Math.Max(1, (newDeparture - newArrival).Days);
                var gstPerNight = oldNights > 0 ? current.Gst / oldNights : 0m;
                var bedPerNight = oldNights > 0 ? current.Bed / oldNights : 0m;

                var newBase = Math.Round(chosenPerNight * newNights, 2, MidpointRounding.AwayFromZero);
                var newGst = Math.Round(gstPerNight * newNights, 2, MidpointRounding.AwayFromZero);
                var newBed = Math.Round(bedPerNight * newNights, 2, MidpointRounding.AwayFromZero);
                var newTotal = Math.Round(newBase + newGst + newBed, 2, MidpointRounding.AwayFromZero);

                await using (var cmd = new SqlCommand(@"
UPDATE dbo.payments
SET ArrivalDate=@arrival,
    DepartureDate=@departure,
    Nights=@nights,
    rate=@rate,
    charge=@rate,
    GST=@gst,
    Bed=@bed,
    totalamount=@total
WHERE hotel_id=@hotel
  AND reg_id=@reg
  AND ID=@id
  AND LTRIM(RTRIM(ISNULL(descr,'')))='Room Rent';", cn, tx))
                {
                    cmd.Parameters.Add("@arrival", SqlDbType.VarChar, 20).Value = LegacyDate(newArrival);
                    cmd.Parameters.Add("@departure", SqlDbType.VarChar, 20).Value = LegacyDate(newDeparture);
                    cmd.Parameters.Add("@nights", SqlDbType.Decimal).Value = newNights;
                    cmd.Parameters.Add("@rate", SqlDbType.Decimal).Value = newBase;
                    cmd.Parameters.Add("@gst", SqlDbType.Decimal).Value = newGst;
                    cmd.Parameters.Add("@bed", SqlDbType.Decimal).Value = newBed;
                    cmd.Parameters.Add("@total", SqlDbType.Decimal).Value = newTotal;
                    cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
                    cmd.Parameters.Add("@reg", SqlDbType.VarChar, 100).Value = NormalizeRegId(request.RegId);
                    cmd.Parameters.Add("@id", SqlDbType.Int).Value = request.PaymentId;
                    if (await cmd.ExecuteNonQueryAsync(ct) != 1)
                        throw new InvalidOperationException("Reservation row changed before save. Please refresh and try again.");
                }

                await SyncMasterDatesFromPaymentsAsync(cn, tx, hotelId, request.RegId, ct);
                await RefreshPaymentSummaryAsync(cn, tx, hotelId, request.RegId, ct);
                await InsertLogAsync(cn, tx, hotelId, userId, userName, ip, request.RegId,
                    "SHRINK RESERVATION",
                    $"PaymentId {request.PaymentId}: {current.Departure:yyyy-MM-dd} -> {newDeparture:yyyy-MM-dd}; rate/night={chosenPerNight:0.00}", ct);

                resultTotal = newTotal;
            }

            await tx.CommitAsync(ct);

            var availabilityStart = isExtend ? current.Departure.Date : newDeparture;
            var availabilityEnd = isExtend ? newDeparture : current.Departure.Date;
            QueueAvailability(hotelId, hotelName, userId, userName, ip, availabilityStart, availabilityEnd, current.CategoryId);

            return FrontDeskOperationResult.Ok(
                isExtend ? "Reservation extended successfully." : "Reservation shortened successfully.",
                new
                {
                    arrival = newArrival,
                    departure = newDeparture,
                    nights = Math.Max(1, (newDeparture - newArrival).Days),
                    direction = isExtend ? "extend" : "shrink",
                    nightlyRate = chosenPerNight,
                    total = resultTotal,
                    taxApplied = isExtend && request.IncludeTaxOnExtension
                });
        }
        catch (Exception ex)
        {
            try { await tx.RollbackAsync(ct); } catch { }
            _logger.Error(ex, "Calendar extend/shrink failed for {RegId}/{PaymentId}.", request.RegId, request.PaymentId);
            await _dbLogger.LogExceptionAsync(ex, "Front Desk Calendar", "Extend / Shrink Reservation", hotelId, userId, userName, ip, ct);
            return FrontDeskOperationResult.Fail("Unable to change reservation dates. " + ex.Message);
        }
    }

    public async Task<FrontDeskOperationResult> DirectCheckInAsync(
        string hotelId, string hotelName, string userId, string userName, string ip,
        FrontDeskDirectCheckInRequest request, CancellationToken ct = default)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.RegId))
            return FrontDeskOperationResult.Fail("Invalid check-in request.");

        var regId = NormalizeRegId(request.RegId);
        try
        {
            var state = await _checkInService.GetReservationStateAsync(
                hotelId, hotelName, userId, userName, regId, ct);

            if (state == null || string.IsNullOrWhiteSpace(state.ReservationId))
                return FrontDeskOperationResult.Fail("Reservation was not found.");

            // WebForms CHECK-IN NOW is room-specific. Only the room bar selected by
            // the user is checked in; other rooms under the same reservation stay unchanged.
            var selected = state.Charges
                .Where(x => x.Description.Equals("Room Rent", StringComparison.OrdinalIgnoreCase))
                .Where(x => x.ReservationStatus.Equals("reservation", StringComparison.OrdinalIgnoreCase))
                .Where(x => request.PaymentId <= 0 || x.Id == request.PaymentId)
                .ToList();

            if (selected.Count == 0)
                return FrontDeskOperationResult.Fail("The selected room is no longer waiting for check-in. Please refresh the calendar.");

            if (request.PaymentId <= 0)
                selected = selected.Take(1).ToList();

            var save = new SaveCheckInRequest
            {
                Guest = state.Guest,
                SelectedChargeIds = selected.Select(x => x.Id).ToList(),
                SelectedRoomNos = selected
                    .Select(x => (x.RoomNo ?? string.Empty).Trim())
                    .Where(x => x.Length > 0 && !x.Equals("UNASSIGNED", StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                PaymentMethod = state.Totals?.PaymentMethod ?? string.Empty,
                PaidAmount = 0m,
                RoomSecurity = 0m,
                CompleteCheckIn = true,
                SaveAndPrint = false,
                PostAndPrint = false
            };

            var result = await _checkInService.SaveGuestAndCheckInAsync(
                hotelId, userId, userName, ip, save, ct);

            if (result.Success)
            {
                await using var logCn = new SqlConnection(_connectionString);
                await logCn.OpenAsync(ct);
                await InsertLogAsync(
                    logCn, null, hotelId, userId, userName, ip, regId,
                    "DIRECT CHECK IN",
                    $"Calendar direct check-in completed for PaymentId {request.PaymentId}.",
                    ct);
            }

            return result.Success
                ? FrontDeskOperationResult.Ok(result.Message, new { result.RegId, result.Id, result.RedirectUrl })
                : FrontDeskOperationResult.Fail(result.Message, result.Data);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Direct calendar check-in failed for hotel {HotelId}, reg {RegId}.", hotelId, regId);
            await _dbLogger.LogExceptionAsync(ex, "Front Desk Calendar", "Direct Check-In", hotelId, userId, userName, ip, ct);
            return FrontDeskOperationResult.Fail("Unable to check in the selected room. " + ex.Message);
        }
    }

    public async Task<FrontDeskOperationResult> PrepareEmailComposerAsync(
        string hotelId, string userId, string baseUrl, FrontDeskEmailPrepareRequest request, CancellationToken ct = default)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.RegId))
            return FrontDeskOperationResult.Fail("Reservation reference is missing.");

        var regId = NormalizeRegId(request.RegId);
        var paymentMode = string.Equals(request.PaymentMode, "hold", StringComparison.OrdinalIgnoreCase)
            ? "hold"
            : "charge";
        var reservationSource = string.Equals(request.Source, "GI", StringComparison.OrdinalIgnoreCase)
            ? "GI"
            : "NR";

        try
        {
            decimal due = 0m;
            string guestName = "Guest";
            string email = string.Empty;

            await using (var cn = new SqlConnection(_connectionString))
            {
                await cn.OpenAsync(ct);

                await using (var summary = new SqlCommand(@"
SELECT TOP (1) ISNULL(TRY_CONVERT(decimal(18,2),remaining_amount),0)
FROM dbo.PaymentsUpdateTB
WHERE hotel_id=@hotel AND reg_id=@reg;", cn))
                {
                    summary.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
                    summary.Parameters.Add("@reg", SqlDbType.VarChar, 100).Value = regId;
                    var raw = await summary.ExecuteScalarAsync(ct);
                    if (raw != null && raw != DBNull.Value)
                        decimal.TryParse(Convert.ToString(raw, CultureInfo.InvariantCulture),
                            NumberStyles.Any, CultureInfo.InvariantCulture, out due);
                }

                await using (var guest = new SqlCommand(@"
SELECT TOP (1) fullname,email
FROM
(
    SELECT 0 AS priority,
           LTRIM(RTRIM(ISNULL(GuestName,'') + ' ' + ISNULL(LastName,''))) AS fullname,
           ISNULL(Email,'') AS email
    FROM dbo.GuestInformationLogTB
    WHERE hotel_id=@hotel AND reg_id=@reg

    UNION ALL

    SELECT 1 AS priority,
           LTRIM(RTRIM(ISNULL(GuestName,'') + ' ' + ISNULL(LastName,''))) AS fullname,
           ISNULL(Email,'') AS email
    FROM dbo.NewReservationsTB
    WHERE hotel_id=@hotel AND reg_id=@reg
) q
ORDER BY priority;", cn))
                {
                    guest.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
                    guest.Parameters.Add("@reg", SqlDbType.VarChar, 100).Value = regId;
                    await using var rd = await guest.ExecuteReaderAsync(ct);
                    if (await rd.ReadAsync(ct))
                    {
                        guestName = string.IsNullOrWhiteSpace(S(rd, "fullname")) ? "Guest" : S(rd, "fullname");
                        email = S(rd, "email");
                    }
                }

            }

            // Email is available even when there is no outstanding balance.
            // Only the Payment Link template depends on a positive balance.
            // WebForms parity: the email composer does not create a Stripe Checkout
            // session up front. It sends the legacy PayNow.aspx URL, which creates the
            // checkout session only when the guest opens the payment page.
            var root = (baseUrl ?? string.Empty).TrimEnd('/');
            var paymentUrl = string.Empty;

            var linkAmount = request.Amount.GetValueOrDefault() > 0m
                ? Math.Round(request.Amount.GetValueOrDefault(), 2)
                : Math.Round(due, 2);

            if (due > 0m && linkAmount > due + 0.005m)
                return FrontDeskOperationResult.Fail("Payment amount cannot exceed the outstanding balance.");

            if (linkAmount > 0m)
            {
                paymentUrl = $"{root}/PayNow.aspx?" +
                    $"reg_id={B64UrlEncode(regId)}&" +
                    $"hotel_id={B64UrlEncode(hotelId)}&" +
                    $"name={B64UrlEncode(guestName)}&" +
                    $"amount={B64UrlEncode(linkAmount.ToString(CultureInfo.InvariantCulture))}&" +
                    $"arrival={B64UrlEncode(string.Empty)}&" +
                    $"depart={B64UrlEncode(string.Empty)}&" +
                    $"src={B64UrlEncode(reservationSource)}&" +
                    $"userid={B64UrlEncode(userId ?? string.Empty)}&" +
                    $"payment_mode={Uri.EscapeDataString(paymentMode)}";
            }

            var invoiceUrl = $"{root}/InvoiceRecieving.aspx?hotel_id={B64UrlEncode(hotelId)}&reg_id={B64UrlEncode(regId)}";
            // The WebForms calendar currently uses the same invoice receiving URL for the
            // Invoice Link and Invoice PDF tabs, so keep that parity here.
            var invoicePdfUrl = invoiceUrl;

            var genericMessage =
$@"Hello {guestName},

";

            var paymentMessage = linkAmount > 0m
                ? (paymentMode == "hold"
                    ? $@"Hello {guestName},

Please use the secure link below to authorize {linkAmount:0.00} on your card. This is an authorization hold only; the hotel may capture it later:

{paymentUrl}

Thank you."
                    : $@"Hello {guestName},

Please use the secure payment link below to complete your payment:

{paymentUrl}

Thank you.")
                : genericMessage;

            var invoiceMessage = due > 0m
                ? $@"Hello {guestName},

Please find your invoice at the link below:

{invoiceUrl}

If you wish to pay online, you can use the secure payment link:

{paymentUrl}

Thank you."
                : $@"Hello {guestName},

Please find your invoice at the link below:

{invoiceUrl}

Thank you.";

            var invoicePdfMessage =
$@"Hello {guestName},

Please download your invoice PDF from below:

{invoicePdfUrl}

Thank you.";

            return FrontDeskOperationResult.Ok("Email composer ready.", new FrontDeskEmailComposerDto
            {
                RegId = regId,
                GuestName = guestName,
                Email = email,
                GenericMessage = genericMessage,
                PaymentUrl = paymentUrl,
                InvoiceUrl = invoiceUrl,
                InvoicePdfUrl = invoicePdfUrl,
                PaymentMessage = paymentMessage,
                InvoiceMessage = invoiceMessage,
                InvoicePdfMessage = invoicePdfMessage
            });
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Calendar email composer preparation failed for {RegId}.", regId);
            await _dbLogger.LogExceptionAsync(ex, "Front Desk Calendar", "Prepare Email", hotelId, userId, userId, string.Empty, ct);
            return FrontDeskOperationResult.Fail("Unable to prepare the email. " + ex.Message);
        }
    }

    public async Task<FrontDeskOperationResult> SendEmailAsync(
        string hotelId, string userId, string userName, string ip,
        FrontDeskEmailSendRequest request, CancellationToken ct = default)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.RegId))
            return FrontDeskOperationResult.Fail("Reservation reference is missing.");
        if (string.IsNullOrWhiteSpace(request.Email) ||
            !new System.ComponentModel.DataAnnotations.EmailAddressAttribute().IsValid(request.Email.Trim()))
            return FrontDeskOperationResult.Fail("Enter a valid guest email address.");
        var regId = NormalizeRegId(request.RegId);
        var emailType = (request.EmailType ?? "GEN").Trim().ToUpperInvariant();
        if (emailType is not ("GEN" or "PAY" or "INV" or "INVPDF")) emailType = "GEN";

        var requestUrl = (request.Url ?? string.Empty).Trim();
        if (emailType != "GEN")
        {
            if (!Uri.TryCreate(requestUrl, UriKind.Absolute, out var url) ||
                (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps))
                return FrontDeskOperationResult.Fail("The email link is invalid.");
        }

        try
        {
            string fromEmail = string.Empty;
            string fromName = string.Empty;
            string host = string.Empty;
            string smtpUser = string.Empty;
            string encryptedPassword = string.Empty;
            int port = 0;
            bool useSsl = false;

            await using (var cn = new SqlConnection(_connectionString))
            {
                await cn.OpenAsync(ct);
                await using var cmd = new SqlCommand(@"
SELECT TOP (1) FromEmail,FromName,SmtpHost,SmtpPort,UseSsl,SmtpUser,SmtpPassEnc
FROM dbo.EmailSettingsTB
WHERE IsActive=1 AND hotel_id IN (@hotel,'-1')
ORDER BY CASE WHEN hotel_id=@hotel THEN 0 ELSE 1 END, ID DESC;", cn);
                cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
                await using var rd = await cmd.ExecuteReaderAsync(ct);
                if (!await rd.ReadAsync(ct))
                    return FrontDeskOperationResult.Fail("No active SMTP configuration was found for this hotel.");

                fromEmail = S(rd, "FromEmail");
                fromName = S(rd, "FromName");
                host = S(rd, "SmtpHost");
                smtpUser = S(rd, "SmtpUser");
                encryptedPassword = S(rd, "SmtpPassEnc");
                port = I(rd, "SmtpPort");
                useSsl = B(rd, "UseSsl");
            }

            if (string.IsNullOrWhiteSpace(fromEmail) || string.IsNullOrWhiteSpace(host) || port <= 0)
                return FrontDeskOperationResult.Fail("SMTP configuration is incomplete.");

            var password = DecryptEmailPassword(encryptedPassword);
            var subject = emailType switch
            {
                "INV" => $"Invoice Link - Reservation {regId}",
                "INVPDF" => $"Invoice PDF - Reservation {regId}",
                "PAY" => $"Payment Link - Reservation {regId}",
                _ => $"Message - Reservation {regId}"
            };

            var customMessage = (request.Message ?? string.Empty).Trim();
            if (customMessage.Length == 0)
                return FrontDeskOperationResult.Fail("Email message is required.");

            if (emailType != "GEN" &&
                !string.IsNullOrWhiteSpace(requestUrl) &&
                !customMessage.Contains(requestUrl, StringComparison.OrdinalIgnoreCase))
            {
                customMessage += Environment.NewLine + Environment.NewLine + requestUrl;
            }

            var safeMessage = WebUtility.HtmlEncode(customMessage)
                .Replace("\r\n", "<br>", StringComparison.Ordinal)
                .Replace("\n", "<br>", StringComparison.Ordinal);

            using var msg = new MailMessage
            {
                From = new MailAddress(fromEmail, string.IsNullOrWhiteSpace(fromName) ? fromEmail : fromName),
                Subject = subject,
                Body = $"<div style='font-family:Arial,sans-serif;font-size:14px;line-height:1.6'>{safeMessage}</div>",
                IsBodyHtml = true
            };
            msg.To.Add(request.Email.Trim());

            using var smtp = new SmtpClient(host, port)
            {
                EnableSsl = useSsl,
                Credentials = new NetworkCredential(smtpUser, password),
                DeliveryMethod = SmtpDeliveryMethod.Network,
                Timeout = 30000
            };
            await smtp.SendMailAsync(msg);
            ct.ThrowIfCancellationRequested();

            await using (var logCn = new SqlConnection(_connectionString))
            {
                await logCn.OpenAsync(ct);
                await InsertLogAsync(logCn, null, hotelId, userId, userName, ip, regId,
                    emailType switch
                    {
                        "PAY" => "SEND PAYMENT LINK",
                        "INV" or "INVPDF" => "SEND INVOICE LINK",
                        _ => "SEND GUEST EMAIL"
                    },
                    $"Sent {emailType} email to {request.Email.Trim()}.", ct);
            }

            return FrontDeskOperationResult.Ok(
                emailType == "PAY" ? "Payment link sent successfully." : "Email sent successfully.");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Calendar email send failed for {RegId}.", regId);
            await _dbLogger.LogExceptionAsync(ex, "Front Desk Calendar", "Send Email", hotelId, userId, userName, ip, ct);
            return FrontDeskOperationResult.Fail("Unable to send the email. " + ex.Message);
        }
    }

    public async Task<FrontDeskOperationResult> NoShowAsync(
        string hotelId, string hotelName, string userId, string userName, string ip,
        FrontDeskNoShowRequest request, CancellationToken ct = default)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.RegId))
            return FrontDeskOperationResult.Fail("Reservation ID is required.");

        var regId = NormalizeRegId(request.RegId);
        var today = _hotelClock.GetHotelToday(hotelId);
        DateTime? availabilityStart = null;
        DateTime? availabilityEnd = null;
        string source = string.Empty;
        int sourceId = 0;
        string bookingId = string.Empty;

        await using var cn = new SqlConnection(_connectionString);
        await cn.OpenAsync(ct);
        await using var tx = (SqlTransaction)await cn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

        try
        {
            // The WebForms ReservationList only offers No Show after the arrival date.
            // Validate the same rule on the server so a stale/forged calendar request
            // cannot mark a current or future reservation as a no-show.
            await using (var dateCmd = new SqlCommand(@"
SELECT
    MIN(COALESCE(
        TRY_CONVERT(date,ArrivalDate,110),
        TRY_CONVERT(date,ArrivalDate,23),
        TRY_CONVERT(date,ArrivalDate,101),
        TRY_CONVERT(date,ArrivalDate,103),
        TRY_CONVERT(date,ArrivalDate)
    )) AS ArrivalDate,
    MAX(COALESCE(
        TRY_CONVERT(date,DepartureDate,110),
        TRY_CONVERT(date,DepartureDate,23),
        TRY_CONVERT(date,DepartureDate,101),
        TRY_CONVERT(date,DepartureDate,103),
        TRY_CONVERT(date,DepartureDate)
    )) AS DepartureDate
FROM dbo.payments
WHERE hotel_id=@hotel AND reg_id=@reg
  AND (@paymentId<=0 OR EXISTS
      (SELECT 1 FROM dbo.payments p2
       WHERE p2.hotel_id=@hotel AND p2.reg_id=@reg AND p2.ID=@paymentId
         AND LTRIM(RTRIM(ISNULL(p2.descr,'')))='Room Rent'
         AND LOWER(LTRIM(RTRIM(ISNULL(p2.res_status,''))))='reservation'))
  AND LTRIM(RTRIM(ISNULL(descr,'')))='Room Rent'
  AND LOWER(LTRIM(RTRIM(ISNULL(res_status,''))))='reservation';", cn, tx))
            {
                dateCmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
                dateCmd.Parameters.Add("@reg", SqlDbType.VarChar, 100).Value = regId;
                dateCmd.Parameters.Add("@paymentId", SqlDbType.Int).Value = request.PaymentId;
                await using var rd = await dateCmd.ExecuteReaderAsync(ct);
                if (await rd.ReadAsync(ct))
                {
                    if (!rd.IsDBNull(0)) availabilityStart = Convert.ToDateTime(rd.GetValue(0), CultureInfo.InvariantCulture).Date;
                    if (!rd.IsDBNull(1)) availabilityEnd = Convert.ToDateTime(rd.GetValue(1), CultureInfo.InvariantCulture).Date;
                }
            }

            if (!availabilityStart.HasValue)
            {
                await tx.RollbackAsync(ct);
                return FrontDeskOperationResult.Fail("This reservation is no longer available for No Show.");
            }

            if (availabilityStart.Value >= today)
            {
                await tx.RollbackAsync(ct);
                return FrontDeskOperationResult.Fail("No Show is only available after the reservation arrival date.");
            }

            // Calendar guest display already gives GuestInformationLogTB precedence over
            // NewReservationsTB. Use the same source preference, then mirror the WebForms
            // NoShow archive/delete behaviour for that source row.
            await using (var sourceCmd = new SqlCommand(@"
SELECT TOP (1) src,id,ISNULL(booking_id,'') AS booking_id
FROM
(
    SELECT 'GI' AS src,id,booking_id,1 AS source_order
    FROM dbo.GuestInformationLogTB
    WHERE hotel_id=@hotel AND reg_id=@reg
      AND LOWER(LTRIM(RTRIM(ISNULL(res_status,''))))='reservation'

    UNION ALL

    SELECT 'NR' AS src,id,booking_id,2 AS source_order
    FROM dbo.NewReservationsTB
    WHERE hotel_id=@hotel AND reg_id=@reg
      AND LOWER(LTRIM(RTRIM(ISNULL(res_status,''))))='reservation'
) s
ORDER BY source_order,id DESC;", cn, tx))
            {
                sourceCmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
                sourceCmd.Parameters.Add("@reg", SqlDbType.VarChar, 100).Value = regId;
                await using var rd = await sourceCmd.ExecuteReaderAsync(ct);
                if (await rd.ReadAsync(ct))
                {
                    source = Convert.ToString(rd["src"], CultureInfo.InvariantCulture) ?? string.Empty;
                    sourceId = Convert.ToInt32(rd["id"], CultureInfo.InvariantCulture);
                    bookingId = Convert.ToString(rd["booking_id"], CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
                }
            }

            if (sourceId <= 0 || (source != "GI" && source != "NR"))
            {
                await tx.RollbackAsync(ct);
                return FrontDeskOperationResult.Fail("The active reservation record could not be found for No Show.");
            }

            var archiveSql = source == "GI" ? @"
INSERT INTO dbo.NoShowTB (
  reg_id, GuestName, LastName, gender, ArrivalDate, dept_date, DOB, Address, Country,
  City, Email, PhoneNo, Agency, Status, shift, date, cb_status, res_status, cnic, visa,
  number_of_adult, adult_male, adult_female, number_of_minor, minor_male, minor_female,
  advance_paid, total_amount, payment_method, hotel_id, user_id, systemUser, systemName,
  ipAddress, council_id, room_category, room_no, reason, booking_id, isupdateavailibilty,
  noofrooms, iscouncilreservationaccepted
)
SELECT
  reg_id, GuestName, LastName, gender, ArrivalDate, DepartureDate, DOB, Address, Country,
  City, Email, PhoneNo, Agency, Status, shift, date, cb_status, res_status, cnic, VisaPassportNo,
  NumberOfAdults, adult_male, adult_female, NumberOfMinors, minor_male, minor_female,
  advance_paid, total_amount, payment_method, hotel_id, user_id, systemUser, systemName,
  ipAddress, council_id, room_category, room_no, reason, booking_id, isupdateavailibilty,
  noofrooms, iscouncilreservationaccepted
FROM dbo.GuestInformationLogTB
WHERE id=@id AND hotel_id=@hotel;" : @"
INSERT INTO dbo.NoShowTB (
  reg_id, GuestName, LastName, gender, ArrivalDate, dept_date, DOB, Address, Country,
  City, Email, PhoneNo, Agency, Status, shift, date, cb_status, res_status, cnic, visa,
  number_of_adult, adult_male, adult_female, number_of_minor, minor_male, minor_female,
  advance_paid, total_amount, payment_method, hotel_id, user_id, systemUser, systemName,
  ipAddress, council_id, room_category, room_no, reason, booking_id, isupdateavailibilty,
  noofrooms, iscouncilreservationaccepted
)
SELECT
  reg_id, GuestName, LastName, gender, ArrivalDate, dept_date, DOB, Address, Country,
  City, Email, PhoneNo, Agency, Status, shift, date, cb_status, res_status, cnic, visa,
  number_of_adult, adult_male, adult_female, number_of_minor, minor_male, minor_female,
  advance_paid, total_amount, payment_method, hotel_id, user_id, systemUser, systemName,
  ipAddress, council_id, room_category, room_no, reason, booking_id, isupdateavailibilty,
  noofrooms, iscouncilreservationaccepted
FROM dbo.NewReservationsTB
WHERE id=@id AND hotel_id=@hotel;";

            int archived;
            await using (var archiveCmd = new SqlCommand(archiveSql, cn, tx))
            {
                archiveCmd.Parameters.Add("@id", SqlDbType.Int).Value = sourceId;
                archiveCmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
                archived = await archiveCmd.ExecuteNonQueryAsync(ct);
            }

            if (archived <= 0)
            {
                await tx.RollbackAsync(ct);
                return FrontDeskOperationResult.Fail("The reservation could not be archived as No Show.");
            }

            var deleteSql = source == "GI"
                ? "DELETE FROM dbo.GuestInformationLogTB WHERE id=@id AND hotel_id=@hotel;"
                : "DELETE FROM dbo.NewReservationsTB WHERE id=@id AND hotel_id=@hotel;";
            await using (var deleteCmd = new SqlCommand(deleteSql, cn, tx))
            {
                deleteCmd.Parameters.Add("@id", SqlDbType.Int).Value = sourceId;
                deleteCmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
                await deleteCmd.ExecuteNonQueryAsync(ct);
            }

            // WebForms removes the master reservation row. The MVC calendar is rendered
            // from payments, so mark its active reservation rows No Show as the equivalent
            // visibility/status change instead of leaving the booking bar on the calendar.
            int changedRows;
            await using (var statusCmd = new SqlCommand(@"
UPDATE dbo.payments
SET res_status='no show'
WHERE hotel_id=@hotel AND reg_id=@reg
  AND LOWER(LTRIM(RTRIM(ISNULL(res_status,''))))='reservation';", cn, tx))
            {
                statusCmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
                statusCmd.Parameters.Add("@reg", SqlDbType.VarChar, 100).Value = regId;
                changedRows = await statusCmd.ExecuteNonQueryAsync(ct);
            }

            if (changedRows <= 0)
            {
                await tx.RollbackAsync(ct);
                return FrontDeskOperationResult.Fail("This reservation is no longer available for No Show.");
            }

            await InsertLogAsync(cn, tx, hotelId, userId, userName, ip, regId,
                "NO SHOW", $"Reservation marked No Show. Source: {source}.", ct);

            await tx.CommitAsync(ct);

            if (availabilityStart.HasValue && availabilityEnd.HasValue)
                QueueAvailability(hotelId, hotelName, userId, userName, ip,
                    availabilityStart.Value, availabilityEnd.Value, "0");

            var channelWarning = await ReportNoShowToChannexAsync(hotelId, bookingId, ct);
            return FrontDeskOperationResult.Ok(channelWarning.Length == 0
                ? "Reservation marked as No Show successfully."
                : "Reservation marked as No Show successfully. " + channelWarning);
        }
        catch (Exception ex)
        {
            try { await tx.RollbackAsync(ct); } catch { }
            _logger.Error(ex, "Unable to mark reservation {RegId} as No Show.", regId);
            await _dbLogger.LogExceptionAsync(ex, "Front Desk Calendar", "No Show", hotelId, userId, userName, ip, ct);
            return FrontDeskOperationResult.Fail("Unable to mark reservation as No Show. " + ex.Message);
        }
    }

    private async Task<string> ReportNoShowToChannexAsync(string hotelId, string bookingId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(bookingId) || bookingId.Equals("N/A", StringComparison.OrdinalIgnoreCase))
            return string.Empty;

        try
        {
            var context = await LoadNoShowChannelContextAsync(hotelId, ct);
            if (context == null) return string.Empty;

            var http = _httpClientFactory.CreateClient("ChannelManager");
            using var message = new HttpRequestMessage(
                HttpMethod.Post,
                $"{context.Value.BaseUrl.TrimEnd('/')}/api/v1/bookings/{Uri.EscapeDataString(bookingId.Trim())}/no_show");
            message.Headers.TryAddWithoutValidation("user-api-key", context.Value.ApiKey);
            message.Headers.Accept.ParseAdd("application/json");
            message.Content = new StringContent(
                "{\"no_show_report\":{\"waived_fees\":false}}",
                Encoding.UTF8,
                "application/json");

            using var response = await http.SendAsync(message, ct);
            if (response.IsSuccessStatusCode) return string.Empty;

            _logger.Warning("Channex No Show report failed for {BookingId}: {StatusCode}.",
                bookingId, (int)response.StatusCode);
            return "The local No Show was saved, but the channel manager did not confirm the update.";
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Channex No Show report failed for booking {BookingId}.", bookingId);
            return "The local No Show was saved, but the channel manager update could not be completed.";
        }
    }

    private async Task<(string BaseUrl, string ApiKey)?> LoadNoShowChannelContextAsync(
        string hotelId, CancellationToken ct)
    {
        await using var cn = new SqlConnection(_connectionString);
        await cn.OpenAsync(ct);

        bool useApp = false;
        await using (var modeCmd = new SqlCommand(@"
SELECT TOP (1) ISNULL(channexstaging,0)
FROM dbo.HotelsSignUpTB
WHERE hotel_id=@hotel;", cn))
        {
            modeCmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
            var raw = await modeCmd.ExecuteScalarAsync(ct);
            if (raw != null && raw != DBNull.Value)
                useApp = Convert.ToBoolean(raw, CultureInfo.InvariantCulture);
        }

        var channelName = useApp ? "app" : "staging";
        string baseUrl;
        await using (var linkCmd = new SqlCommand(
            "SELECT TOP (1) link FROM dbo.channexlink WHERE channelname=@channel;", cn))
        {
            linkCmd.Parameters.Add("@channel", SqlDbType.VarChar, 50).Value = channelName;
            baseUrl = Convert.ToString(await linkCmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
        }

        if (baseUrl.Length == 0) return null;

        string apiKey = string.Empty;
        await using (var keyCmd = new SqlCommand(
            "SELECT TOP (1) apikey,username FROM dbo.channelmanagerapikey ORDER BY id DESC;", cn))
        await using (var rd = await keyCmd.ExecuteReaderAsync(ct))
        {
            if (await rd.ReadAsync(ct))
            {
                apiKey = baseUrl.TrimEnd('/').Equals("https://app.channex.io", StringComparison.OrdinalIgnoreCase)
                    ? Convert.ToString(rd["apikey"], CultureInfo.InvariantCulture)?.Trim() ?? string.Empty
                    : Convert.ToString(rd["username"], CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
            }
        }

        return apiKey.Length == 0 ? null : (baseUrl, apiKey);
    }

    public async Task<FrontDeskOperationResult> CancelReservationAsync(
        string hotelId, string hotelName, string userId, string userName, string ip,
        FrontDeskCancelReservationRequest request, CancellationToken ct = default)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.RegId))
            return FrontDeskOperationResult.Fail("Reservation ID is required.");

        var reason = (request.Reason ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(reason))
            return FrontDeskOperationResult.Fail("Cancellation reason is required.");

        var regId = NormalizeRegId(request.RegId);
        DateTime? availabilityStart = null;
        DateTime? availabilityEnd = null;

        await using var cn = new SqlConnection(_connectionString);
        await cn.OpenAsync(ct);
        await using var tx = (SqlTransaction)await cn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

        try
        {
            // Keep the same cancellation audit/history behaviour as the WebForms calendar:
            // copy the active guest/reservation record to CancelledReservationsTB with the
            // entered reason before removing the master row. The payments rows are also
            // marked cancelled because the MVC calendar renders room-level stays from payments.
            await using (var dateCmd = new SqlCommand(@"
SELECT
    MIN(COALESCE(
        TRY_CONVERT(date,ArrivalDate,110),
        TRY_CONVERT(date,ArrivalDate,23),
        TRY_CONVERT(date,ArrivalDate,101),
        TRY_CONVERT(date,ArrivalDate,103),
        TRY_CONVERT(date,ArrivalDate)
    )) AS ArrivalDate,
    MAX(COALESCE(
        TRY_CONVERT(date,DepartureDate,110),
        TRY_CONVERT(date,DepartureDate,23),
        TRY_CONVERT(date,DepartureDate,101),
        TRY_CONVERT(date,DepartureDate,103),
        TRY_CONVERT(date,DepartureDate)
    )) AS DepartureDate
FROM dbo.payments
WHERE hotel_id=@hotel AND reg_id=@reg
  AND LTRIM(RTRIM(ISNULL(descr,'')))='Room Rent'
  AND LOWER(LTRIM(RTRIM(ISNULL(res_status,'')))) IN ('reservation','provisional','tentative');", cn, tx))
            {
                dateCmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
                dateCmd.Parameters.Add("@reg", SqlDbType.VarChar, 100).Value = regId;
                await using var rd = await dateCmd.ExecuteReaderAsync(ct);
                if (await rd.ReadAsync(ct))
                {
                    if (!rd.IsDBNull(0)) availabilityStart = Convert.ToDateTime(rd.GetValue(0), CultureInfo.InvariantCulture).Date;
                    if (!rd.IsDBNull(1)) availabilityEnd = Convert.ToDateTime(rd.GetValue(1), CultureInfo.InvariantCulture).Date;
                }
            }

            int archived = 0;

            await using (var archiveGuest = new SqlCommand(@"
INSERT INTO dbo.CancelledReservationsTB (
  reg_id, GuestName, LastName, gender, ArrivalDate, dept_date, DOB, Address, Country,
  City, Email, PhoneNo, Agency, Status, shift, cb_status, res_status, cnic, visa,
  number_of_adult, adult_male, adult_female, number_of_minor, minor_male, minor_female,
  advance_paid, total_amount, payment_method, hotel_id, user_id, systemUser, systemName,
  ipAddress, council_id, room_category, room_no, reason, booking_id, isupdateavailibilty,
  noofrooms, iscouncilreservationaccepted, rateplanname
)
SELECT
  reg_id, GuestName, LastName, gender, ArrivalDate, DepartureDate, DOB, Address, Country,
  City, Email, PhoneNo, Agency, Status, shift, cb_status, res_status, cnic, VisaPassportNo,
  NumberOfAdults, adult_male, adult_female, NumberOfMinors, minor_male, minor_female,
  advance_paid, total_amount, payment_method, hotel_id, @userid, systemUser, systemName,
  ipAddress, council_id, room_category, room_no, @reason, booking_id, isupdateavailibilty,
  noofrooms, iscouncilreservationaccepted, rateplanname
FROM dbo.GuestInformationLogTB
WHERE reg_id=@reg AND hotel_id=@hotel;", cn, tx))
            {
                archiveGuest.Parameters.Add("@reg", SqlDbType.VarChar, 100).Value = regId;
                archiveGuest.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
                archiveGuest.Parameters.Add("@reason", SqlDbType.VarChar, 500).Value = reason;
                archiveGuest.Parameters.Add("@userid", SqlDbType.VarChar, 100).Value = userId ?? string.Empty;
                archived += await archiveGuest.ExecuteNonQueryAsync(ct);
            }

            await using (var archiveReservation = new SqlCommand(@"
INSERT INTO dbo.CancelledReservationsTB (
  reg_id, GuestName, LastName, gender, ArrivalDate, dept_date, DOB, Address, Country,
  City, Email, PhoneNo, Agency, Status, shift, cb_status, res_status, cnic, visa,
  number_of_adult, adult_male, adult_female, number_of_minor, minor_male, minor_female,
  advance_paid, total_amount, payment_method, hotel_id, user_id, systemUser, systemName,
  ipAddress, council_id, room_category, room_no, reason, booking_id, isupdateavailibilty,
  noofrooms, iscouncilreservationaccepted, rateplanname
)
SELECT
  reg_id, GuestName, LastName, gender, ArrivalDate, dept_date, DOB, Address, Country,
  City, Email, PhoneNo, Agency, Status, shift, cb_status, res_status, cnic, visa,
  number_of_adult, adult_male, adult_female, number_of_minor, minor_male, minor_female,
  advance_paid, total_amount, payment_method, hotel_id, @userid, systemUser, systemName,
  ipAddress, council_id, room_category, room_no, @reason, booking_id, isupdateavailibilty,
  noofrooms, iscouncilreservationaccepted, rateplanname
FROM dbo.NewReservationsTB
WHERE reg_id=@reg AND hotel_id=@hotel;", cn, tx))
            {
                archiveReservation.Parameters.Add("@reg", SqlDbType.VarChar, 100).Value = regId;
                archiveReservation.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
                archiveReservation.Parameters.Add("@reason", SqlDbType.VarChar, 500).Value = reason;
                archiveReservation.Parameters.Add("@userid", SqlDbType.VarChar, 100).Value = userId ?? string.Empty;
                archived += await archiveReservation.ExecuteNonQueryAsync(ct);
            }

            int cancelledRows;
            await using (var cancelPayments = new SqlCommand(@"
UPDATE dbo.payments
SET res_status='cancelled'
WHERE hotel_id=@hotel AND reg_id=@reg
  AND LOWER(LTRIM(RTRIM(ISNULL(res_status,'')))) IN ('reservation','provisional','tentative');", cn, tx))
            {
                cancelPayments.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
                cancelPayments.Parameters.Add("@reg", SqlDbType.VarChar, 100).Value = regId;
                cancelledRows = await cancelPayments.ExecuteNonQueryAsync(ct);
            }

            if (cancelledRows <= 0 && archived <= 0)
            {
                await tx.RollbackAsync(ct);
                return FrontDeskOperationResult.Fail("Reservation could not be cancelled in its current status.");
            }

            await using (var deleteMaster = new SqlCommand(@"
DELETE FROM dbo.GuestInformationLogTB WHERE reg_id=@reg AND hotel_id=@hotel;
DELETE FROM dbo.NewReservationsTB      WHERE reg_id=@reg AND hotel_id=@hotel;", cn, tx))
            {
                deleteMaster.Parameters.Add("@reg", SqlDbType.VarChar, 100).Value = regId;
                deleteMaster.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
                await deleteMaster.ExecuteNonQueryAsync(ct);
            }

            await InsertLogAsync(cn, tx, hotelId, userId, userName, ip, regId,
                "CANCEL RESERVATION", $"Reason: {reason}", ct);

            await tx.CommitAsync(ct);

            if (availabilityStart.HasValue && availabilityEnd.HasValue)
                QueueAvailability(hotelId, hotelName, userId, userName, ip,
                    availabilityStart.Value, availabilityEnd.Value, "0");

            return FrontDeskOperationResult.Ok("Reservation cancelled successfully.");
        }
        catch (Exception ex)
        {
            try { await tx.RollbackAsync(ct); } catch { }
            _logger.Error(ex, "Unable to cancel reservation {RegId}.", regId);
            await _dbLogger.LogExceptionAsync(ex, "Front Desk Calendar", "Cancel Reservation", hotelId, userId, userName, ip, ct);
            return FrontDeskOperationResult.Fail("Unable to cancel reservation. " + ex.Message);
        }
    }

    private async Task<FrontDeskOperationResult> SaveBlockInternalAsync(
        string hotelId, string hotelName, string userId, string userName, string ip,
        FrontDeskRoomBlockRequest request, int? blockId, CancellationToken ct)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.RoomNo) || string.IsNullOrWhiteSpace(request.Reason))
            return FrontDeskOperationResult.Fail("Room and reason are required.");
        if (request.EndDate.Date < request.StartDate.Date) return FrontDeskOperationResult.Fail("End date cannot be before start date.");

        var start = request.StartDate.Date;
        var end = request.EndDate.Date;
        await using var cn = new SqlConnection(_connectionString);
        await cn.OpenAsync(ct);
        await using var tx = (SqlTransaction)await cn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        try
        {
            var conflict = await FindBlockConflictAsync(cn, tx, hotelId, request.RoomNo, start, end, blockId, ct);
            if (conflict.Length > 0)
                return FrontDeskOperationResult.Fail("This room is already blocked in the selected date range.", new { conflictInfo = conflict });
            conflict = await FindBookingConflictAsync(cn, tx, hotelId, request.RoomNo, start, end.AddDays(1), 0, ct);
            if (conflict.Length > 0)
                return FrontDeskOperationResult.Fail("This room already has a reservation/check-in in the selected date range.", new { conflictInfo = conflict });

            // RoomBlocksTB.RoomID is required in several legacy property databases.
            // Resolve the actual legacy room identifier from RoomsTB when such a column
            // exists; otherwise fall back to room_no.  This avoids both the old
            // "Invalid column name ID" error and NULL RoomID inserts.
            var legacyRoomId = await ResolveRoomBlockRoomIdAsync(cn, tx, hotelId, request.RoomNo, ct);

            int savedBlockId;
            if (blockId.HasValue && blockId.Value > 0)
            {
                await using var cmd = new SqlCommand(@"
UPDATE dbo.RoomBlocksTB
SET BlockStartDate=@start,BlockEndDate=@end,Reason=@reason,IsActive=1,username=@username,userid=@userid
WHERE HotelID=@hotel AND BlockID=@id;
SELECT @id;", cn, tx);
                AddBlockParams(cmd, hotelId, userId, userName, request, start, end);
                cmd.Parameters.Add("@id", SqlDbType.Int).Value = blockId.Value;
                savedBlockId = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
            }
            else
            {
                await using var cmd = new SqlCommand(@"
INSERT INTO dbo.RoomBlocksTB(HotelID,RoomID,RoomNo,BlockStartDate,BlockEndDate,Reason,username,userid,CreatedOn,IsActive)
OUTPUT INSERTED.BlockID
VALUES(@hotel,@roomId,@room,@start,@end,@reason,@username,@userid,@created,1);", cn, tx);
                AddBlockParams(cmd, hotelId, userId, userName, request, start, end);
                cmd.Parameters.Add("@roomId", SqlDbType.VarChar, 100).Value = legacyRoomId;
                cmd.Parameters.Add("@created", SqlDbType.DateTime).Value = _hotelClock.GetHotelNow(hotelId);
                savedBlockId = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
            }

            await InsertLogAsync(cn, tx, hotelId, userId, userName, ip, string.Empty,
                blockId.HasValue ? "UPDATE ROOM BLOCK" : "BLOCK ROOM",
                $"Room {request.RoomNo}, {start:yyyy-MM-dd} to {end:yyyy-MM-dd}, reason: {request.Reason}", ct);
            await tx.CommitAsync(ct);
            QueueAvailability(hotelId, hotelName, userId, userName, ip, start, end, request.CategoryId);
            return FrontDeskOperationResult.Ok(blockId.HasValue ? "Room block updated." : "Room blocked successfully.", new { blockId = savedBlockId });
        }
        catch (Exception ex)
        {
            try { await tx.RollbackAsync(ct); } catch { }
            _logger.Error(ex, "Room block operation failed for hotel {HotelId}, room {RoomNo}.", hotelId, request.RoomNo);
            await _dbLogger.LogExceptionAsync(ex, "Front Desk Calendar", blockId.HasValue ? "Update Room Block" : "Block Room", hotelId, userId, userName, ip, ct);
            return FrontDeskOperationResult.Fail("Unable to save room block. " + ex.Message);
        }
    }

    private async Task<string> ResolveRoomBlockRoomIdAsync(
        SqlConnection cn, SqlTransaction tx, string hotelId, string roomNo, CancellationToken ct)
    {
        // Different legacy databases used different physical room identifier names.
        // Discover the existing column first so we never reference a column that is absent.
        string columnName = string.Empty;
        await using (var find = new SqlCommand(@"
SELECT TOP (1) c.name
FROM sys.columns c
WHERE c.object_id=OBJECT_ID('dbo.RoomsTB')
  AND c.name IN ('RoomID','room_id','ID','id')
ORDER BY CASE c.name
           WHEN 'RoomID' THEN 0
           WHEN 'room_id' THEN 1
           WHEN 'ID' THEN 2
           ELSE 3
         END;", cn, tx))
        {
            columnName = Convert.ToString(await find.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(columnName))
        {
            // columnName comes only from the fixed whitelist above.
            await using var lookup = new SqlCommand($@"
SELECT TOP (1) CONVERT(varchar(100),[{columnName}])
FROM dbo.RoomsTB
WHERE Hotel_id=@hotel AND room_no=@room;", cn, tx);
            lookup.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
            lookup.Parameters.Add("@room", SqlDbType.VarChar, 50).Value = roomNo.Trim();
            var value = Convert.ToString(await lookup.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture)?.Trim();
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }

        // Older schemas store the room number itself in RoomBlocksTB.RoomID.
        return roomNo.Trim();
    }

    private static void AddBlockParams(SqlCommand cmd, string hotelId, string userId, string userName,
        FrontDeskRoomBlockRequest request, DateTime start, DateTime end)
    {
        cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
        cmd.Parameters.Add("@room", SqlDbType.VarChar, 50).Value = request.RoomNo.Trim();
        cmd.Parameters.Add("@start", SqlDbType.VarChar, 20).Value = LegacyDate(start);
        cmd.Parameters.Add("@end", SqlDbType.VarChar, 20).Value = LegacyDate(end);
        cmd.Parameters.Add("@reason", SqlDbType.VarChar, 500).Value = request.Reason.Trim();
        cmd.Parameters.Add("@username", SqlDbType.VarChar, 150).Value = userName ?? string.Empty;
        cmd.Parameters.Add("@userid", SqlDbType.VarChar, 100).Value = userId ?? string.Empty;
    }

    private async Task<string> FindBlockConflictAsync(SqlConnection cn, SqlTransaction tx, string hotelId, string roomNo,
        DateTime start, DateTime endInclusive, int? excludeBlockId, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(@"
SELECT TOP (1) BlockID,
 COALESCE(TRY_CONVERT(date,BlockStartDate,110),TRY_CONVERT(date,BlockStartDate,23),TRY_CONVERT(date,BlockStartDate)) S,
 COALESCE(TRY_CONVERT(date,BlockEndDate,110),TRY_CONVERT(date,BlockEndDate,23),TRY_CONVERT(date,BlockEndDate)) E,
 ISNULL(Reason,'') Reason
FROM dbo.RoomBlocksTB WITH (UPDLOCK,HOLDLOCK)
WHERE HotelID=@hotel AND RoomNo=@room AND ISNULL(IsActive,0)=1
 AND (@exclude<=0 OR BlockID<>@exclude)
 AND COALESCE(TRY_CONVERT(date,BlockStartDate,110),TRY_CONVERT(date,BlockStartDate,23),TRY_CONVERT(date,BlockStartDate)) < DATEADD(day,1,@newEnd)
 AND @newStart < DATEADD(day,1,COALESCE(TRY_CONVERT(date,BlockEndDate,110),TRY_CONVERT(date,BlockEndDate,23),TRY_CONVERT(date,BlockEndDate)));", cn, tx);
        cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
        cmd.Parameters.Add("@room", SqlDbType.VarChar, 50).Value = roomNo;
        cmd.Parameters.Add("@exclude", SqlDbType.Int).Value = excludeBlockId ?? 0;
        cmd.Parameters.Add("@newStart", SqlDbType.Date).Value = start;
        cmd.Parameters.Add("@newEnd", SqlDbType.Date).Value = endInclusive;
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct)) return string.Empty;
        var blockStart = D(rd, "S");
        var blockEnd = D(rd, "E");
        return $"BlockID {I(rd, "BlockID")}, {blockStart?.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) ?? "?"} - {blockEnd?.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) ?? "?"}, {S(rd, "Reason")}";
    }

    private async Task<string> FindBookingConflictAsync(SqlConnection cn, SqlTransaction tx, string hotelId, string roomNo,
        DateTime arrival, DateTime departureExclusive, int excludePaymentId, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(@"
SELECT TOP (1) ID,reg_id,
 COALESCE(TRY_CONVERT(date,ArrivalDate,110),TRY_CONVERT(date,ArrivalDate,23),TRY_CONVERT(date,ArrivalDate,103),TRY_CONVERT(date,ArrivalDate)) A,
 COALESCE(TRY_CONVERT(date,DepartureDate,110),TRY_CONVERT(date,DepartureDate,23),TRY_CONVERT(date,DepartureDate,103),TRY_CONVERT(date,DepartureDate)) D
FROM dbo.payments WITH (UPDLOCK,HOLDLOCK)
WHERE hotel_id=@hotel AND LTRIM(RTRIM(ISNULL(room_no,'')))=LTRIM(RTRIM(@room))
 AND LTRIM(RTRIM(ISNULL(descr,'')))='Room Rent'
 AND LOWER(LTRIM(RTRIM(ISNULL(res_status,'')))) IN ('reservation','check in','provisional','tentative')
 AND (@exclude<=0 OR ID<>@exclude)
 AND COALESCE(TRY_CONVERT(date,ArrivalDate,110),TRY_CONVERT(date,ArrivalDate,23),TRY_CONVERT(date,ArrivalDate,103),TRY_CONVERT(date,ArrivalDate)) < @departure
 AND @arrival < COALESCE(TRY_CONVERT(date,DepartureDate,110),TRY_CONVERT(date,DepartureDate,23),TRY_CONVERT(date,DepartureDate,103),TRY_CONVERT(date,DepartureDate))
ORDER BY ID;", cn, tx);
        cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
        cmd.Parameters.Add("@room", SqlDbType.VarChar, 50).Value = roomNo;
        cmd.Parameters.Add("@exclude", SqlDbType.Int).Value = excludePaymentId;
        cmd.Parameters.Add("@arrival", SqlDbType.Date).Value = arrival;
        cmd.Parameters.Add("@departure", SqlDbType.Date).Value = departureExclusive;
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct)) return string.Empty;
        return $"Reg {S(rd, "reg_id")}, {D(rd, "A"):dd/MM/yyyy} - {D(rd, "D"):dd/MM/yyyy}";
    }

    private async Task<bool> IsRoomFreeForReservationAsync(
        SqlConnection cn, SqlTransaction tx, string hotelId, string roomNo,
        DateTime arrival, DateTime departure, string excludeRegId, CancellationToken ct)
    {
        await using (var booking = new SqlCommand(@"
SELECT TOP (1) ID
FROM dbo.payments WITH (UPDLOCK,HOLDLOCK)
WHERE hotel_id=@hotel
  AND LTRIM(RTRIM(ISNULL(room_no,'')))=LTRIM(RTRIM(@room))
  AND LTRIM(RTRIM(ISNULL(descr,'')))='Room Rent'
  AND LOWER(LTRIM(RTRIM(ISNULL(res_status,'')))) IN ('reservation','check in','provisional','tentative')
  AND LTRIM(RTRIM(ISNULL(reg_id,'')))<>LTRIM(RTRIM(@excludeReg))
  AND COALESCE(TRY_CONVERT(date,ArrivalDate,110),TRY_CONVERT(date,ArrivalDate,23),TRY_CONVERT(date,ArrivalDate,103),TRY_CONVERT(date,ArrivalDate)) < @departure
  AND @arrival < COALESCE(TRY_CONVERT(date,DepartureDate,110),TRY_CONVERT(date,DepartureDate,23),TRY_CONVERT(date,DepartureDate,103),TRY_CONVERT(date,DepartureDate));", cn, tx))
        {
            booking.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
            booking.Parameters.Add("@room", SqlDbType.VarChar, 50).Value = roomNo;
            booking.Parameters.Add("@excludeReg", SqlDbType.VarChar, 100).Value = NormalizeRegId(excludeRegId);
            booking.Parameters.Add("@arrival", SqlDbType.Date).Value = arrival.Date;
            booking.Parameters.Add("@departure", SqlDbType.Date).Value = departure.Date;
            if (await booking.ExecuteScalarAsync(ct) != null) return false;
        }

        var block = await FindBlockConflictAsync(cn, tx, hotelId, roomNo, arrival, departure.AddDays(-1), null, ct);
        if (block.Length > 0) return false;

        await using var room = new SqlCommand(@"
SELECT TOP (1) ISNULL(room_status,'')
FROM dbo.RoomsTB WITH (UPDLOCK,ROWLOCK)
WHERE Hotel_id=@hotel AND room_no=@room;", cn, tx);
        room.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
        room.Parameters.Add("@room", SqlDbType.VarChar, 50).Value = roomNo;
        var status = Convert.ToString(await room.ExecuteScalarAsync(ct)) ?? string.Empty;
        return !status.Equals("Blocked", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<bool> IsRoomFreeAsync(SqlConnection cn, SqlTransaction tx, string hotelId, string roomNo,
        DateTime arrival, DateTime departure, int excludePaymentId, CancellationToken ct)
    {
        var booking = await FindBookingConflictAsync(cn, tx, hotelId, roomNo, arrival, departure, excludePaymentId, ct);
        if (booking.Length > 0) return false;
        var block = await FindBlockConflictAsync(cn, tx, hotelId, roomNo, arrival, departure.AddDays(-1), null, ct);
        if (block.Length > 0) return false;
        await using var cmd = new SqlCommand(@"
SELECT TOP (1) ISNULL(room_status,'') FROM dbo.RoomsTB WITH (UPDLOCK,HOLDLOCK)
WHERE Hotel_id=@hotel AND room_no=@room;", cn, tx);
        cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
        cmd.Parameters.Add("@room", SqlDbType.VarChar, 50).Value = roomNo;
        var status = Convert.ToString(await cmd.ExecuteScalarAsync(ct)) ?? string.Empty;
        return !status.Equals("Blocked", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<BookingRow?> GetBookingRowForUpdateAsync(SqlConnection cn, SqlTransaction? tx, string hotelId, string regId, int paymentId, CancellationToken ct)
    {
        // READPAST is safe for read-only loading, but not when the move/resize transaction
        // locks the selected payment row.  Using READPAST together with HOLDLOCK/UPDLOCK can
        // trigger SQL Server 4132 on databases that use READ_COMMITTED_SNAPSHOT.
        var paymentHint = tx == null ? "READPAST" : "UPDLOCK,ROWLOCK";
        var categoryHint = tx == null ? "WITH (READPAST)" : string.Empty;
        await using var cmd = new SqlCommand($@"
SELECT TOP (1) p.ID,p.reg_id,ISNULL(p.room_no,'') room_no,ISNULL(p.[Type],'') room_category,
 ISNULL(CONVERT(varchar(100),p.category_id),ISNULL(CONVERT(varchar(100),cr.localcategoryid),'')) category_id,
 COALESCE(TRY_CONVERT(date,p.ArrivalDate,110),TRY_CONVERT(date,p.ArrivalDate,23),TRY_CONVERT(date,p.ArrivalDate,103),TRY_CONVERT(date,p.ArrivalDate)) A,
 COALESCE(TRY_CONVERT(date,p.DepartureDate,110),TRY_CONVERT(date,p.DepartureDate,23),TRY_CONVERT(date,p.DepartureDate,103),TRY_CONVERT(date,p.DepartureDate)) D,
 ISNULL(p.res_status,'') res_status,
 ISNULL(TRY_CONVERT(decimal(18,2),p.rate),0) rate,
 ISNULL(TRY_CONVERT(decimal(18,2),p.GST),0) gst,
 ISNULL(TRY_CONVERT(decimal(18,2),p.Bed),0) bed,
 ISNULL(TRY_CONVERT(decimal(18,2),p.totalamount),0) total
FROM dbo.payments p WITH ({paymentHint})
LEFT JOIN dbo.create_room cr {categoryHint} ON cr.hotel_id=p.hotel_id AND cr.description=p.[Type]
WHERE p.hotel_id=@hotel AND p.reg_id=@reg AND p.ID=@id AND LTRIM(RTRIM(ISNULL(p.descr,'')))='Room Rent';", cn, tx);
        cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
        cmd.Parameters.Add("@reg", SqlDbType.VarChar, 100).Value = NormalizeRegId(regId);
        cmd.Parameters.Add("@id", SqlDbType.Int).Value = paymentId;
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct)) return null;
        var a = D(rd, "A"); var d = D(rd, "D");
        if (!a.HasValue || !d.HasValue) return null;
        return new BookingRow
        {
            PaymentId = I(rd, "ID"), RegId = S(rd, "reg_id"), RoomNo = S(rd, "room_no"),
            CategoryName = S(rd, "room_category"), CategoryId = S(rd, "category_id"),
            Arrival = a.Value, Departure = d.Value, Status = S(rd, "res_status"),
            Rate = M(rd, "rate"), Gst = M(rd, "gst"), Bed = M(rd, "bed"), Total = M(rd, "total")
        };
    }

    private async Task<(string CategoryId, string CategoryName)?> GetRoomInfoAsync(SqlConnection cn, SqlTransaction tx, string hotelId, string roomNo, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(@"
SELECT TOP (1) ISNULL(CONVERT(varchar(100),cr.localcategoryid),'') category_id,ISNULL(r.room_category,'') category_name
FROM dbo.RoomsTB r WITH (UPDLOCK,ROWLOCK)
LEFT JOIN dbo.create_room cr ON cr.hotel_id=r.Hotel_id AND cr.description=r.room_category
WHERE r.Hotel_id=@hotel AND r.room_no=@room;", cn, tx);
        cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
        cmd.Parameters.Add("@room", SqlDbType.VarChar, 50).Value = roomNo;
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct)) return null;
        return (S(rd, "category_id"), S(rd, "category_name"));
    }

    private async Task<decimal> GetCategoryRateAsync(SqlConnection cn, SqlTransaction tx, string hotelId, string categoryId, string categoryName, DateTime date, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(@"
SELECT TOP (1)
 COALESCE(TRY_CONVERT(decimal(18,2),dr.rate),TRY_CONVERT(decimal(18,2),cp.rate),TRY_CONVERT(decimal(18,2),cr.rate),0)
FROM dbo.create_room cr
LEFT JOIN dbo.category_plan cp ON cp.hotel_id=cr.hotel_id AND cp.category_id=cr.localcategoryid
LEFT JOIN dbo.datesrates dr ON dr.hotel_id=cr.hotel_id AND dr.category_id=cr.localcategoryid
  AND dr.planid=cp.plan_id AND COALESCE(TRY_CONVERT(date,dr.date,110),TRY_CONVERT(date,dr.date,23),TRY_CONVERT(date,dr.date))=@date
WHERE cr.hotel_id=@hotel AND LTRIM(RTRIM(ISNULL(cr.category,'')))='Room Rent'
 AND ((@cid<>'' AND CONVERT(varchar(100),cr.localcategoryid)=@cid) OR cr.description=@name)
ORDER BY CASE WHEN dr.rate IS NOT NULL THEN 0 WHEN cp.rate IS NOT NULL THEN 1 ELSE 2 END;", cn, tx);
        cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
        cmd.Parameters.Add("@cid", SqlDbType.VarChar, 100).Value = categoryId ?? string.Empty;
        cmd.Parameters.Add("@name", SqlDbType.VarChar, 150).Value = categoryName ?? string.Empty;
        cmd.Parameters.Add("@date", SqlDbType.Date).Value = date.Date;
        try
        {
            var value = await cmd.ExecuteScalarAsync(ct);
            return value == null || value == DBNull.Value ? 0m : Convert.ToDecimal(value, CultureInfo.InvariantCulture);
        }
        catch (SqlException)
        {
            // Some legacy databases do not have every optional rate column/table shape.
            await using var fallback = new SqlCommand(@"
SELECT TOP (1) ISNULL(TRY_CONVERT(decimal(18,2),rate),0)
FROM dbo.create_room WHERE hotel_id=@hotel AND ((@cid<>'' AND CONVERT(varchar(100),localcategoryid)=@cid) OR description=@name);", cn, tx);
            fallback.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
            fallback.Parameters.Add("@cid", SqlDbType.VarChar, 100).Value = categoryId ?? string.Empty;
            fallback.Parameters.Add("@name", SqlDbType.VarChar, 150).Value = categoryName ?? string.Empty;
            var value = await fallback.ExecuteScalarAsync(ct);
            return value == null || value == DBNull.Value ? 0m : Convert.ToDecimal(value, CultureInfo.InvariantCulture);
        }
    }

    private async Task<int> CountActiveRoomRowsAsync(SqlConnection cn, SqlTransaction? tx, string hotelId, string regId, CancellationToken ct)
    {
        var hint = tx == null ? " WITH (READPAST)" : string.Empty;
        await using var cmd = new SqlCommand($@"
SELECT COUNT(1) FROM dbo.payments{hint}
WHERE hotel_id=@hotel AND reg_id=@reg AND LTRIM(RTRIM(ISNULL(descr,'')))='Room Rent'
 AND LOWER(LTRIM(RTRIM(ISNULL(res_status,'')))) IN ('reservation','check in','provisional','tentative');", cn, tx);
        cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
        cmd.Parameters.Add("@reg", SqlDbType.VarChar, 100).Value = NormalizeRegId(regId);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    private async Task UpdateMasterDatesWhenSingleRoomAsync(SqlConnection cn, SqlTransaction tx, string hotelId, string regId, DateTime arrival, DateTime departure, CancellationToken ct)
    {
        if (await CountActiveRoomRowsAsync(cn, tx, hotelId, regId, ct) != 1) return;
        await using var cmd = new SqlCommand(@"
UPDATE dbo.GuestInformationLogTB SET ArrivalDate=@a,DepartureDate=@d WHERE hotel_id=@hotel AND reg_id=@reg;
UPDATE dbo.NewReservationsTB SET ArrivalDate=@a,dept_date=@d WHERE hotel_id=@hotel AND reg_id=@reg;", cn, tx);
        cmd.Parameters.Add("@a", SqlDbType.VarChar, 20).Value = LegacyDate(arrival);
        cmd.Parameters.Add("@d", SqlDbType.VarChar, 20).Value = LegacyDate(departure);
        cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
        cmd.Parameters.Add("@reg", SqlDbType.VarChar, 100).Value = NormalizeRegId(regId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<decimal> GetHistoricalTaxPerNightAsync(
        SqlConnection cn, SqlTransaction tx, string hotelId, string regId,
        string roomNo, string roomType, string planId, string taxColumn, CancellationToken ct)
    {
        // taxColumn is controlled by this service; never pass arbitrary user input here.
        taxColumn = taxColumn.Equals("Bed", StringComparison.OrdinalIgnoreCase) ? "Bed" : "GST";
        await using var cmd = new SqlCommand($@"
SELECT TOP (1)
    TRY_CONVERT(decimal(18,4),ISNULL({taxColumn},0)) /
    NULLIF(TRY_CONVERT(decimal(18,4),NULLIF(Nights,'')),0)
FROM dbo.payments
WHERE hotel_id=@hotel
  AND reg_id=@reg
  AND LTRIM(RTRIM(ISNULL(descr,'')))='Room Rent'
  AND LTRIM(RTRIM(ISNULL(room_no,'')))=@room
  AND LTRIM(RTRIM(ISNULL([Type],'')))=@type
  AND LTRIM(RTRIM(ISNULL(rateplan,'')))=@plan
  AND TRY_CONVERT(decimal(18,4),ISNULL({taxColumn},0))>0
  AND TRY_CONVERT(decimal(18,4),NULLIF(Nights,''))>0
ORDER BY
    COALESCE(
        TRY_CONVERT(date,ArrivalDate,110),
        TRY_CONVERT(date,ArrivalDate,23),
        TRY_CONVERT(date,ArrivalDate,101),
        TRY_CONVERT(date,ArrivalDate,103),
        TRY_CONVERT(date,ArrivalDate)
    ) ASC,
    ID ASC;", cn, tx);
        cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
        cmd.Parameters.Add("@reg", SqlDbType.VarChar, 100).Value = NormalizeRegId(regId);
        cmd.Parameters.Add("@room", SqlDbType.VarChar, 50).Value = roomNo ?? string.Empty;
        cmd.Parameters.Add("@type", SqlDbType.VarChar, 150).Value = roomType ?? string.Empty;
        cmd.Parameters.Add("@plan", SqlDbType.VarChar, 100).Value = planId ?? string.Empty;
        var raw = await cmd.ExecuteScalarAsync(ct);
        if (raw == null || raw == DBNull.Value) return 0m;
        return decimal.TryParse(Convert.ToString(raw, CultureInfo.InvariantCulture),
            NumberStyles.Any, CultureInfo.InvariantCulture, out var value)
            ? Math.Round(value, 4, MidpointRounding.AwayFromZero)
            : 0m;
    }

    private async Task SyncMasterDatesFromPaymentsAsync(
        SqlConnection cn, SqlTransaction tx, string hotelId, string regId, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(@"
;WITH x AS
(
    SELECT
        MIN(COALESCE(TRY_CONVERT(date,ArrivalDate,110),TRY_CONVERT(date,ArrivalDate,23),TRY_CONVERT(date,ArrivalDate,103),TRY_CONVERT(date,ArrivalDate))) MinArr,
        MAX(COALESCE(TRY_CONVERT(date,DepartureDate,110),TRY_CONVERT(date,DepartureDate,23),TRY_CONVERT(date,DepartureDate,103),TRY_CONVERT(date,DepartureDate))) MaxDep
    FROM dbo.payments
    WHERE hotel_id=@hotel AND reg_id=@reg AND LTRIM(RTRIM(ISNULL(descr,'')))='Room Rent'
      AND LOWER(LTRIM(RTRIM(ISNULL(res_status,'')))) IN ('reservation','check in','provisional','tentative')
)
UPDATE g
SET ArrivalDate=CONVERT(varchar(10),x.MinArr,110),
    DepartureDate=CONVERT(varchar(10),x.MaxDep,110),
    totalnights=CONVERT(varchar(20),DATEDIFF(day,x.MinArr,x.MaxDep))
FROM dbo.GuestInformationLogTB g CROSS JOIN x
WHERE g.hotel_id=@hotel AND g.reg_id=@reg AND x.MinArr IS NOT NULL AND x.MaxDep IS NOT NULL;

;WITH x AS
(
    SELECT
        MIN(COALESCE(TRY_CONVERT(date,ArrivalDate,110),TRY_CONVERT(date,ArrivalDate,23),TRY_CONVERT(date,ArrivalDate,103),TRY_CONVERT(date,ArrivalDate))) MinArr,
        MAX(COALESCE(TRY_CONVERT(date,DepartureDate,110),TRY_CONVERT(date,DepartureDate,23),TRY_CONVERT(date,DepartureDate,103),TRY_CONVERT(date,DepartureDate))) MaxDep
    FROM dbo.payments
    WHERE hotel_id=@hotel AND reg_id=@reg AND LTRIM(RTRIM(ISNULL(descr,'')))='Room Rent'
      AND LOWER(LTRIM(RTRIM(ISNULL(res_status,'')))) IN ('reservation','check in','provisional','tentative')
)
UPDATE n
SET ArrivalDate=CONVERT(varchar(10),x.MinArr,110),
    dept_date=CONVERT(varchar(10),x.MaxDep,110),
    totalnights=CONVERT(varchar(20),DATEDIFF(day,x.MinArr,x.MaxDep))
FROM dbo.NewReservationsTB n CROSS JOIN x
WHERE n.hotel_id=@hotel AND n.reg_id=@reg AND x.MinArr IS NOT NULL AND x.MaxDep IS NOT NULL;", cn, tx);
        cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
        cmd.Parameters.Add("@reg", SqlDbType.VarChar, 100).Value = NormalizeRegId(regId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task RefreshPaymentSummaryAsync(
        SqlConnection cn, SqlTransaction tx, string hotelId, string regId, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(@"
DECLARE @grand decimal(18,2) = ISNULL((
    SELECT SUM(ISNULL(TRY_CONVERT(decimal(18,2),totalamount),0))
    FROM dbo.payments WHERE hotel_id=@hotel AND reg_id=@reg
),0);
DECLARE @paid decimal(18,2) = ISNULL((
    SELECT SUM(ISNULL(TRY_CONVERT(decimal(18,2),paid_amount),0))
    FROM dbo.PaymentsLogTB WHERE hotel_id=@hotel AND reg_id=@reg
),0);
UPDATE dbo.PaymentsUpdateTB
SET grand_total=@grand,
    payable=@grand,
    paid_amount=@paid,
    remaining_amount=@grand-@paid
WHERE hotel_id=@hotel AND reg_id=@reg;", cn, tx);
        cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
        cmd.Parameters.Add("@reg", SqlDbType.VarChar, 100).Value = NormalizeRegId(regId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task InsertRoomChangeLogAsync(SqlConnection cn, SqlTransaction tx, string hotelId, string regId,
        string oldCatId, string newCatId, string oldCat, string newCat, string oldRoom, string newRoom,
        string userId, string userName, string ip, string remarks, CancellationToken ct)
    {
        try
        {
            var now = _hotelClock.GetHotelNow(hotelId);
            var actionType =
                !string.Equals(oldRoom ?? string.Empty, newRoom ?? string.Empty, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(oldCat ?? string.Empty, newCat ?? string.Empty, StringComparison.OrdinalIgnoreCase)
                    ? "CHANGE"
                    : "CHANGE";

            // Match the legacy WebForms RoomChangeLogTB column names exactly.
            await using var cmd = new SqlCommand(@"
IF OBJECT_ID('dbo.RoomChangeLogTB','U') IS NOT NULL
BEGIN
    INSERT INTO dbo.RoomChangeLogTB
    (
        ActionType, LogDate, LogTime,
        HotelID, RegID,
        OldCategoryId, NewCategoryId,
        OldCategoryName, NewCategoryName,
        OldRoomNo, NewRoomNo,
        UserId, username, SystemName, IPAddress,
        Remarks
    )
    VALUES
    (
        @ActionType, @LogDate, @LogTime,
        @HotelID, @RegID,
        @OldCategoryId, @NewCategoryId,
        @OldCategoryName, @NewCategoryName,
        @OldRoomNo, @NewRoomNo,
        @UserId, @UserName, @SystemName, @IPAddress,
        @Remarks
    );
END;", cn, tx);

            cmd.Parameters.Add("@ActionType", SqlDbType.VarChar, 50).Value = actionType;
            cmd.Parameters.Add("@LogDate", SqlDbType.VarChar, 20).Value = now.ToString("MM-dd-yyyy", CultureInfo.InvariantCulture);
            cmd.Parameters.Add("@LogTime", SqlDbType.Time).Value = now.TimeOfDay;
            cmd.Parameters.Add("@HotelID", SqlDbType.VarChar, 100).Value = hotelId ?? string.Empty;
            cmd.Parameters.Add("@RegID", SqlDbType.VarChar, 100).Value = NormalizeRegId(regId);
            cmd.Parameters.Add("@OldCategoryId", SqlDbType.VarChar, 100).Value = oldCatId ?? string.Empty;
            cmd.Parameters.Add("@NewCategoryId", SqlDbType.VarChar, 100).Value = newCatId ?? string.Empty;
            cmd.Parameters.Add("@OldCategoryName", SqlDbType.VarChar, 150).Value = oldCat ?? string.Empty;
            cmd.Parameters.Add("@NewCategoryName", SqlDbType.VarChar, 150).Value = newCat ?? string.Empty;
            cmd.Parameters.Add("@OldRoomNo", SqlDbType.VarChar, 50).Value = oldRoom ?? string.Empty;
            cmd.Parameters.Add("@NewRoomNo", SqlDbType.VarChar, 50).Value = newRoom ?? string.Empty;
            cmd.Parameters.Add("@UserId", SqlDbType.VarChar, 100).Value = userId ?? string.Empty;
            cmd.Parameters.Add("@UserName", SqlDbType.VarChar, 150).Value = userName ?? string.Empty;
            cmd.Parameters.Add("@SystemName", SqlDbType.VarChar, 150).Value = Environment.MachineName ?? string.Empty;
            cmd.Parameters.Add("@IPAddress", SqlDbType.VarChar, 100).Value = ip ?? string.Empty;
            cmd.Parameters.Add("@Remarks", SqlDbType.VarChar, -1).Value = remarks ?? string.Empty;
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (SqlException ex)
        {
            _logger.Debug(ex, "RoomChangeLogTB insert skipped due to legacy schema mismatch.");
        }
    }


    private async Task InsertLogAsync(SqlConnection cn, SqlTransaction? tx, string hotelId, string userId, string userName, string ip,
        string regId, string action, string detail, CancellationToken ct)
    {
        // Keep the MVC-specific log when its table/schema is available.
        // It is isolated from the legacy-compatible logging below so a schema mismatch
        // in one logging table never prevents the other logs from being written.
        try
        {
            await using var cmd = new SqlCommand(@"
IF OBJECT_ID('dbo.ReservationActionLogTB','U') IS NOT NULL AND @reg<>''
INSERT INTO dbo.ReservationActionLogTB
(hotel_id,reg_id,action_type,description,currentdate,user_id,systemUser,systemName,ipAddress)
VALUES(@hotel,@reg,@action,@detail,@now,@user,@username,@system,@ip);", cn, tx);
            var now = _hotelClock.GetHotelNow(hotelId);
            cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId ?? string.Empty;
            cmd.Parameters.Add("@reg", SqlDbType.VarChar, 100).Value = NormalizeRegId(regId);
            cmd.Parameters.Add("@action", SqlDbType.VarChar, 100).Value = action ?? string.Empty;
            cmd.Parameters.Add("@detail", SqlDbType.VarChar, -1).Value = detail ?? string.Empty;
            cmd.Parameters.Add("@now", SqlDbType.DateTime).Value = now;
            cmd.Parameters.Add("@user", SqlDbType.VarChar, 100).Value = userId ?? string.Empty;
            cmd.Parameters.Add("@username", SqlDbType.VarChar, 150).Value = userName ?? string.Empty;
            cmd.Parameters.Add("@system", SqlDbType.VarChar, 150).Value = Environment.MachineName ?? string.Empty;
            cmd.Parameters.Add("@ip", SqlDbType.VarChar, 100).Value = ip ?? string.Empty;
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "ReservationActionLogTB insert skipped.");
        }

        // Match the existing WebForms Log_helper behavior:
        // ActionLogTB for every calendar action + Reserv_log for reservation-specific actions.
        await _dbLogger.LogActionTransactionalAsync(
            cn, tx,
            "FDO Calander",
            action ?? string.Empty,
            hotelId,
            userId,
            detail ?? string.Empty,
            ip,
            ct);

        if (!string.IsNullOrWhiteSpace(regId))
        {
            await _dbLogger.InsertReservationLogTransactionalAsync(
                cn, tx,
                NormalizeRegId(regId),
                detail ?? string.Empty,
                userId,
                userName,
                hotelId,
                action ?? string.Empty,
                ip,
                ct);
        }
    }


    private void QueueAvailability(string hotelId, string hotelName, string userId, string userName, string ip,
        DateTime start, DateTime end, string categoryId)
    {
        if (string.IsNullOrWhiteSpace(categoryId)) return;
        if (end < start) (start, end) = (end, start);
        _availabilityQueue.Queue(new AvailabilityAutoUpdateJob(
            hotelId, hotelName, userId, userName, ip, start.Date, end.Date, categoryId.Trim()));
    }

    private async Task<FrontDeskCalendarPermissions> GetPermissionsAsync(string hotelId, string userId, string role, CancellationToken ct)
    {
        var cacheKey = $"fdc:perm:{hotelId}:{userId}:{role}";
        if (_cache.TryGetValue(cacheKey, out FrontDeskCalendarPermissions? cached) && cached != null) return cached;

        var permissions = new FrontDeskCalendarPermissions();
        if (role.Equals("hotel", StringComparison.OrdinalIgnoreCase) || role.Equals("manager", StringComparison.OrdinalIgnoreCase))
        {
            permissions.CanDeleteReservation = true;
            permissions.CanDeleteAfterCheckIn = true;
            permissions.CanDeleteAfterCheckOut = true;
            permissions.CanRefund = true;
            _cache.Set(cacheKey, permissions, TimeSpan.FromMinutes(2));
            return permissions;
        }

        try
        {
            await using var cn = new SqlConnection(_connectionString);
            await cn.OpenAsync(ct);
            await using var cmd = new SqlCommand(@"
DECLARE @menuid int=(SELECT TOP (1) menuid FROM dbo.AddMenuTB WITH (READPAST)
 WHERE REPLACE(REPLACE(LOWER(ISNULL(pagename,'')),'.aspx',''),' ','') IN ('frontdeskcalender','frontdeskcalendar','calendar')
 ORDER BY menuid);
SELECT pa.action_name,
       CASE WHEN NOT EXISTS(SELECT 1 FROM dbo.UserActionPermissionsTB x WITH (READPAST) WHERE x.menuid=@menuid AND x.action_id=pa.action_id AND ISNULL(x.is_active,0)=1)
            THEN 1
            ELSE ISNULL(MAX(CASE WHEN ISNULL(u.is_allowed,0)=1 THEN 1 ELSE 0 END),0) END AS allowed
FROM dbo.PageActionsTB pa WITH (READPAST)
LEFT JOIN dbo.UserActionPermissionsTB u WITH (READPAST)
  ON u.menuid=pa.menuid AND u.action_id=pa.action_id AND ISNULL(u.is_active,0)=1
 AND (CONVERT(varchar(100),u.user_id)=@user OR (CONVERT(varchar(100),u.hotel_id)=@hotel AND LOWER(LTRIM(RTRIM(ISNULL(u.permission_for,''))))='hotel'))
WHERE pa.menuid=@menuid AND ISNULL(pa.is_active,1)=1
GROUP BY pa.action_id,pa.action_name;", cn);
            cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
            cmd.Parameters.Add("@user", SqlDbType.VarChar, 100).Value = userId;
            var map = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) map[S(rd, "action_name")] = I(rd, "allowed") == 1;
            bool Has(string name, bool fallback = true) => !map.TryGetValue(name, out var value) ? fallback : value;
            bool HasAny(bool fallback, params string[] aliases)
            {
                var configured = aliases.Where(x => map.ContainsKey(x)).ToArray();
                return configured.Length == 0 ? fallback : configured.Any(x => map[x]);
            }
            permissions.CanViewDetails = Has("ReservationViewDetails");
            permissions.CanDragDrop = Has("ReservationDragDrop");
            permissions.CanExtendShrink = Has("ReservationExtendShrink");
            permissions.CanBlockRoom = Has("BlockRoom");
            permissions.CanMarkRoomClean = Has("AllowDirty") || Has("RoomClean");
            permissions.CanChangeRoom = Has("ChangeRoom") || Has("ChangeReservationRoom");
            // Match the legacy action aliases used by the WebForms calendar.
            permissions.CanOnlineCardPayment = HasAny(true, "vcPayment", "ChargeVC", "VirtualCard", "VC_PAYMENT", "CardPayment", "card_payment");
            permissions.CanPdqPayment = HasAny(true, "pdqPayment", "PDQ_PAYMENT", "PDQPayment", "pdq_payment");
            permissions.CanRefund = Has("Refund", true);
            permissions.CanDeleteReservation = Has("DeleteReservation", false);
            permissions.CanDeleteAfterCheckIn = Has("DeleteAfterCheckIn", false);
            permissions.CanDeleteAfterCheckOut = Has("DeleteAfterCheckOut", false);
        }
        catch (Exception ex)
        {
            // Preserve calendar usability if permissions were not configured in an older property database.
            _logger.Debug(ex, "Calendar permissions could not be loaded; non-destructive actions stay enabled.");
        }

        _cache.Set(cacheKey, permissions, TimeSpan.FromMinutes(2));
        return permissions;
    }

    private sealed class PaymentCapabilities
    {
        public bool StripeConfigured { get; set; }
        public bool CloverConfigured { get; set; }
    }

    private async Task<PaymentCapabilities> GetPaymentCapabilitiesAsync(string hotelId, CancellationToken ct)
    {
        var cacheKey = $"fdc:payment-capabilities:{hotelId}";
        if (_cache.TryGetValue(cacheKey, out PaymentCapabilities? cached) && cached != null)
            return cached;

        var result = new PaymentCapabilities();
        await using var cn = new SqlConnection(_connectionString);
        await cn.OpenAsync(ct);

        // Use the same property configuration checks already used by Check-In.
        // Older property databases may not have one of these tables, so each
        // provider is isolated and simply remains unavailable when not configured.
        try
        {
            await using var stripe = new SqlCommand(
                "SELECT COUNT(*) FROM dbo.HotelStripeAccounts WITH (READPAST) WHERE HotelId=@hotel AND ISNULL(AccessToken,'')<>'';", cn);
            stripe.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
            result.StripeConfigured = Convert.ToInt32(await stripe.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture) > 0;
        }
        catch (SqlException ex)
        {
            _logger.Debug(ex, "Stripe availability could not be resolved for Front Desk Calendar.");
        }

        try
        {
            await using var clover = new SqlCommand(
                "SELECT COUNT(*) FROM dbo.clovertb WITH (READPAST) WHERE hotel_id=@hotel AND ISNULL(access_token,'')<>'';", cn);
            clover.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
            result.CloverConfigured = Convert.ToInt32(await clover.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture) > 0;
        }
        catch (SqlException ex)
        {
            _logger.Debug(ex, "PDQ/Clover availability could not be resolved for Front Desk Calendar.");
        }

        _cache.Set(cacheKey, result, TimeSpan.FromMinutes(2));
        return result;
    }

    private async Task<string> GetCurrencySymbolAsync(string hotelId, CancellationToken ct)
    {
        var key = $"fdc:currency:{hotelId}";
        if (_cache.TryGetValue(key, out string? cached) && !string.IsNullOrWhiteSpace(cached)) return cached;
        try
        {
            await using var cn = new SqlConnection(_connectionString);
            await cn.OpenAsync(ct);
            await using var cmd = new SqlCommand("SELECT TOP (1) ISNULL(NULLIF(currency_sign,''),'£') FROM dbo.HotelsSignUpTB WITH (READPAST) WHERE hotel_id=@hotel;", cn);
            cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
            var symbol = Convert.ToString(await cmd.ExecuteScalarAsync(ct))?.Trim();
            if (string.IsNullOrWhiteSpace(symbol)) symbol = "£";
            _cache.Set(key, symbol, TimeSpan.FromMinutes(10));
            return symbol;
        }
        catch { return "£"; }
    }

    private static DateTime? ParseLegacyDateValue(object? value)
    {
        if (value == null || value == DBNull.Value) return null;
        if (value is DateTime dt) return dt.Date;
        var text = Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim();
        if (string.IsNullOrWhiteSpace(text)) return null;
        return TryParseLegacyDate(text, out var parsed) ? parsed.Date : null;
    }

    private static string B64UrlEncode(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
        return Convert.ToBase64String(bytes).Replace('+','-').Replace('/','_').TrimEnd('=');
    }

    private static string NormalizeCurrencyCode(string? value)
    {
        var v = (value ?? string.Empty).Trim().ToUpperInvariant();
        return v switch
        {
            "£" or "GBP" => "GBP",
            "$" or "USD" => "USD",
            "€" or "EUR" => "EUR",
            "PKR" or "RS" or "RS." or "₨" => "PKR",
            _ when v.Length == 3 => v,
            _ => "GBP"
        };
    }

    private static string DecryptEmailPassword(string encrypted)
    {
        if (string.IsNullOrWhiteSpace(encrypted)) return string.Empty;
        var entropy = Encoding.UTF8.GetBytes("ORA_PMS_EMAIL_SETTINGS_V1");
        var bytes = Convert.FromBase64String(encrypted);
        return Encoding.UTF8.GetString(
            ProtectedData.Unprotect(bytes, entropy, DataProtectionScope.LocalMachine));
    }

    private static string LegacyStatusCode(string status)
    {
        var s = (status ?? string.Empty).Trim().ToLowerInvariant();
        if (s is "check out" or "checked out" or "checkout") return "CO";
        if (s is "check in" or "checked in" or "checkin") return "O";
        if (s == "provisional") return "P";
        return "R";
    }

    private static string LegacyPaymentStatus(decimal grandTotal, decimal paidAmount, decimal remainingAmount)
    {
        grandTotal = Math.Round(grandTotal, 2);
        paidAmount = Math.Round(paidAmount, 2);
        remainingAmount = Math.Round(remainingAmount, 2);
        var received = Math.Round(grandTotal - remainingAmount, 2);

        if (paidAmount == 0m && remainingAmount == grandTotal) return "Not Paid";
        if (paidAmount == grandTotal && remainingAmount == 0m) return "Fully Paid";
        if (paidAmount > 0m && remainingAmount > 0m && received == paidAmount) return "Partially Paid";
        return remainingAmount <= 0.005m ? "Fully Paid" :
               paidAmount > 0.005m ? "Partially Paid" : "Not Paid";
    }

    private static bool TryParseLegacyDate(string value, out DateTime date)
    {
        if (DateTime.TryParseExact(value?.Trim(), DateFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out date))
        {
            date = date.Date;
            return true;
        }

        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
        {
            date = date.Date;
            return true;
        }

        date = default;
        return false;
    }

    private static string DisplayStatus(string status, DateTime arrival, DateTime today)
    {
        var s = (status ?? string.Empty).Trim().ToLowerInvariant();
        if (s is "check in" or "checked in" or "checkin") return "In house";
        if (s is "check out" or "checked out" or "checkout") return "Checked out";
        if (s is "provisional" or "tentative") return "Tentative";
        if (s == "reservation" && arrival.Date <= today.Date) return "Due to arrive";
        return "Confirmed";
    }

    private static bool IsCheckedOutStatus(string status)
    {
        var s = (status ?? string.Empty).Trim().ToLowerInvariant();
        return s is "check out" or "checked out" or "checkout";
    }

    private static bool IsCancelledStatus(string status)
    {
        var s = (status ?? string.Empty).Trim().ToLowerInvariant();
        return s is "cancelled" or "canceled";
    }

    private static bool IsClosedStatus(string status)
    {
        var s = (status ?? string.Empty).Trim().ToLowerInvariant();
        return s is "check out" or "checked out" or "checkout" or "cancelled" or "canceled";
    }

    private async Task<HashSet<string>> LoadNotebookIndicatorRegIdsAsync(
        string hotelId,
        IReadOnlyCollection<string> visibleRegIds,
        CancellationToken ct)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ids = (visibleRegIds ?? Array.Empty<string>())
            .Select(NormalizeRegId)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (ids.Count == 0) return result;

        try
        {
            await using var cn = new SqlConnection(_connectionString);
            await cn.OpenAsync(ct);

            // Same idea as the WebForms notebook cache: load the visible
            // reservation IDs in bounded batches and never query per bar/cell.
            const int batchSize = 400;
            for (var offset = 0; offset < ids.Count; offset += batchSize)
            {
                var batch = ids.Skip(offset).Take(batchSize).ToList();
                await using var cmd = cn.CreateCommand();
                cmd.CommandTimeout = 15;
                cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId.Trim();

                var parameterNames = new List<string>(batch.Count);
                for (var i = 0; i < batch.Count; i++)
                {
                    var parameterName = "@reg" + i.ToString(CultureInfo.InvariantCulture);
                    parameterNames.Add(parameterName);
                    cmd.Parameters.Add(parameterName, SqlDbType.VarChar, 100).Value = batch[i];
                }

                cmd.CommandText = @"
SELECT CONVERT(varchar(100),reg_id) AS reg_id
FROM dbo.GuestNoteBook WITH (READPAST)
WHERE hotel_id=@hotel
  AND reg_id IN (" + string.Join(",", parameterNames) + @")
  AND ISNULL(CONVERT(varchar(max),description),'') <> '';";

                await using var rd = await cmd.ExecuteReaderAsync(ct);
                while (await rd.ReadAsync(ct))
                {
                    var regId = NormalizeRegId(S(rd, "reg_id"));
                    if (!string.IsNullOrWhiteSpace(regId)) result.Add(regId);
                }
            }
        }
        catch (Exception ex)
        {
            // Indicator lookup is deliberately non-fatal, matching WebForms.
            _logger.Debug(ex, "Calendar notebook indicator lookup skipped.");
        }

        return result;
    }

    private async Task<HashSet<string>> LoadRoomChangedIndicatorRegIdsAsync(
        string hotelId,
        IReadOnlyCollection<string> visibleRegIds,
        CancellationToken ct)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visible = new HashSet<string>(
            (visibleRegIds ?? Array.Empty<string>())
                .Select(NormalizeRegId)
                .Where(x => !string.IsNullOrWhiteSpace(x)),
            StringComparer.OrdinalIgnoreCase);

        if (visible.Count == 0) return result;

        try
        {
            await using var cn = new SqlConnection(_connectionString);
            await cn.OpenAsync(ct);
            await using var cmd = new SqlCommand(@"
SELECT DISTINCT
       LTRIM(RTRIM(CONVERT(varchar(100),RegID))) AS RegID
FROM dbo.RoomChangeLogTB WITH (READPAST)
WHERE HotelID=@hotel
  AND RegID IS NOT NULL
  AND LTRIM(RTRIM(CONVERT(varchar(100),RegID)))<>'';", cn)
            {
                CommandTimeout = 15
            };
            cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId.Trim();

            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                var regId = NormalizeRoomChangeIndicatorRegId(S(rd, "RegID"));
                if (regId.Length > 0 && visible.Contains(regId)) result.Add(regId);
            }
        }
        catch (Exception ex)
        {
            // The old page also allowed the calendar to load when this optional
            // lookup failed. Preserve that behaviour in MVC.
            _logger.Debug(ex, "Calendar room-change indicator lookup skipped.");
        }

        return result;
    }

    private static string NormalizeRoomChangeIndicatorRegId(string? value)
    {
        var v = NormalizeRegId(value);
        if (v.Length == 0) return string.Empty;

        return v.ToUpperInvariant() switch
        {
            "A" or "B" or "D" or "O" or "CO" or "R" or "P" or
            "AVAILABLE" or "DIRTY" or "BLOCKED" or
            "CHECK IN" or "CHECK OUT" or "CHECKIN" or "CHECKOUT" or
            "PROVISIONAL" or "RESERVATION" or "⛔" => string.Empty,
            _ => v
        };
    }

    private static string NormalizeRegId(string? value)
    {
        var v = (value ?? string.Empty).Trim();
        return v.EndsWith("_R", StringComparison.OrdinalIgnoreCase) ? v[..^2].Trim() : v;
    }

    private static string LegacyDate(DateTime date) => date.ToString("MM-dd-yyyy", CultureInfo.InvariantCulture);
    private static string FirstNonEmpty(params string[] values) => values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? string.Empty;
    private static void EnsureSession(string hotelId, string userId)
    {
        if (string.IsNullOrWhiteSpace(hotelId) || string.IsNullOrWhiteSpace(userId))
            throw new UnauthorizedAccessException("Hotel session is missing.");
    }

    private static int Ordinal(SqlDataReader rd, string name)
    {
        for (var i = 0; i < rd.FieldCount; i++) if (rd.GetName(i).Equals(name, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }
    private static string S(SqlDataReader rd, string name)
    {
        var i = Ordinal(rd, name); return i < 0 || rd.IsDBNull(i) ? string.Empty : Convert.ToString(rd.GetValue(i), CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
    }
    private static int I(SqlDataReader rd, string name)
    {
        var i = Ordinal(rd, name); if (i < 0 || rd.IsDBNull(i)) return 0;
        return int.TryParse(Convert.ToString(rd.GetValue(i), CultureInfo.InvariantCulture), NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }
    private static decimal M(SqlDataReader rd, string name)
    {
        var i = Ordinal(rd, name); if (i < 0 || rd.IsDBNull(i)) return 0m;
        return decimal.TryParse(Convert.ToString(rd.GetValue(i), CultureInfo.InvariantCulture), NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0m;
    }
    private static bool B(SqlDataReader rd, string name)
    {
        var i = Ordinal(rd, name); if (i < 0 || rd.IsDBNull(i)) return false;
        var o = rd.GetValue(i); if (o is bool b) return b;
        if (int.TryParse(Convert.ToString(o, CultureInfo.InvariantCulture), out var n)) return n != 0;
        return bool.TryParse(Convert.ToString(o, CultureInfo.InvariantCulture), out var v) && v;
    }
    private static DateTime? D(SqlDataReader rd, string name)
    {
        var i = Ordinal(rd, name); if (i < 0 || rd.IsDBNull(i)) return null;
        var o = rd.GetValue(i); if (o is DateTime dt) return dt.Date;
        var s = Convert.ToString(o, CultureInfo.InvariantCulture)?.Trim(); if (string.IsNullOrWhiteSpace(s)) return null;
        if (DateTime.TryParseExact(s, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var exact)) return exact.Date;
        return DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ? parsed.Date : null;
    }
    private static DateTime? DTime(SqlDataReader rd, string name)
    {
        var i = Ordinal(rd, name); if (i < 0 || rd.IsDBNull(i)) return null;
        var o = rd.GetValue(i); if (o is DateTime dt) return dt;
        return DateTime.TryParse(Convert.ToString(o, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ? parsed : null;
    }

    private sealed class BookingRow
    {
        public int PaymentId { get; init; }
        public string RegId { get; init; } = string.Empty;
        public string RoomNo { get; init; } = string.Empty;
        public string CategoryId { get; init; } = string.Empty;
        public string CategoryName { get; init; } = string.Empty;
        public DateTime Arrival { get; init; }
        public DateTime Departure { get; init; }
        public string Status { get; init; } = string.Empty;
        public decimal Rate { get; init; }
        public decimal Gst { get; init; }
        public decimal Bed { get; init; }
        public decimal Total { get; init; }
    }
}
