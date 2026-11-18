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
        CachedAsync($"dashboard:today-arrivals:v1:{hotelId}:{date:yyyyMMdd}", TimeSpan.FromSeconds(6),
            () => LoadArrivalsTodayDetailsCoreAsync(hotelId, date.Date, ct), OperationDetailCacheLocks);

    private Task<DashboardStatisticDetailResult> GetCheckoutsTodayDetailsAsync(
        string hotelId, DateTime date, CancellationToken ct) =>
        CachedAsync($"dashboard:today-checkouts:v1:{hotelId}:{date:yyyyMMdd}", TimeSpan.FromSeconds(6),
            () => LoadCheckoutsTodayDetailsCoreAsync(hotelId, date.Date, ct), OperationDetailCacheLocks);

    private Task<DashboardStatisticDetailResult> GetCreatedTodayDetailsAsync(
        string hotelId, DateTime date, CancellationToken ct) =>
        CachedAsync($"dashboard:today-created:v1:{hotelId}:{date:yyyyMMdd}", TimeSpan.FromSeconds(6),
            () => LoadCreatedTodayDetailsCoreAsync(hotelId, date.Date, ct), OperationDetailCacheLocks);

    // An arrival is a RESERVATION (reg_id), even if that reservation has
    // multiple rooms. Include reservation-only, provisional, in-house and
    // same-day checked-out bookings; do not require a physical room assignment.
    // NR is moved to GuestInformationLogTB during check-in, so both sources
    // and room-rent payment rows must be considered, without double counting.
    private async Task<DashboardStatisticDetailResult> LoadArrivalsTodayDetailsCoreAsync(
        string hotelId, DateTime date, CancellationToken ct)
    {
        const string sql = @"
;WITH Candidates AS
(
    SELECT
        CONVERT(varchar(100), g.reg_id) AS reg_id,
        CONVERT(varchar(100), g.ArrivalDate) AS arrival_text,
        CONVERT(varchar(60), g.res_status) AS stay_status,
        LTRIM(RTRIM(CONCAT(ISNULL(CONVERT(varchar(150),g.GuestName),''), ' ',
                           ISNULL(CONVERT(varchar(150),g.LastName),'')))) AS guest_name,
        ISNULL(CONVERT(varchar(150),g.room_no),'') AS room_no,
        ISNULL(CONVERT(varchar(150),g.room_category),'') AS category,
        0 AS priority,
        g.ID AS source_id
    FROM dbo.GuestInformationLogTB g
    WHERE g.hotel_id = @hotel
    UNION ALL
    SELECT
        CONVERT(varchar(100), n.reg_id),
        CONVERT(varchar(100), n.ArrivalDate),
        CONVERT(varchar(60), n.res_status),
        LTRIM(RTRIM(CONCAT(ISNULL(CONVERT(varchar(150),n.GuestName),''), ' ',
                           ISNULL(CONVERT(varchar(150),n.LastName),'')))),
        ISNULL(CONVERT(varchar(150),n.room_no),''),
        ISNULL(CONVERT(varchar(150),n.room_category),''),
        1,
        n.ID
    FROM dbo.NewReservationsTB n
    WHERE n.hotel_id = @hotel
    UNION ALL
    SELECT
        CONVERT(varchar(100), p.reg_id),
        CONVERT(varchar(100), p.ArrivalDate),
        CONVERT(varchar(60), p.res_status),
        ISNULL(CONVERT(varchar(300),p.guestname),''),
        ISNULL(CONVERT(varchar(150),p.room_no),''),
        ISNULL(CONVERT(varchar(150),p.[Type]),''),
        2,
        p.ID
    FROM dbo.payments p
    WHERE p.hotel_id = @hotel
      AND LOWER(LTRIM(RTRIM(ISNULL(CONVERT(varchar(100),p.descr),'')))) = 'room rent'
),
Normalized AS
(
    SELECT
        LTRIM(RTRIM(ISNULL(reg_id,''))) AS reg_id,
        guest_name,
        room_no,
        category,
        priority,
        source_id,
        LOWER(REPLACE(REPLACE(LTRIM(RTRIM(ISNULL(stay_status,''))),'-',' '),'  ',' ')) AS state,
        COALESCE(
            TRY_CONVERT(date,arrival_text,110),
            TRY_CONVERT(date,arrival_text,103),
            TRY_CONVERT(date,arrival_text,101),
            TRY_CONVERT(date,arrival_text,23),
            TRY_CONVERT(date,arrival_text,100),
            TRY_CONVERT(date,arrival_text)) AS arrival_date
    FROM Candidates
),
LatestReservation AS
(
    SELECT *, ROW_NUMBER() OVER
    (
        PARTITION BY reg_id
        ORDER BY priority, source_id DESC
    ) AS rn
    FROM Normalized
    WHERE reg_id <> ''
)
SELECT reg_id, guest_name, room_no, category, state
FROM LatestReservation
WHERE rn = 1
  AND arrival_date = @date
  AND state IN ('reservation','provisional','tentative',
                'check in','checked in','checkin',
                'check out','checked out','checkout')
ORDER BY reg_id;";

        var result = NewDetail("Check-ins Today · arriving reservations", date,
            "Reservations", "0", "Reservation", "Guest", "Room", "Category", "Status");
        using var slot = await EnterDashboardSqlAsync(ct);
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 25 };
        command.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
        command.Parameters.Add("@date", SqlDbType.Date).Value = date.Date;
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var state = Convert.ToString(reader["state"]) ?? string.Empty;
            result.Rows.Add(new List<string>
            {
                Convert.ToString(reader["reg_id"])?.Trim() ?? string.Empty,
                Convert.ToString(reader["guest_name"])?.Trim() ?? string.Empty,
                string.IsNullOrWhiteSpace(Convert.ToString(reader["room_no"]))
                    ? "Unassigned" : Convert.ToString(reader["room_no"])!.Trim(),
                Convert.ToString(reader["category"])?.Trim() ?? string.Empty,
                state is "check in" or "checked in" or "checkin" ? "In house"
                    : state is "check out" or "checked out" or "checkout" ? "Checked out"
                    : state is "provisional" or "tentative" ? "Provisional" : "Reserved"
            });
        }
        result.SummaryValue = result.Rows.Count.ToString(CultureInfo.InvariantCulture);
        result.Subtitle = date.ToString("dd MMM yyyy", CultureInfo.InvariantCulture)
            + " · Includes reserved and checked-in arrivals, one per reservation";
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
