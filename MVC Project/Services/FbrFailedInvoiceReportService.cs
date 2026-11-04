using System.Data;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Orapmshms.Models;

namespace Orapmshms.Services;

/// <summary>
/// MVC port of FbrFailedInvoiceReport.aspx.cs. Read-only reporting is based on
/// GuestInformationLogTB; retries never alter charges, stays or payment records.
/// No Program.cs registration is required; controller creates this service.
/// </summary>
public sealed class FbrFailedInvoiceReportService
{
    private readonly string _connectionString;
    private readonly ICheckInService _checkIn;
    private readonly ILogger<FbrFailedInvoiceReportService> _log;

    public FbrFailedInvoiceReportService(IConfiguration config,
        ICheckInService checkIn, ILogger<FbrFailedInvoiceReportService> log)
    {
        _connectionString = config.GetConnectionString("con")
            ?? throw new InvalidOperationException("Connection string 'con' is missing.");
        _checkIn = checkIn;
        _log = log;
    }

    // This matches the WebForms report's latest-by-RegId filtering.
    private const string SourceSql = @"
;WITH SourceRows AS
(
 SELECT ID RowId, CONVERT(nvarchar(60),reg_id) RegId,
  LTRIM(RTRIM(CONCAT(ISNULL(GuestName,''),' ',ISNULL(LastName,'')))) GuestName,
  ISNULL(CONVERT(nvarchar(80),PhoneNo),'') GuestPhone,
  ISNULL(CONVERT(nvarchar(100),visit_id),'') VisitId,
  UPPER(LTRIM(RTRIM(ISNULL(fbr_post_status,'')))) Status,
  fbr_posted_at PostedAt,
  ISNULL(CONVERT(nvarchar(100),fbr_invoice_no),'') InvoiceNo,
  ISNULL(CONVERT(nvarchar(100),room_security),'') RoomSecurity,
  ISNULL(CONVERT(nvarchar(100),payment_method),'') PaymentMethod
 FROM dbo.GuestInformationLogTB
 WHERE hotel_id=@HotelId
 AND UPPER(LTRIM(RTRIM(ISNULL(fbr_post_status,'')))) IN ('POSTED','FAILED')
), LatestRows AS
(
 SELECT *,ROW_NUMBER() OVER(PARTITION BY RegId ORDER BY PostedAt DESC,RowId DESC) rn
 FROM SourceRows WHERE NULLIF(LTRIM(RTRIM(RegId)),'') IS NOT NULL
), Filtered AS
(
 SELECT RegId,GuestName,GuestPhone,VisitId,Status,PostedAt,InvoiceNo,RoomSecurity,PaymentMethod
 FROM LatestRows WHERE rn=1
 AND (@Search='' OR GuestName LIKE @SearchLike OR GuestPhone LIKE @SearchLike OR RegId LIKE @SearchLike)
 AND (@FromDate IS NULL OR PostedAt>=@FromDate)
 AND (@ToDateExclusive IS NULL OR PostedAt<@ToDateExclusive)
) ";

    // Same charge selection as CheckInService.PostToFbrAsync; never aggregate
    // all payments for a hotel before filtering by the report's reservation IDs.
    private const string EligibleInvoiceAmountsApply = @"
OUTER APPLY (
 SELECT COUNT_BIG(1) ChargeCount,
        COALESCE(SUM(TRY_CONVERT(decimal(18,2), pay.totalamount)),0) InvoiceTotal,
        COALESCE(SUM(COALESCE(TRY_CONVERT(decimal(18,2), pay.GST),0)),0) TaxAmount
 FROM dbo.payments pay
 WHERE pay.hotel_id=@HotelId AND pay.reg_id=p.RegId
   AND ISNULL(pay.descr,'')<>'Refund'
   AND ISNULL(pay.Type,'') NOT IN ('Refund','Discount Offers')
   AND ISNULL(TRY_CONVERT(decimal(18,2),pay.totalamount),0)>0
) inv";

