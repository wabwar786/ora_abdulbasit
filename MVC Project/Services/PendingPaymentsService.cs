using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;
using Orapmshms.Models;

namespace Orapmshms.Services;

/// <summary>
/// Read-only Remaining Balances report. All writes remain in the shared
/// CheckIn/RecordPayment endpoint used by the Record Payment partial.
/// </summary>
public sealed class PendingPaymentsService
{
    private readonly string _connectionString;
    public PendingPaymentsService(IConfiguration config) => _connectionString =
        config.GetConnectionString("con") ?? throw new InvalidOperationException("ConnectionStrings:con is missing.");

    public async Task<(string CurrencySymbol, string CurrencyCode, List<string> Sources)> GetMetadataAsync(string hotelId, CancellationToken ct)
    {
        await using var cn = new SqlConnection(_connectionString);
        await cn.OpenAsync(ct);
        var symbol = "£";
        var code = "GBP";
        await using (var cmd = new SqlCommand(@"SELECT TOP 1 ISNULL(NULLIF(currency_sign,''),'£') FROM dbo.HotelsSignUpTB WHERE hotel_id=@hotel;", cn))
        {
            cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
            symbol = (Convert.ToString(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture) ?? "£").Trim();
            if (symbol.Length == 0) symbol = "£";
        }
        await using (var cmd = new SqlCommand("SELECT TOP 1 ISNULL(NULLIF(currency,''),'GBP') FROM dbo.HotelsSignUpTB WHERE hotel_id=@hotel;",cn))
        {
            cmd.Parameters.Add("@hotel",SqlDbType.VarChar,50).Value=hotelId;
            code=(Convert.ToString(await cmd.ExecuteScalarAsync(ct)) ?? "GBP").Trim().ToUpperInvariant();
            if(code.Length!=3)code="GBP";
        }
        var sources = new List<string>();
        await using (var cmd = new SqlCommand(@"
SELECT DISTINCT TOP (150) s.SourceName FROM
(
 SELECT NULLIF(LTRIM(RTRIM(Agency)),'') SourceName FROM dbo.NewReservationsTB WHERE hotel_id=@hotel
 UNION ALL
 SELECT NULLIF(LTRIM(RTRIM(Agency)),'') SourceName FROM dbo.GuestInformationLogTB WHERE hotel_id=@hotel
) s
WHERE s.SourceName IS NOT NULL AND s.SourceName <> '--Select--'
ORDER BY s.SourceName;", cn) { CommandTimeout = 15 })
        {
            cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) sources.Add(rd.GetString(0));
        }
        if (!sources.Contains("Walk In", StringComparer.OrdinalIgnoreCase)) sources.Insert(0, "Walk In");
        return (symbol, code, sources);
    }


    // Match the Front Desk Calendar capability checks; never expose card/PDQ
    // solely because a provider account exists without staff permission.
    public async Task<(bool OnlineCard, bool Pdq)> GetPaymentOptionsAsync(string hotelId, string userId, string role, CancellationToken ct)
    {
        // Match FrontDeskCalendarService.GetPermissionsAsync: card/PDQ actions
        // default to allowed unless a configured action explicitly denies them.
        // The configured payment provider is still required below.
        var ownerOrManager = role.Equals("hotel", StringComparison.OrdinalIgnoreCase)
                          || role.Equals("manager", StringComparison.OrdinalIgnoreCase);
        var canOnline = true;
        var canPdq = true;
        await using var cn = new SqlConnection(_connectionString);
        await cn.OpenAsync(ct);
        if (!ownerOrManager)
        {
            try
            {
                await using var cmd = new SqlCommand(@"
DECLARE @menuid int=(SELECT TOP (1) menuid FROM dbo.AddMenuTB
 WHERE REPLACE(REPLACE(LOWER(ISNULL(pagename,'')),'.aspx',''),' ','')
 IN ('frontdeskcalender','frontdeskcalendar','calendar') ORDER BY menuid);
SELECT pa.action_name, CASE WHEN NOT EXISTS
 (SELECT 1 FROM dbo.UserActionPermissionsTB x WHERE x.menuid=@menuid
  AND x.action_id=pa.action_id AND ISNULL(x.is_active,0)=1) THEN 1
 ELSE ISNULL(MAX(CASE WHEN ISNULL(u.is_allowed,0)=1 THEN 1 ELSE 0 END),0) END allowed
FROM dbo.PageActionsTB pa
LEFT JOIN dbo.UserActionPermissionsTB u ON u.menuid=pa.menuid
 AND u.action_id=pa.action_id AND ISNULL(u.is_active,0)=1
 AND (CONVERT(varchar(100),u.user_id)=@user OR
    (CONVERT(varchar(100),u.hotel_id)=@hotel AND
     LOWER(LTRIM(RTRIM(ISNULL(u.permission_for,''))))='hotel'))
WHERE pa.menuid=@menuid AND ISNULL(pa.is_active,1)=1
GROUP BY pa.action_id,pa.action_name;",cn) { CommandTimeout = 8 };
                cmd.Parameters.Add("@hotel",SqlDbType.VarChar,50).Value=hotelId;
                cmd.Parameters.Add("@user",SqlDbType.VarChar,100).Value=userId;
                var permissions=new Dictionary<string,bool>(StringComparer.OrdinalIgnoreCase);
                await using var rd=await cmd.ExecuteReaderAsync(ct);
                while(await rd.ReadAsync(ct))permissions[rd.GetString(0)]=Convert.ToInt32(rd[1])==1;
                // Use the exact alias/default semantics used by the Calendar:
                // if none of the aliases is configured, allow; if configured,
                // at least one must explicitly be allowed.
                bool HasAny(bool fallback, params string[] aliases)
                {
                    var configured = aliases.Where(x => permissions.ContainsKey(x)).ToArray();
                    return configured.Length == 0 ? fallback : configured.Any(x => permissions[x]);
                }
                canOnline=HasAny(true,"vcPayment","ChargeVC","VirtualCard","VC_PAYMENT","CardPayment","card_payment");
                canPdq=HasAny(true,"pdqPayment","PDQ_PAYMENT","PDQPayment","pdq_payment");
            }
            catch(SqlException)
            {
                // Same legacy-schema fallback as FrontDeskCalendarService:
                // the provider check below still gates both options.
            }
        }
        bool stripe=false, clover=false;
        try
        {
            await using var cmd=new SqlCommand("SELECT COUNT(*) FROM dbo.HotelStripeAccounts WHERE HotelId=@hotel AND ISNULL(AccessToken,'')<>'';",cn);
            cmd.Parameters.Add("@hotel",SqlDbType.VarChar,50).Value=hotelId;
            stripe=Convert.ToInt32(await cmd.ExecuteScalarAsync(ct))>0;
        } catch(SqlException) { }
        try
        {
            await using var cmd=new SqlCommand("SELECT COUNT(*) FROM dbo.clovertb WHERE hotel_id=@hotel AND ISNULL(access_token,'')<>'';",cn);
            cmd.Parameters.Add("@hotel",SqlDbType.VarChar,50).Value=hotelId;
            clover=Convert.ToInt32(await cmd.ExecuteScalarAsync(ct))>0;
        } catch(SqlException) { }
        return (canOnline && stripe,canPdq && (stripe || clover));
    }

    private const string SourcesSql = @"
;WITH sources AS
(
    SELECT g.hotel_id, g.reg_id, g.ID AS record_id, 'NR' AS src,
           CONVERT(nvarchar(40),g.ArrivalDate) AS arrival_text,
           CONVERT(nvarchar(40),g.dept_date) AS departure_text,
           CONVERT(nvarchar(180),ISNULL(g.GuestName,'')) AS guest_first,
           CONVERT(nvarchar(160),ISNULL(g.LastName,'')) AS guest_last,
           CONVERT(nvarchar(100),ISNULL(g.PhoneNo,'')) AS phone,
           CONVERT(nvarchar(250),ISNULL(g.Email,'')) AS email,
           CONVERT(nvarchar(150),ISNULL(g.booking_id,'')) AS booking_id,
           CONVERT(nvarchar(150),ISNULL(g.BookID,'')) AS book_id,
           CONVERT(nvarchar(250),ISNULL(g.room_category,'')) AS room_category,
           CONVERT(nvarchar(150),ISNULL(g.Agency,'')) AS agency,
           CONVERT(nvarchar(70),ISNULL(g.res_status,'')) AS res_status,
           TRY_CONVERT(decimal(18,2), NULLIF(CONVERT(varchar(60),g.total_amount),'')) AS source_total,
           CONVERT(nvarchar(50),g.ID) AS visit_id
    FROM dbo.NewReservationsTB g WHERE g.hotel_id=@hotel
      AND LOWER(LTRIM(RTRIM(ISNULL(g.res_status,'')))) IN ('reservation','provisional')
    UNION ALL
    SELECT g.hotel_id, g.reg_id, g.ID, 'GI',
           CONVERT(nvarchar(40),g.ArrivalDate), CONVERT(nvarchar(40),g.DepartureDate),
           CONVERT(nvarchar(180),ISNULL(g.GuestName,'')), CONVERT(nvarchar(160),ISNULL(g.LastName,'')),
           CONVERT(nvarchar(100),ISNULL(g.PhoneNo,'')), CONVERT(nvarchar(250),ISNULL(g.Email,'')),
           CONVERT(nvarchar(150),ISNULL(g.booking_id,'')), CONVERT(nvarchar(150),ISNULL(g.BookID,'')),
           CONVERT(nvarchar(250),ISNULL(g.room_category,'')), CONVERT(nvarchar(150),ISNULL(g.Agency,'')),
           CONVERT(nvarchar(70),ISNULL(g.res_status,'')),
           TRY_CONVERT(decimal(18,2), NULLIF(CONVERT(varchar(60),g.total_amount),'')),
           CONVERT(nvarchar(50),g.visit_id)
    FROM dbo.GuestInformationLogTB g WHERE g.hotel_id=@hotel
    UNION ALL
    SELECT g.hotel_id, g.reg_id, g.ID, 'CR',
           CONVERT(nvarchar(40),g.ArrivalDate), CONVERT(nvarchar(40),g.dept_date),
           CONVERT(nvarchar(180),ISNULL(g.GuestName,'')), CONVERT(nvarchar(160),ISNULL(g.LastName,'')),
           CONVERT(nvarchar(100),ISNULL(g.PhoneNo,'')), CONVERT(nvarchar(250),ISNULL(g.Email,'')),
           CONVERT(nvarchar(150),ISNULL(g.booking_id,'')), CONVERT(nvarchar(150),''),
           CONVERT(nvarchar(250),ISNULL(g.room_category,'')), CONVERT(nvarchar(150),ISNULL(g.Agency,'')),
           CONVERT(nvarchar(70),'Cancelled'),
           TRY_CONVERT(decimal(18,2), NULLIF(CONVERT(varchar(60),g.total_amount),'')),
           CONVERT(nvarchar(50),g.ID)
    FROM dbo.CancelledReservationsTB g WHERE g.hotel_id=@hotel
    UNION ALL
    SELECT g.hotel_id, g.reg_id, g.ID, 'NS',
           CONVERT(nvarchar(40),g.ArrivalDate), CONVERT(nvarchar(40),g.dept_date),
           CONVERT(nvarchar(180),ISNULL(g.GuestName,'')), CONVERT(nvarchar(160),ISNULL(g.LastName,'')),
           CONVERT(nvarchar(100),ISNULL(g.PhoneNo,'')), CONVERT(nvarchar(250),ISNULL(g.Email,'')),
           CONVERT(nvarchar(150),ISNULL(g.booking_id,'')), CONVERT(nvarchar(150),''),
           CONVERT(nvarchar(250),ISNULL(g.room_category,'')), CONVERT(nvarchar(150),ISNULL(g.Agency,'')),
           CONVERT(nvarchar(70),'No Show'),
           TRY_CONVERT(decimal(18,2), NULLIF(CONVERT(varchar(60),g.total_amount),'')),
           CONVERT(nvarchar(50),g.ID)
    FROM dbo.NoShowTB g WHERE g.hotel_id=@hotel
),
ranked_sources AS
(
    SELECT s.*, ROW_NUMBER() OVER
      (PARTITION BY s.hotel_id,s.reg_id
       ORDER BY CASE s.src WHEN 'CR' THEN 1 WHEN 'NS' THEN 2 WHEN 'GI' THEN 3 ELSE 4 END,
                s.record_id DESC) AS rn
    FROM sources s WHERE NULLIF(LTRIM(RTRIM(s.reg_id)),'') IS NOT NULL
),
latest_payments AS
(
    SELECT hotel_id,reg_id, grand_total,paid_amount,remaining_amount,
       ROW_NUMBER() OVER (PARTITION BY hotel_id,reg_id ORDER BY id DESC) AS rn
    FROM dbo.PaymentsUpdateTB WHERE hotel_id=@hotel
),
receivables AS
(
    SELECT s.*,
      COALESCE(TRY_CONVERT(date,s.arrival_text,110), TRY_CONVERT(date,s.arrival_text,103),
               TRY_CONVERT(date,s.arrival_text,23),TRY_CONVERT(date,s.arrival_text)) AS arrival,
      COALESCE(TRY_CONVERT(date,s.departure_text,110),TRY_CONVERT(date,s.departure_text,103),
               TRY_CONVERT(date,s.departure_text,23),TRY_CONVERT(date,s.departure_text)) AS departure,
      CASE WHEN NULLIF(LTRIM(RTRIM(s.agency)),'') IS NULL OR s.agency='--Select--'
           THEN 'Walk In' ELSE LTRIM(RTRIM(s.agency)) END AS booking_source,
      CASE WHEN s.src='CR' THEN 'Cancelled' WHEN s.src='NS' THEN 'No Show'
           WHEN LOWER(REPLACE(REPLACE(s.res_status,'-',' '),'_',' ')) LIKE '%check%out%' THEN 'Check out'
           WHEN LOWER(REPLACE(REPLACE(s.res_status,'-',' '),'_',' ')) LIKE '%check%in%' THEN 'Check in'
           WHEN LOWER(s.res_status)='provisional' THEN 'Provisional'
           ELSE 'Reservation' END AS display_status,
      COALESCE(TRY_CONVERT(decimal(18,2),NULLIF(pu.grand_total,'')), s.source_total,0) AS total_value,
      COALESCE(TRY_CONVERT(decimal(18,2),NULLIF(pu.paid_amount,'')),0) AS paid_value,
      COALESCE(TRY_CONVERT(decimal(18,2),NULLIF(pu.remaining_amount,'')),
               TRY_CONVERT(decimal(18,2),NULLIF(pu.grand_total,''))-
               COALESCE(TRY_CONVERT(decimal(18,2),NULLIF(pu.paid_amount,'')),0),0) AS remaining_value,
      -- Every listed guest with a positive outstanding balance can receive a
      -- payment, including records archived to CancelledReservationsTB/NoShowTB.
      -- CheckInService resolves those source tables when recording the payment.
      CAST(1 AS bit) AS can_pay
    FROM ranked_sources s
    -- Only show registrations that have a PaymentsUpdateTB record.
    -- Never invent a balance from the reservation total if no payment row exists.
    INNER JOIN latest_payments pu ON pu.hotel_id=s.hotel_id AND pu.reg_id=s.reg_id AND pu.rn=1
    WHERE s.rn=1
),
filtered AS
(
 SELECT * FROM receivables r
 WHERE r.remaining_value > 0.005
   AND (@search='' OR r.reg_id LIKE @pattern ESCAPE '\'
        OR r.booking_id LIKE @pattern ESCAPE '\' OR r.book_id LIKE @pattern ESCAPE '\'
        OR r.guest_first LIKE @pattern ESCAPE '\' OR r.guest_last LIKE @pattern ESCAPE '\'
        OR (r.guest_first+' '+r.guest_last) LIKE @pattern ESCAPE '\'
        OR r.phone LIKE @pattern ESCAPE '\' OR REPLACE(r.phone,' ','') LIKE @phonePattern ESCAPE '\'
        OR r.email LIKE @pattern ESCAPE '\')
   -- Arrival range must apply whether or not a guest-search term is entered.
   AND (@start IS NULL OR (r.arrival >= @start AND r.arrival <= @end))
   AND (@source='' OR r.booking_source=@source)
   AND (@status='all' OR LOWER(r.display_status)=@status)
),
numbered AS
(
 SELECT *,
        COUNT(*) OVER() AS record_count,
        SUM(remaining_value) OVER() AS receivables_total,
        SUM(CASE WHEN display_status NOT IN ('Cancelled','No Show')
                 THEN remaining_value ELSE CAST(0 AS decimal(18,2)) END) OVER()
                 AS active_receivables_total,
        SUM(total_value) OVER() AS billed_total,
        SUM(paid_value) OVER() AS paid_total,
        ROW_NUMBER() OVER (ORDER BY arrival DESC, record_id DESC, reg_id) AS row_number
 FROM filtered
)
SELECT reg_id,booking_id,book_id,guest_first,guest_last,phone,email,room_category,
       booking_source,display_status,src,visit_id,arrival,departure,total_value,paid_value,
       remaining_value,can_pay,record_count,receivables_total,active_receivables_total,billed_total,paid_total
FROM numbered WHERE row_number BETWEEN @fromRow AND @toRow ORDER BY row_number;";

    public async Task<PendingPaymentsResult> SearchAsync(
        string hotelId, DateTime? start, DateTime? end, string? search, string? source,
        string? status, int page, int pageSize, CancellationToken ct)
    {
        search = (search ?? "").Trim();
        source = (source ?? "").Trim();
        status = (status ?? "all").Trim().ToLowerInvariant();
        if (status is not ("all" or "reservation" or "provisional" or "check in" or "check out" or "cancelled" or "no show"))
            status = "all";
        page = Math.Clamp(page, 1, 100000);
        pageSize = Math.Clamp(pageSize, 10, 100);
        var offset = (page - 1) * pageSize;
        var result = new PendingPaymentsResult();
        await using var cn = new SqlConnection(_connectionString);
        await using var cmd = new SqlCommand(SourcesSql, cn) { CommandTimeout = 35 };
        cmd.Parameters.Add("@hotel",SqlDbType.VarChar,50).Value=hotelId;
        cmd.Parameters.Add("@search",SqlDbType.NVarChar,160).Value=search.Length>160?search[..160]:search;
        cmd.Parameters.Add("@pattern",SqlDbType.NVarChar,350).Value="%"+EscapeLike(search.Length>160?search[..160]:search)+"%";
        cmd.Parameters.Add("@phonePattern",SqlDbType.NVarChar,350).Value="%"+EscapeLike(search.Replace(" ","").Length>160?search.Replace(" ","")[..160]:search.Replace(" ",""))+"%";
        cmd.Parameters.Add("@source",SqlDbType.NVarChar,150).Value=source.Length>150?source[..150]:source;
        cmd.Parameters.Add("@status",SqlDbType.NVarChar,25).Value=status;
        cmd.Parameters.Add("@start",SqlDbType.Date).Value=start.HasValue?(object)start.Value.Date:DBNull.Value;
        cmd.Parameters.Add("@end",SqlDbType.Date).Value=end.HasValue?(object)end.Value.Date:DBNull.Value;
        cmd.Parameters.Add("@fromRow",SqlDbType.Int).Value=offset+1;
        cmd.Parameters.Add("@toRow",SqlDbType.Int).Value=offset+pageSize;
        await cn.OpenAsync(ct);
        await using var rd=await cmd.ExecuteReaderAsync(ct);
        while(await rd.ReadAsync(ct))
        {
            if(result.Rows.Count==0)
            {
                result.TotalRecords=Convert.ToInt32(rd["record_count"],CultureInfo.InvariantCulture);
                result.TotalReceivables=Decimal(rd,"receivables_total");
                result.TotalReceivablesActiveOnly=Decimal(rd,"active_receivables_total");
                result.TotalBilled=Decimal(rd,"billed_total");
                result.TotalPaid=Decimal(rd,"paid_total");
            }
            var first=Text(rd,"guest_first"); var last=Text(rd,"guest_last");
            result.Rows.Add(new PendingPaymentRow {
                RegId=Text(rd,"reg_id"), BookingId=Text(rd,"booking_id").Length>0?Text(rd,"booking_id"):Text(rd,"book_id"),
                GuestName=string.IsNullOrWhiteSpace((first+" "+last).Trim()) ? "Guest" : (first+" "+last).Trim(),
                Phone=Text(rd,"phone"),Email=Text(rd,"email"), RoomCategory=Text(rd,"room_category"),
                BookingSource=Text(rd,"booking_source"), Status=Text(rd,"display_status"), SourceTable=Text(rd,"src"),
                VisitId=Text(rd,"visit_id"),Arrival=NullableDate(rd,"arrival"),Departure=NullableDate(rd,"departure"),
                GrandTotal=Decimal(rd,"total_value"),Paid=Decimal(rd,"paid_value"),Remaining=Decimal(rd,"remaining_value"),
                CanReceivePayment=rd["can_pay"]!=DBNull.Value && Convert.ToBoolean(rd["can_pay"],CultureInfo.InvariantCulture)
            });
        }
        // No rows on a page beyond the end: reset the client to page 1 instead of
        // displaying fictitious zero receivables. Client clamps pages using TotalRecords.
        return result;
    }

    private static string EscapeLike(string value) => value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_").Replace("[", "\\[");
    private static string Text(SqlDataReader r,string col) => r[col]==DBNull.Value?"":Convert.ToString(r[col],CultureInfo.InvariantCulture)?.Trim()??"";
    private static decimal Decimal(SqlDataReader r,string col) => r[col]==DBNull.Value?0:Convert.ToDecimal(r[col],CultureInfo.InvariantCulture);
    private static DateTime? NullableDate(SqlDataReader r,string col) => r[col]==DBNull.Value?null:Convert.ToDateTime(r[col],CultureInfo.InvariantCulture);
}
