using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Net.Http.Headers;
using Orapmshms.Services.AvailabilityJobs;
using System.Text;
using Microsoft.Data.SqlClient;
using Orapmshms.Models;

namespace Orapmshms.Services;

/// <summary>
/// ASP.NET Core port of CheckinList.aspx.cs (Bulk Check-out).
/// Checkout rules, SQL and logs are the same as the Web Forms page. Differences (safety / speed only):
///  - hotel and user come from the signed-in session, never from Base64 query-string values;
///  - posted rooms carry only payment id + reg id; the room, visit and money are read again from the database;
///  - the listing's payments CTE is limited to the current hotel (same rows, less work);
///  - hotel time comes from IHotelClock (port of HotelTimeHelper); default date is the hotel's today;
///  - no exception text is shown to users (logged instead);
///  - security settlement, e-mail and Log_helper go through IBulkCheckoutIntegration.
/// </summary>
public sealed class BulkCheckoutService : IBulkCheckoutService
{
    private const int CommandTimeoutSeconds = 60;
    private const string PageName = "CheckinList";

    private readonly string _connectionString;
    private readonly IHotelClock _hotelClock;
    private readonly IBulkCheckoutIntegration _integration;
    private readonly ILogger<BulkCheckoutService> _logger;

    public BulkCheckoutService(
        IConfiguration configuration,
        IHotelClock hotelClock,
        IBulkCheckoutIntegration integration,
        ILogger<BulkCheckoutService> logger)
    {
        _connectionString = configuration.GetConnectionString("con")
            ?? throw new InvalidOperationException("ConnectionStrings:con is missing from configuration.");
        _hotelClock = hotelClock;
        _integration = integration;
        _logger = logger;
    }

    // =====================================================================
    // Listing (BindCheckindata)
    // =====================================================================
    public async Task<List<BulkCheckoutRow>> GetDeparturesAsync(
        string hotelId, DateTime? departureDate, string? search, CancellationToken cancellationToken = default)
    {
        var term = (search ?? string.Empty).Trim();
        if (term.Length > 100) term = term[..100];

        var condition = new StringBuilder();
        if (term.Length > 0)
        {
            condition.Append(@"
  AND (
        CAST(g.CNIC AS NVARCHAR(100)) = @cn
     OR CAST(g.Email AS NVARCHAR(255)) = @cn
     OR CAST(g.PhoneNo AS NVARCHAR(100)) = @cn
     OR g.GuestName LIKE '%' + @cn + '%'
     OR g.LastName LIKE '%' + @cn + '%'
     OR p.PayRoomNo LIKE '%' + @cn + '%'
     OR p.PayRoomCategory LIKE '%' + @cn + '%'
  )");
        }
        if (departureDate.HasValue)
            condition.Append(" AND p.PayDepartureDate = @Today");

        var sql = @"
WITH pay AS
(
    SELECT
        p.id,
        p.hotel_id,
        p.reg_id,
        p.res_status,
        LTRIM(RTRIM(p.room_no)) AS PayRoomNo,
        LTRIM(RTRIM(p.[Type])) AS PayRoomCategory,
        COALESCE(
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(p.ArrivalDate)), ''), 110),
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(p.ArrivalDate)), ''), 103),
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(p.ArrivalDate)), ''))
        ) AS PayArrivalDate,
        COALESCE(
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(p.DepartureDate)), ''), 110),
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(p.DepartureDate)), ''), 103),
            TRY_CONVERT(date, NULLIF(LTRIM(RTRIM(p.DepartureDate)), ''))
        ) AS PayDepartureDate
    FROM dbo.payments p
    WHERE p.hotel_id = @HotelId
      AND LTRIM(RTRIM(ISNULL(p.descr,''))) = 'Room Rent'
      AND LTRIM(RTRIM(ISNULL(p.res_status,''))) = 'check in'
      AND NULLIF(LTRIM(RTRIM(p.room_no)), '') IS NOT NULL
),
guestOne AS
(
    SELECT *
    FROM
    (
        SELECT g.*,
               ROW_NUMBER() OVER (PARTITION BY g.hotel_id, g.reg_id ORDER BY TRY_CONVERT(bigint, g.ID) DESC) AS rn
        FROM dbo.GuestInformationLogTB g
        WHERE g.hotel_id = @HotelId
          AND g.res_status = 'check in'
    ) x
    WHERE x.rn = 1
),
puOne AS
(
    SELECT *
    FROM
    (
        SELECT pu.*,
               ROW_NUMBER() OVER (PARTITION BY pu.hotel_id, pu.reg_id ORDER BY TRY_CONVERT(datetime, pu.currentdate) DESC) AS rn
        FROM dbo.PaymentsUpdateTB pu
        WHERE pu.hotel_id = @HotelId
    ) x
    WHERE x.rn = 1
),
securityAgg AS
(
    SELECT
        rs.hotel_id,
        rs.reg_id,
        MAX(NULLIF(LTRIM(RTRIM(rs.currency)), '')) AS security_currency,
        MAX(NULLIF(LTRIM(RTRIM(rs.payment_intent_id)), '')) AS security_payment_intent_id,
        MAX(NULLIF(LTRIM(RTRIM(rs.charge_id)), '')) AS security_charge_id,
        MAX(NULLIF(LTRIM(RTRIM(rs.payment_method)), '')) AS security_payment_method,
        SUM(COALESCE(TRY_CAST(REPLACE(NULLIF(LTRIM(RTRIM(rs.security)), ''), ',', '') AS DECIMAL(18,2)), 0)) AS security_balance
    FROM dbo.RoomSecurityTB rs
    WHERE rs.hotel_id = @HotelId
    GROUP BY rs.hotel_id, rs.reg_id
)
SELECT
    p.id,
    g.visit_id,
    g.reg_id,
    g.GuestName,
    g.LastName,
    g.Email,
    g.PhoneNo,
    CONVERT(varchar(10), p.PayArrivalDate, 110) AS ArrivalDate,
    CONVERT(varchar(10), p.PayDepartureDate, 110) AS DepartureDate,
    p.PayRoomNo,
    p.PayRoomCategory,
    COALESCE(TRY_CAST(pu.grand_total AS DECIMAL(18,2)), 0) AS grand_total,
    COALESCE(TRY_CAST(pu.paid_amount AS DECIMAL(18,2)), 0) AS paid_amount,
    COALESCE(TRY_CAST(pu.remaining_amount AS DECIMAL(18,2)), 0) AS remaining_amount,
    CASE
        WHEN sec.reg_id IS NOT NULL THEN
            CASE WHEN COALESCE(sec.security_balance, 0) < 0 THEN 0 ELSE COALESCE(sec.security_balance, 0) END
        ELSE COALESCE(TRY_CAST(pu.room_security AS DECIMAL(18,2)), TRY_CAST(g.room_security AS DECIMAL(18,2)), 0)
    END AS room_security_balance,
    COALESCE(sec.security_currency, 'gbp') AS security_currency,
    CASE
        WHEN COALESCE(sec.security_payment_intent_id, '') <> '' THEN 'Card'
        WHEN COALESCE(sec.security_charge_id, '') <> '' THEN 'Card'
        WHEN LOWER(COALESCE(sec.security_payment_method, '')) LIKE '%card%' THEN 'Card'
        WHEN LOWER(COALESCE(sec.security_payment_method, '')) LIKE '%stripe%' THEN 'Card'
        WHEN LOWER(COALESCE(sec.security_payment_method, '')) LIKE '%terminal%' THEN 'Card'
        WHEN LOWER(COALESCE(sec.security_payment_method, '')) LIKE '%pdq%' THEN 'Card'
        ELSE 'Cash'
    END AS security_method
FROM pay p
INNER JOIN guestOne g
        ON p.hotel_id = g.hotel_id
       AND p.reg_id = g.reg_id
LEFT JOIN puOne pu
       ON pu.reg_id = g.reg_id
      AND pu.hotel_id = g.hotel_id
LEFT JOIN securityAgg sec
       ON sec.hotel_id = g.hotel_id
      AND sec.reg_id = g.reg_id
WHERE p.hotel_id = @HotelId
  AND p.res_status = 'check in'