    public async Task VerifyHotelMembershipAsync(string hotelId, string userId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(hotelId) || string.IsNullOrWhiteSpace(userId))
            throw new UnauthorizedAccessException("Hotel session is missing.");
        await using var cn = new SqlConnection(_connectionString);
        await cn.OpenAsync(ct);
        // Match the master PMS hotel-access rules: assigned hotel, cross-property
        // users (hotel_id=-1), and explicit UserHotelAccess grants.
        await using var cmd = new SqlCommand(
            @"SELECT TOP(1) 1
FROM dbo.Hms_accounts a
WHERE CONVERT(varchar(100),a.user_id)=@u
  AND UPPER(LTRIM(RTRIM(ISNULL(a.activestatus,''))))='ACTIVE'
  AND (
       CONVERT(varchar(100),a.hotel_id)=@h
    OR CONVERT(varchar(100),a.hotel_id)='-1'
    OR EXISTS (
        SELECT 1 FROM dbo.UserHotelAccess uha
        WHERE CONVERT(varchar(100),uha.userid)=@u
          AND CONVERT(varchar(100),uha.HotelId)=@h
    )
  );", cn);
        cmd.Parameters.Add("@h", SqlDbType.VarChar, 50).Value = hotelId;
        cmd.Parameters.Add("@u", SqlDbType.VarChar, 100).Value = userId;
        if (await cmd.ExecuteScalarAsync(ct) is null)
            throw new UnauthorizedAccessException("You do not have access to this hotel's fiscal reports.");
    }

