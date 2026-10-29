using System.Data;
using System.Globalization;
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

    public async Task VerifyHotelMembershipAsync(string hotelId, string userId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(hotelId) || string.IsNullOrWhiteSpace(userId))
            throw new UnauthorizedAccessException("Hotel session is missing.");
        await using var cn = new SqlConnection(_connectionString);
        await cn.OpenAsync(ct);
        await using var cmd = new SqlCommand(
            "SELECT TOP (1) 1 FROM dbo.Hms_accounts WHERE hotel_id=@h AND user_id=@u", cn);
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
 SUM(CASE WHEN Status='FAILED' THEN 1 ELSE 0 END) FailedRows,
 SUM(CASE WHEN Status='POSTED' THEN 1 ELSE 0 END) PostedRows,
 SUM(CASE WHEN @Status='ALL' OR Status=@Status THEN 1 ELSE 0 END) MatchingRows
FROM Filtered;", cn))
        {
            AddFilters(cmd, hotelId, filter);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            if (await rd.ReadAsync(ct))
            {
                filter.Failed = rd.IsDBNull(1) ? 0 : Convert.ToInt32(rd.GetValue(1));
                filter.Posted = rd.IsDBNull(2) ? 0 : Convert.ToInt32(rd.GetValue(2));
                filter.Matching = rd.IsDBNull(3) ? 0 : Convert.ToInt32(rd.GetValue(3));
            }
        }
        filter.Page = Math.Min(filter.Page, filter.Pages);
        await using (var cmd = new SqlCommand(SourceSql + @"
SELECT p.RegId, CASE WHEN p.GuestName='' THEN '(Guest name unavailable)' ELSE p.GuestName END GuestName,
 p.GuestPhone,p.VisitId,p.RoomSecurity,
 ISNULL(CONVERT(nvarchar(100),b.PaidAmount),'') PaidAmount,
 ISNULL(CONVERT(nvarchar(100),CASE WHEN b.RemainingAmount=0 AND b.GrandTotal>0
   THEN b.GrandTotal-b.PaidAmount ELSE b.RemainingAmount END),'') RemainingAmount,
 p.PaymentMethod,p.Status,p.PostedAt,p.InvoiceNo
FROM (SELECT * FROM Filtered WHERE @Status='ALL' OR Status=@Status
 ORDER BY PostedAt DESC,RegId DESC OFFSET @Skip ROWS FETCH NEXT @Take ROWS ONLY) p
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
        var csv = new StringBuilder("Reservation ID,Guest,Phone,Status,Last Posted,Invoice Number\r\n");
        await using var cn = new SqlConnection(_connectionString);
        await cn.OpenAsync(ct);
        await using var cmd = new SqlCommand(SourceSql + @"
SELECT TOP (50000) RegId,GuestName,GuestPhone,Status,PostedAt,InvoiceNo
FROM Filtered WHERE @Status='ALL' OR Status=@Status ORDER BY PostedAt DESC,RegId DESC;", cn);
        AddFilters(cmd, hotelId, filter);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            var values = new[] { ReadString(rd,"RegId"),ReadString(rd,"GuestName"),ReadString(rd,"GuestPhone"),
                ReadString(rd,"Status"),ReadString(rd,"PostedAt"),ReadString(rd,"InvoiceNo") };
            csv.AppendLine(string.Join(',', values.Select(CsvCell)));
        }
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
        string ip, string regId, string authority, CancellationToken ct)
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
            await using(var pending=new SqlCommand(@"
SELECT TOP(1) ResultStatus FROM dbo.FbrInvoiceRetryLogTB
WHERE HotelId=@h AND RegId=@r AND ResultStatus IN ('Started','Needs Review')
ORDER BY Id DESC;",cn))
            {
                pending.Parameters.Add("@h",SqlDbType.VarChar,50).Value=hotelId;
                pending.Parameters.Add("@r",SqlDbType.VarChar,60).Value=regId;
                if(await pending.ExecuteScalarAsync(ct) is not null)
                    throw new InvalidOperationException("An earlier fiscal retry is unconfirmed. Reconcile it with the fiscal provider before retrying again.");
            }
            await using(var create=new SqlCommand(@"
INSERT INTO dbo.FbrInvoiceRetryLogTB
(HotelId,RegId,RequestedBy,FiscalAuthority,PaymentMethod,StartedAt,ResultStatus)
OUTPUT INSERTED.Id
VALUES(@h,@r,@u,@a,@m,GETDATE(),'Started');",cn))
            {
                create.Parameters.Add("@h",SqlDbType.VarChar,50).Value=hotelId;
                create.Parameters.Add("@r",SqlDbType.VarChar,60).Value=regId;
                create.Parameters.Add("@u",SqlDbType.VarChar,100).Value=userId;
                create.Parameters.Add("@a",SqlDbType.NVarChar,30).Value=route.Authority;
                create.Parameters.Add("@m",SqlDbType.NVarChar,50).Value=current.PaymentMethod;
                auditId=Convert.ToInt64(await create.ExecuteScalarAsync(ct));
            }
            // Audit exists and reservations still failed at the moment of sending.
            // Calling CheckInService rechecks existing invoice numbers itself.
            var result=await _checkIn.PostToFbrAsync(hotelId,userId,userName,ip,new FbrPostRequest
            {
                RegId=regId, VisitId=current.VisitId, PaymentMethod=current.PaymentMethod,
                FiscalAuthority=route.Authority
            },ct);
            var invoiceNo=await ReadValidInvoiceAsync(cn,hotelId,regId,ct);
            var confirmed=result.Success && invoiceNo.Length>0;
            await FinishAuditAsync(cn,auditId,confirmed?"Posted":"Needs Review",invoiceNo,result.Message,ct);
            if(!confirmed)
                throw new InvalidOperationException(result.Message + " Confirm whether an invoice was accepted in the fiscal portal before trying again.");
            return result.Message + " Invoice: " + invoiceNo;
        }
        catch(Exception ex)
        {
            if(auditId!=0)
            {
                try { await FinishAuditAsync(cn,auditId,"Needs Review","",ex.Message,CancellationToken.None); }
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