" + condition + @"
ORDER BY
    CASE WHEN TRY_CONVERT(int, p.PayRoomNo) IS NULL THEN 1 ELSE 0 END,
    TRY_CONVERT(int, p.PayRoomNo),
    p.PayRoomNo,
    g.GuestName ASC;";

        var rows = new List<BulkCheckoutRow>();
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var cmd = new SqlCommand(sql, connection) { CommandTimeout = CommandTimeoutSeconds };
        cmd.Parameters.Add("@HotelId", SqlDbType.NVarChar, 100).Value = hotelId;
        if (term.Length > 0) cmd.Parameters.Add("@cn", SqlDbType.NVarChar, 100).Value = term;
        if (departureDate.HasValue) cmd.Parameters.Add("@Today", SqlDbType.Date).Value = departureDate.Value.Date;

        await using var r = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await r.ReadAsync(cancellationToken))
        {
            rows.Add(new BulkCheckoutRow
            {
                PaymentId = Int(r["id"]),
                VisitId = Str(r["visit_id"]),
                RegId = Str(r["reg_id"]),
                GuestName = Str(r["GuestName"]),
                LastName = Str(r["LastName"]),
                Email = Str(r["Email"]),
                PhoneNo = Str(r["PhoneNo"]),
                ArrivalDate = Str(r["ArrivalDate"]),
                DepartureDate = Str(r["DepartureDate"]),
                RoomNo = Str(r["PayRoomNo"]),
                RoomCategory = Str(r["PayRoomCategory"]),
                GrandTotal = Dec(r["grand_total"]),
                PaidAmount = Dec(r["paid_amount"]),
                RemainingAmount = Dec(r["remaining_amount"]),
                SecurityBalance = Dec(r["room_security_balance"]),
                SecurityCurrency = Str(r["security_currency"]),
                SecurityMethod = Str(r["security_method"])
            });
        }
        return rows;
    }

    // =====================================================================
    // Bulk check-out (btnBulkCheckout_Click)
    // =====================================================================
    public async Task<BulkCheckoutResult> CheckOutAsync(
        BulkCheckinUserContext user, IReadOnlyCollection<string> roomKeys, bool autoRefundSecurity, string legacyBaseUrl,
        CancellationToken cancellationToken = default)
    {
        var result = new BulkCheckoutResult();

        var requested = new List<(int PaymentId, string RegId)>();
        var seen = new HashSet<int>();
        foreach (var raw in roomKeys.Take(500))
        {
            var parts = (raw ?? string.Empty).Split('|');
            if (parts.Length != 2) continue;
            if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var paymentId) || paymentId <= 0) continue;
            var regId = parts[1].Trim();
            if (regId.Length == 0 || regId.Length > 100) continue;
            if (seen.Add(paymentId)) requested.Add((paymentId, regId));
        }

        result.Selected = requested.Count;
        if (requested.Count == 0) return result;

        // Re-validate on the server: the room line must be in-house for THIS hotel, and reg_id must match.
        var inHouse = (await GetDeparturesAsync(user.HotelId, null, null, cancellationToken))
            .GroupBy(r => r.PaymentId).ToDictionary(g => g.Key, g => g.First());
        var allowDirtyRoom = await HasAllowDirtyAsync(user.UserId, user.HotelId, cancellationToken);

        foreach (var (paymentId, regId) in requested)
        {
            var item = new BulkCheckoutRoomResult { RegId = regId };
            if (!inHouse.TryGetValue(paymentId, out var row) || !string.Equals(row.RegId, regId, StringComparison.Ordinal))
            {
                item.Message = "Not in-house any more (already checked out or changed). Refresh the list.";
                result.Rooms.Add(item);
                continue;
            }

            item.RoomNo = row.RoomNo;
            item.GuestName = row.FullName;

            var outcome = await TryCheckoutOneAsync(user, paymentId, regId, row.VisitId, row.RoomNo,
                autoRefundSecurity, allowDirtyRoom, legacyBaseUrl, cancellationToken);
            item.Success = outcome.Ok;
            item.Message = outcome.Ok ? "Checked out." : outcome.Reason;
            item.SecurityMessage = outcome.SecurityMessage;
            if (outcome.Ok) item.InvoiceUrl = BuildInvoiceUrl(legacyBaseUrl, user.HotelId, regId);
            result.Rooms.Add(item);
        }

        result.CheckedOut = result.Rooms.Count(r => r.Success);
        result.Failed = result.Rooms.Count - result.CheckedOut;
        result.AutoSecurity = result.Rooms.Count(r => r.SecurityMessage.Length > 0);

        // Same summary text as before, for Log_helper.Log.
        var sb = new StringBuilder();
        sb.AppendLine("Total Selected: " + result.Selected);
        sb.AppendLine("Checked Out   : " + result.CheckedOut);
        sb.AppendLine("Skipped/Failed: " + result.Failed);
        sb.AppendLine("Auto Security : " + result.AutoSecurity);
        foreach (var f in result.Rooms.Where(x => !x.Success).Take(25))
            sb.AppendLine("- " + f.RegId + " (Room " + f.RoomNo + "): " + f.Message);
        var summary = sb.ToString();
        _ = Task.Run(async () =>
        {
            try { await _integration.WriteActivityLogAsync(user.HotelId, user.UserId, summary); }
            catch (Exception ex) { _logger.LogWarning(ex, "Bulk checkout activity log failed for hotel {HotelId}.", user.HotelId); }
        });

        return result;
    }

    private sealed record CheckoutOutcome(bool Ok, string Reason, string SecurityMessage);

    /// <summary>Port of TryCheckoutOne.</summary>
    private async Task<CheckoutOutcome> TryCheckoutOneAsync(
        BulkCheckinUserContext user, int paymentId, string regId, string visitId, string roomNo,
        bool autoRefundSecurity, bool allowDirtyRoom, string legacyBaseUrl, CancellationToken cancellationToken)
    {
        var hotelId = user.HotelId;
        var securityMessage = string.Empty;
        try
        {
            await using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync(cancellationToken);

            // ---- guest status + security held (same query) ----
            string resStatus;
            decimal roomSecurity;
            await using (var cmd = Cmd(conn, null, @"
SELECT TOP 1
    g.res_status,
    COALESCE((
        SELECT SUM(COALESCE(TRY_CONVERT(DECIMAL(18,2), REPLACE(NULLIF(LTRIM(RTRIM(rs.security)), ''), ',', '')), 0))
        FROM dbo.RoomSecurityTB rs
        WHERE rs.hotel_id = @H
          AND rs.reg_id = @R
    ), 0) AS room_security
FROM dbo.GuestInformationLogTB g
WHERE g.hotel_id = @H
  AND g.reg_id = @R
ORDER BY TRY_CONVERT(bigint, g.ID) DESC;"))
            {
                AddKeys(cmd, hotelId, regId);
                await using var r = await cmd.ExecuteReaderAsync(cancellationToken);
                if (!await r.ReadAsync(cancellationToken)) return new(false, "Reservation not found.", securityMessage);
                resStatus = Str(r["res_status"]).ToLowerInvariant();
                roomSecurity = Dec(r["room_security"]);
            }

            if (resStatus == "reservation") return new(false, "Guest not checked in.", securityMessage);
            if (resStatus == "check out") return new(false, "Guest already checked out.", securityMessage);

            // ---- restaurant / laundry bills still open ----
            await using (var cmd = Cmd(conn, null, @"
SELECT 1
FROM POSstockout
WHERE hotel_id = @H
  AND regid = @R
  AND (@V = '' OR visitno = @V)
  AND bill_status = '1';"))
            {
                AddKeys(cmd, hotelId, regId);
                cmd.Parameters.Add("@V", SqlDbType.NVarChar, 100).Value = visitId ?? string.Empty;
                if (await cmd.ExecuteScalarAsync(cancellationToken) is not null)
                    return new(false, "Restaurant payments remaining.", securityMessage);
            }

            await using (var cmd = Cmd(conn, null, @"
SELECT 1
FROM LaundryTB
WHERE hotel_id = @H
  AND reg_id = @R
  AND status = '1';"))
            {
                AddKeys(cmd, hotelId, regId);
                if (await cmd.ExecuteScalarAsync(cancellationToken) is not null)
                    return new(false, "Laundry payments remaining.", securityMessage);
            }

            // ---- the selected room line ----
            string selectedRoomNo, selectedRoomCategory, payDepartureDate;
            decimal selectedRoomTotal;
            await using (var cmd = Cmd(conn, null, @"
SELECT TOP 1
    p.id,
    p.room_no,
    p.[Type] AS room_category,
    p.ArrivalDate,
    p.DepartureDate,
    COALESCE(TRY_CAST(p.totalamount AS DECIMAL(18,2)), 0) AS room_total
FROM dbo.payments p
WHERE p.hotel_id = @H
  AND p.reg_id = @R
  AND p.id = @PaymentId
  AND LTRIM(RTRIM(ISNULL(p.descr,''))) = 'Room Rent'
  AND LTRIM(RTRIM(ISNULL(p.res_status,''))) = 'check in';"))
            {
                AddKeys(cmd, hotelId, regId);
                cmd.Parameters.Add("@PaymentId", SqlDbType.Int).Value = paymentId;
                await using var rr = await cmd.ExecuteReaderAsync(cancellationToken);
                if (!await rr.ReadAsync(cancellationToken))
                    return new(false, "Selected room not found in payments table or already checked out.", securityMessage);
                selectedRoomNo = Str(rr["room_no"]);
                selectedRoomCategory = Str(rr["room_category"]);
                payDepartureDate = Str(rr["DepartureDate"]);
                selectedRoomTotal = Dec(rr["room_total"]);
            }

            int activeRoomCountBeforeCheckout;
            await using (var cmd = Cmd(conn, null, @"
SELECT COUNT(1)
FROM dbo.payments
WHERE hotel_id = @H
  AND reg_id = @R
  AND LTRIM(RTRIM(ISNULL(descr,''))) = 'Room Rent'
  AND LTRIM(RTRIM(ISNULL(res_status,''))) != 'check out';"))
            {
                AddKeys(cmd, hotelId, regId);
                activeRoomCountBeforeCheckout = Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
            }

            var isLastRoomForGuest = activeRoomCountBeforeCheckout <= 1;

            // ---- security on the last room (same rules) ----
            if (isLastRoomForGuest && Math.Round(roomSecurity, 2) != 0m)
            {
                // A negative security ledger is inconsistent; never silently clear it.
                if (roomSecurity < 0m)
                    return new(false, "Room security ledger has a negative balance. Please review security movements.", securityMessage);

                if (!autoRefundSecurity)
                    return new(false, "Room security not refunded.", securityMessage);

                var balanceReason = await ReservationBalanceNotClearAsync(conn, hotelId, regId, cancellationToken);
                if (balanceReason.Length > 0)
                    return new(false, balanceReason, securityMessage);

                var settle = await TryAutoRefundSecurityAsync(user, regId, visitId ?? string.Empty, cancellationToken);
                if (!settle.Ok)
                    return new(false, "Auto security refund failed: " + settle.Message, securityMessage);

                securityMessage = settle.Message;
            }

            // The last-room checkout must not proceed if a webhook or another session
            // has not finished settling security. Re-read the authoritative ledger
            // immediately before updating room/reservation statuses.
            if (isLastRoomForGuest)
            {
                await using var remainingSecurity = Cmd(conn, null, @"
SELECT COALESCE(SUM(COALESCE(
    TRY_CONVERT(decimal(18,2), REPLACE(NULLIF(LTRIM(RTRIM(security)), ''), ',', '')), 0)), 0)
FROM dbo.RoomSecurityTB
WHERE hotel_id=@H AND reg_id=@R;");
                AddKeys(remainingSecurity, hotelId, regId);
                var currentBalance = Dec(await remainingSecurity.ExecuteScalarAsync(cancellationToken));
                if (Math.Abs(currentBalance) > 0.005m)
                    return new(false,
                        "Room security is not fully settled in the ledger. Wait for the Stripe webhook or settle the remaining balance.",
                        securityMessage);
            }

            // ---- the checkout itself, one transaction ----
            var hotelNow = _hotelClock.GetHotelNow(hotelId);
            var nowTime = hotelNow.ToString("hh:mm tt", CultureInfo.InvariantCulture);
            var allRoomsCheckedOut = false;

            await using (var tx = (SqlTransaction)await conn.BeginTransactionAsync(cancellationToken))
            {
                try
                {
                    // Status-guarded: a line that is no longer 'check in' is never touched twice.
                    await using (var u = Cmd(conn, tx, @"
UPDATE dbo.payments
   SET res_status = 'check out',
       DepartureTime = @DepartureTime
 WHERE hotel_id = @H
   AND reg_id = @R
   AND id = @PaymentId
   AND LTRIM(RTRIM(ISNULL(descr,''))) = 'Room Rent'
   AND LTRIM(RTRIM(ISNULL(res_status,''))) = 'check in';"))
                    {
                        AddKeys(u, hotelId, regId);
                        u.Parameters.Add("@DepartureTime", SqlDbType.NVarChar, 20).Value = nowTime;
                        u.Parameters.Add("@PaymentId", SqlDbType.Int).Value = paymentId;
                        if (await u.ExecuteNonQueryAsync(cancellationToken) == 0)
                        {
                            await tx.RollbackAsync(cancellationToken);
                            return new(false, "Selected room already checked out or not found.", securityMessage);
                        }
                    }

                    await using (var u = Cmd(conn, tx, @"
UPDATE RoomsTB
   SET room_status = @room_status
 WHERE Hotel_id = @H
   AND LTRIM(RTRIM(room_no)) = LTRIM(RTRIM(@N))
   AND room_category = @C;"))
                    {
                        u.Parameters.Add("@H", SqlDbType.NVarChar, 100).Value = hotelId;
                        u.Parameters.Add("@room_status", SqlDbType.NVarChar, 50).Value = allowDirtyRoom ? "CheckOut" : "Available";
                        u.Parameters.Add("@N", SqlDbType.NVarChar, 50).Value = selectedRoomNo;
                        u.Parameters.Add("@C", SqlDbType.NVarChar, 200).Value = selectedRoomCategory;
                        await u.ExecuteNonQueryAsync(cancellationToken);
                    }

                    await using (var u = Cmd(conn, tx, @"
UPDATE LaundryTB
   SET customerstatus = 'check out'
 WHERE hotel_id = @H
   AND reg_id = @R;"))
                    {
                        AddKeys(u, hotelId, regId);
                        await u.ExecuteNonQueryAsync(cancellationToken);
                    }

                    await using (var chk = Cmd(conn, tx, @"
SELECT COUNT(1)
FROM dbo.payments
WHERE hotel_id = @H
  AND reg_id = @R
  AND LTRIM(RTRIM(ISNULL(descr,''))) = 'Room Rent'
  AND LTRIM(RTRIM(ISNULL(res_status,''))) != 'check out';"))
                    {
                        AddKeys(chk, hotelId, regId);
                        allRoomsCheckedOut = Convert.ToInt32(await chk.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) == 0;
                    }

                    if (allRoomsCheckedOut)
                    {
                        var balanceReason = await ReservationBalanceNotClearAsync(conn, hotelId, regId, cancellationToken, tx);
                        if (balanceReason.Length > 0)
                        {
                            await tx.RollbackAsync(cancellationToken);
                            return new(false, balanceReason, securityMessage);
                        }

                        await using (var u = Cmd(conn, tx, @"
UPDATE GuestInformationLogTB
   SET res_status = 'check out',
       DepartureTime = @T
 WHERE hotel_id = @H
   AND reg_id = @R;"))
                        {
                            AddKeys(u, hotelId, regId);
                            u.Parameters.Add("@T", SqlDbType.NVarChar, 20).Value = nowTime;
                            await u.ExecuteNonQueryAsync(cancellationToken);
                        }

                        await using (var u = Cmd(conn, tx, @"
UPDATE GuestInformation
   SET DepartureTime = @T
 WHERE hotel_id = @H
   AND reg_id = @R;"))
                        {
                            AddKeys(u, hotelId, regId);
                            u.Parameters.Add("@T", SqlDbType.NVarChar, 20).Value = nowTime;
                            await u.ExecuteNonQueryAsync(cancellationToken);
                        }

                        await using (var u = Cmd(conn, tx, @"
UPDATE PaymentsLogTB
   SET status = 'check out',
       departure_date = @D
 WHERE hotel_id = @H
   AND reg_id = @R;"))
                        {
                            AddKeys(u, hotelId, regId);
                            u.Parameters.Add("@D", SqlDbType.NVarChar, 50).Value = payDepartureDate;
                            await u.ExecuteNonQueryAsync(cancellationToken);
                        }

                        await using (var u = Cmd(conn, tx, @"
UPDATE PaymentsUpdateTB
   SET status = 'check out',
       departure_date = @D
 WHERE hotel_id = @H
   AND reg_id = @R;"))
                        {
                            AddKeys(u, hotelId, regId);
                            u.Parameters.Add("@D", SqlDbType.NVarChar, 50).Value = payDepartureDate;
                            await u.ExecuteNonQueryAsync(cancellationToken);
                        }
                    }

                    await InsertLogAsync(user, "(Update Rooms Status Check out)," + hotelId + "," + regId + "," + visitId + "," + selectedRoomNo + "," + hotelNow, CancellationToken.None);
                    await tx.CommitAsync(cancellationToken);
                }
                catch (Exception txEx)
                {
                    try { await tx.RollbackAsync(CancellationToken.None); } catch { /* connection gone */ }
                    _logger.LogError(txEx, "Bulk checkout transaction failed for hotel {HotelId}, reg {RegId}, payment {PaymentId}.", hotelId, regId, paymentId);
                    await InsertLogAsync(user, "(Bulk checkout transaction failed) " + txEx, CancellationToken.None);
                    return new(false, "Could not check out this room. Please try again.", securityMessage);
                }
            }

            // ---- after commit: same logs, e-mail route ----
            await InsertLogAsync(user,
                "(Bulk Checkout) reg_id=" + regId +
                ", payment_id=" + paymentId +
                ", room_no=" + roomNo +
                ", selected_room_total=" + selectedRoomTotal.ToString("N2", CultureInfo.InvariantCulture) +
                ", visit_id=" + visitId +
                ", hotel_id=" + hotelId +
                ", by=" + user.UserName +
                " (" + user.UserId + ") @ " + hotelNow, CancellationToken.None);

            var reservLogDescription = hotelNow.ToString("yyyy-MM-dd hh:mm tt", CultureInfo.InvariantCulture) + "," + user.UserName;
            var reservLogStatus = allRoomsCheckedOut ? "Check Out" : "Room Check Out";
            string? reviewUrl = null;
            if (allRoomsCheckedOut)
            {
                var route = await ResolveCheckoutEmailRouteAsync(hotelId, regId, legacyBaseUrl, CancellationToken.None);
                if (route.ShouldSend && !string.IsNullOrWhiteSpace(route.ReviewUrl))
                    reviewUrl = route.ReviewUrl;
                else
                    await InsertLogAsync(user, "(Bulk checkout review email skipped)," + regId + "," + hotelId + "," +
                                               route.CreatedRole + "," + route.Agency + "," + route.Reason, CancellationToken.None);
            }

            _ = Task.Run(async () =>
            {
                try { await _integration.WriteReservationLogAsync(user, regId, reservLogDescription, reservLogStatus); }
                catch (Exception ex) { _logger.LogWarning(ex, "Reservation log failed after checkout (hotel {HotelId}, reg {RegId}).", hotelId, regId); }

                if (reviewUrl is null) return;
                try { await _integration.SendCheckoutThanksEmailOnceAsync(hotelId, regId, reviewUrl); }
                catch (Exception ex) { _logger.LogWarning(ex, "Checkout e-mail failed (hotel {HotelId}, reg {RegId}).", hotelId, regId); }
            });

            return new(true, string.Empty, securityMessage);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Bulk checkout failed for hotel {HotelId}, reg {RegId}, payment {PaymentId}.", hotelId, regId, paymentId);
            await LogExceptionAsync(user, ex);
            return new(false, "Something went wrong with this room. Please try again.", securityMessage);
        }
    }

    /// <summary>Port of IsReservationBalanceClearForCheckout. Returns the reason, or "" when the balance is zero.</summary>
    private static async Task<string> ReservationBalanceNotClearAsync(
        SqlConnection conn, string hotelId, string regId, CancellationToken cancellationToken, SqlTransaction? tx = null)
    {
        decimal grand = 0m, paid = 0m, rem = 0m;
        await using (var bal = Cmd(conn, tx, @"
SELECT TOP 1
    COALESCE(TRY_CAST(grand_total AS DECIMAL(18,2)), 0) AS grand_total,
    COALESCE(TRY_CAST(paid_amount AS DECIMAL(18,2)), 0) AS paid_amount,
    COALESCE(TRY_CAST(remaining_amount AS DECIMAL(18,2)), 0) AS remaining_amount
FROM dbo.PaymentsUpdateTB
WHERE hotel_id = @H
  AND reg_id = @R
ORDER BY TRY_CONVERT(datetime, currentdate) DESC;"))
        {
            AddKeys(bal, hotelId, regId);
            await using var br = await bal.ExecuteReaderAsync(cancellationToken);
            if (await br.ReadAsync(cancellationToken))
            {
                grand = Dec(br["grand_total"]);
                paid = Dec(br["paid_amount"]);
                rem = Dec(br["remaining_amount"]);
            }
        }
        if (rem == 0m && grand > 0m) rem = grand - paid;
        return Math.Round(rem, 2) != 0m ? "This is the last room. Full reservation remaining amount is not zero." : string.Empty;
    }

    // =====================================================================
    // Security
    // =====================================================================
    private async Task<BulkCheckoutSettlementResult> TryAutoRefundSecurityAsync(
        BulkCheckinUserContext user, string regId, string visitId, CancellationToken cancellationToken)
    {
        try
        {
            var security = await GetCurrentSecurityInfoAsync(user.HotelId, regId, cancellationToken);
            if (security is null || security.Balance <= 0m)
                return new(true, "No active room security found.");

            var finalVisitId = !string.IsNullOrWhiteSpace(visitId) ? visitId : security.VisitId;
            var currency = string.IsNullOrWhiteSpace(security.Currency) ? "gbp" : security.Currency.Trim().ToLowerInvariant();
            var method = ResolveSecurityMethod(security.PaymentIntentId, security.ChargeId, security.PaymentMethod);
            var amount = Math.Round(security.Balance, 2, MidpointRounding.AwayFromZero);

            var result = await _integration.SettleSecurityDepositAsync(
                user.HotelId, regId, finalVisitId, currency, method, amount, amount,
                "Auto Settle / Refund Security during Bulk Checkout", user.UserId, user.UserName, cancellationToken);

            if (!result.Ok)
                return new(false, string.IsNullOrWhiteSpace(result.Message) ? "Security settlement failed." : result.Message);

            return new(true, "Auto refunded/released " + amount.ToString("0.00", CultureInfo.InvariantCulture) + " " + currency.ToUpperInvariant() + ".");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Auto security refund failed for hotel {HotelId}, reg {RegId}.", user.HotelId, regId);
            return new(false, "Security settlement failed.");
        }
    }

    public async Task<BulkCheckoutActionResponse> SettleSecurityAsync(
        BulkCheckinUserContext user, string regId, decimal releaseAmount, CancellationToken cancellationToken = default)
    {
        regId = (regId ?? string.Empty).Trim();
        if (regId.Length == 0 || regId.Length > 100)
            return new BulkCheckoutActionResponse { Message = "Missing reservation." };

        if (!_integration.SecuritySettlementAvailable)
            return new BulkCheckoutActionResponse { Message = "Security settlement is not connected in this page yet. Please use the security page for now." };

        // The reservation must be in-house for this hotel; the amount held is read from the database.
        var row = (await GetDeparturesAsync(user.HotelId, null, null, cancellationToken)).FirstOrDefault(r => r.RegId == regId);
        if (row is null)
            return new BulkCheckoutActionResponse { Message = "This reservation is not in-house any more. Refresh the list." };

        var security = await GetCurrentSecurityInfoAsync(user.HotelId, regId, cancellationToken);
        var hold = security?.Balance ?? 0m;
        if (hold <= 0m)
            return new BulkCheckoutActionResponse { Message = "No active room security found." };

        releaseAmount = Math.Round(releaseAmount, 2, MidpointRounding.AwayFromZero);
        if (releaseAmount < 0m)
            return new BulkCheckoutActionResponse { Message = "Release amount cannot be negative." };
        if (releaseAmount > hold)
            return new BulkCheckoutActionResponse { Message = "Release amount cannot be greater than room security." };

        var capture = hold - releaseAmount;
        var currency = string.IsNullOrWhiteSpace(security!.Currency) ? "gbp" : security.Currency.Trim().ToLowerInvariant();
        var method = ResolveSecurityMethod(security.PaymentIntentId, security.ChargeId, security.PaymentMethod);
        var visitId = !string.IsNullOrWhiteSpace(row.VisitId) ? row.VisitId : security.VisitId;

        try
        {
            var result = await _integration.SettleSecurityDepositAsync(
                user.HotelId, regId, visitId, currency, method, hold, releaseAmount,
                "Room security settlement from Bulk Checkout", user.UserId, user.UserName, cancellationToken);
            if (!result.Ok)
                return new BulkCheckoutActionResponse { Message = string.IsNullOrWhiteSpace(result.Message) ? "Security settlement failed." : result.Message };

            // Same as the old EnsureSecurityDeductionPayment call: refresh the payment totals after a deduction.
            if (capture > 0m)
                await UpdatePaymentsTotalsAsync(user.HotelId, CancellationToken.None);

            await InsertLogAsync(user, "(Bulk checkout security settlement) reg_id=" + regId + ", hold=" + hold.ToString("0.00", CultureInfo.InvariantCulture) +
                                       ", release=" + releaseAmount.ToString("0.00", CultureInfo.InvariantCulture) +
                                       ", deduct=" + capture.ToString("0.00", CultureInfo.InvariantCulture) + ", method=" + method +
                                       ", by=" + user.UserName + " (" + user.UserId + ")", CancellationToken.None);

            return new BulkCheckoutActionResponse { Ok = true, Message = "Security settled." };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Security settlement failed for hotel {HotelId}, reg {RegId}.", user.HotelId, regId);
            await LogExceptionAsync(user, ex);
            return new BulkCheckoutActionResponse { Message = "Security settlement failed." };
        }
    }

    /// <summary>Port of GetCurrentSecurityInfo (same query).</summary>
    private async Task<BulkCheckoutSecurityInfo?> GetCurrentSecurityInfoAsync(string hotelId, string regId, CancellationToken cancellationToken)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = Cmd(conn, null, @"
;WITH securityRows AS
(
    SELECT
        rs.*,
        COALESCE(
            TRY_CONVERT(decimal(18,2), REPLACE(NULLIF(LTRIM(RTRIM(CONVERT(varchar(50), rs.security))), ''), ',', '')),
            0
        ) AS security_amount
    FROM dbo.RoomSecurityTB rs
    WHERE rs.hotel_id = @HotelId
      AND rs.reg_id = @RegId
),
securityBalance AS
(
    SELECT hotel_id, reg_id, SUM(security_amount) AS security_balance
    FROM securityRows
    GROUP BY hotel_id, reg_id
),
latestSecurity AS
(
    SELECT TOP 1 visit_id, currency, payment_intent_id, charge_id, payment_method
    FROM securityRows
    WHERE security_amount > 0
    ORDER BY id DESC
)
SELECT
    COALESCE(b.security_balance, 0) AS security_balance,
    ISNULL(l.currency, 'gbp') AS currency,
    ISNULL(l.visit_id, '') AS visit_id,
    ISNULL(l.payment_intent_id, '') AS payment_intent_id,
    ISNULL(l.charge_id, '') AS charge_id,
    ISNULL(l.payment_method, '') AS payment_method
FROM securityBalance b
OUTER APPLY (SELECT * FROM latestSecurity) l;");
        cmd.Parameters.Add("@HotelId", SqlDbType.VarChar, 50).Value = hotelId;
        cmd.Parameters.Add("@RegId", SqlDbType.VarChar, 50).Value = regId;
        await using var r = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await r.ReadAsync(cancellationToken)) return null;
        return new BulkCheckoutSecurityInfo
        {
            Balance = Dec(r["security_balance"]),
            Currency = Str(r["currency"]),
            VisitId = Str(r["visit_id"]),
            PaymentIntentId = Str(r["payment_intent_id"]),
            ChargeId = Str(r["charge_id"]),
            PaymentMethod = Str(r["payment_method"])
        };
    }

    private static string ResolveSecurityMethod(string paymentIntentId, string chargeId, string paymentMethod)
    {
        if (!string.IsNullOrWhiteSpace(paymentIntentId) || !string.IsNullOrWhiteSpace(chargeId)) return "Card";
        var m = (paymentMethod ?? string.Empty).Trim().ToLowerInvariant();
        return m.Contains("card") || m.Contains("stripe") || m.Contains("terminal") || m.Contains("pdq") ? "Card" : "Cash";
    }

    /// <summary>Port of UpdatePaymentsTotals (same SQL) - used after a security deduction.</summary>
    private async Task UpdatePaymentsTotalsAsync(string hotelId, CancellationToken cancellationToken)
    {
        try
        {
            await using var con = new SqlConnection(_connectionString);
            await con.OpenAsync(cancellationToken);
            await using var cmd = new SqlCommand(@"
DECLARE @IsRoundTotal bit = 0;
SELECT @IsRoundTotal = ISNULL(isRoundTotal, 0) FROM dbo.HotelsSignUpTB WHERE hotel_id = @HotelId;
;WITH Totals AS
(
    SELECT p.hotel_id, p.reg_id,
           GrandTotal = CAST(SUM(ISNULL(TRY_CONVERT(decimal(18,2), p.totalamount), 0)) AS decimal(18,2))
    FROM dbo.payments p
    WHERE p.hotel_id = @HotelId
    GROUP BY p.hotel_id, p.reg_id
),
Paid AS
(
    SELECT l.hotel_id, l.reg_id,
           PaidAmount = CAST(SUM(ISNULL(TRY_CONVERT(decimal(18,2), l.paid_amount), 0)) AS decimal(18,2))
    FROM dbo.PaymentsLogTB l
    WHERE l.hotel_id = @HotelId
    GROUP BY l.hotel_id, l.reg_id
),
Calc AS
(
    SELECT t.hotel_id, t.reg_id,
           GrandTotal = CAST(CASE WHEN @IsRoundTotal = 1 THEN ROUND(ISNULL(t.GrandTotal, 0), 0) ELSE ISNULL(t.GrandTotal, 0) END AS decimal(18,2)),
           PaidAmount = CAST(CASE WHEN @IsRoundTotal = 1 THEN ROUND(ISNULL(p.PaidAmount, 0), 0) ELSE ISNULL(p.PaidAmount, 0) END AS decimal(18,2))
    FROM Totals t
    LEFT JOIN Paid p ON p.hotel_id = t.hotel_id AND p.reg_id = t.reg_id
),
FinalCalc AS
(
    SELECT hotel_id, reg_id, GrandTotal, PaidAmount, Remaining = CAST(GrandTotal - PaidAmount AS decimal(18,2))
    FROM Calc
)
UPDATE pu
SET pu.grand_total      = CAST(c.GrandTotal AS varchar(50)),
    pu.paid_amount      = CAST(c.PaidAmount AS varchar(50)),
    pu.remaining_amount = CAST(CASE WHEN c.Remaining < 0 THEN 0 ELSE c.Remaining END AS varchar(50))
FROM dbo.PaymentsUpdateTB pu
JOIN FinalCalc c ON c.hotel_id = pu.hotel_id AND c.reg_id = pu.reg_id
WHERE pu.hotel_id = @HotelId;", con) { CommandTimeout = 100 };
            cmd.Parameters.Add("@HotelId", SqlDbType.VarChar, 50).Value = hotelId.Trim();
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "UpdatePaymentsTotals failed for hotel {HotelId}.", hotelId);
        }
    }

    // =====================================================================
    // Permission: AllowDirty on the "Reservation" page (same as applypermission)
    // =====================================================================
    private async Task<bool> HasAllowDirtyAsync(string userId, string hotelId, CancellationToken cancellationToken)
    {
        try
        {
            await using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync(cancellationToken);

            await using var menuCmd = Cmd(conn, null, @"
SELECT TOP 1 menu_id
FROM dbo.AddMenuTB
WHERE ISNULL(page_name,'') = @page_name
  AND ISNULL([show],1) = 1
ORDER BY menu_id;");
            menuCmd.Parameters.Add("@page_name", SqlDbType.NVarChar, 200).Value = "Reservation";
            var menuObject = await menuCmd.ExecuteScalarAsync(cancellationToken);

            // Web Forms: page not mapped in AddMenuTB -> applypermission returned early -> AllowDirty stayed false.
            if (menuObject is null || menuObject is DBNull) return false;
            var menuId = Convert.ToInt32(menuObject, CultureInfo.InvariantCulture);
            if (menuId <= 0) return false;

            await using var actionCmd = Cmd(conn, null, @"
SELECT TOP (1)
    ISNULL(resolved.is_allowed, 0)
FROM dbo.PageActionsTB pa
OUTER APPLY
(
    SELECT TOP (1) uap.is_allowed
    FROM dbo.UserActionPermissionsTB uap
    WHERE uap.action_id = pa.action_id
      AND uap.menuid = pa.menuid
      AND ISNULL(uap.is_active, 0) = 1
      AND (
            (CONVERT(varchar(50), uap.hotel_id) = @hotel_id AND LOWER(LTRIM(RTRIM(ISNULL(uap.permission_for, '')))) = 'hotel')
         OR (CONVERT(varchar(50), uap.user_id) = @user_id AND CONVERT(varchar(50), uap.hotel_id) IN (@hotel_id, '-1'))
      )
    ORDER BY
        CASE
            WHEN CONVERT(varchar(50), uap.hotel_id) = @hotel_id AND CONVERT(varchar(50), uap.user_id) = @user_id
             AND LOWER(LTRIM(RTRIM(ISNULL(uap.permission_for, '')))) <> 'hotel' THEN 0
            WHEN CONVERT(varchar(50), uap.hotel_id) = @hotel_id AND LOWER(LTRIM(RTRIM(ISNULL(uap.permission_for, '')))) = 'hotel' THEN 1
            WHEN CONVERT(varchar(50), uap.hotel_id) = @hotel_id AND CONVERT(varchar(50), uap.user_id) = @user_id THEN 2
            WHEN CONVERT(varchar(50), uap.hotel_id) = '-1' AND CONVERT(varchar(50), uap.user_id) = @user_id THEN 3
            ELSE 4
        END,
        ISNULL(uap.updated_date, uap.created_date) DESC,
        uap.permission_id DESC
) resolved
WHERE pa.menuid = @menuid
  AND pa.action_name = @action_name
  AND ISNULL(pa.is_active, 0) = 1;");
            actionCmd.Parameters.Add("@hotel_id", SqlDbType.VarChar, 100).Value = hotelId;
            actionCmd.Parameters.Add("@user_id", SqlDbType.VarChar, 100).Value = userId;
            actionCmd.Parameters.Add("@menuid", SqlDbType.Int).Value = menuId;
            actionCmd.Parameters.Add("@action_name", SqlDbType.NVarChar, 200).Value = "AllowDirty";
            var actionObject = await actionCmd.ExecuteScalarAsync(cancellationToken);

            // PermissionHelper.HasAction allows an action that is not registered for the page.
            if (actionObject is null || actionObject is DBNull) return true;
            return Convert.ToBoolean(actionObject, CultureInfo.InvariantCulture);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AllowDirty permission lookup failed for hotel {HotelId}; room status 'Available' is used.", hotelId);
            return false;
        }
    }

    // =====================================================================
    // Checkout e-mail route (same rules as ResolveCheckoutEmailRoute)
    // =====================================================================
    private sealed class CheckoutEmailRoute
    {
        public bool ShouldSend { get; set; }
        public string ReviewUrl { get; set; } = string.Empty;
        public string CreatedRole { get; set; } = string.Empty;
        public string Agency { get; set; } = string.Empty;
        public string CategoryId { get; set; } = string.Empty;
        public string Reason { get; set; } = string.Empty;
    }

    private async Task<CheckoutEmailRoute> ResolveCheckoutEmailRouteAsync(
        string hotelId, string regId, string legacyBaseUrl, CancellationToken cancellationToken)
    {
        var route = new CheckoutEmailRoute
        {
            ShouldSend = true,
            ReviewUrl = legacyBaseUrl + "Feedback.aspx",
            Reason = "PMS checkout feedback link"
        };
        hotelId = (hotelId ?? string.Empty).Trim();
        regId = (regId ?? string.Empty).Trim();
        if (hotelId.Length == 0 || regId.Length == 0) return route;

        try
        {
            await using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = Cmd(connection, null, @"
SELECT TOP (1)
    ISNULL(LTRIM(RTRIM(createdRole)), '') AS createdRole,
    ISNULL(LTRIM(RTRIM(Agency)), '') AS Agency,
    ISNULL(LTRIM(RTRIM(CONVERT(varchar(200), cetogory_id))), '') AS CategoryId
FROM dbo.GuestInformationLogTB
WHERE CONVERT(varchar(50), hotel_id) = @hotel_id
  AND LTRIM(RTRIM(CONVERT(varchar(100), reg_id))) = @reg_id
ORDER BY TRY_CONVERT(bigint, ID) DESC, ID DESC;");
            command.Parameters.Add("@hotel_id", SqlDbType.VarChar, 50).Value = hotelId;
            command.Parameters.Add("@reg_id", SqlDbType.VarChar, 100).Value = regId;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return route;
            route.CreatedRole = Str(reader["createdRole"]);
            route.Agency = Str(reader["Agency"]);
            route.CategoryId = Str(reader["CategoryId"]);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Checkout e-mail route lookup failed for hotel {HotelId}, reg {RegId}.", hotelId, regId);
            return route;
        }

        if (!route.CreatedRole.Equals("Channex", StringComparison.OrdinalIgnoreCase)) return route;

        if (!route.Agency.Equals("bnbuk", StringComparison.OrdinalIgnoreCase))
        {
            route.ShouldSend = false; route.ReviewUrl = string.Empty; route.Reason = "Channex reservation is not a BNBUK booking";
            return route;
        }
        if (hotelId.Equals("638935275363396747", StringComparison.OrdinalIgnoreCase))
        {
            route.ShouldSend = false; route.ReviewUrl = string.Empty; route.Reason = "Hotel is excluded from the BNBUK review route";
            return route;
        }
        if (string.IsNullOrWhiteSpace(route.CategoryId))
        {
            route.ShouldSend = false; route.ReviewUrl = string.Empty; route.Reason = "BNBUK category id is missing";
            return route;
        }

        route.ReviewUrl = "https://www.bnbuk.co.uk/review/" + B64Url(regId) + "/" + B64Url(route.CategoryId) + "/" + B64Url(hotelId);
        route.ShouldSend = true;
        route.Reason = "BNBUK review link";
        return route;
    }

    // =====================================================================
    // Logging (same tables and columns as InsertLog / LogException)
    // =====================================================================
    private async Task InsertLogAsync(BulkCheckinUserContext user, string description, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var cmd = new SqlCommand(
                "INSERT INTO LogTB (hotel_id, description, date, ip, system, username) " +
                "VALUES (@Hotel, @Description, @Date, @IP, @System, @Username)", connection);
            cmd.Parameters.Add("@Hotel", SqlDbType.NVarChar, 100).Value = user.HotelId;
            cmd.Parameters.Add("@Description", SqlDbType.NVarChar, -1).Value = description;
            cmd.Parameters.Add("@Date", SqlDbType.NVarChar, 50).Value = DateTime.Now.ToString();
            cmd.Parameters.Add("@IP", SqlDbType.NVarChar, 50).Value = "ip";
            cmd.Parameters.Add("@System", SqlDbType.NVarChar, 256).Value = user.IdentityName;
            cmd.Parameters.Add("@Username", SqlDbType.NVarChar, 256).Value = user.UserName;
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LogTB insert failed for hotel {HotelId}.", user.HotelId);
        }
    }

    private async Task LogExceptionAsync(BulkCheckinUserContext user, Exception exception)
    {
        try
        {
            await using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();
            await using var cmd = new SqlCommand(
                "INSERT INTO ErrorLogTB (page_name, description, date, hotel_id, ip, system, username) " +
                "VALUES (@pagename, @Description, @Date, @hotel, @IP, @System, @Username)", connection);
            cmd.Parameters.Add("@pagename", SqlDbType.NVarChar, 100).Value = PageName;
            cmd.Parameters.Add("@Description", SqlDbType.NVarChar, -1).Value = exception.Message;
            cmd.Parameters.Add("@Date", SqlDbType.DateTime).Value = DateTime.Now;
            cmd.Parameters.Add("@hotel", SqlDbType.NVarChar, 100).Value = user.HotelId;
            cmd.Parameters.Add("@IP", SqlDbType.NVarChar, 50).Value = "ip";
            cmd.Parameters.Add("@System", SqlDbType.NVarChar, 256).Value = user.SystemName;
            cmd.Parameters.Add("@Username", SqlDbType.NVarChar, 256).Value = user.UserName;
            await cmd.ExecuteNonQueryAsync();
        }
        catch
        {
            // Never let error logging break the page.
        }
    }

    // =====================================================================
    // Helpers
    // =====================================================================
    private static SqlCommand Cmd(SqlConnection conn, SqlTransaction? tx, string sql) =>
        new(sql, conn, tx) { CommandTimeout = CommandTimeoutSeconds };

    private static void AddKeys(SqlCommand cmd, string hotelId, string regId)
    {
        cmd.Parameters.Add("@H", SqlDbType.NVarChar, 100).Value = hotelId;
        cmd.Parameters.Add("@R", SqlDbType.NVarChar, 100).Value = regId;
    }

    /// <summary>InvoiceRecieving.aspx link in the same format as the other pages (reg_id base64url, hotel_id = hd).</summary>
    private static string BuildInvoiceUrl(string legacyBaseUrl, string hotelId, string regId) =>
        legacyBaseUrl + "InvoiceRecieving.aspx?reg_id=" + Uri.EscapeDataString(B64Url(regId)) +
        "&hotel_id=" + Uri.EscapeDataString(Convert.ToBase64String(Encoding.UTF8.GetBytes(hotelId)));

    private static string B64Url(string value)
    {
        value = (value ?? string.Empty).Trim();
        if (value.Length == 0) return string.Empty;
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(value)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static string Str(object? value) =>
        value is null || value is DBNull ? string.Empty : (Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty).Trim();

    private static decimal Dec(object? value)
    {
        if (value is null || value is DBNull) return 0m;
        try { return Convert.ToDecimal(value, CultureInfo.InvariantCulture); }
        catch { return 0m; }
    }

    private static int Int(object? value)
    {
        if (value is null || value is DBNull) return 0;
        try { return Convert.ToInt32(value, CultureInfo.InvariantCulture); }
        catch { return 0; }
    }
}

/// <summary>
/// Keeps the existing bulk-checkout optional integrations unchanged, but connects its
/// AUTOMATIC full-security refund to the working Check-In security settlement flow.
/// The separate manual security button remains disabled, as before this change.
/// </summary>
public sealed class BulkCheckoutIntegration : IBulkCheckoutIntegration
{
    private readonly IConfiguration _configuration;
    private readonly IServiceProvider _services;
    private readonly ILogger<BulkCheckoutIntegration> _logger;
    private readonly string _connectionString;

    public BulkCheckoutIntegration(
        IConfiguration configuration,
        IServiceProvider services,
        ILogger<BulkCheckoutIntegration> logger)
    {
        _configuration = configuration;
        _services = services;
        _logger = logger;
        _connectionString = configuration.GetConnectionString("con")
            ?? throw new InvalidOperationException("ConnectionStrings:con is missing.");
    }

    // This switch only controls the existing MANUAL security popup; no changes to it.
    public bool SecuritySettlementAvailable => false;

    private sealed record DepositRow(
        int Id, decimal Amount, string Status, string TerminalStatus,
        string VisitId, string PaymentMethod, string PaymentIntentId, string ChargeId, string Currency);

    private static bool IsClosed(string status) =>
        (status ?? string.Empty).Trim().ToLowerInvariant() is
            "settled" or "canceled" or "cancelled" or "captured" or "released" or "refunded";

    /// <summary>
    /// Refunds every still-open *full* deposit through CheckInService.SecurityMovementAsync,
    /// which performs the Stripe cancel/refund (when applicable) and the PMS accounting.
    /// Previously partially-settled / inconsistent ledgers are deliberately rejected so
    /// automatic checkout cannot refund someone twice. They require manual reconciliation.
    /// </summary>
    public async Task<BulkCheckoutSettlementResult> SettleSecurityDepositAsync(
        string hotelId, string regId, string visitId, string currency, string method,
        decimal roomSecurity, decimal refundAmount, string note, string userId, string userName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(hotelId) || string.IsNullOrWhiteSpace(regId) ||
            string.IsNullOrWhiteSpace(userId))
            return new(false, "Missing hotel, reservation or user for security refund.");

        // This implementation is AUTOMATIC FULL REFUND ONLY; do not enable the manual
        // partial-release route by accident. The balance is always re-read from SQL.
        if (roomSecurity <= 0m || Math.Abs(roomSecurity - refundAmount) > 0.005m)
            return new(false, "Automatic security refund requires the full held amount.");

        // A SQL session application lock covers concurrent requests on different app
        // instances. Keep it open while the payment provider and database are updated.
        await using var lockConnection = new SqlConnection(_connectionString);
        try
        {
            await lockConnection.OpenAsync(cancellationToken);
            var lockResource = "BulkCheckoutAutoRefund:" + Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(hotelId + "|" + regId)));
            await using (var cmd = new SqlCommand(@"
EXEC @lockResult = sys.sp_getapplock
    @Resource = @resource, @LockMode = 'Exclusive',
    @LockOwner = 'Session', @LockTimeout = 0;", lockConnection))
            {
                var result = cmd.Parameters.Add("@lockResult", SqlDbType.Int);
                result.Direction = ParameterDirection.Output;
                cmd.Parameters.Add("@resource", SqlDbType.NVarChar, 255).Value = lockResource;
                await cmd.ExecuteNonQueryAsync(cancellationToken);
                if (Convert.ToInt32(result.Value, CultureInfo.InvariantCulture) < 0)
                    return new(false, "Another security refund is in progress for this reservation. Please retry.");
            }

            var (deposits, balance, terminalStatusAvailable) =
                await ReadDepositsAsync(lockConnection, hotelId, regId, cancellationToken);

            if (balance <= 0m)
                return new(false, "Security is already settled or there is no refundable balance.");

            // Compare to the amount the bulk checkout saw. If another operation changed
            // the balance, do not refund against a stale amount or a different deposit.
            if (Math.Abs(balance - refundAmount) > 0.005m)
                return new(false, "Security balance changed. Refresh the page and retry checkout.");

            var active = deposits.Where(d =>
                d.Amount > 0m &&
                d.Status.Equals("Deposit", StringComparison.OrdinalIgnoreCase) &&
                !IsClosed(d.TerminalStatus)).ToList();
            var sumOpen = active.Sum(d => d.Amount);

            // The security ledger must consist of whole, unspent deposits. An older
            // partial refund may not be attributable to a specific deposit. Refuse to
            // guess rather than create an incorrect Stripe refund or deduction.
            if (active.Count == 0 || Math.Abs(sumOpen - balance) > 0.005m ||
                (!terminalStatusAvailable && deposits.Any(d => d.Amount < 0m)))
                return new(false, "Security contains prior or partial settlements. Please reconcile it manually before checkout.");

            // If the deposit says Card but has no provider reference, never pretend that
            // writing a cash ledger movement actually refunds a bank card.
            // Also, CheckInService's legacy-schema fallback does not load Stripe IDs
            // when terminal_status is absent, so that schema is not auto-refund-safe
            // for card deposits. Fail closed instead of misreporting a cash ledger entry.
            foreach (var d in active)
            {
                var isCardMethod = d.PaymentMethod.Contains("card", StringComparison.OrdinalIgnoreCase) ||
                                   d.PaymentMethod.Contains("stripe", StringComparison.OrdinalIgnoreCase) ||
                                   d.PaymentMethod.Contains("terminal", StringComparison.OrdinalIgnoreCase) ||
                                   d.PaymentMethod.Contains("pdq", StringComparison.OrdinalIgnoreCase);
                var isCard = isCardMethod || !string.IsNullOrWhiteSpace(d.PaymentIntentId) ||
                             !string.IsNullOrWhiteSpace(d.ChargeId);
                if (isCardMethod && string.IsNullOrWhiteSpace(d.PaymentIntentId) && string.IsNullOrWhiteSpace(d.ChargeId))
                    return new(false, "Card security is missing its Stripe reference. Settle it manually.");
                if (isCard && !terminalStatusAvailable)
                    return new(false, "Card security requires terminal_status support to refund safely. Please settle it manually.");
            }

            // Multiple deposit rows referencing the same Stripe payment would otherwise
            // try to cancel/refund it more than once.
            var duplicateProvider = active
                .Select(d => !string.IsNullOrWhiteSpace(d.PaymentIntentId) ? "pi:" + d.PaymentIntentId :
                             !string.IsNullOrWhiteSpace(d.ChargeId) ? "ch:" + d.ChargeId : string.Empty)
                .Where(id => id.Length > 0)
                .GroupBy(id => id, StringComparer.OrdinalIgnoreCase)
                .Any(g => g.Count() > 1);
            if (duplicateProvider)
                return new(false, "Duplicate card-security payment references found. Please reconcile manually.");

            // Stripe's security_deposit cancellation webhook owns PDQ authorization releases.
            // Do NOT call SecurityMovementAsync for these: that method writes a second
            // negative RoomSecurityTB row, racing the idempotent payment_intent.canceled
            // webhook and potentially double-refunding the reservation's ledger.
            // Check the provider metadata first so a reservation payment hold is NEVER
            // released as a room-security deposit.
            var localSettlements = new List<DepositRow>();
            var webhookHolds = new List<DepositRow>();
            foreach (var d in active)
            {
                var isCard = !string.IsNullOrWhiteSpace(d.PaymentIntentId) ||
                             !string.IsNullOrWhiteSpace(d.ChargeId) ||
                             IsCardSecurityMethod(d.PaymentMethod);
                if (!isCard)
                {
                    localSettlements.Add(d);  // Cash ledger handling is unchanged.
                    continue;
                }

                // Card security without an intent cannot be identified as a webhook-owned
                // authorization. Do not silently convert it into a cash refund.
                if (string.IsNullOrWhiteSpace(d.PaymentIntentId))
                {
                    if (IsPdqSecurityMethod(d.PaymentMethod))
                        return new(false, "PDQ security is missing its Stripe PaymentIntent. Settle it manually.");
                    localSettlements.Add(d);  // Preserve existing charge-only behavior.
                    continue;
                }

                var authorization = await GetSecurityAuthorizationAsync(lockConnection, hotelId, regId,
                    d.PaymentIntentId, cancellationToken);

                if (authorization.IsSecurityDeposit)
                {
                    // Webhook processes payment_intent.canceled for payment_for=security_deposit.
                    if (authorization.Status.Equals("succeeded", StringComparison.OrdinalIgnoreCase))
                        return new(false, "PDQ security was captured. The cancellation webhook cannot refund a captured payment. Review the security ledger and settle the captured payment separately.");
                    if (!authorization.Status.Equals("requires_capture", StringComparison.OrdinalIgnoreCase) &&
                        !authorization.Status.Equals("canceled", StringComparison.OrdinalIgnoreCase))
                        return new(false, "Stripe security authorization is not releasable: " + authorization.Status);
                    webhookHolds.Add(d);
                }
                else
                {
                    if (IsPdqSecurityMethod(d.PaymentMethod))
                        return new(false, "PDQ authorization metadata does not identify a room-security deposit. Refund was stopped.");
                    localSettlements.Add(d); // Non-webhook card behavior remains unchanged.
                }
            }

            if (webhookHolds.Count > 0)
            {
                // Request release from Stripe; the webhook, not Bulk Checkout, saves
                // the refund/release row, updates terminal_status and balances the ledger.
                foreach (var d in webhookHolds)
                {
                    var release = await RequestSecurityAuthorizationReleaseAsync(lockConnection,
                        hotelId, regId, d.PaymentIntentId, d.Amount, d.Currency, cancellationToken);
                    if (!release.Ok)
                        return new(false, release.Message);
                }

                // A webhook is asynchronous. Only finish checkout when its committed
                // RoomSecurityTB ledger has the expected final balance. Do not write
                // a local fallback row or claim success based on Stripe's API response.
                // A small bounded poll allows an already-arriving webhook to complete
                // without requiring an unnecessary second click.
                for (var attempt = 0; attempt < 8; attempt++)
                {
                    var (webhookRows, webhookBalance, _) = await ReadDepositsAsync(
                        lockConnection, hotelId, regId, cancellationToken);
                    bool allRecorded = webhookHolds.All(d => webhookRows.Any(r =>
                        r.Id == d.Id && IsClosed(r.TerminalStatus) &&
                        webhookRows.Any(x => x.Amount < 0m &&
                            x.Status.Equals("refund", StringComparison.OrdinalIgnoreCase) &&
                            x.PaymentIntentId.Equals(d.PaymentIntentId, StringComparison.OrdinalIgnoreCase))));

                    // If the reservation had only PDQ holds, proceed immediately on a
                    // balanced ledger. Otherwise ask the user to retry for remaining
                    // cash/other deposits after the provider release is recorded.
                    if (allRecorded && Math.Abs(webhookBalance) <= 0.005m)
                    {
                        var amountLabel1 = balance.ToString("0.00", CultureInfo.InvariantCulture) + " " +
                            (string.IsNullOrWhiteSpace(currency) ? "GBP" : currency.Trim().ToUpperInvariant());
                        return new(true, "PDQ security authorization released by Stripe and confirmed by webhook: " + amountLabel1 + ".");
                    }
                    if (allRecorded)
                        return new(false, "PDQ security release confirmed by webhook. Other security deposits remain to be settled. Retry checkout with Refund security selected.");
                    if (attempt < 7)
                        await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
                }
                return new(false, "PDQ security release requested/confirmed at Stripe; waiting for payment_intent.canceled webhook to record the refund in RoomSecurityTB. No checkout performed. Retry after webhook processing.");
            }

            // Cash and non-webhook card security retain their original settlement flow.
            var checkIn = new CheckInService(
                _configuration,
                _services.GetRequiredService<IHotelClock>(),
                _services.GetRequiredService<IHttpClientFactory>(),
                _services.GetRequiredService<IAvailabilityAutoUpdateQueue>(),
                _services.GetRequiredService<ILogger<CheckInService>>());

            foreach (var d in localSettlements)
            {
                var outcome = await checkIn.SecurityMovementAsync(
                    hotelId, userId, userName, "BulkCheckout",
                    new SecurityMovementRequest
                    {
                        RegId = regId,
                        VisitId = string.IsNullOrWhiteSpace(d.VisitId) ? visitId : d.VisitId,
                        Amount = d.Amount,
                        Note = string.IsNullOrWhiteSpace(note) ? "Auto security refund on bulk checkout" : note,
                        Movement = "refund",
                        Method = d.PaymentMethod,
                        SecurityId = d.Id,
                        PaymentIntentId = d.PaymentIntentId,
                        ChargeId = d.ChargeId
                    }, cancellationToken);

                if (!outcome.Success)
                {
                    _logger.LogWarning("Auto security settlement refused for hotel {HotelId}, reg {RegId}, security row {SecurityId}: {Reason}",
                        hotelId, regId, d.Id, outcome.Message);
                    return new(false, outcome.Message + " No checkout was performed. Check the security ledger before retrying.");
                }
            }

            var (_, finalBalance, _) = await ReadDepositsAsync(lockConnection, hotelId, regId, cancellationToken);
            if (Math.Abs(finalBalance) > 0.005m)
                return new(false, "Security settlement did not clear the full ledger. Checkout was stopped; review the ledger.");

            var amountLabel = balance.ToString("0.00", CultureInfo.InvariantCulture) + " " +
                              (string.IsNullOrWhiteSpace(currency) ? "GBP" : currency.Trim().ToUpperInvariant());
            var includesCash = active.Any(d => string.IsNullOrWhiteSpace(d.PaymentIntentId) &&
                                               string.IsNullOrWhiteSpace(d.ChargeId));
            return new(true, "Security refunded/released: " + amountLabel +
                (includesCash ? ". Cash refunds were recorded in the PMS ledger." : "."));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Auto security settlement error for hotel {HotelId}, reg {RegId}.", hotelId, regId);
            return new(false, "Security refund could not be confirmed. Check the provider and security ledger before retrying.");
        }
    }

    private static bool IsCardSecurityMethod(string? method) =>
        (method ?? string.Empty).Contains("card", StringComparison.OrdinalIgnoreCase) ||
        (method ?? string.Empty).Contains("stripe", StringComparison.OrdinalIgnoreCase) ||
        IsPdqSecurityMethod(method);

    private static bool IsPdqSecurityMethod(string? method) =>
        (method ?? string.Empty).Contains("pdq", StringComparison.OrdinalIgnoreCase) ||
        (method ?? string.Empty).Contains("terminal", StringComparison.OrdinalIgnoreCase);

    private sealed record SecurityAuthorization(bool IsSecurityDeposit, string Status, decimal CapturableAmount, string Currency);

    // Use the same HotelStripeAccounts credentials and Stripe-Account header as
    // CheckInService. Never use the app's global StripeConfiguration for tenant calls.
    private async Task<(string Secret, string Account)> ReadStripeCredentialsAsync(
        SqlConnection connection, string hotelId, CancellationToken cancellationToken)
    {
        await using var cmd = new SqlCommand(@"
SELECT TOP 1 AccessToken, StripeUserId
FROM dbo.HotelStripeAccounts
WHERE HotelId=@hotel
ORDER BY CreatedAt DESC;", connection);
        cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
        await using var r = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await r.ReadAsync(cancellationToken))
            throw new InvalidOperationException("Stripe account is not configured for the hotel.");
        var secret = Convert.ToString(r["AccessToken"], CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
        var account = Convert.ToString(r["StripeUserId"], CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
        if (secret.Length == 0)
            throw new InvalidOperationException("Stripe access token is missing for the hotel.");
        return (secret, account);
    }

    private async Task<JsonDocument> StripeSecurityRequestAsync(
        string secret, string stripeAccount, HttpMethod method, string uri,
        string? idempotencyKey, CancellationToken cancellationToken)
    {
        var httpFactory = _services.GetRequiredService<IHttpClientFactory>();
        var client = httpFactory.CreateClient();
        using var message = new HttpRequestMessage(method, uri);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        if (stripeAccount.Length > 0)
            message.Headers.TryAddWithoutValidation("Stripe-Account", stripeAccount);
        if (!string.IsNullOrWhiteSpace(idempotencyKey))
            message.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        if (method == HttpMethod.Post)
            message.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["cancellation_reason"] = "requested_by_customer"
            });
        using var response = await client.SendAsync(message, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Stripe security authorization API error: HTTP {Status} ({Path})",
                (int)response.StatusCode, uri.Contains("/cancel", StringComparison.Ordinal) ? "cancel" : "get");
            throw new InvalidOperationException("Stripe security release could not be confirmed (HTTP " +
                (int)response.StatusCode + "). Review the payment in Stripe before retrying.");
        }
        return JsonDocument.Parse(raw);
    }

    private static string StripeJsonString(JsonElement root, string field)
    {
        return root.ValueKind == JsonValueKind.Object &&
               root.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty : string.Empty;
    }

    private async Task<SecurityAuthorization> GetSecurityAuthorizationAsync(
        SqlConnection connection, string hotelId, string regId, string paymentIntentId,
        CancellationToken cancellationToken)
    {
        var (secret, account) = await ReadStripeCredentialsAsync(connection, hotelId, cancellationToken);
        using var piDoc = await StripeSecurityRequestAsync(secret, account, HttpMethod.Get,
            "https://api.stripe.com/v1/payment_intents/" + Uri.EscapeDataString(paymentIntentId),
            null, cancellationToken);
        var pi = piDoc.RootElement;
        var metadata = pi.TryGetProperty("metadata", out var md) && md.ValueKind == JsonValueKind.Object
            ? md : default;
        var paymentFor = StripeJsonString(metadata, "payment_for");
        var isSecurity = paymentFor.Equals("security_deposit", StringComparison.OrdinalIgnoreCase);
        if (isSecurity &&
            (!StripeJsonString(metadata, "hotel_id").Equals(hotelId, StringComparison.Ordinal) ||
             !StripeJsonString(metadata, "reg_id").Equals(regId, StringComparison.Ordinal)))
            throw new InvalidOperationException("Stripe security authorization does not belong to this hotel and reservation.");
        var currency = StripeJsonString(pi, "currency");
        var amountMinor = pi.TryGetProperty("amount_capturable", out var amountEl) &&
                          amountEl.ValueKind == JsonValueKind.Number && amountEl.TryGetInt64(out var minor)
            ? minor : 0L;
        var zeroDecimalCurrency = new[] { "BIF", "CLP", "DJF", "GNF", "JPY", "KMF", "KRW", "MGA",
            "PYG", "RWF", "UGX", "VND", "VUV", "XAF", "XOF", "XPF" };
        var capturable = amountMinor / (zeroDecimalCurrency.Contains(currency, StringComparer.OrdinalIgnoreCase) ? 1m : 100m);
        return new SecurityAuthorization(isSecurity, StripeJsonString(pi, "status"), capturable, currency);
    }

    private async Task<BulkCheckoutSettlementResult> RequestSecurityAuthorizationReleaseAsync(
        SqlConnection connection, string hotelId, string regId, string paymentIntentId, decimal expectedAmount, string expectedCurrency,
        CancellationToken cancellationToken)
    {
        // Re-read provider state immediately before cancellation. In particular, never
        // cancel a captured PI or issue a second refund for a canceled PI.
        var state = await GetSecurityAuthorizationAsync(connection, hotelId, regId, paymentIntentId, cancellationToken);
        if (!state.IsSecurityDeposit)
            return new(false, "Stripe PaymentIntent is not a room-security authorization; cancellation stopped.");
        if (state.Status.Equals("canceled", StringComparison.OrdinalIgnoreCase))
            return new(true, "Stripe authorization is already canceled; awaiting webhook accounting.");
        if (!state.Status.Equals("requires_capture", StringComparison.OrdinalIgnoreCase))
            return new(false, "Stripe authorization is not releasable: " + state.Status);

        if (!string.IsNullOrWhiteSpace(expectedCurrency) &&
            !state.Currency.Equals(expectedCurrency, StringComparison.OrdinalIgnoreCase))
            return new(false, "PDQ authorization currency differs from the security deposit. Automatic release stopped.");
        if (state.CapturableAmount <= 0m || Math.Abs(state.CapturableAmount - expectedAmount) > 0.005m)
            return new(false, "PDQ authorized amount differs from the room-security deposit. Do not release automatically; reconcile the security ledger.");

        var (secret, account) = await ReadStripeCredentialsAsync(connection, hotelId, cancellationToken);
        // Stable key makes retries idempotent at Stripe. The corresponding webhook
        // ensures only one negative security movement exists in RoomSecurityTB.
        var idempotencyKey = "bulk-security-cancel-" + paymentIntentId;
        try
        {
            using var cancelled = await StripeSecurityRequestAsync(secret, account, HttpMethod.Post,
                "https://api.stripe.com/v1/payment_intents/" + Uri.EscapeDataString(paymentIntentId) + "/cancel",
                idempotencyKey, cancellationToken);
            if (!StripeJsonString(cancelled.RootElement, "status").Equals("canceled", StringComparison.OrdinalIgnoreCase))
                return new(false, "Stripe did not confirm the authorization cancellation.");
        }
        catch (InvalidOperationException)
        {
            // If another request canceled the hold between GET and POST,
            // treat it as awaiting webhook instead of initiating a second refund.
            var current = await GetSecurityAuthorizationAsync(connection, hotelId, regId,
                paymentIntentId, cancellationToken);
            if (!current.IsSecurityDeposit ||
                !current.Status.Equals("canceled", StringComparison.OrdinalIgnoreCase))
                throw;
        }
        _logger.LogInformation("PDQ security release requested: hotel {HotelId}, reg {RegId}, PI {PaymentIntentId}; waiting for webhook ledger update.",
            hotelId, regId, paymentIntentId);
        return new(true, "Stripe authorization canceled; waiting for webhook accounting.");
    }

    private static async Task<(List<DepositRow> Rows, decimal Balance, bool HasTerminalStatus)> ReadDepositsAsync(
        SqlConnection connection, string hotelId, string regId, CancellationToken cancellationToken)
    {
        bool hasTerminalStatus;
        await using (var schema = new SqlCommand(
            "SELECT CASE WHEN COL_LENGTH('dbo.RoomSecurityTB','terminal_status') IS NULL THEN 0 ELSE 1 END;", connection))
        {
            hasTerminalStatus = Convert.ToInt32(await schema.ExecuteScalarAsync(cancellationToken),
                CultureInfo.InvariantCulture) == 1;
        }

        var rows = new List<DepositRow>();
        var balance = 0m;
        var sql = @"
SELECT id, status, visit_id, payment_method, payment_intent_id, charge_id, currency,
       TRY_CONVERT(decimal(18,2),REPLACE(NULLIF(LTRIM(RTRIM(CONVERT(varchar(50), security))),''),',','')) AS amount, " +
                  (hasTerminalStatus ? "ISNULL(terminal_status,'')" : "''") + @" AS terminal_status
FROM dbo.RoomSecurityTB
WHERE hotel_id=@hotel AND reg_id=@reg
ORDER BY id ASC;";
        await using var cmd = new SqlCommand(sql, connection) { CommandTimeout = 60 };
        cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
        cmd.Parameters.Add("@reg", SqlDbType.VarChar, 100).Value = regId;
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            // Malformed security values must not be ignored during money movement.
            if (reader.IsDBNull(reader.GetOrdinal("amount")))
                throw new InvalidOperationException("Invalid security amount in the security ledger.");
            var amount = Convert.ToDecimal(reader["amount"], CultureInfo.InvariantCulture);
            balance += amount;
            rows.Add(new DepositRow(
                Convert.ToInt32(reader["id"], CultureInfo.InvariantCulture), amount,
                Convert.ToString(reader["status"], CultureInfo.InvariantCulture)?.Trim() ?? string.Empty,
                Convert.ToString(reader["terminal_status"], CultureInfo.InvariantCulture)?.Trim() ?? string.Empty,
                Convert.ToString(reader["visit_id"], CultureInfo.InvariantCulture)?.Trim() ?? string.Empty,
                Convert.ToString(reader["payment_method"], CultureInfo.InvariantCulture)?.Trim() ?? string.Empty,
                Convert.ToString(reader["payment_intent_id"], CultureInfo.InvariantCulture)?.Trim() ?? string.Empty,
                Convert.ToString(reader["charge_id"], CultureInfo.InvariantCulture)?.Trim() ?? string.Empty,
                Convert.ToString(reader["currency"], CultureInfo.InvariantCulture)?.Trim() ?? string.Empty));
        }
        return (rows, balance, hasTerminalStatus);
    }

    public Task SendCheckoutThanksEmailOnceAsync(string hotelId, string regId, string reviewUrl, CancellationToken cancellationToken = default)
    {
        // Unchanged: WebForms EmailSender has not been migrated in this service.
        _logger.LogWarning("TODO Bulk checkout: EmailSender not ported - checkout e-mail skipped for reg {RegId}.", regId);
        return Task.CompletedTask;
    }

    public Task WriteReservationLogAsync(BulkCheckinUserContext user, string regId, string description, string status, CancellationToken cancellationToken = default)
    {
        // Unchanged: WebForms Log_helper has not been migrated in this service.
        _logger.LogWarning("TODO Bulk checkout: Log_helper not ported - reservation log '{Status}' skipped for reg {RegId}.", status, regId);
        return Task.CompletedTask;
    }

    public Task WriteActivityLogAsync(string hotelId, string userId, string summary, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Bulk checkout summary (hotel {HotelId}, user {UserId}): {Summary}", hotelId, userId, summary);
        return Task.CompletedTask;
    }
}
