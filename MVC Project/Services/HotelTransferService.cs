using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Orapmshms.Models;

namespace Orapmshms.Services;

/// <summary>
/// Explicit superuser, same-database hotel transfer. No schema or DI registration changes.
/// No OTA calls, gateway operations, emails or fiscal submissions. Each reservation is an
/// atomic transfer unit; progress uses private on-disk JSON, not a new SQL table.
/// </summary>
public sealed class HotelTransferService
{
    private readonly string _connectionString;
    private readonly string _directory;
    private readonly ILogger<HotelTransferService> _logger;
    public const string DemoHotelId = "638935275363396747";
    public const string DemoResetPhrase = "DELETE DEMO HOTEL 638935275363396747";

    // All tables here come directly from the approved demo reset scope. The order
    // deletes dependent financial/stay rows before master category/plan records.
    // No other hotel, table, or row can be targeted by this action.
    private static readonly string[] DemoResetTables =
    {
        "NewReservationRate", "PaymentsLogTB", "PaymentsUpdateTB", "payments",
        "Reserv_log", "GuestInformationLogTB", "NewReservationsTB",
        "CancelledReservationsTB", "NoShowTB", "RateUploadHistoryTb",
        "datesrates", "category_plan", "plans", "RoomsTB", "create_room"
    };
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> DemoResetLocks = new();
    private sealed class DemoResetTicket
    {
        public Guid Id { get; set; }
        public string Owner { get; set; } = "";
        public string DestinationHotelId { get; set; } = "";
        public DateTime CreatedUtc { get; set; }
        public string Status { get; set; } = "Preview";
        public List<HotelDemoResetTableCount> Tables { get; set; } = new();
        public int DeletedTotal { get; set; }
    }
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> Locks = new();
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private static readonly HotelTransferArea[] TransactionAreas =
    {
        HotelTransferArea.Reservations, HotelTransferArea.CheckIns, HotelTransferArea.CheckOuts,
        HotelTransferArea.Payments, HotelTransferArea.Cancellations, HotelTransferArea.NoShows
    };
    private static readonly HashSet<string> BlockedColumns = new(StringComparer.OrdinalIgnoreCase)
    {
        // Never carry source identities, cross-hotel integration IDs, card tokens or fiscal submissions.
        "id", "booking_id", "channex_booking_id", "channex_id", "category_id_external",
        "category_id", "plan_id", "rateplan_id_external",
        "fbr_invoice_no", "fbr_response", "fbr_post_status", "fbr_posted_at",
        "pra_invoice_no", "fiscal_invoice_no", "receipturl", "receipt_url", "paymentid",
        "chargeid", "chargid", "charge_id", "refundid", "payment_intent", "stripe_session_id",
        "token", "access_token", "publishable_key", "last4digits", "authcode", "transactionid",
        "bank_reference", "pin", "card_number", "cvv", "authorizationcode", "gatewayresponse",
        "fbr_post_attempts", "fbr_last_error", "fbr_status", "fbr_request", "fbr_payload"
    };
    private static readonly HashSet<string> SkipSqlTypes = new(StringComparer.OrdinalIgnoreCase)
        { "timestamp", "rowversion", "image", "varbinary", "binary", "geography", "geometry", "hierarchyid" };
    private sealed class Column
    {
        public string Name { get; set; } = "";
        public bool Auto { get; set; }
        public string SqlType { get; set; } = "";
    }
    private sealed class Unit
    {
        public string Key { get; set; } = ""; // Room/category/plan key, or reg_id
        public string Category { get; set; } = ""; // Config or Booking
        public List<HotelTransferArea> Areas { get; set; } = new();
        public List<string> PaymentIds { get; set; } = new();
        public List<string> CheckOutRoomIds { get; set; } = new();
        public string Status { get; set; } = "Ready";
        public string Message { get; set; } = "";
    }
    private sealed class Batch
    {
        public Guid Id { get; set; }
        public string Owner { get; set; } = "";
        public HotelTransferRequest Request { get; set; } = new();
        public string Status { get; set; } = "Preview";
        public string DemoResetEpoch { get; set; } = "";
        public List<Unit> Units { get; set; } = new();
        public List<HotelTransferCount> Counts { get; set; } = new();
        public List<string> Warnings { get; set; } = new();
    }

