using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;
using Orapmshms.Models;

namespace Orapmshms.Services;

// Daily Operations KPIs and their detail popups deliberately share each query.
// Data is read-only; no reservation, room, or payment state is modified.
public sealed partial class DashboardService
{
    // Separate from the page/operations cache stripes. The operations loader
    // calls these cached detail loaders while holding its own cache lock.
    private static readonly SemaphoreSlim[] OperationDetailCacheLocks =
        Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    private Task<DashboardStatisticDetailResult> GetArrivalsTodayDetailsAsync(
        string hotelId, DateTime date, CancellationToken ct) =>
        CachedAsync($"dashboard:today-arrivals:v4:{hotelId}:{date:yyyyMMdd}", TimeSpan.FromSeconds(6),
            () => LoadArrivalsTodayDetailsCoreAsync(hotelId, date.Date, ct), OperationDetailCacheLocks);

    private Task<DashboardStatisticDetailResult> GetCheckoutsTodayDetailsAsync(
        string hotelId, DateTime date, CancellationToken ct) =>
        CachedAsync($"dashboard:today-checkouts:v1:{hotelId}:{date:yyyyMMdd}", TimeSpan.FromSeconds(6),
            () => LoadCheckoutsTodayDetailsCoreAsync(hotelId, date.Date, ct), OperationDetailCacheLocks);

    private Task<DashboardStatisticDetailResult> GetCreatedTodayDetailsAsync(
        string hotelId, DateTime date, CancellationToken ct) =>
        CachedAsync($"dashboard:today-created:v1:{hotelId}:{date:yyyyMMdd}", TimeSpan.FromSeconds(6),
            () => LoadCreatedTodayDetailsCoreAsync(hotelId, date.Date, ct), OperationDetailCacheLocks);