    private static void AddFilters(SqlCommand cmd, string hotelId, FbrFailedInvoiceReportViewModel f)
    {
        var search = (f.Search ?? "").Trim();
        if (search.Length > 120) search = search[..120];
        cmd.Parameters.Add("@HotelId", SqlDbType.VarChar, 50).Value = hotelId;
        cmd.Parameters.Add("@Search", SqlDbType.NVarChar, 120).Value = search;
        cmd.Parameters.Add("@SearchLike", SqlDbType.NVarChar, 250).Value = "%" + search.Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]") + "%";
        cmd.Parameters.Add("@FromDate", SqlDbType.DateTime).Value = f.From.HasValue ? (object)f.From.Value.Date : DBNull.Value;
        cmd.Parameters.Add("@ToDateExclusive", SqlDbType.DateTime).Value = f.To.HasValue ? (object)f.To.Value.Date.AddDays(1) : DBNull.Value;
        cmd.Parameters.Add("@Status", SqlDbType.VarChar, 16).Value = f.Status;
    }

    public async Task<FbrFailedInvoiceReportViewModel> SearchAsync(string hotelId,
        FbrFailedInvoiceReportViewModel filter, CancellationToken ct)
    {
        filter.Status = (filter.Status ?? "ALL").Trim().ToUpperInvariant();
        if (filter.Status != "POSTED" && filter.Status != "FAILED") filter.Status = "ALL";
        filter.Page = Math.Clamp(filter.Page, 1, 100000);
        if (filter.From > filter.To) throw new ArgumentException("From date cannot be after To date.");
        filter.PageSize = 25;
        await using var cn = new SqlConnection(_connectionString);
        await cn.OpenAsync(ct);
        await using (var cmd = new SqlCommand(SourceSql + @"
SELECT COUNT(*) TotalRows,
 COALESCE(SUM(CASE WHEN p.Status='FAILED' THEN 1 ELSE 0 END),0) FailedRows,
 COALESCE(SUM(CASE WHEN p.Status='POSTED' THEN 1 ELSE 0 END),0) PostedRows,
 COALESCE(SUM(CASE WHEN @Status='ALL' OR p.Status=@Status THEN 1 ELSE 0 END),0) MatchingRows,
 COALESCE(SUM(CASE WHEN @Status='ALL' OR p.Status=@Status
    THEN COALESCE(inv.InvoiceTotal,0) ELSE 0 END),0) TotalInvoiceAmount,
 COALESCE(SUM(CASE WHEN @Status='ALL' OR p.Status=@Status
    THEN COALESCE(inv.TaxAmount,0) ELSE 0 END),0) TotalTaxAmount
FROM Filtered p
" + EligibleInvoiceAmountsApply + ";", cn))
        {
            AddFilters(cmd, hotelId, filter);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            if (await rd.ReadAsync(ct))
            {
                filter.Failed = rd.IsDBNull(1) ? 0 : Convert.ToInt32(rd.GetValue(1));
                filter.Posted = rd.IsDBNull(2) ? 0 : Convert.ToInt32(rd.GetValue(2));
                filter.Matching = rd.IsDBNull(3) ? 0 : Convert.ToInt32(rd.GetValue(3));
                filter.TotalInvoiceAmount = rd.IsDBNull(4) ? 0 : Convert.ToDecimal(rd.GetValue(4));
                filter.TotalTaxAmount = rd.IsDBNull(5) ? 0 : Convert.ToDecimal(rd.GetValue(5));
            }
        }
        filter.Page = Math.Min(filter.Page, filter.Pages);
        await using (var cmd = new SqlCommand(SourceSql + @"
SELECT p.RegId, CASE WHEN p.GuestName='' THEN '(Guest name unavailable)' ELSE p.GuestName END GuestName,
 p.GuestPhone,p.VisitId,p.RoomSecurity,
 ISNULL(CONVERT(nvarchar(100),b.PaidAmount),'') PaidAmount,
 ISNULL(CONVERT(nvarchar(100),CASE WHEN b.RemainingAmount=0 AND b.GrandTotal>0
   THEN b.GrandTotal-b.PaidAmount ELSE b.RemainingAmount END),'') RemainingAmount,
 p.PaymentMethod,p.Status,p.PostedAt,p.InvoiceNo,
 CASE WHEN inv.ChargeCount>0 THEN inv.InvoiceTotal END InvoiceTotal,
 CASE WHEN inv.ChargeCount>0 THEN inv.TaxAmount END TaxAmount
FROM (SELECT * FROM Filtered WHERE @Status='ALL' OR Status=@Status
 ORDER BY PostedAt DESC,RegId DESC OFFSET @Skip ROWS FETCH NEXT @Take ROWS ONLY) p
" + EligibleInvoiceAmountsApply + @"
OUTER APPLY (SELECT TOP(1)
 COALESCE(TRY_CONVERT(decimal(18,2),grand_total),0) GrandTotal,
 COALESCE(TRY_CONVERT(decimal(18,2),paid_amount),0) PaidAmount,
 COALESCE(TRY_CONVERT(decimal(18,2),remaining_amount),0) RemainingAmount
 FROM dbo.PaymentsUpdateTB WHERE hotel_id=@HotelId AND reg_id=p.RegId ORDER BY id DESC) b
ORDER BY p.PostedAt DESC,p.RegId DESC;", cn))
        {
            AddFilters(cmd, hotelId, filter);
            cmd.Parameters.Add("@Skip", SqlDbType.Int).Value = (filter.Page - 1) * filter.PageSize;
            cmd.Parameters.Add("@Take", SqlDbType.Int).Value = filter.PageSize;
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) filter.Rows.Add(new FbrInvoiceReportRow
            {
                RegId = ReadString(rd, "RegId"), GuestName = ReadString(rd, "GuestName"),
                GuestPhone = ReadString(rd, "GuestPhone"), VisitId = ReadString(rd, "VisitId"),
                RoomSecurity = ReadString(rd, "RoomSecurity"), PaidAmount = ReadString(rd, "PaidAmount"),
                RemainingAmount = ReadString(rd, "RemainingAmount"), PaymentMethod = ReadString(rd, "PaymentMethod"),
                Status = ReadString(rd, "Status"), InvoiceNo = ReadString(rd, "InvoiceNo"),
                InvoiceTotal = rd.IsDBNull(rd.GetOrdinal("InvoiceTotal")) ? null : Convert.ToDecimal(rd["InvoiceTotal"]),
                TaxAmount = rd.IsDBNull(rd.GetOrdinal("TaxAmount")) ? null : Convert.ToDecimal(rd["TaxAmount"]),
                PostedAt = rd.IsDBNull(rd.GetOrdinal("PostedAt")) ? null : rd.GetDateTime(rd.GetOrdinal("PostedAt"))
            });
        }
        return filter;
    }

    public async Task<byte[]> ExportCsvAsync(string hotelId,
        FbrFailedInvoiceReportViewModel filter, CancellationToken ct)
    {
        filter.Status = (filter.Status ?? "ALL").Trim().ToUpperInvariant();
        if (filter.Status != "POSTED" && filter.Status != "FAILED") filter.Status = "ALL";
        if (filter.From > filter.To) throw new ArgumentException("From date cannot be after To date.");
        var csv = new StringBuilder("Reservation ID,Guest,Phone,Status,Last Posted,Invoice Number,Invoice Total,Tax\r\n");
        decimal exportInvoiceSum = 0m, exportTaxSum = 0m;
        await using var cn = new SqlConnection(_connectionString);
        await cn.OpenAsync(ct);
        await using var cmd = new SqlCommand(SourceSql + @"
SELECT p.RegId,p.GuestName,p.GuestPhone,p.Status,p.PostedAt,p.InvoiceNo,
 CASE WHEN inv.ChargeCount>0 THEN inv.InvoiceTotal END InvoiceTotal,
 CASE WHEN inv.ChargeCount>0 THEN inv.TaxAmount END TaxAmount
FROM (SELECT TOP (50000) * FROM Filtered WHERE @Status='ALL' OR Status=@Status
 ORDER BY PostedAt DESC,RegId DESC) p
" + EligibleInvoiceAmountsApply + @"
ORDER BY p.PostedAt DESC,p.RegId DESC;", cn);
        AddFilters(cmd, hotelId, filter);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            var invoice = rd.IsDBNull(rd.GetOrdinal("InvoiceTotal")) ? (decimal?)null : Convert.ToDecimal(rd["InvoiceTotal"]);
            var tax = rd.IsDBNull(rd.GetOrdinal("TaxAmount")) ? (decimal?)null : Convert.ToDecimal(rd["TaxAmount"]);
            exportInvoiceSum += invoice ?? 0m;
            exportTaxSum += tax ?? 0m;
            var values = new[] { ReadString(rd,"RegId"),ReadString(rd,"GuestName"),ReadString(rd,"GuestPhone"),
                ReadString(rd,"Status"),ReadString(rd,"PostedAt"),ReadString(rd,"InvoiceNo"),
                invoice?.ToString("F2",CultureInfo.InvariantCulture) ?? "",
                tax?.ToString("F2",CultureInfo.InvariantCulture) ?? "" };
            csv.AppendLine(string.Join(',', values.Select(CsvCell)));
        }
        // Export is capped at 50,000 rows as before. This footer totals only exported rows.
        csv.AppendLine(string.Join(',', new[] { "Exported rows total", "", "", "", "", "",
            exportInvoiceSum.ToString("F2",CultureInfo.InvariantCulture),
            exportTaxSum.ToString("F2",CultureInfo.InvariantCulture) }.Select(CsvCell)));
        return new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(csv.ToString())).ToArray();
    }

    private static string CsvCell(string? value)
    {
        var text = (value ?? "").Replace('\r',' ').Replace('\n',' ');
        if (text.Length > 0 && "=+-@\t".Contains(text[0])) text = "'" + text;
        return "\"" + text.Replace("\"", "\"\"") + "\"";
    }

    private static bool IsValidInvoice(string? invoice)
    {
        var value=(invoice??"").Trim();
        if (value.Length==0 || value.Contains("*Test*", StringComparison.OrdinalIgnoreCase)) return false;
        var normalized=value.Replace(" ","").Replace("-","").ToLowerInvariant();
        return normalized is not ("notfound" or "notavailable" or "na" or "n/a" or "null");
    }

    private static async Task<bool> HasInvoiceAsync(SqlConnection cn, string hotelId, string regId, CancellationToken ct)
    {
        await using var cmd=new SqlCommand(@"
SELECT fbr_invoice_no FROM dbo.GuestInformationLogTB WHERE hotel_id=@HotelId AND reg_id=@RegId
UNION ALL
SELECT fbr_invoice_no FROM dbo.NewReservationsTB WHERE hotel_id=@HotelId AND reg_id=@RegId;",cn);
        cmd.Parameters.Add("@HotelId",SqlDbType.VarChar,50).Value=hotelId;
        cmd.Parameters.Add("@RegId",SqlDbType.VarChar,60).Value=regId;
        await using var rd=await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
            if (!rd.IsDBNull(0) && IsValidInvoice(Convert.ToString(rd.GetValue(0)))) return true;
        return false;
    }

    private async Task<FbrRetryContext> ReadRetryContextAsync(SqlConnection cn,
        string hotelId, string regId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(regId) || regId.Length>60)
            throw new ArgumentException("Reservation ID is invalid.");
        await using var cmd = new SqlCommand(@"
SELECT TOP(1) CONVERT(nvarchar(60),reg_id) RegId,
 LTRIM(RTRIM(CONCAT(ISNULL(GuestName,''),' ',ISNULL(LastName,'')))) GuestName,
 UPPER(LTRIM(RTRIM(ISNULL(fbr_post_status,'')))) Status,
 ISNULL(CONVERT(nvarchar(100),visit_id),'') VisitId
FROM dbo.GuestInformationLogTB WHERE hotel_id=@h AND reg_id=@r
ORDER BY fbr_posted_at DESC, ID DESC;",cn);
        cmd.Parameters.Add("@h",SqlDbType.VarChar,50).Value=hotelId;
        cmd.Parameters.Add("@r",SqlDbType.VarChar,60).Value=regId;
        await using var rd=await cmd.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct)) throw new InvalidOperationException("Reservation was not found for this hotel.");
        var result=new FbrRetryContext {
            RegId=ReadString(rd,"RegId"),GuestName=ReadString(rd,"GuestName"),
            Status=ReadString(rd,"Status"), VisitId=ReadString(rd,"VisitId")
        };
        await rd.CloseAsync();
        if (result.Status!="FAILED") throw new InvalidOperationException("Only failed invoices can be retried.");
        if (await HasInvoiceAsync(cn, hotelId, regId,ct))
            throw new InvalidOperationException("A valid fiscal invoice already exists. Resending is blocked.");
        // Earlier MVC checkouts used PaymentsUpdateTB / PaymentsLogTB and did not
        // persist payment_method on GuestInformationLogTB. Read the same durable
        // sources before declaring a legacy invoice unretryable. Never default to Cash.
        (result.PaymentMethod, result.PaymentMethodSource) =
            await ResolveSavedCheckoutPaymentMethodAsync(cn, hotelId, regId, ct);
        var routes=await FiscalIntegrationService.GetEnabledRoutesAsync(cn,hotelId,ct);
        result.AvailableAuthorities=routes.Select(r=>r.Authority).Distinct().ToList();
        if (result.AvailableAuthorities.Count==0)
            result.RetryBlockedReason="No enabled fiscal integration exists for this hotel.";
        else if (string.IsNullOrWhiteSpace(result.PaymentMethod))
            result.RetryBlockedReason="No recorded checkout payment method was found in the guest or payment history. Retry is blocked; do not assume Cash or Bank Transfer.";

        // Make historical uncertain attempts visible in the review dialog instead
        // of surprising the user with a permanent failure only after clicking Retry.
        await using (var table = new SqlCommand("SELECT OBJECT_ID(N'dbo.FbrInvoiceRetryLogTB',N'U')",cn))
        {
            var tableId=await table.ExecuteScalarAsync(ct);
            if (tableId is not null && tableId!=DBNull.Value)
            {
                await using var pending = new SqlCommand(@"
SELECT TOP(1) ISNULL(ResponseSummary,'')
FROM dbo.FbrInvoiceRetryLogTB
WHERE HotelId=@h AND RegId=@r AND ResultStatus IN ('Started','Needs Review')
ORDER BY Id DESC;", cn);
                pending.Parameters.Add("@h",SqlDbType.VarChar,50).Value=hotelId;
                pending.Parameters.Add("@r",SqlDbType.VarChar,60).Value=regId;
                var summary=await pending.ExecuteScalarAsync(ct);
                if(summary is not null)
                {
                    result.NeedsPreviousAttemptReconciliation=true;
                    result.PreviousAttemptSummary=Convert.ToString(summary)?.Trim()??"";
                    if(result.PreviousAttemptSummary.Length>350)
                        result.PreviousAttemptSummary=result.PreviousAttemptSummary[..350]+"…";
                }
            }
        }
        return result;
    }


    // Checkout currently resolves payment method from these same persisted sources.
    // Prefer an explicit method saved with the checked-out guest; for legacy
    // rows, use the latest nonempty payment snapshot / log entry.
    private static async Task<(string Method, string Source)> ResolveSavedCheckoutPaymentMethodAsync(
        SqlConnection cn, string hotelId, string regId, CancellationToken ct)
    {
        await using (var guest = new SqlCommand(@"
SELECT TOP (1) LTRIM(RTRIM(CONVERT(nvarchar(100),payment_method)))
FROM dbo.GuestInformationLogTB
WHERE hotel_id=@h AND reg_id=@r
 AND NULLIF(LTRIM(RTRIM(CONVERT(nvarchar(100),payment_method))), '') IS NOT NULL
ORDER BY CASE WHEN LOWER(LTRIM(RTRIM(ISNULL(res_status,''))))
 IN ('check out','checkout','checked out') THEN 0 ELSE 1 END,
 ID DESC;", cn))
        {
            guest.Parameters.Add("@h",SqlDbType.VarChar,50).Value=hotelId;
            guest.Parameters.Add("@r",SqlDbType.VarChar,60).Value=regId;
            var saved=(Convert.ToString(await guest.ExecuteScalarAsync(ct))??"").Trim();
            if(saved.Length>0) return (saved,"Checkout guest record");
        }
        await using var history = new SqlCommand(@"
SELECT TOP(1) payment_method, source_order
FROM
(
 SELECT LTRIM(RTRIM(CONVERT(nvarchar(100), payment_method))) payment_method,
        TRY_CONVERT(datetime, currentdate) recorded_at, 1 source_order, id
 FROM dbo.PaymentsUpdateTB
 WHERE hotel_id=@h AND reg_id=@r
   AND NULLIF(LTRIM(RTRIM(CONVERT(nvarchar(100),payment_method))), '') IS NOT NULL
 UNION ALL
 SELECT LTRIM(RTRIM(CONVERT(nvarchar(100), payment_method))),
        TRY_CONVERT(datetime, currentdate), 2, id
 FROM dbo.PaymentsLogTB
 WHERE hotel_id=@h AND reg_id=@r
   AND NULLIF(LTRIM(RTRIM(CONVERT(nvarchar(100),payment_method))), '') IS NOT NULL
) x
ORDER BY recorded_at DESC, source_order, id DESC;", cn);
        history.Parameters.Add("@h",SqlDbType.VarChar,50).Value=hotelId;
        history.Parameters.Add("@r",SqlDbType.VarChar,60).Value=regId;
        await using var rd=await history.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct)) return ("","");
        var method=rd.IsDBNull(0)?"":(Convert.ToString(rd.GetValue(0))??"").Trim();
        var source=Convert.ToInt32(rd.GetValue(1))==1 ? "Payment summary" : "Payment log";
        return method.Length>0 ? (method,source) : ("","");
    }

    public async Task<FbrRetryContext> GetRetryContextAsync(string hotelId, string regId, CancellationToken ct)
    {
        await using var cn=new SqlConnection(_connectionString);
        await cn.OpenAsync(ct);
        return await ReadRetryContextAsync(cn,hotelId,regId,ct);
    }

    private static string ReadString(SqlDataReader rd,string name)
    {
        var i=rd.GetOrdinal(name);
        return rd.IsDBNull(i) ? "" : Convert.ToString(rd.GetValue(i),CultureInfo.InvariantCulture) ?? "";
    }

    /// <summary>
    /// Mirrors WebForms safe-retry workflow: session app lock, revalidation,
    /// mandatory audit entry BEFORE the external HTTP call, and no charge edits.
    /// The actual JSON posting is delegated to the SAME CheckInService FBR method
    /// as normal checkout, so there are not two competing fiscal payloads.
    /// </summary>
    public async Task<string> RetryAsync(string hotelId, string userId, string userName,
        string ip, string regId, string authority, bool confirmedNoInvoice, CancellationToken ct)
    {
        await using var cn=new SqlConnection(_connectionString);
        await cn.OpenAsync(ct);
        var lockName="ORA:FiscalRetry:" + hotelId + ":" + regId;
        bool locked=false;
        long auditId=0;
        try
        {
            await using(var acquire=new SqlCommand(@"
DECLARE @result int;
EXEC @result=sys.sp_getapplock @Resource=@resource,@LockMode='Exclusive',@LockOwner='Session',@LockTimeout=0;
SELECT @result;",cn))
            {
                acquire.Parameters.Add("@resource",SqlDbType.NVarChar,255).Value=lockName;
                locked=Convert.ToInt32(await acquire.ExecuteScalarAsync(ct))>=0;
            }
            if(!locked) throw new InvalidOperationException("This invoice is already being retried.");
            var current=await ReadRetryContextAsync(cn,hotelId,regId,ct);
            if (!string.IsNullOrWhiteSpace(current.RetryBlockedReason))
                throw new InvalidOperationException(current.RetryBlockedReason);
            // The submitted authority selects only among enabled database routes.
            var route=await FiscalIntegrationService.ResolveCheckoutRouteAsync(cn,hotelId,authority,ct);
            if(route==null || (FiscalIntegrationService.NormalizeAuthority(authority).Length>0 &&
                route.Authority!=FiscalIntegrationService.NormalizeAuthority(authority)))
                throw new InvalidOperationException("The selected fiscal route is not enabled.");
            if(route.PosId<=0 || route.InvoiceUrl.Length==0)
                throw new InvalidOperationException("Configured fiscal route has an invalid POS ID or URL.");

            // Database administrator runs the provided script once; never issue a POST
            // without an audit table that can track uncertain provider responses.
            await using(var check=new SqlCommand("SELECT OBJECT_ID(N'dbo.FbrInvoiceRetryLogTB',N'U')",cn))
            {
                var auditTable=await check.ExecuteScalarAsync(ct);
                if(auditTable==null || auditTable==DBNull.Value)
                    throw new InvalidOperationException("Fiscal retry audit table is missing. Run docs/Install_FbrRetryAudit.sql before enabling retries.");
            }
            // Any prior Started/Needs Review record requires explicit user
            // attestation that the fiscal portal has no accepted invoice.
            // Preserve the old audit history with the reconciling user/time.
            await using(var pending=new SqlCommand(@"
SELECT TOP(1) Id FROM dbo.FbrInvoiceRetryLogTB
WHERE HotelId=@h AND RegId=@r AND ResultStatus IN ('Started','Needs Review')
ORDER BY Id DESC;",cn))
            {
                pending.Parameters.Add("@h",SqlDbType.VarChar,50).Value=hotelId;
                pending.Parameters.Add("@r",SqlDbType.VarChar,60).Value=regId;
                var prior = await pending.ExecuteScalarAsync(ct);
                if(prior is not null)
                {
                    if(!confirmedNoInvoice)
                        throw new InvalidOperationException("A prior fiscal submission has an unknown outcome. Verify the fiscal portal and tick the reconciliation confirmation before retrying.");
                    // A second invoice check under the SQL application lock.
                    if(await HasInvoiceAsync(cn,hotelId,regId,ct))
                        throw new InvalidOperationException("A fiscal invoice number now exists; retry is blocked.");
                    await using var reconcile = new SqlCommand(@"
UPDATE dbo.FbrInvoiceRetryLogTB
SET ResultStatus='Reconciled',
    CompletedAt=ISNULL(CompletedAt,GETDATE()),
    ResponseSummary=LEFT(CONCAT(ISNULL(ResponseSummary,''),
      ' | Fiscal portal checked: no invoice accepted. Reconciled by user ',@user,
      ' at ',CONVERT(varchar(19),GETUTCDATE(),120),' UTC.'),4000)
WHERE HotelId=@h AND RegId=@r AND ResultStatus IN ('Started','Needs Review');",cn);
                    reconcile.Parameters.Add("@h",SqlDbType.VarChar,50).Value=hotelId;
                    reconcile.Parameters.Add("@r",SqlDbType.VarChar,60).Value=regId;
                    reconcile.Parameters.Add("@user",SqlDbType.VarChar,100).Value=userId;
                    await reconcile.ExecuteNonQueryAsync(ct);
                    _log.LogWarning("User {UserId} reconciled uncertain fiscal retries for hotel {HotelId} reservation {RegId} based on portal verification",userId,hotelId,regId);
                }
            }
            await using(var create=new SqlCommand(@"
INSERT INTO dbo.FbrInvoiceRetryLogTB
(HotelId,RegId,RequestedBy,FiscalAuthority,PaymentMethod,StartedAt,ResultStatus)
OUTPUT INSERTED.Id
VALUES(@h,@r,@u,@a,@m,GETDATE(),'Preparing');",cn))
            {
                create.Parameters.Add("@h",SqlDbType.VarChar,50).Value=hotelId;
                create.Parameters.Add("@r",SqlDbType.VarChar,60).Value=regId;
                create.Parameters.Add("@u",SqlDbType.VarChar,100).Value=userId;
                create.Parameters.Add("@a",SqlDbType.NVarChar,30).Value=route.Authority;
                create.Parameters.Add("@m",SqlDbType.NVarChar,50).Value=current.PaymentMethod;
                auditId=Convert.ToInt64(await create.ExecuteScalarAsync(ct));
            }
            // A TCP test before the real HTTP request lets us prove a failed
            // connection was NOT sent. This probe never posts an invoice.
            var (reachable, connectionError) = await ProbeFiscalEndpointAsync(route.InvoiceUrl,ct);
            if(!reachable)
            {
                await FinishAuditAsync(cn,auditId,"Not Sent","",
                    "TCP connection check failed before any HTTP invoice POST. "+connectionError,CancellationToken.None);
                auditId=0;
                throw new InvalidOperationException("Fiscal server is unreachable. No invoice was submitted by this retry. " + connectionError);
            }
            await using(var markStarted=new SqlCommand(@"
UPDATE dbo.FbrInvoiceRetryLogTB SET ResultStatus='Started' WHERE Id=@id AND ResultStatus='Preparing';",cn))
            {
                markStarted.Parameters.Add("@id",SqlDbType.BigInt).Value=auditId;
                if(await markStarted.ExecuteNonQueryAsync(ct)!=1)
                    throw new InvalidOperationException("Unable to record fiscal send-start state; request was not sent.");
            }
            // Audit is now Started. Only after this point may an actual HTTP
            // invoice POST be made. CheckInService performs another invoice check.
            var result=await _checkIn.PostToFbrAsync(hotelId,userId,userName,ip,new FbrPostRequest
            {
                RegId=regId, VisitId=current.VisitId, PaymentMethod=current.PaymentMethod,
                FiscalAuthority=route.Authority
            },ct);
            var invoiceNo=await ReadValidInvoiceAsync(cn,hotelId,regId,ct);
            var confirmed=result.Success && invoiceNo.Length>0;
            var notSent = !confirmed && result.Data is string marker && marker=="Not Sent";
            var finalStatus=confirmed ? "Posted" : notSent ? "Not Sent" : "Needs Review";
            await FinishAuditAsync(cn,auditId,finalStatus,invoiceNo,result.Message,ct);
            auditId=0; // Completed status must not be overwritten by catch.
            if(!confirmed)
                throw new InvalidOperationException(result.Message + (notSent
                    ? " No fiscal invoice was sent. Retry after restoring connectivity or correcting the request."
                    : " The outcome may be uncertain; verify the fiscal portal before another retry."));
            return result.Message + " Invoice: " + invoiceNo;
        }
        catch(Exception ex)
        {
            if(auditId!=0)
            {
                try {
                    // An attempt still marked Preparing has never been handed
                    // to CheckInService for HTTP transmission.
                    var state = await ReadAuditStateAsync(cn,auditId);
                    await FinishAuditAsync(cn,auditId,state=="Preparing" ? "Not Sent" : "Needs Review",
                        "",ex.Message,CancellationToken.None);
                }
                catch(Exception auditEx){_log.LogError(auditEx,"Could not finalize retry audit {AuditId}",auditId);}
            }
            throw;
        }
        finally
        {
            if(locked && cn.State==ConnectionState.Open)
            {
                try
                {
                    await using var release=new SqlCommand("EXEC sys.sp_releaseapplock @Resource=@resource,@LockOwner='Session';",cn);
                    release.Parameters.Add("@resource",SqlDbType.NVarChar,255).Value=lockName;
                    await release.ExecuteNonQueryAsync(CancellationToken.None);
                }
                catch(Exception ex){_log.LogWarning(ex,"Failed to release fiscal retry lock; SQL session close will release it.");}
            }
        }
    }

    private static async Task<string> ReadAuditStateAsync(SqlConnection cn,long auditId)
    {
        await using var cmd=new SqlCommand("SELECT ResultStatus FROM dbo.FbrInvoiceRetryLogTB WHERE Id=@id",cn);
        cmd.Parameters.Add("@id",SqlDbType.BigInt).Value=auditId;
        return Convert.ToString(await cmd.ExecuteScalarAsync(CancellationToken.None))??"";
    }

    // A failed connect means the HTTP invoice POST has not even begun. A
    // successful probe does NOT prove a subsequent HTTP POST succeeded; those
    // later errors must still be handled as potentially delivered.
    private static async Task<(bool Connected,string Failure)> ProbeFiscalEndpointAsync(string url,CancellationToken ct)
    {
        if(!Uri.TryCreate(url,UriKind.Absolute,out var uri) ||
            (uri.Scheme!=Uri.UriSchemeHttp && uri.Scheme!=Uri.UriSchemeHttps))
            return (false,"Invalid configured fiscal endpoint URL.");
        using var tcp=new TcpClient();
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            await tcp.ConnectAsync(uri.Host,uri.Port,timeout.Token);
            return (true,"");
        }
        catch(OperationCanceledException) when(!ct.IsCancellationRequested)
        {
            return (false,"TCP connection timed out at "+uri.Host+":"+uri.Port+" (5 seconds).");
        }
        catch(SocketException ex)
        {
            return (false,"TCP connection to "+uri.Host+":"+uri.Port+" failed: "+ex.SocketErrorCode+".");
        }
        // Request cancellation (e.g. app shutdown) is allowed to bubble out;
        // RetryAsync records any still-Preparing attempt as Not Sent.
    }

    private static async Task<string> ReadValidInvoiceAsync(SqlConnection cn,string hotelId,string regId,CancellationToken ct)
    {
        await using var cmd=new SqlCommand(@"
SELECT fbr_invoice_no FROM dbo.GuestInformationLogTB WHERE hotel_id=@h AND reg_id=@r
UNION ALL
SELECT fbr_invoice_no FROM dbo.NewReservationsTB WHERE hotel_id=@h AND reg_id=@r;",cn);
        cmd.Parameters.Add("@h",SqlDbType.VarChar,50).Value=hotelId;
        cmd.Parameters.Add("@r",SqlDbType.VarChar,60).Value=regId;
        await using var rd=await cmd.ExecuteReaderAsync(ct);
        while(await rd.ReadAsync(ct))
        {
            if(rd.IsDBNull(0)) continue;
            var candidate=Convert.ToString(rd.GetValue(0))?.Trim()??"";
            if(IsValidInvoice(candidate)) return candidate;
        }
        return "";
    }

    private static async Task FinishAuditAsync(SqlConnection cn,long auditId,string status,
        string invoiceNo,string response,CancellationToken ct)
    {
        await using var cmd=new SqlCommand(@"
UPDATE dbo.FbrInvoiceRetryLogTB SET CompletedAt=GETDATE(),ResultStatus=@status,
 InvoiceNo=@invoiceNo,ResponseSummary=@summary WHERE Id=@id;",cn);
        cmd.Parameters.Add("@id",SqlDbType.BigInt).Value=auditId;
        cmd.Parameters.Add("@status",SqlDbType.NVarChar,50).Value=status;
        cmd.Parameters.Add("@invoiceNo",SqlDbType.NVarChar,100).Value=invoiceNo;
        cmd.Parameters.Add("@summary",SqlDbType.NVarChar,4000).Value=response.Length>4000?response[..4000]:response;
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