    public HotelTransferService(IConfiguration config, IWebHostEnvironment env, ILogger<HotelTransferService> logger)
    {
        _connectionString = config.GetConnectionString("con")
            ?? throw new InvalidOperationException("ConnectionStrings:con is not configured.");
        _logger = logger;
        _directory = Path.Combine(env.ContentRootPath, "App_Data", "HotelTransfer");
    }
    private static string Text(object? value) => value is null or DBNull ? "" :
        (Convert.ToString(value, CultureInfo.InvariantCulture) ?? "").Trim();
    private static void P(SqlCommand cmd, string key, object? val) =>
        cmd.Parameters.AddWithValue(key, val is null or DBNull ? DBNull.Value : val);
    private async Task<SqlConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        return conn;
    }
    private static SqlCommand Cmd(SqlConnection cn, SqlTransaction? tx, string sql) =>
        new(sql, cn, tx) { CommandTimeout = 60 };
    private static string Identifier(string raw) => "[" + raw.Replace("]", "]]", StringComparison.Ordinal) + "]";
    private string FileName(Guid id, string destination) => Path.Combine(_directory,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(destination)))[..20] + "_" + id.ToString("N") + ".json");
    private string DemoResetEpochFile => Path.Combine(_directory, "demo-reset-epoch.txt");
    private async Task<string> ReadDemoResetEpochAsync(CancellationToken ct)
    {
        var file = DemoResetEpochFile;
        return File.Exists(file) ? (await File.ReadAllTextAsync(file, ct)).Trim() : "";
    }
    private async Task InvalidateOlderBatchesAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(_directory);
        var temp = DemoResetEpochFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temp, Guid.NewGuid().ToString("N"), ct);
            File.Move(temp, DemoResetEpochFile, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private async Task WriteBatchAsync(Batch batch, CancellationToken ct)
    {
        Directory.CreateDirectory(_directory);
        var path = FileName(batch.Id, batch.Request.DestinationHotelId);
        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(batch, Json), ct);
            File.Move(tmp, path, true);
        }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }
    private async Task<Batch> ReadBatchAsync(Guid id, string destination, string user, CancellationToken ct)
    {
        var file = FileName(id, destination);
        if (!File.Exists(file)) throw new ArgumentException("Transfer batch not found for this destination.");
        var b = JsonSerializer.Deserialize<Batch>(await File.ReadAllTextAsync(file, ct), Json)
            ?? throw new InvalidOperationException("Transfer batch could not be read.");
        if (!string.Equals(destination, DemoHotelId, StringComparison.Ordinal) ||
            b.Id != id || b.Owner != user || b.Request.DestinationHotelId != destination)
            throw new ArgumentException("Batch does not belong to this user/destination.");
        if (b.DemoResetEpoch != await ReadDemoResetEpochAsync(ct))
            throw new InvalidOperationException("This batch was created before a demo-hotel reset. Start a new preview.");
        return b;
    }
    private static HotelTransferPreview Result(Batch b) => new()
    {
        BatchId = b.Id, Status = b.Status, SourceHotelId = b.Request.SourceHotelId,
        DestinationHotelId = b.Request.DestinationHotelId, ReplaceMatching = b.Request.ReplaceMatching,
        Total = b.Units.Count, Processed = b.Units.Count(x => x.Status != "Ready"),
        Copied = b.Units.Count(x => x.Status == "Copied"),
        Skipped = b.Units.Count(x => x.Status == "Skipped"),
        Failed = b.Units.Count(x => x.Status == "Failed"),
        Counts = b.Counts, Warnings = b.Warnings
    };

    public async Task<List<ImportDataHotelOption>> HotelsAsync(CancellationToken ct)
    {
        var list = new List<ImportDataHotelOption>();
        await using var cn = await OpenAsync(ct);
        await using var cmd = Cmd(cn, null, "SELECT hotel_id, name, currency_sign FROM dbo.HotelsSignUpTB WHERE hotel_id IS NOT NULL ORDER BY name, hotel_id");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var id = Text(r["hotel_id"]);
            if (id.Length == 0 || list.Any(x => x.HotelId == id)) continue;
            list.Add(new ImportDataHotelOption { HotelId = id, HotelName = Text(r["name"]), Currency = Text(r["currency_sign"]) });
        }
        return list;
    }
    private static void Validate(HotelTransferRequest r, bool start)
    {
        r.SourceHotelId = r.SourceHotelId?.Trim() ?? "";
        r.DestinationHotelId = r.DestinationHotelId?.Trim() ?? "";
        if (r.SourceHotelId.Length == 0 || r.DestinationHotelId.Length == 0 ||
            r.SourceHotelId.Length > 50 || r.DestinationHotelId.Length > 50 ||
            r.SourceHotelId == r.DestinationHotelId)
            throw new ArgumentException("Select two different valid hotels.");
        if (!string.Equals(r.DestinationHotelId, DemoHotelId, StringComparison.Ordinal))
            throw new ArgumentException("Hotel transfers are restricted to demo destination " + DemoHotelId + ".");
        if (string.Equals(r.SourceHotelId, DemoHotelId, StringComparison.Ordinal))
            throw new ArgumentException("The demo hotel cannot be used as the source hotel.");
        if (r.Areas is null || r.Areas.Count == 0 || r.Areas.Any(a => !Enum.IsDefined(a)))
            throw new ArgumentException("Select at least one valid transfer module.");
        r.Areas = r.Areas.Distinct().ToList();
        if (r.FromDate == default || r.ToDate == default || r.FromDate.Date > r.ToDate.Date ||
            (r.ToDate.Date - r.FromDate.Date).TotalDays > 366)
            throw new ArgumentException("Select a valid date range of at most 367 days.");
        if (r.ReplaceMatching)
        {
            // Prevent partial replacement of a booking's dependent financial/stay history.
            if (!TransactionAreas.All(x => r.Areas.Contains(x)))
                throw new ArgumentException("To replace existing destination bookings, select all six dated modules (Reservations, Check-ins, Check-outs, Payments, Cancellations and No-shows). The transfer must be complete before an old booking can be removed.");
            if (start && (!string.Equals(r.ConfirmDestinationId?.Trim(), r.DestinationHotelId,
                               StringComparison.Ordinal)))
                throw new ArgumentException("Enter the exact destination hotel ID to confirm replacement.");
        }
    }
    private static async Task AssertHotelsAsync(SqlConnection cn, HotelTransferRequest req, CancellationToken ct)
    {
        var currencies = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        await using var cmd = Cmd(cn, null, @"SELECT hotel_id,currency_sign FROM dbo.HotelsSignUpTB WHERE hotel_id IN (@s,@d)");
        P(cmd, "@s", req.SourceHotelId); P(cmd, "@d", req.DestinationHotelId);
        await using var r=await cmd.ExecuteReaderAsync(ct);
        while(await r.ReadAsync(ct))currencies[Text(r["hotel_id"])]=Text(r["currency_sign"]);
        if(currencies.Count!=2)throw new ArgumentException("One or both selected hotels no longer exist.");
        var a=currencies[req.SourceHotelId];var b=currencies[req.DestinationHotelId];
        if(a.Length>0 && b.Length>0 && !string.Equals(a,b,StringComparison.OrdinalIgnoreCase)
           && req.Areas.Any(x=>TransactionAreas.Contains(x)))
            throw new ArgumentException("Source and destination hotel currencies differ ("+a+" vs "+b+"). Financial transfers require an explicit currency conversion and are blocked by this tool.");
    }

    private async Task<HashSet<string>> ColumnsAsync(SqlConnection cn, SqlTransaction? tx, string table, CancellationToken ct)
    {
        var columns = await MetadataAsync(cn, tx, table, ct);
        return columns.Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
    private static readonly HashSet<string> AllowedTables = new(StringComparer.OrdinalIgnoreCase)
    {
        "create_room", "RoomsTB", "plans", "category_plan", "NewReservationsTB", "GuestInformationLogTB",
        "payments", "PaymentsLogTB", "PaymentsUpdateTB", "NewReservationRate",
        "CancelledReservationsTB", "NoShowTB", "Reserv_log"
    };
    private static async Task<List<Column>> MetadataAsync(SqlConnection cn, SqlTransaction? tx, string table, CancellationToken ct)
    {
        if (!AllowedTables.Contains(table)) throw new InvalidOperationException("Unapproved table name.");
        var cols = new List<Column>();
        await using var cmd = Cmd(cn, tx, @"
SELECT c.name, c.is_identity, c.is_computed, t.name AS sql_type
FROM sys.columns c INNER JOIN sys.tables tb ON tb.object_id=c.object_id
INNER JOIN sys.schemas sch ON sch.schema_id=tb.schema_id
INNER JOIN sys.types t ON t.user_type_id=c.user_type_id
WHERE sch.name='dbo' AND tb.name=@table ORDER BY c.column_id;");
        P(cmd, "@table", table);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) cols.Add(new Column
        {
            Name = Text(r["name"]),
            Auto = Convert.ToBoolean(r["is_identity"]) || Convert.ToBoolean(r["is_computed"]),
            SqlType = Text(r["sql_type"])
        });
        return cols;
    }
    private static string? Pick(HashSet<string> fields, params string[] candidates) =>
        candidates.FirstOrDefault(fields.Contains);
    private static string DateExpr(string field)
    {
        var fieldName = Identifier(field);
        return $@"COALESCE(
TRY_CONVERT(date, {fieldName}, 126), TRY_CONVERT(date, {fieldName}, 121),
TRY_CONVERT(date, {fieldName}, 120), TRY_CONVERT(date, {fieldName}, 110),
TRY_CONVERT(date, {fieldName}, 103), TRY_CONVERT(date, {fieldName}, 100),
TRY_CONVERT(date, {fieldName}))";
    }
    private static readonly Dictionary<HotelTransferArea, (string Table, string[] Dates)> SourceMap = new()
    {
        [HotelTransferArea.Reservations] = ("NewReservationsTB", new[] { "ArrivalDate" }),
        [HotelTransferArea.CheckIns] = ("GuestInformationLogTB", new[] { "actual_checkin_date", "ActualCheckInDate", "checkin_date", "CheckInDate", "CreatedDate" }),
        [HotelTransferArea.CheckOuts] = ("payments", new[] { "actual_checkout_date", "ActualCheckOutDate" }),
        [HotelTransferArea.Payments] = ("PaymentsLogTB", new[] { "currentdate" }),
        [HotelTransferArea.Cancellations] = ("CancelledReservationsTB", new[] { "cancelled_at", "CancelledDate", "cancellation_date", "date", "CreatedDate" }),
        [HotelTransferArea.NoShows] = ("NoShowTB", new[] { "no_show_at", "NoShowDate", "noshow_date", "date", "CreatedDate" })
    };
    private static async Task<int> CountAsync(SqlConnection cn, SqlTransaction? tx, string table, string hotel, string reg, CancellationToken ct)
    {
        await using var cmd = Cmd(cn, tx, $"SELECT COUNT(*) FROM dbo.{Identifier(table)} WHERE hotel_id=@hotel AND reg_id=@reg");
        P(cmd, "@hotel", hotel); P(cmd, "@reg", reg);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }
    private static async Task<List<string>> FindKeysAsync(SqlConnection cn, string source,
        HotelTransferArea area, string? dateField, DateTime from, DateTime to, CancellationToken ct)
    {
        // RatePlanService creates the master record in dbo.plans (Save Details)
        // and category defaults in dbo.category_plan (Save Rates). Enumerate
        // master plans, not the per-category rows, to transfer each plan once.
        var table = area switch
        {
            HotelTransferArea.Categories => "create_room",
            HotelTransferArea.Rooms => "RoomsTB",
            HotelTransferArea.RatePlans => "plans",
            _ => SourceMap[area].Table
        };
        var key = area switch
        {
            HotelTransferArea.Categories => "description",
            HotelTransferArea.Rooms => "room_no",
            HotelTransferArea.RatePlans => "name",
            _ => "reg_id"
        };
        var filter = dateField is null ? "" : $"AND {DateExpr(dateField)} >= @from AND {DateExpr(dateField)} < DATEADD(day,1,@to)";
        if (area == HotelTransferArea.Categories) filter += " AND category='Room Rent'";
        if (area == HotelTransferArea.CheckIns) filter += " AND LOWER(LTRIM(RTRIM(res_status))) IN ('check in','checked in')";
        if (area == HotelTransferArea.CheckOuts) filter += " AND LOWER(LTRIM(RTRIM(res_status))) IN ('check out','checked out') AND LOWER(LTRIM(RTRIM(descr)))='room rent'";
        var sql = $@"SELECT DISTINCT TOP (5001) CONVERT(nvarchar(200),{key}) AS k
FROM dbo.{Identifier(table)} WHERE hotel_id=@hotel AND {key} IS NOT NULL {filter} ORDER BY k";
        var list = new List<string>();
        await using var cmd = Cmd(cn, null, sql);
        P(cmd, "@hotel", source); P(cmd, "@from", from.Date); P(cmd, "@to", to.Date);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var id = Text(r["k"]);
            if (id.Length != 0) list.Add(id);
        }
        if (list.Count > 5000) throw new ArgumentException("Too many records. Choose a shorter period (maximum 5,000 per module).");
        return list;
    }
    private async Task<Batch> BuildPreviewAsync(HotelTransferRequest req, string user, CancellationToken ct)
    {
        Validate(req, false);
        await using var cn = await OpenAsync(ct);
        await AssertHotelsAsync(cn, req, ct);
        var b = new Batch { Id = Guid.NewGuid(), Owner = user, Request = req,
            DemoResetEpoch = await ReadDemoResetEpochAsync(ct) };
        var bookings = new Dictionary<string, Unit>(StringComparer.OrdinalIgnoreCase);
        foreach (var area in req.Areas.OrderBy(x => x))
        {
            var map = SourceMap.TryGetValue(area, out var m) ? m : default;
            string? dateField = null;
            if (map.Table != null)
            {
                var fields = await ColumnsAsync(cn, null, map.Table, ct);
                dateField = Pick(fields, map.Dates);
                if (dateField is null && area != HotelTransferArea.NoShows)
                    throw new ArgumentException($"{area}: no actual event date column was found in {map.Table}. This module cannot be filtered safely by the requested period.");
            }
            var keys = area == HotelTransferArea.NoShows && dateField is null
                ? await NoShowKeysFromAuditAsync(cn, req.SourceHotelId, req.FromDate, req.ToDate, ct)
                : await FindKeysAsync(cn, req.SourceHotelId, area, dateField, req.FromDate, req.ToDate, ct);
            if (area == HotelTransferArea.NoShows && dateField is null)
                b.Warnings.Add("NoShowTB has no actual no-show event date. Only no-shows with an explicit matching Reserv_log no-show action timestamp are eligible. Others are excluded, not guessed from arrival dates.");
            if (area == HotelTransferArea.CheckIns && string.Equals(dateField,"CreatedDate",StringComparison.OrdinalIgnoreCase))
                b.Warnings.Add("Check-ins use GuestInformationLogTB.CreatedDate because no explicit actual check-in date column is available. Confirm this represents the true check-in event before committing.");
            if (area == HotelTransferArea.Cancellations && string.Equals(dateField,"date",StringComparison.OrdinalIgnoreCase))
                b.Warnings.Add("Cancellations use CancelledReservationsTB.date; verify the source stores the cancellation event date there, not the original booking creation date.");
            var counter = new HotelTransferCount { Area = area.ToString(), SourceRows = keys.Count };
            if (map.Table == null)
            {
                foreach (var key in keys)
                    b.Units.Add(new Unit { Key = key, Category = area.ToString(), Areas = new() { area } });
            }
            else
            {
                foreach (var reg in keys)
                {
                    if (!bookings.TryGetValue(reg, out var unit))
                        bookings.Add(reg, unit = new Unit { Key = reg, Category = "Booking" });
                    unit.Areas.Add(area);
                    if (area == HotelTransferArea.Payments)
                    {
                        var ids = await FilteredIdsAsync(cn, null, "PaymentsLogTB", req.SourceHotelId,
                            reg, dateField!, req.FromDate, req.ToDate, ct);
                        unit.PaymentIds.AddRange(ids);
                        counter.SourceRows += ids.Count - 1;
                    }
                    if (area == HotelTransferArea.CheckOuts)
                    {
                        var ids = await FilteredIdsAsync(cn, null, "payments", req.SourceHotelId,
                            reg, dateField!, req.FromDate, req.ToDate, ct, " AND LOWER(LTRIM(RTRIM(descr)))='room rent' AND LOWER(LTRIM(RTRIM(res_status))) IN ('check out','checked out')");
                        unit.CheckOutRoomIds.AddRange(ids);
                        counter.SourceRows += ids.Count - 1;
                    }
                    if (await DestinationHasAsync(cn, area, req.DestinationHotelId, reg, ct))
                        counter.ExistingDestinationRows++;
                }
            }
            b.Counts.Add(counter);
        }
        b.Units.AddRange(bookings.Values.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase));
        if (b.Units.Count > 5000) throw new ArgumentException("Transfer contains more than 5,000 units. Reduce the date range.");
        if (req.ReplaceMatching)
            b.Warnings.Add("REPLACE is enabled. Only matching destination reservation IDs are eligible. Existing fiscal/gateway-linked bookings and incomplete payment histories are blocked; source bookings are never deleted.");
        b.Warnings.Add("Transferred financial records are historical copies, not new card charges. FBR/PRA references, Channex booking links and payment tokens are not copied.");
        b.Warnings.Add("Reservation and room transfers require a destination availability reconciliation after completion. The importer does not push availability to Channex.");
        return b;
    }
    private static async Task<List<string>> NoShowKeysFromAuditAsync(SqlConnection cn,string hotel,
        DateTime from,DateTime to,CancellationToken ct)
    {
        var keys=new List<string>();
        await using var cmd=Cmd(cn,null,@"
SELECT DISTINCT TOP (5001) n.reg_id
FROM dbo.NoShowTB n
WHERE n.hotel_id=@hotel AND EXISTS (
SELECT 1 FROM dbo.Reserv_log l
WHERE l.hotel_id=n.hotel_id AND l.reg_id=n.reg_id
AND LOWER(LTRIM(RTRIM(l.status))) IN ('no show','noshow','no-show')
AND COALESCE(TRY_CONVERT(date,l.createdat,126),TRY_CONVERT(date,l.createdat,121),
             TRY_CONVERT(date,l.date,110),TRY_CONVERT(date,l.date,103))>=@from
AND COALESCE(TRY_CONVERT(date,l.createdat,126),TRY_CONVERT(date,l.createdat,121),
             TRY_CONVERT(date,l.date,110),TRY_CONVERT(date,l.date,103))<DATEADD(day,1,@to))
ORDER BY n.reg_id");
        P(cmd,"@hotel",hotel);P(cmd,"@from",from.Date);P(cmd,"@to",to.Date);
        await using var reader=await cmd.ExecuteReaderAsync(ct);
        while(await reader.ReadAsync(ct))keys.Add(Text(reader[0]));
        if(keys.Count>5000)throw new ArgumentException("More than 5,000 no-show records. Narrow the period.");
        return keys;
    }

    private static async Task<List<string>> FilteredIdsAsync(SqlConnection cn, SqlTransaction? tx, string table,
        string hotel, string reg, string dateCol, DateTime from, DateTime to,
        CancellationToken ct, string extraWhere = "")
    {
        var result = new List<string>();
        await using var cmd = Cmd(cn, tx, $@"SELECT CONVERT(nvarchar(40),ID) AS pk FROM dbo.{Identifier(table)}
WHERE hotel_id=@hotel AND reg_id=@reg AND {DateExpr(dateCol)}>=@from
AND {DateExpr(dateCol)}<DATEADD(day,1,@to) {extraWhere} ORDER BY ID");
        P(cmd,"@hotel",hotel); P(cmd,"@reg",reg); P(cmd,"@from",from.Date); P(cmd,"@to",to.Date);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result.Add(Text(reader["pk"]));
        return result;
    }
    private static async Task<bool> DestinationHasAsync(SqlConnection cn, HotelTransferArea area,
        string destination, string reg, CancellationToken ct)
    {
        var table = SourceMap[area].Table;
        await using var cmd = Cmd(cn, null, $@"SELECT CASE WHEN EXISTS(
SELECT 1 FROM dbo.{Identifier(table)} WHERE hotel_id=@hotel AND reg_id=@reg)
THEN 1 ELSE 0 END");
        P(cmd,"@hotel",destination); P(cmd,"@reg",reg);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture) == 1;
    }
    public async Task<HotelTransferPreview> PreviewAsync(HotelTransferRequest req, string user, CancellationToken ct)
    {
        var b = await BuildPreviewAsync(req, user, ct);
        await WriteBatchAsync(b, ct);
        return Result(b);
    }
    public async Task<HotelTransferPreview> StartAsync(HotelTransferRequest req, string user, CancellationToken ct)
    {
        Validate(req, true);
        // Build a fresh preview so the confirmation cannot apply to a stale source/destination.
        var b = await BuildPreviewAsync(req, user, ct);
        b.Status = b.Units.Count == 0 ? "Completed" : "Running";
        await WriteBatchAsync(b, ct);
        return Result(b);
    }
    public async Task<HotelTransferPreview> ProgressAsync(Guid id, string destination, string user, CancellationToken ct)
        => Result(await ReadBatchAsync(id, destination, user, ct));

    public async Task<HotelTransferStepResult> StepAsync(Guid id, string destination, string user, CancellationToken ct)
    {
        var gate = Locks.GetOrAdd(id, _ => new SemaphoreSlim(1,1));
        await gate.WaitAsync(ct);
        try
        {
            var b = await ReadBatchAsync(id, destination, user, ct);
            if (b.Status != "Running") throw new InvalidOperationException("This batch is not running.");
            var messages = new List<string>();
            // Small synchronous chunk invoked repeatedly by the browser: real committed progress.
            foreach (var unit in b.Units.Where(x => x.Status == "Ready").Take(5))
            {
                try
                {
                    var copied = await ApplyUnitAsync(b, unit, ct);
                    unit.Status = copied ? "Copied" : "Skipped";
                    unit.Message = copied ? "Transferred" : "Already exists or no eligible rows";
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    unit.Status = "Failed";
                    unit.Message = ex.Message.Length > 250 ? ex.Message[..250] : ex.Message;
                    _logger.LogWarning(ex, "Hotel transfer {Batch}: {Area} {Key}", b.Id, unit.Category, unit.Key);
                }
                messages.Add(unit.Category + " " + unit.Key + ": " + unit.Status +
                    (unit.Status == "Failed" ? " — " + unit.Message : ""));
                await WriteBatchAsync(b, ct);
            }
            if (!b.Units.Any(x => x.Status == "Ready"))
            {
                b.Status = b.Units.Any(x => x.Status == "Failed") ? "CompletedWithErrors" : "Completed";
                await WriteBatchAsync(b, ct);
            }
            return new HotelTransferStepResult { Progress = Result(b), Messages = messages };
        }
        finally { gate.Release(); }
    }
    private static async Task<List<Dictionary<string,object?>>> ReadRowsAsync(SqlConnection cn, SqlTransaction tx,
        string table, string hotel, string? reg, CancellationToken ct)
    {
        var list = new List<Dictionary<string,object?>>();
        var where = table switch
        {
            "RoomsTB" => "Hotel_id=@hotel",
            _ => "hotel_id=@hotel"
        };
        if (reg != null) where += " AND reg_id=@reg";
        await using var cmd = Cmd(cn,tx,$"SELECT * FROM dbo.{Identifier(table)} WHERE {where}");
        P(cmd,"@hotel",hotel); if (reg != null) P(cmd,"@reg",reg);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var data = new Dictionary<string,object?>(StringComparer.OrdinalIgnoreCase);
            for (var i=0;i<reader.FieldCount;i++) data[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            list.Add(data);
            if (list.Count > 10000) throw new InvalidOperationException("Too many rows in this reservation. Split the source dataset.");
        }
        return list;
    }
    private static string Value(Dictionary<string,object?> row,string key) =>
        row.TryGetValue(key, out var val) ? Text(val) : "";
    private static DateTime? DateValue(Dictionary<string,object?> row, string key)
    {
        if (!row.TryGetValue(key,out var val) || val is null) return null;
        if (val is DateTime dt) return dt;
        foreach (var culture in new[] { CultureInfo.InvariantCulture, CultureInfo.GetCultureInfo("en-GB"), CultureInfo.GetCultureInfo("en-US") })
            if (DateTime.TryParse(Text(val), culture, DateTimeStyles.None,out dt)) return dt;
        return null;
    }
    private static decimal DecimalValue(Dictionary<string,object?> row,string key)
    {
        var s = Value(row,key).Replace(",", "").Replace("£", "").Replace("Rs.", "");
        return decimal.TryParse(s,NumberStyles.Number|NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture,out var v) ? v : 0m;
    }
    private static bool IsInRange(DateTime date, HotelTransferRequest req) =>
        date.Date >= req.FromDate.Date && date.Date <= req.ToDate.Date;

    private static async Task<int> LockRegAsync(SqlConnection cn, SqlTransaction tx, string dest, string reg, CancellationToken ct)
    {
        await using var cmd = Cmd(cn,tx,@"
DECLARE @result int;
EXEC @result=sp_getapplock @Resource=@lockName,@LockMode='Exclusive',
    @LockOwner='Transaction',@LockTimeout=10000;
SELECT @result;");
        P(cmd,"@lockName","HotelTransfer:"+dest+":"+reg);
        var result = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct),CultureInfo.InvariantCulture);
        if (result < 0) throw new InvalidOperationException("Another operation is modifying this destination reservation. Retry later.");
        return result;
    }
    private async Task<bool> MarkerExistsAsync(SqlConnection cn, SqlTransaction tx, string dest, string marker, CancellationToken ct)
    {
        await using var cmd = Cmd(cn,tx,@"SELECT TOP 1 1 FROM dbo.Reserv_log WITH(UPDLOCK,HOLDLOCK)
WHERE hotel_id=@hotel AND descp=@marker");
        P(cmd,"@hotel",dest);P(cmd,"@marker",marker);
        return await cmd.ExecuteScalarAsync(ct) != null;
    }
    private static string Marker(Batch b, Unit u) =>
        "HTX:"+b.Id.ToString("N")+":"+u.Category+":"+
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(u.Key)))[..16];
    private static async Task RecordMarkerAsync(SqlConnection cn, SqlTransaction tx, Batch b,Unit u,CancellationToken ct)
    {
        await using var cmd = Cmd(cn,tx,@"
INSERT INTO dbo.Reserv_log
(reg_id,descp,user_id,username,hotel_id,status,ipAddress,systemname,createdat,date)
VALUES(@reg,@text,@user,@user,@hotel,'HotelTransfer','ImportData','HotelTransfer',GETDATE(),CONVERT(varchar(10),GETDATE(),23));");
        P(cmd,"@reg",u.Category=="Booking"?u.Key:"");P(cmd,"@text",Marker(b,u));
        P(cmd,"@user",b.Owner);P(cmd,"@hotel",b.Request.DestinationHotelId);
        await cmd.ExecuteNonQueryAsync(ct);
    }
    private async Task<bool> ApplyUnitAsync(Batch b, Unit unit,CancellationToken ct)
    {
        await using var cn = await OpenAsync(ct);
        await using var tx = (SqlTransaction)await cn.BeginTransactionAsync(IsolationLevel.Serializable,ct);
        try
        {
            await LockDestinationTransferAsync(cn, tx, b.Request.DestinationHotelId, ct);
            if (b.DemoResetEpoch != await ReadDemoResetEpochAsync(ct))
                throw new InvalidOperationException("Transfer batch is stale after a demo reset. Create a new preview.");
            await LockRegAsync(cn,tx,b.Request.DestinationHotelId,unit.Key,ct);
            if (await MarkerExistsAsync(cn,tx,b.Request.DestinationHotelId,Marker(b,unit),ct))
            {
                await tx.CommitAsync(ct);return false;
            }
            var did = unit.Category switch
            {
                "Categories" => await CopyCategoryAsync(cn,tx,b.Request,unit.Key,ct),
                "Rooms" => await CopyRoomAsync(cn,tx,b.Request,unit.Key,ct),
                "RatePlans" => await CopyPlanAsync(cn,tx,b.Request,unit.Key,ct),
                "Booking" => await CopyBookingAsync(cn,tx,b,unit,ct),
                _ => throw new InvalidOperationException("Unknown transfer item.")
            };
            // Even skipped units are marked, so retry cannot delete/recopy them.
            await RecordMarkerAsync(cn,tx,b,unit,ct);
            await tx.CommitAsync(ct);
            return did;
        }
        catch { await tx.RollbackAsync(CancellationToken.None);throw; }
    }
    private async Task<List<Dictionary<string,object?>>> ReadKeyAsync(SqlConnection cn,SqlTransaction tx,
        string table,string hotel,string column,string key,CancellationToken ct)
    {
        if (!AllowedTables.Contains(table) || column is not ("description" or "room_no" or "planname" or "name"))
            throw new InvalidOperationException("Invalid lookup configuration.");
        var result = new List<Dictionary<string,object?>>();
        var hotelColumn=table=="RoomsTB"?"Hotel_id":"hotel_id";
        await using var cmd=Cmd(cn,tx,$"SELECT * FROM dbo.{Identifier(table)} WHERE {Identifier(hotelColumn)}=@hotel AND {Identifier(column)}=@key");
        P(cmd,"@hotel",hotel);P(cmd,"@key",key);
        await using var r=await cmd.ExecuteReaderAsync(ct);
        while(await r.ReadAsync(ct))
        {
            var dict=new Dictionary<string,object?>(StringComparer.OrdinalIgnoreCase);
            for(var i=0;i<r.FieldCount;i++)dict[r.GetName(i)]=r.IsDBNull(i)?null:r.GetValue(i);
            result.Add(dict);
        }
        return result;
    }
    private static string LocalId() => (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()*100000L+
        RandomNumberGenerator.GetInt32(10000,99999)).ToString(CultureInfo.InvariantCulture);
    private async Task<string> EnsureCategoryAsync(SqlConnection cn,SqlTransaction tx,HotelTransferRequest req,
        string category,CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(category))throw new InvalidOperationException("Room category name is empty.");
        var dest=await ReadKeyAsync(cn,tx,"create_room",req.DestinationHotelId,"description",category,ct);
        var matching=dest.FirstOrDefault(x=>Value(x,"category").Equals("Room Rent",StringComparison.OrdinalIgnoreCase));
        if(matching!=null)
        {
            var id=Value(matching,"localcategoryid");
            if(id.Length==0)throw new InvalidOperationException("Destination category has no localcategoryid: "+category);
            return id;
        }
        var src=await ReadKeyAsync(cn,tx,"create_room",req.SourceHotelId,"description",category,ct);
        var original=src.FirstOrDefault(x=>Value(x,"category").Equals("Room Rent",StringComparison.OrdinalIgnoreCase));
        if(original==null)throw new InvalidOperationException("Category does not exist in source: "+category);
        var newId=LocalId();
        await InsertRowAsync(cn,tx,"create_room",original,req.DestinationHotelId,new()
        {
            ["localcategoryid"]=newId,["no_of_rooms"]=0
        },ct);
        return newId;
    }
    private async Task<bool> CopyCategoryAsync(SqlConnection cn,SqlTransaction tx,HotelTransferRequest req,
        string category,CancellationToken ct)
    {
        var existing=await ReadKeyAsync(cn,tx,"create_room",req.DestinationHotelId,"description",category,ct);
        if(existing.Any(x=>Value(x,"category").Equals("Room Rent",StringComparison.OrdinalIgnoreCase)))return false;
        await EnsureCategoryAsync(cn,tx,req,category,ct);
        return true;
    }
    private async Task<bool> CopyRoomAsync(SqlConnection cn,SqlTransaction tx,HotelTransferRequest req,
        string roomNo,CancellationToken ct)
    {
        var existing=await ReadKeyAsync(cn,tx,"RoomsTB",req.DestinationHotelId,"room_no",roomNo,ct);
        // Never delete or overwrite a live destination physical room in a reservation transfer.
        if(existing.Count>0)return false;
        var source=await ReadKeyAsync(cn,tx,"RoomsTB",req.SourceHotelId,"room_no",roomNo,ct);
        var row=source.FirstOrDefault()??throw new InvalidOperationException("Source room not found: "+roomNo);
        var cat=Value(row,"room_category");
        var mapped=await EnsureCategoryAsync(cn,tx,req,cat,ct);
        await InsertRowAsync(cn,tx,"RoomsTB",row,req.DestinationHotelId,new()
        {
            ["localroomid"]=LocalId(),["localcategoryid"]=mapped,
            ["room_status"]="Available",["isassigned"]=0
        },ct);
        await using var cmd=Cmd(cn,tx,@"UPDATE dbo.create_room SET no_of_rooms=
(SELECT COUNT(*) FROM dbo.RoomsTB WHERE Hotel_id=@hotel AND localcategoryid=@cat)
WHERE hotel_id=@hotel AND localcategoryid=@cat");
        P(cmd,"@hotel",req.DestinationHotelId);P(cmd,"@cat",mapped);
        await cmd.ExecuteNonQueryAsync(ct);
        return true;
    }
    private async Task<string> MapPlanAsync(SqlConnection cn,SqlTransaction tx,HotelTransferRequest req,
        string category,string planName,CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(planName))return "";
        await using var cmd=Cmd(cn,tx,@"SELECT TOP 1 localplanid FROM dbo.category_plan
WHERE hotel_id=@hotel AND category=@cat AND planname=@plan ORDER BY id");
        P(cmd,"@hotel",req.DestinationHotelId);P(cmd,"@cat",category);P(cmd,"@plan",planName);
        return Text(await cmd.ExecuteScalarAsync(ct));
    }
    /// <summary>
    /// Copies a plan in the same two-table shape used by RatePlanService:
    /// dbo.plans is the parent; dbo.category_plan has one default rate per category.
    /// A destination hotel owns one *integer* localplanid shared by all its
    /// category_plan rows. Source-local category and plan IDs are never reused.
    /// Existing destination plan metadata and rates are not overwritten.
    /// </summary>
    private async Task<bool> CopyPlanAsync(SqlConnection cn, SqlTransaction tx,
        HotelTransferRequest req, string sourcePlanName, CancellationToken ct)
    {
        // Support older unprocessed batch files that used "category|plan" keys.
        // Newly generated batches always contain the plan name alone.
        var planName = sourcePlanName.Trim();
        if (planName.Length == 0)
            throw new InvalidOperationException("Source rate plan name is empty.");
        var headerRows = await ReadKeyAsync(cn, tx, "plans", req.SourceHotelId,
            "name", planName, ct);
        if (headerRows.Count == 0 && planName.Contains('|'))
        {
            planName = planName[(planName.IndexOf('|') + 1)..].Trim();
            headerRows = await ReadKeyAsync(cn, tx, "plans", req.SourceHotelId,
                "name", planName, ct);
        }
        if (headerRows.Count == 0)
            throw new InvalidOperationException(
                "Rate plan '" + planName + "' has no master row in source dbo.plans. " +
                "Review it in the Rate Plan module before transferring.");
        if (headerRows.Count > 1)
            throw new InvalidOperationException(
                "Source contains duplicate dbo.plans master rows for '" + planName + "'.");

        var destinationRows = await ReadKeyAsync(cn, tx, "plans",
            req.DestinationHotelId, "name", planName, ct);
        if (destinationRows.Count > 1)
            throw new InvalidOperationException(
                "Destination has duplicate dbo.plans rows for '" + planName + "'.");

        int destinationPlanId;
        bool changed = false;
        if (destinationRows.Count == 1)
        {
            if (!int.TryParse(Value(destinationRows[0], "localplanid"),
                    NumberStyles.Integer, CultureInfo.InvariantCulture, out destinationPlanId) ||
                destinationPlanId <= 0)
                throw new InvalidOperationException(
                    "Destination plan '" + planName + "' has an invalid localplanid.");
        }
        else
        {
            // Match RatePlanService.SaveDetailsAsync(): localplanid is an integer
            // allocated within this hotel; do not use the 18-digit room/category IDs.
            await using (var idCommand = Cmd(cn, tx, @"
SELECT ISNULL(MAX(TRY_CAST(localplanid AS int)), 0) + 1
FROM dbo.plans WITH (UPDLOCK, HOLDLOCK)
WHERE hotel_id=@hotel"))
            {
                P(idCommand, "@hotel", req.DestinationHotelId);
                destinationPlanId = Convert.ToInt32(
                    await idCommand.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
            }
            await InsertRowAsync(cn, tx, "plans", headerRows[0],
                req.DestinationHotelId, new Dictionary<string,object?>
                {
                    ["localplanid"] = destinationPlanId
                }, ct);
            changed = true;
        }

        var sourceCategoryRates = await ReadKeyAsync(cn, tx, "category_plan",
            req.SourceHotelId, "planname", planName, ct);
        var seenCategories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sourceRow in sourceCategoryRates)
        {
            var categoryName = Value(sourceRow, "category");
            if (categoryName.Length == 0)
                throw new InvalidOperationException(
                    "Rate plan '" + planName + "' contains a category with no name.");
            if (!seenCategories.Add(categoryName))
                throw new InvalidOperationException(
                    "Rate plan '" + planName + "' has duplicate default rows for category '" + categoryName + "'.");

            var destinationCategoryId = await EnsureCategoryAsync(cn, tx,
                req, categoryName, ct);
            // Match RatePlanService's unique rate key (hotel, planname, category_id).
            await using var existingCommand = Cmd(cn, tx, @"
SELECT TOP (1) localplanid FROM dbo.category_plan WITH (UPDLOCK, HOLDLOCK)
WHERE hotel_id=@hotel AND planname=@plan AND category_id=@category");
            P(existingCommand, "@hotel", req.DestinationHotelId);
            P(existingCommand, "@plan", planName);
            P(existingCommand, "@category", destinationCategoryId);
            var existingId = await existingCommand.ExecuteScalarAsync(ct);
            if (existingId is not null && existingId != DBNull.Value)
            {
                if (!string.Equals(Text(existingId),
                        destinationPlanId.ToString(CultureInfo.InvariantCulture),
                        StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        "Existing category rate for '" + planName + "' / '" + categoryName +
                        "' has a different localplanid. The existing rate was not overwritten.");
                continue;
            }
            // Another row with the same plan/category name but an invalid foreign
            // category ID is a conflict, not something to silently duplicate.
            await using (var conflictCommand = Cmd(cn, tx, @"
SELECT TOP (1) category_id FROM dbo.category_plan WITH (UPDLOCK, HOLDLOCK)
WHERE hotel_id=@hotel AND planname=@plan AND category=@category"))
            {
                P(conflictCommand, "@hotel", req.DestinationHotelId);
                P(conflictCommand, "@plan", planName);
                P(conflictCommand, "@category", categoryName);
                var conflictingCategory = await conflictCommand.ExecuteScalarAsync(ct);
                if (conflictingCategory is not null && conflictingCategory != DBNull.Value)
                    throw new InvalidOperationException(
                        "Destination has a rate plan row for '" + planName + "' / '" +
                        categoryName + "' with a different category ID. Review mapping first.");
            }
            // category_plan.category_id is the local create_room.localcategoryid,
            // NOT the source category's provider/channel category_id.
            await InsertRowAsync(cn, tx, "category_plan", sourceRow,
                req.DestinationHotelId, new Dictionary<string,object?>
                {
                    ["localplanid"] = destinationPlanId,
                    ["category_id"] = destinationCategoryId,
                    ["localcategoryid"] = destinationCategoryId,
                    ["plainid"] = null
                }, ct);
            changed = true;
        }
        // A plan with no category defaults is still a valid "Save Details only"
        // plan, as the existing Rate Plan module permits that state.
        return changed;
    }
    /// <summary>Schema-compatible copy; excludes identity, encrypted/sensitive and integration fields.
    /// Destination-specific room/category/plan IDs are passed as overrides.</summary>
    private static async Task InsertRowAsync(SqlConnection cn,SqlTransaction tx,string table,
        Dictionary<string,object?> source,string destination,Dictionary<string,object?> overrides,CancellationToken ct)
    {
        var schema=await MetadataAsync(cn,tx,table,ct);
        var names=new List<string>();var bindings=new List<string>();
        await using var cmd=Cmd(cn,tx,"");
        foreach(var col in schema)
        {
            if(col.Auto || SkipSqlTypes.Contains(col.SqlType))continue;
            var name=col.Name;
            var overriden=overrides.TryGetValue(name,out var value);
            if(!overriden && !source.TryGetValue(name,out value))continue;
            if(BlockedColumns.Contains(name) && !overriden)continue;
            // Fail closed for any additional confidential or tax-authority-specific fields.
            if(!overriden && new[] { "token", "secret", "password", "credential", "stripe_", "fbr_", "pra_", "channex_", "gateway_", "card_number", "cardnumber" }
                   .Any(term => name.Contains(term, StringComparison.OrdinalIgnoreCase)))continue;
            // Never transmit payment gateway or digital invoicing columns across hotels.
            if((name.StartsWith("di_",StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("fbr_",StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("stripe_",StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("channex_",StringComparison.OrdinalIgnoreCase)) && !overriden)continue;
            if(name.Equals("hotel_id",StringComparison.OrdinalIgnoreCase))value=destination;
            names.Add(Identifier(name));
            var parameter="@v"+names.Count;
            bindings.Add(parameter);
            P(cmd,parameter,value);
        }
        if(names.Count==0)throw new InvalidOperationException("No copyable fields in "+table);
        cmd.CommandText=$"INSERT INTO dbo.{Identifier(table)} ({string.Join(",",names)}) VALUES ({string.Join(",",bindings)});";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<bool> ExistsRegAsync(SqlConnection cn,SqlTransaction tx,string dest,string reg,CancellationToken ct)
    {
        // Include orphaned financial rows: a reservation must never be recreated on top
        // of preexisting payments or room charges for the same destination reg_id.
        foreach(var table in new[]{"NewReservationsTB","GuestInformationLogTB","CancelledReservationsTB","NoShowTB",
            "payments","PaymentsUpdateTB","PaymentsLogTB"})
            if(await CountAsync(cn,tx,table,dest,reg,ct)>0)return true;
        return false;
    }
    private static bool IsSensitiveValue(string field,string value)
    {
        if(value.Length==0 || value is "0" or "false" or "False")return false;
        return field.Contains("token",StringComparison.OrdinalIgnoreCase) ||
            field.Contains("fbr_invoice",StringComparison.OrdinalIgnoreCase) ||
            field.Contains("pra_invoice",StringComparison.OrdinalIgnoreCase) ||
            field.Contains("paymentid",StringComparison.OrdinalIgnoreCase) ||
            field.Contains("chargeid",StringComparison.OrdinalIgnoreCase) ||
            field.Contains("charge_id",StringComparison.OrdinalIgnoreCase) ||
            field.Contains("refundid",StringComparison.OrdinalIgnoreCase) ||
            field.Contains("checkout_session",StringComparison.OrdinalIgnoreCase) ||
            field.Contains("booking_id",StringComparison.OrdinalIgnoreCase) ||
            field.Contains("receipturl",StringComparison.OrdinalIgnoreCase) ||
            field.Contains("fiscal_invoice",StringComparison.OrdinalIgnoreCase);
    }
    /// <summary>Replacement is intentionally narrow: same reservation ID only, never an entire date range.
    /// Forbid deleting financial/fiscal/gateway-linked records and partial payment histories.</summary>
    private async Task DeleteConflictingBookingAsync(SqlConnection cn,SqlTransaction tx,HotelTransferRequest req,
        string reg,CancellationToken ct)
    {
        if(!TransactionAreas.All(req.Areas.Contains))
            throw new InvalidOperationException("Full six-module selection is required for replacement.");
        foreach(var hotel in new[]{req.SourceHotelId,req.DestinationHotelId})
        {
            foreach(var table in new[]{"PaymentsLogTB","NewReservationsTB","GuestInformationLogTB","payments",
                           "PaymentsUpdateTB","CancelledReservationsTB","NoShowTB"})
            {
                var rows=await ReadRowsAsync(cn,tx,table,hotel,reg,ct);
                foreach(var row in rows)
                {
                    // Reject existing live gateway or fiscal links in the destination.
                    if(hotel==req.DestinationHotelId && row.Any(pair=>IsSensitiveValue(pair.Key,Text(pair.Value))))
                        throw new InvalidOperationException("Destination reservation has a live gateway, channel or fiscal reference; cannot replace it automatically.");
                    if(table=="PaymentsLogTB")
                    {
                        var when=DateValue(row,"currentdate")
                            ?? throw new InvalidOperationException("Payment date cannot be parsed; replacement blocked.");
                        if(!IsInRange(when,req))
                            throw new InvalidOperationException("Reservation "+reg+" has source or destination payments outside the selected date range. Expand the date range before replacing it.");
                    }
                    // If a source or destination booking contains room stay dates outside the
                    // selection, that is allowed as a dependency, but no extra payment is copied.
                }
            }
        }
        foreach(var table in new[]{"NewReservationRate","PaymentsLogTB","PaymentsUpdateTB","payments",
            "GuestInformationLogTB","NewReservationsTB","CancelledReservationsTB","NoShowTB"})
        {
            await using var cmd=Cmd(cn,tx,$"DELETE FROM dbo.{Identifier(table)} WHERE hotel_id=@hotel AND reg_id=@reg");
            P(cmd,"@hotel",req.DestinationHotelId);P(cmd,"@reg",reg);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }
    private async Task<string> CategoryIdAsync(SqlConnection cn,SqlTransaction tx,HotelTransferRequest req,string category,CancellationToken ct)
        => await EnsureCategoryAsync(cn,tx,req,category,ct);
    private async Task<Dictionary<string,object?>> BookingOverridesAsync(SqlConnection cn,SqlTransaction tx,
        HotelTransferRequest req,string table,Dictionary<string,object?> row,CancellationToken ct)
    {
        var result=new Dictionary<string,object?>(StringComparer.OrdinalIgnoreCase);
        var category=table=="payments"?Value(row,"Type"):Value(row,"room_category");
        var planName=Value(row,"rateplanname");
        if(category.Length>0 && (table!="payments" ||
            Value(row,"descr").Equals("Room Rent",StringComparison.OrdinalIgnoreCase)))
        {
            var catId=await CategoryIdAsync(cn,tx,req,category,ct);
            if(table=="payments")result["category_id"]=catId;
            else result["localcategoryid"]=catId;
            var plan=await MapPlanAsync(cn,tx,req,category,planName,ct);
            if(table=="payments")result["rateplan"]=plan.Length==0?null:plan;
            else if(plan.Length>0)result["rateplan_id"]=plan;
        }
        // No retained external channel/DI tax identifiers; preserve Rate, discount, GST, Bed.
        if(table is "NewReservationsTB" or "GuestInformationLogTB")
        {
            // Blank, not copied from source: never treat an imported booking as a live OTA booking.
            result["booking_id"]="";
        }
        return result;
    }
    private async Task<bool> CopyBookingAsync(SqlConnection cn,SqlTransaction tx,Batch b,Unit item,CancellationToken ct)
    {
        var req=b.Request;var reg=item.Key;
        var sourceNR=await ReadRowsAsync(cn,tx,"NewReservationsTB",req.SourceHotelId,reg,ct);
        var sourceGI=await ReadRowsAsync(cn,tx,"GuestInformationLogTB",req.SourceHotelId,reg,ct);
        var sourceCancel=await ReadRowsAsync(cn,tx,"CancelledReservationsTB",req.SourceHotelId,reg,ct);
        var sourceNoShow=await ReadRowsAsync(cn,tx,"NoShowTB",req.SourceHotelId,reg,ct);
        var sourceCharges=await ReadRowsAsync(cn,tx,"payments",req.SourceHotelId,reg,ct);
        var sourcePaymentLog=await ReadRowsAsync(cn,tx,"PaymentsLogTB",req.SourceHotelId,reg,ct);
        if(sourceNR.Count==0 && sourceGI.Count==0 && sourceCancel.Count==0 && sourceNoShow.Count==0)
            throw new InvalidOperationException("Source has no reservation/stay record for this reg_id. Orphan payments were not copied.");
        if ((sourceCancel.Count>0 || sourceNoShow.Count>0) && (sourceNR.Count>0 || sourceGI.Count>0 || (sourceCancel.Count>0 && sourceNoShow.Count>0)))
            throw new InvalidOperationException("Source contains conflicting active and closed status records for this reservation. Review manually.");
        var destExists=await ExistsRegAsync(cn,tx,req.DestinationHotelId,reg,ct);
        if(destExists && req.ReplaceMatching)
        {
            // Atomic delete/recreate: if anything fails, the complete delete rolls back.
            await DeleteConflictingBookingAsync(cn,tx,req,reg,ct);
            destExists=false;
        }
        // A booking already exists: do not overwrite its stay/charges without replacement.
        // Payment-only transfer is allowed to an existing destination booking.
        var copyingParent=!destExists;
        if(destExists && item.Areas.Any(a=>a!=HotelTransferArea.Payments))
            throw new InvalidOperationException("Destination reservation already exists. Use carefully reviewed replacement, or select Payments alone to add dated transactions.");
        bool did=false;
        if(copyingParent)
        {
            // Preserve mixed-state, multi-room reservations: NR may hold the un-checked-in
            // rooms while GuestInformationLogTB contains checked-in rooms. Both can exist.
            // Do not collapse them to one header or alter room-level status flags.
            var parents = new List<(string Table,Dictionary<string,object?> Row)>();
            if(sourceNR.Count>0 || sourceGI.Count>0)
            {
                parents.AddRange(sourceNR.Select(x=>("NewReservationsTB",x)));
                parents.AddRange(sourceGI.Select(x=>("GuestInformationLogTB",x)));
            }
            else if(sourceCancel.Count>0)parents.AddRange(sourceCancel.Select(x=>("CancelledReservationsTB",x)));
            else parents.AddRange(sourceNoShow.Select(x=>("NoShowTB",x)));
            foreach(var (parentTable,row) in parents)
            {
                var room=Value(row,"room_no");
                if(room.Length>0 && !await RoomsExistAsync(cn,tx,req.DestinationHotelId,room,ct))
                    throw new InvalidOperationException("Destination room '"+room+"' does not exist. Select Rooms first or map the physical room.");
                await InsertRowAsync(cn,tx,parentTable,row,req.DestinationHotelId,
                    await BookingOverridesAsync(cn,tx,req,parentTable,row,ct),ct);
                did=true;
            }
            // Related room-level charge rows have their own IDs. Preserve each separate
            // charge, including its original tax/discount/date and its room-level status.
            foreach(var row in sourceCharges)
            {
                var room=Value(row,"room_no");
                if(room.Length>0 && !await RoomsExistAsync(cn,tx,req.DestinationHotelId,room,ct))
                    throw new InvalidOperationException("Cannot transfer room charge; room '"+room+"' is absent at destination.");
                await EnsureNoRoomConflictAsync(cn,tx,req.DestinationHotelId,reg,row,ct);
                await InsertRowAsync(cn,tx,"payments",row,req.DestinationHotelId,
                    await BookingOverridesAsync(cn,tx,req,"payments",row,ct),ct);
                did=true;
            }
            var rates=await ReadRowsAsync(cn,tx,"NewReservationRate",req.SourceHotelId,reg,ct);
            foreach(var row in rates)
            {
                // plan_name stores localplanid in ORA; resolve source id to name/category.
                var planId=Value(row,"plan_name");
                var mapped=await ResolveSourcePlanAsync(cn,tx,req,planId,ct);
                if(mapped.Length==0)continue; // Never keep a source-local plan ID.
                await InsertRowAsync(cn,tx,"NewReservationRate",row,req.DestinationHotelId,new()
                { ["plan_name"]=mapped },ct);
                did=true;
            }
            var summary=await ReadRowsAsync(cn,tx,"PaymentsUpdateTB",req.SourceHotelId,reg,ct);
            foreach(var row in summary.OrderByDescending(x=>long.TryParse(Value(x,"ID"),out var id)?id:0).Take(1))
            {
                await InsertRowAsync(cn,tx,"PaymentsUpdateTB",row,req.DestinationHotelId,new()
                {
                    ["paid_amount"]=0m,
                    ["remaining_amount"]=DecimalValue(row,"grand_total")
                },ct);
                did=true;
            }
        }
        // Copy only date-selected payment IDs; other historical receipts are not fabricated.
        var chosen=new HashSet<string>(item.PaymentIds,StringComparer.OrdinalIgnoreCase);
        foreach(var payment in sourcePaymentLog.Where(x=>chosen.Contains(Value(x,"ID"))))
        {
            if(await SimilarPaymentExistsAsync(cn,tx,req.DestinationHotelId,reg,payment,ct))continue;
            await InsertRowAsync(cn,tx,"PaymentsLogTB",payment,req.DestinationHotelId,new(),ct);
            did=true;
        }
        if(did)await RecalculatePaidAsync(cn,tx,req.DestinationHotelId,reg,ct);
        return did;
    }
    private static async Task<bool> RoomsExistAsync(SqlConnection cn,SqlTransaction tx,string hotel,string roomList,CancellationToken ct)
    {
        // Reservation headers can store "01,02" while payment charges store one room per row.
        var rooms=roomList.Split(',',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries);
        foreach(var room in rooms)
            if(!await RoomExistsAsync(cn,tx,hotel,room,ct))return false;
        return rooms.Length>0;
    }
    private static async Task<bool> RoomExistsAsync(SqlConnection cn,SqlTransaction tx,string hotel,string room,CancellationToken ct)
    {
        await using var cmd=Cmd(cn,tx,"SELECT TOP 1 1 FROM dbo.RoomsTB WHERE Hotel_id=@hotel AND room_no=@room");
        P(cmd,"@hotel",hotel);P(cmd,"@room",room);
        return await cmd.ExecuteScalarAsync(ct)!=null;
    }
    private static async Task EnsureNoRoomConflictAsync(SqlConnection cn,SqlTransaction tx,string hotel,string reg,
        Dictionary<string,object?> roomRow,CancellationToken ct)
    {
        if(!Value(roomRow,"descr").Equals("Room Rent",StringComparison.OrdinalIgnoreCase))return;
        if(new[]{"cancel","cancelled","canceled","no show","noshow","no-show"}
              .Contains(Value(roomRow,"res_status").ToLowerInvariant()))return;
        var room=Value(roomRow,"room_no");
        var arrival=DateValue(roomRow,"ArrivalDate");var departure=DateValue(roomRow,"DepartureDate");
        if(room.Length==0 || arrival is null || departure is null || departure<=arrival)return;
        await using var cmd=Cmd(cn,tx,@"
SELECT TOP 1 1 FROM dbo.payments WITH(UPDLOCK,HOLDLOCK)
WHERE hotel_id=@hotel AND room_no=@room AND reg_id<>@reg AND LOWER(LTRIM(RTRIM(descr)))='room rent'
AND LOWER(ISNULL(res_status,'')) NOT IN ('cancel','cancelled','canceled','no show','noshow','no-show')
AND COALESCE(TRY_CONVERT(date,ArrivalDate,110),TRY_CONVERT(date,ArrivalDate,103))<@departure
AND COALESCE(TRY_CONVERT(date,DepartureDate,110),TRY_CONVERT(date,DepartureDate,103))>@arrival");
        P(cmd,"@hotel",hotel);P(cmd,"@room",room);P(cmd,"@reg",reg);
        P(cmd,"@arrival",arrival.Value.Date);P(cmd,"@departure",departure.Value.Date);
        if(await cmd.ExecuteScalarAsync(ct)!=null)
            throw new InvalidOperationException("Room '"+room+"' overlaps a different destination reservation. No transfer performed.");
    }
    private static async Task<bool> SimilarPaymentExistsAsync(SqlConnection cn,SqlTransaction tx,string dest,string reg,
        Dictionary<string,object?> row,CancellationToken ct)
    {
        await using var cmd=Cmd(cn,tx,@"
SELECT TOP 1 1 FROM dbo.PaymentsLogTB WITH(UPDLOCK,HOLDLOCK)
WHERE hotel_id=@hotel AND reg_id=@reg AND payment_method=@method
AND TRY_CONVERT(decimal(18,2),paid_amount)=@amount
AND COALESCE(TRY_CONVERT(datetime2,currentdate,121),TRY_CONVERT(datetime2,currentdate,120),
TRY_CONVERT(datetime2,currentdate,126),TRY_CONVERT(datetime2,currentdate,103),
TRY_CONVERT(datetime2,currentdate,110),TRY_CONVERT(datetime2,currentdate,100),
TRY_CONVERT(datetime2,currentdate))=@date");
        P(cmd,"@hotel",dest);P(cmd,"@reg",reg);P(cmd,"@method",Value(row,"payment_method"));
        P(cmd,"@amount",DecimalValue(row,"paid_amount"));
        P(cmd,"@date",DateValue(row,"currentdate")??DateTime.MinValue);
        return await cmd.ExecuteScalarAsync(ct)!=null;
    }
    private static async Task RecalculatePaidAsync(SqlConnection cn,SqlTransaction tx,string hotel,string reg,CancellationToken ct)
    {
        await using var cmd=Cmd(cn,tx,@"
UPDATE dbo.PaymentsUpdateTB
SET paid_amount=COALESCE((SELECT SUM(COALESCE(TRY_CONVERT(decimal(18,2),l.paid_amount),0))
FROM dbo.PaymentsLogTB l WHERE l.hotel_id=@hotel AND l.reg_id=@reg),0),
remaining_amount=COALESCE(TRY_CONVERT(decimal(18,2),grand_total),0)-
COALESCE((SELECT SUM(COALESCE(TRY_CONVERT(decimal(18,2),l.paid_amount),0))
FROM dbo.PaymentsLogTB l WHERE l.hotel_id=@hotel AND l.reg_id=@reg),0)
WHERE hotel_id=@hotel AND reg_id=@reg;
UPDATE dbo.NewReservationsTB SET advance_paid =
    COALESCE((SELECT TOP 1 TRY_CONVERT(decimal(18,2),paid_amount) FROM dbo.PaymentsUpdateTB
              WHERE hotel_id=@hotel AND reg_id=@reg ORDER BY id DESC),0)
WHERE hotel_id=@hotel AND reg_id=@reg;
UPDATE dbo.GuestInformationLogTB SET advance_paid =
    COALESCE((SELECT TOP 1 TRY_CONVERT(decimal(18,2),paid_amount) FROM dbo.PaymentsUpdateTB
              WHERE hotel_id=@hotel AND reg_id=@reg ORDER BY id DESC),0)
WHERE hotel_id=@hotel AND reg_id=@reg;");
        P(cmd,"@hotel",hotel);P(cmd,"@reg",reg);
        await cmd.ExecuteNonQueryAsync(ct);
    }
    private async Task<string> ResolveSourcePlanAsync(SqlConnection cn,SqlTransaction tx,HotelTransferRequest req,
        string sourcePlanId,CancellationToken ct)
    {
        if(sourcePlanId.Length==0)return "";
        await using var cmd=Cmd(cn,tx,@"SELECT TOP 1 category,planname FROM dbo.category_plan WHERE hotel_id=@hotel AND localplanid=@plan");
        P(cmd,"@hotel",req.SourceHotelId);P(cmd,"@plan",sourcePlanId);
        await using var r=await cmd.ExecuteReaderAsync(ct);
        string cat="",name="";
        if(await r.ReadAsync(ct)){cat=Text(r["category"]);name=Text(r["planname"]);}
        await r.DisposeAsync();
        return cat.Length>0 && name.Length>0 ? await MapPlanAsync(cn,tx,req,cat,name,ct):"";
    }


    // -------------------------------------------------------- Demo hotel data reset
    // This is separate from ReplaceMatching. It never affects the source hotel.
    private string DemoResetFile(Guid token) => Path.Combine(_directory,
        "demo-reset-" + token.ToString("N") + ".json");

    private async Task WriteDemoResetTicketAsync(DemoResetTicket ticket, CancellationToken ct)
    {
        Directory.CreateDirectory(_directory);
        var file = DemoResetFile(ticket.Id);
        var temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(ticket, Json), ct);
            File.Move(temp, file, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private async Task<DemoResetTicket> ReadDemoResetTicketAsync(Guid token, string user, CancellationToken ct)
    {
        var file = DemoResetFile(token);
        if (token == Guid.Empty || !File.Exists(file))
            throw new ArgumentException("Demo reset preview not found. Preview again.");
        var ticket = JsonSerializer.Deserialize<DemoResetTicket>(await File.ReadAllTextAsync(file, ct), Json)
            ?? throw new InvalidOperationException("Invalid demo reset preview.");
        if (ticket.Id != token || ticket.Owner != user || ticket.DestinationHotelId != DemoHotelId)
            throw new ArgumentException("Demo reset preview does not belong to this user or hotel.");
        if (ticket.Status != "Preview")
            throw new InvalidOperationException("This reset preview was already used or is awaiting review. Preview again.");
        if (DateTime.UtcNow - ticket.CreatedUtc > TimeSpan.FromMinutes(15))
            throw new InvalidOperationException("Demo reset preview expired. Preview again.");
        return ticket;
    }

    private static async Task LockDestinationTransferAsync(SqlConnection cn, SqlTransaction tx,
        string hotelId, CancellationToken ct)
    {
        await using var cmd = Cmd(cn, tx, @"
DECLARE @lockResult int;
EXEC @lockResult = sys.sp_getapplock
    @Resource=@resource, @LockMode='Exclusive',
    @LockOwner='Transaction', @LockTimeout=15000;
SELECT @lockResult;");
        P(cmd, "@resource", "HotelTransferDestination:" + hotelId);
        if (Convert.ToInt32(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture) < 0)
            throw new InvalidOperationException("Another hotel transfer or reset is in progress.");
    }

    private static async Task<bool> DemoResetTableExistsAsync(SqlConnection cn, SqlTransaction? tx,
        string name, CancellationToken ct)
    {
        if (!DemoResetTables.Contains(name, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("Invalid demo reset table.");
        await using var cmd = Cmd(cn, tx, "SELECT OBJECT_ID(@tableName, 'U')");
        P(cmd, "@tableName", "dbo." + name);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is not null and not DBNull;
    }

    private static async Task<List<HotelDemoResetTableCount>> DemoResetCountsAsync(
        SqlConnection cn, SqlTransaction? tx, CancellationToken ct)
    {
        var counts = new List<HotelDemoResetTableCount>();
        foreach (var table in DemoResetTables)
        {
            if (!await DemoResetTableExistsAsync(cn, tx, table, ct))
            {
                counts.Add(new HotelDemoResetTableCount { Table = table, Exists = false });
                continue;
            }
            await using var cmd = Cmd(cn, tx,
                $"SELECT COUNT_BIG(*) FROM dbo.{Identifier(table)} WHERE hotel_id=@hotel");
            P(cmd, "@hotel", DemoHotelId);
            counts.Add(new HotelDemoResetTableCount
            {
                Table = table, Exists = true,
                Rows = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture)
            });
        }
        return counts;
    }

    public async Task<HotelDemoResetPreview> PreviewDemoResetAsync(string user, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(user)) throw new ArgumentException("Login is required.");
        await using var cn = await OpenAsync(ct);
        await using (var verify = Cmd(cn, null,
            "SELECT COUNT(*) FROM dbo.HotelsSignUpTB WHERE hotel_id=@hotel"))
        {
            P(verify, "@hotel", DemoHotelId);
            if (Convert.ToInt32(await verify.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture) != 1)
                throw new InvalidOperationException("The configured demo hotel was not found.");
        }
        var counts = await DemoResetCountsAsync(cn, null, ct);
        var ticket = new DemoResetTicket
        {
            Id = Guid.NewGuid(), Owner = user, DestinationHotelId = DemoHotelId,
            CreatedUtc = DateTime.UtcNow, Tables = counts
        };
        await WriteDemoResetTicketAsync(ticket, ct);
        return new HotelDemoResetPreview
        {
            Token = ticket.Id, DestinationHotelId = DemoHotelId,
            Tables = counts, TotalRows = counts.Sum(x => x.Rows),
            ExpiresUtc = ticket.CreatedUtc.AddMinutes(15)
        };
    }

    public async Task<HotelDemoResetResult> ResetDemoHotelAsync(Guid token, string user,
        string destinationHotelId, string confirmation, bool backupConfirmed, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(user) ||
            !string.Equals(destinationHotelId, DemoHotelId, StringComparison.Ordinal) ||
            !string.Equals(confirmation?.Trim(), DemoResetPhrase, StringComparison.Ordinal) ||
            !backupConfirmed)
            throw new ArgumentException("Demo reset requires the exact hotel, confirmation phrase and a verified backup.");
        var gate = DemoResetLocks.GetOrAdd(token, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var ticket = await ReadDemoResetTicketAsync(token, user, ct);
            if (ticket.Tables.Count != DemoResetTables.Length ||
                ticket.Tables.Select(x => x.Table).Where((x, i) =>
                    !string.Equals(x, DemoResetTables[i], StringComparison.OrdinalIgnoreCase)).Any())
                throw new InvalidOperationException("Unexpected reset scope; preview again.");

            // Persist the attempt BEFORE SQL executes. If the application crashes after
            // commit, the same token stays blocked instead of deleting freshly imported data.
            ticket.Status = "Attempting";
            await WriteDemoResetTicketAsync(ticket, ct);
            // Disable all previously prepared transfer batches BEFORE the deletion.
            // Even if SQL rolls back, stale batches must not be resumed silently.
            await InvalidateOlderBatchesAsync(ct);
            try
            {
                await using var cn = await OpenAsync(ct);
                await using var tx = (SqlTransaction)await cn.BeginTransactionAsync(IsolationLevel.Serializable, ct);
                try
                {
                    await LockDestinationTransferAsync(cn, tx, DemoHotelId, ct);
                    var current = await DemoResetCountsAsync(cn, tx, ct);
                    if (current.Count != ticket.Tables.Count || current.Where((x, i) =>
                        x.Table != ticket.Tables[i].Table || x.Exists != ticket.Tables[i].Exists ||
                        x.Rows != ticket.Tables[i].Rows).Any())
                        throw new InvalidOperationException("Demo data changed since preview. No rows deleted; preview again.");
                    long deleted = 0;
                    foreach (var count in current.Where(x => x.Exists))
                    {
                        await using var cmd = Cmd(cn, tx,
                            $"DELETE FROM dbo.{Identifier(count.Table)} WHERE hotel_id=@hotel");
                        cmd.CommandTimeout = 180;
                        P(cmd, "@hotel", DemoHotelId);
                        deleted += await cmd.ExecuteNonQueryAsync(ct);
                    }
                    // Validate scope inside the same transaction before commit.
                    var after = await DemoResetCountsAsync(cn, tx, ct);
                    if (after.Any(x => x.Exists && x.Rows != 0))
                        throw new InvalidOperationException("Demo reset left remaining rows; all changes rolled back.");
                    await tx.CommitAsync(ct);
                    ticket.DeletedTotal = checked((int)Math.Min(deleted, int.MaxValue));
                }
                catch
                {
                    await tx.RollbackAsync(CancellationToken.None);
                    throw;
                }
            }
            catch (Exception ex)
            {
                ticket.Status = "Failed";
                _logger.LogError(ex, "Demo hotel reset {Token} failed for {HotelId}", token, DemoHotelId);
                try { await WriteDemoResetTicketAsync(ticket, CancellationToken.None); }
                catch (Exception auditError) { _logger.LogError(auditError, "Demo reset failure audit could not be saved"); }
                throw;
            }
            ticket.Status = "Committed";
            try { await WriteDemoResetTicketAsync(ticket, CancellationToken.None); }
            catch (Exception auditError)
            {
                _logger.LogError(auditError, "Demo reset committed but disk audit could not be saved for {Token}", token);
            }
            _logger.LogWarning("Superuser {User} cleared {Count} records from demo hotel {Hotel}; reset token {Token}",
                user, ticket.DeletedTotal, DemoHotelId, token);
            return new HotelDemoResetResult
            {
                DestinationHotelId = DemoHotelId, DeletedRows = ticket.DeletedTotal,
                Message = "Demo hotel records cleared. Source hotel data was not modified."
            };
        }
        finally { gate.Release(); }
    }

}