    // Arrivals count ROOM STAYS, not distinct registration numbers.  One reservation
    // containing three rooms arriving today contributes three arrivals.  Each room
    // gets its own effective arrival date; a second room arriving tomorrow must not
    // be pulled into today's count merely because the first room arrives today.
    // The same details query feeds the Dashboard KPI, popup and Night Audit.
    private async Task<DashboardStatisticDetailResult> LoadArrivalsTodayDetailsCoreAsync(
        string hotelId, DateTime date, CancellationToken ct)
    {
        const string sql = @"
;WITH PaymentRoomRows AS
(
    SELECT
        LTRIM(RTRIM(ISNULL(CONVERT(varchar(100),p.reg_id),''))) AS reg_id,
        LTRIM(RTRIM(ISNULL(CONVERT(varchar(100),p.room_no),''))) AS room_no,
        ISNULL(CONVERT(varchar(300),p.guestname),'') AS guest_name,
        ISNULL(CONVERT(varchar(150),p.[Type]),'') AS category,
        LOWER(REPLACE(REPLACE(LTRIM(RTRIM(ISNULL(CONVERT(varchar(60),p.res_status),''))),'-',' '),'  ',' ')) AS state,
        COALESCE(TRY_CONVERT(date,p.ArrivalDate,110),TRY_CONVERT(date,p.ArrivalDate,103),
                 TRY_CONVERT(date,p.ArrivalDate,101),TRY_CONVERT(date,p.ArrivalDate,23),
                 TRY_CONVERT(date,p.ArrivalDate,100),TRY_CONVERT(date,p.ArrivalDate)) AS arrival_day,
        CASE WHEN TRY_CONVERT(int,p.NumberOfRoom) BETWEEN 1 AND 100
             THEN TRY_CONVERT(int,p.NumberOfRoom) ELSE 1 END AS number_of_rooms,
        MIN(CASE WHEN LOWER(REPLACE(REPLACE(LTRIM(RTRIM(ISNULL(CONVERT(varchar(60),p.res_status),''))),'-',' '),'  ',' '))
                     IN ('reservation','reserved','provisional','tentative','check in','checked in','checkin','check out','checked out','checkout')
                 THEN COALESCE(TRY_CONVERT(date,p.ArrivalDate,110),TRY_CONVERT(date,p.ArrivalDate,103),
                               TRY_CONVERT(date,p.ArrivalDate,101),TRY_CONVERT(date,p.ArrivalDate,23),
                               TRY_CONVERT(date,p.ArrivalDate)) END)
           OVER (PARTITION BY LTRIM(RTRIM(ISNULL(CONVERT(varchar(100),p.reg_id),''))),
                        slot.room_key) AS first_active_arrival,
        'payment' AS source_type,
        ROW_NUMBER() OVER
        (
            PARTITION BY LTRIM(RTRIM(ISNULL(CONVERT(varchar(100),p.reg_id),''))),
                         slot.room_key
            ORDER BY p.ID DESC
        ) AS rn
    FROM dbo.payments p
    CROSS APPLY (VALUES
    (
        CASE WHEN NULLIF(LTRIM(RTRIM(ISNULL(CONVERT(varchar(100),p.room_no),''))),'') IS NULL
                       OR UPPER(LTRIM(RTRIM(ISNULL(CONVERT(varchar(100),p.room_no),''))))='UNASSIGNED'
             -- One pending room-category/plan/date group may hold several rooms;
             -- do not collapse distinct categories into a single empty room number.
             THEN CONCAT('#PENDING:',ISNULL(CONVERT(varchar(150),p.[Type]),''),':',
                         ISNULL(CONVERT(varchar(100),p.rateplan),''),':',
                         ISNULL(CONVERT(varchar(100),p.ArrivalDate),''))
             ELSE LTRIM(RTRIM(ISNULL(CONVERT(varchar(100),p.room_no),''))) END
    )) slot(room_key)
    WHERE p.hotel_id=@hotel
      AND LOWER(LTRIM(RTRIM(ISNULL(CONVERT(varchar(100),p.descr),''))))='room rent'
),
ReservationHeaders AS
(
    SELECT
        LTRIM(RTRIM(ISNULL(CONVERT(varchar(100),n.reg_id),''))) AS reg_id,
        LTRIM(RTRIM(ISNULL(CONVERT(varchar(150),n.room_no),''))) AS room_no,
        LTRIM(RTRIM(CONCAT(ISNULL(n.GuestName,''),' ',ISNULL(n.LastName,'')))) AS guest_name,
        ISNULL(CONVERT(varchar(150),n.room_category),'') AS category,
        LOWER(REPLACE(REPLACE(LTRIM(RTRIM(ISNULL(CONVERT(varchar(60),n.res_status),''))),'-',' '),'  ',' ')) AS state,
        COALESCE(TRY_CONVERT(date,n.ArrivalDate,110),TRY_CONVERT(date,n.ArrivalDate,103),
                 TRY_CONVERT(date,n.ArrivalDate,101),TRY_CONVERT(date,n.ArrivalDate,23),
                 TRY_CONVERT(date,n.ArrivalDate)) AS arrival_day,
        CASE WHEN TRY_CONVERT(int,n.noofrooms) BETWEEN 1 AND 100
             THEN TRY_CONVERT(int,n.noofrooms) ELSE 1 END AS number_of_rooms,
        'header' AS source_type,
        ROW_NUMBER() OVER (PARTITION BY n.reg_id ORDER BY n.ID DESC) AS rn
    FROM dbo.NewReservationsTB n
    WHERE n.hotel_id=@hotel
      AND NOT EXISTS (SELECT 1 FROM PaymentRoomRows p WHERE p.reg_id=LTRIM(RTRIM(CONVERT(varchar(100),n.reg_id))))
),
GuestHeaders AS
(
    SELECT
        LTRIM(RTRIM(ISNULL(CONVERT(varchar(100),g.reg_id),''))) AS reg_id,
        LTRIM(RTRIM(ISNULL(CONVERT(varchar(150),g.room_no),''))) AS room_no,
        LTRIM(RTRIM(CONCAT(ISNULL(g.GuestName,''),' ',ISNULL(g.LastName,'')))) AS guest_name,
        ISNULL(CONVERT(varchar(150),g.room_category),'') AS category,
        LOWER(REPLACE(REPLACE(LTRIM(RTRIM(ISNULL(CONVERT(varchar(60),g.res_status),''))),'-',' '),'  ',' ')) AS state,
        COALESCE(TRY_CONVERT(date,g.ArrivalDate,110),TRY_CONVERT(date,g.ArrivalDate,103),
                 TRY_CONVERT(date,g.ArrivalDate,101),TRY_CONVERT(date,g.ArrivalDate,23),
                 TRY_CONVERT(date,g.ArrivalDate)) AS arrival_day,
        1 AS number_of_rooms,
        'guest' AS source_type,
        ROW_NUMBER() OVER (PARTITION BY g.reg_id ORDER BY g.ID DESC) AS rn
    FROM dbo.GuestInformationLogTB g
    WHERE g.hotel_id=@hotel
      AND NOT EXISTS (SELECT 1 FROM PaymentRoomRows p WHERE p.reg_id=LTRIM(RTRIM(CONVERT(varchar(100),g.reg_id))))
      AND NOT EXISTS (SELECT 1 FROM ReservationHeaders n WHERE n.reg_id=LTRIM(RTRIM(CONVERT(varchar(100),g.reg_id))))
),
RoomArrivals AS
(
    SELECT reg_id,room_no,guest_name,category,state,number_of_rooms,source_type
    FROM PaymentRoomRows WHERE rn=1 AND COALESCE(first_active_arrival,arrival_day)=@date
      AND state IN ('reservation','reserved','provisional','tentative','check in','checked in','checkin','check out','checked out','checkout')
    UNION ALL
    SELECT reg_id,room_no,guest_name,category,state,number_of_rooms,source_type
    FROM ReservationHeaders WHERE rn=1 AND arrival_day=@date
      AND state IN ('reservation','reserved','provisional','tentative','check in','checked in','checkin','check out','checked out','checkout')
    UNION ALL
    SELECT reg_id,room_no,guest_name,category,state,number_of_rooms,source_type
    FROM GuestHeaders WHERE rn=1 AND arrival_day=@date
      AND state IN ('reservation','reserved','provisional','tentative','check in','checked in','checkin','check out','checked out','checkout')
)
SELECT a.reg_id,a.guest_name,a.room_no,a.category,a.state,
       CASE WHEN a.source_type='header' OR NULLIF(a.room_no,'') IS NULL OR UPPER(a.room_no)='UNASSIGNED'
            THEN a.number_of_rooms ELSE 1 END AS room_count
FROM RoomArrivals a
WHERE a.reg_id<>''
  AND NOT EXISTS (SELECT 1 FROM dbo.CancelledReservationsTB c
                  WHERE c.hotel_id=@hotel AND LTRIM(RTRIM(CONVERT(varchar(100),c.reg_id)))=a.reg_id)
  AND NOT EXISTS (SELECT 1 FROM dbo.NoShowTB ns
                  WHERE ns.hotel_id=@hotel AND LTRIM(RTRIM(CONVERT(varchar(100),ns.reg_id)))=a.reg_id)
ORDER BY a.reg_id,a.room_no;";

        var result = NewDetail("Check-ins Today · rooms arriving", date,
            "Rooms", "0", "Reservation", "Guest", "Room", "Category", "Status");
        using var slot = await EnterDashboardSqlAsync(ct);
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 30 };
        command.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
        command.Parameters.Add("@date", SqlDbType.Date).Value = date.Date;
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var state = Convert.ToString(reader["state"]) ?? string.Empty;
            var room = Convert.ToString(reader["room_no"])?.Trim() ?? string.Empty;
            var count = Math.Clamp(ToInt(reader["room_count"]), 1, 100);
            for (var i = 0; i < count; ++i)
            {
                result.Rows.Add(new List<string>
                {
                    Convert.ToString(reader["reg_id"])?.Trim() ?? string.Empty,
                    Convert.ToString(reader["guest_name"])?.Trim() ?? string.Empty,
                    room.Length == 0 || room.Equals("Unassigned", StringComparison.OrdinalIgnoreCase)
                        ? (count == 1 ? "Unassigned" : $"Unassigned #{i + 1}")
                        : (count == 1 ? room : $"{room} · room {i + 1}"),
                    Convert.ToString(reader["category"])?.Trim() ?? string.Empty,
                    state is "check in" or "checked in" or "checkin" ? "In house"
                        : state is "check out" or "checked out" or "checkout" ? "Checked out"
                        : state is "provisional" or "tentative" ? "Provisional" : "Reserved"
                });
            }
        }
        result.SummaryValue = result.Rows.Count.ToString(CultureInfo.InvariantCulture);
        result.Subtitle = date.ToString("dd MMM yyyy", CultureInfo.InvariantCulture)
            + " · Each arriving room is counted; multi-room reservations can contribute several arrivals";
        return result;
    }

    // Use the actual checkout event date written by CheckInService. For old
    // records where actual_checkout_date was not recorded, fall back to the
    // scheduled departure date. Count each checked-out room only once.
    private async Task<DashboardStatisticDetailResult> LoadCheckoutsTodayDetailsCoreAsync(
        string hotelId, DateTime date, CancellationToken ct)
    {
        const string sql = @"
;WITH RoomRows AS
(
    SELECT
        p.ID,
        LTRIM(RTRIM(ISNULL(CONVERT(varchar(100),p.reg_id),''))) AS reg_id,
        LTRIM(RTRIM(ISNULL(CONVERT(varchar(100),p.room_no),''))) AS room_no,
        ISNULL(CONVERT(varchar(150),p.[Type]),'') AS category,
        ISNULL(CONVERT(varchar(300),p.guestname),'') AS payment_guest,
        LOWER(REPLACE(REPLACE(LTRIM(RTRIM(ISNULL(CONVERT(varchar(60),p.res_status),''))),'-',' '),'  ',' ')) AS state,
        COALESCE(TRY_CONVERT(date,p.actual_checkout_date,110),
                 TRY_CONVERT(date,p.actual_checkout_date,103),
                 TRY_CONVERT(date,p.actual_checkout_date)) AS actual_day,
        COALESCE(TRY_CONVERT(date,p.DepartureDate,110),
                 TRY_CONVERT(date,p.DepartureDate,103),
                 TRY_CONVERT(date,p.DepartureDate,101),
                 TRY_CONVERT(date,p.DepartureDate,23),
                 TRY_CONVERT(date,p.DepartureDate)) AS departure_day,
        ROW_NUMBER() OVER
        (
            PARTITION BY LTRIM(RTRIM(ISNULL(CONVERT(varchar(100),p.reg_id),''))),
                         LTRIM(RTRIM(ISNULL(CONVERT(varchar(100),p.room_no),'')))
            ORDER BY p.ID DESC
        ) AS rn
    FROM dbo.payments p
    WHERE p.hotel_id = @hotel
      AND LOWER(LTRIM(RTRIM(ISNULL(CONVERT(varchar(100),p.descr),'')))) = 'room rent'
      AND NULLIF(LTRIM(RTRIM(ISNULL(CONVERT(varchar(100),p.room_no),''))),'') IS NOT NULL
      AND UPPER(LTRIM(RTRIM(ISNULL(CONVERT(varchar(100),p.room_no),'')))) <> 'UNASSIGNED'
),
Finished AS
(
    SELECT * FROM RoomRows
    WHERE rn = 1
      AND state IN ('check out','checked out','checkout')
      AND COALESCE(actual_day, departure_day) = @date
)
SELECT f.room_no, f.category, f.reg_id,
       COALESCE(NULLIF(g.guest_name,''),NULLIF(n.guest_name,''),f.payment_guest,'') AS guest_name,
       CONVERT(varchar(10),COALESCE(f.actual_day,f.departure_day),23) AS checkout_date
FROM Finished f
OUTER APPLY
(
    SELECT TOP (1) LTRIM(RTRIM(CONCAT(ISNULL(gi.GuestName,''),' ',ISNULL(gi.LastName,'')))) AS guest_name
    FROM dbo.GuestInformationLogTB gi
    WHERE gi.hotel_id=@hotel AND gi.reg_id=f.reg_id
    ORDER BY gi.ID DESC
) g
OUTER APPLY
(
    SELECT TOP (1) LTRIM(RTRIM(CONCAT(ISNULL(nr.GuestName,''),' ',ISNULL(nr.LastName,'')))) AS guest_name
    FROM dbo.NewReservationsTB nr
    WHERE nr.hotel_id=@hotel AND nr.reg_id=f.reg_id
    ORDER BY nr.ID DESC
) n
ORDER BY TRY_CONVERT(int,f.room_no), f.room_no, f.reg_id;";

        var result = NewDetail("Check-outs Today · completed rooms", date,
            "Rooms", "0", "Room", "Guest", "Category", "Reservation", "Check-out date");
        using var slot = await EnterDashboardSqlAsync(ct);
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 25 };
        command.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
        command.Parameters.Add("@date", SqlDbType.Date).Value = date.Date;
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Rows.Add(new List<string>
            {
                Convert.ToString(reader["room_no"])?.Trim() ?? string.Empty,
                Convert.ToString(reader["guest_name"])?.Trim() ?? string.Empty,
                Convert.ToString(reader["category"])?.Trim() ?? string.Empty,
                Convert.ToString(reader["reg_id"])?.Trim() ?? string.Empty,
                Convert.ToString(reader["checkout_date"])?.Trim() ?? string.Empty
            });
        }
        result.SummaryValue = result.Rows.Count.ToString(CultureInfo.InvariantCulture);
        result.Subtitle = date.ToString("dd MMM yyyy", CultureInfo.InvariantCulture)
            + " · Actual checkout date, with scheduled-date fallback for legacy rows";
        return result;
    }

    // The created-today card is booking-volume, NOT arrivals or current room
    // inventory. NewReservationsTB is removed on guest check-in. Because that
    // transfer makes GuestInformationLogTB.CreatedAt a later *copy* date, use
    // NR.CreatedAt first, otherwise the first Room Rent creation timestamp,
    // and GI.CreatedAt only as a last resort. This retains checked-in stays
    // without treating their guest-log copy date as a new reservation.
    private async Task<DashboardStatisticDetailResult> LoadCreatedTodayDetailsCoreAsync(
        string hotelId, DateTime date, CancellationToken ct)
    {
        const string sql = @"
;WITH Sources AS
(
    SELECT
        LTRIM(RTRIM(ISNULL(CONVERT(varchar(100),n.reg_id),''))) AS reg_id,
        COALESCE(TRY_CONVERT(datetime2(0),n.CreatedAt),
                 TRY_CONVERT(datetime2(0),n.CreatedDate,23),
                 TRY_CONVERT(datetime2(0),n.CreatedDate,110),
                 TRY_CONVERT(datetime2(0),n.CreatedDate,103)) AS created_at,
        LTRIM(RTRIM(CONCAT(ISNULL(CONVERT(varchar(150),n.GuestName),''),' ',
                           ISNULL(CONVERT(varchar(150),n.LastName),'')))) AS guest_name,
        ISNULL(CONVERT(varchar(100),n.ArrivalDate),'') AS arrival_text,
        ISNULL(CONVERT(varchar(100),n.Agency),'') AS source,
        0 AS priority,
        n.ID AS source_id
    FROM dbo.NewReservationsTB n
    WHERE n.hotel_id=@hotel
    UNION ALL
    SELECT
        LTRIM(RTRIM(ISNULL(CONVERT(varchar(100),g.reg_id),''))),
        TRY_CONVERT(datetime2(0),g.createdat),
        LTRIM(RTRIM(CONCAT(ISNULL(CONVERT(varchar(150),g.GuestName),''),' ',
                           ISNULL(CONVERT(varchar(150),g.LastName),'')))),
        ISNULL(CONVERT(varchar(100),g.ArrivalDate),''),
        ISNULL(CONVERT(varchar(100),g.Agency),''),
        1,
        g.ID
    FROM dbo.GuestInformationLogTB g
    WHERE g.hotel_id=@hotel
    UNION ALL
    SELECT
        LTRIM(RTRIM(ISNULL(CONVERT(varchar(100),p.reg_id),''))),
        COALESCE(TRY_CONVERT(datetime2(0),p.currentdate,100),
                 TRY_CONVERT(datetime2(0),p.currentdate,110),
                 TRY_CONVERT(datetime2(0),p.currentdate,103),
                 TRY_CONVERT(datetime2(0),p.currentdate,120),
                 TRY_CONVERT(datetime2(0),p.currentdate)),
        ISNULL(CONVERT(varchar(300),p.guestname),''),
        ISNULL(CONVERT(varchar(100),p.ArrivalDate),''),
        '',
        2,
        p.ID
    FROM dbo.payments p
    WHERE p.hotel_id=@hotel
      AND LOWER(LTRIM(RTRIM(ISNULL(CONVERT(varchar(100),p.descr),''))))='room rent'
),
Created AS
(
    SELECT reg_id,
           COALESCE(
               MIN(CASE WHEN priority=0 THEN created_at END),
               MIN(CASE WHEN priority=2 THEN created_at END),
               MIN(CASE WHEN priority=1 THEN created_at END)
           ) AS first_created_at
    FROM Sources
    WHERE reg_id <> '' AND created_at IS NOT NULL
    GROUP BY reg_id
),
Today AS
(
    SELECT reg_id, first_created_at
    FROM Created
    WHERE first_created_at >= @date
      AND first_created_at < DATEADD(day,1,@date)
),
Preferred AS
(
    SELECT reg_id, guest_name, arrival_text, source,
           ROW_NUMBER() OVER
           (
               PARTITION BY reg_id
               ORDER BY CASE WHEN NULLIF(guest_name,'') IS NULL THEN 1 ELSE 0 END,
                        priority, source_id DESC
           ) AS rn
    FROM Sources
    WHERE reg_id IN (SELECT reg_id FROM Today)
)
SELECT c.reg_id, c.first_created_at, p.guest_name, p.arrival_text, p.source
FROM Today c
LEFT JOIN Preferred p ON p.reg_id = c.reg_id AND p.rn=1
ORDER BY c.first_created_at DESC, c.reg_id;";

        var result = NewDetail("Reservations Today · newly created", date,
            "Reservations", "0", "Reservation", "Guest", "Created at", "Arrival", "Source");
        using var slot = await EnterDashboardSqlAsync(ct);
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 30 };
        command.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
        command.Parameters.Add("@date", SqlDbType.Date).Value = date.Date;
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var created = reader["first_created_at"] is DateTime dt
                ? dt.ToString("dd MMM yyyy HH:mm", CultureInfo.InvariantCulture)
                : FormatDate(reader["first_created_at"]);
            var rawArrival = Convert.ToString(reader["arrival_text"])?.Trim() ?? string.Empty;
            var arrival = DateTime.TryParseExact(rawArrival,
                new[] { "MM-dd-yyyy", "dd-MM-yyyy", "yyyy-MM-dd", "MM/dd/yyyy", "dd/MM/yyyy" },
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
                ? parsed.ToString("dd MMM yyyy", CultureInfo.InvariantCulture)
                : rawArrival;
            result.Rows.Add(new List<string>
            {
                Convert.ToString(reader["reg_id"])?.Trim() ?? string.Empty,
                Convert.ToString(reader["guest_name"])?.Trim() ?? string.Empty,
                created,
                arrival,
                Convert.ToString(reader["source"])?.Trim() ?? string.Empty
            });
        }
        result.SummaryValue = result.Rows.Count.ToString(CultureInfo.InvariantCulture);
        result.Subtitle = date.ToString("dd MMM yyyy", CultureInfo.InvariantCulture)
            + " · Bookings created on this date, regardless of check-in date";
        return result;
    }
}
