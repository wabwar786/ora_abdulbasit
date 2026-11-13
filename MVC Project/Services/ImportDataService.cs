using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using Orapmshms.Models;

namespace Orapmshms.Services;

/// <summary>
/// ImportData without schema changes: private JSON batch previews on the application
/// server and transactional import markers in the EXISTING dbo.Reserv_log.
/// Do not store the JSON folder under wwwroot; keep it backed up and accessible only
/// to the application identity. For multi-server hosting configure a shared path.
/// </summary>
public sealed class ImportDataService
{
    private readonly string _cs;
    private readonly ILogger<ImportDataService> _logger;
    private readonly IHotelClock _clock;
    private readonly string _batchRoot;
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> BatchGates = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private sealed class StoredRow
    {
        public StoredRow() { }
        public int Number { get; set; }
        public string Key { get; set; } = "";
        public string Summary { get; set; } = "";
        public string Status { get; set; } = "Ready";
        public string Message { get; set; } = "";
        public string TargetRegId { get; set; } = "";
        public Dictionary<string, string> Data { get; set; } = new();
    }

    private sealed class BatchDocument
    {
        public BatchDocument() { }
        public Guid BatchId { get; set; }
        public string HotelId { get; set; } = "";
        public string CreatedBy { get; set; } = "";
        public string SourceSystem { get; set; } = "";
        public string Kind { get; set; } = "";
        public string FileName { get; set; } = "";
        public string Status { get; set; } = "Preview";
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
        public DateTime? AppliedUtc { get; set; }
        public List<StoredRow> Rows { get; set; } = new();
    }

    public ImportDataService(IConfiguration configuration, IWebHostEnvironment environment,
        IHotelClock clock, ILogger<ImportDataService> logger)
    {
        _cs = configuration.GetConnectionString("con")
            ?? throw new InvalidOperationException("ConnectionStrings:con not configured.");
        _clock = clock;
        _logger = logger;
        // StoragePath may be an absolute directory on a persistent/shared disk.
        // Default is outside wwwroot and requires no SQL schema migration.
        var configured = configuration["ImportData:StoragePath"];
        _batchRoot = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(environment.ContentRootPath, "App_Data", "ImportData")
            : Path.GetFullPath(configured);
    }

    private string HotelDirectory(string hotel)
        => Path.Combine(_batchRoot, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(hotel)))[..32]);
    private string BatchPath(string hotel, Guid batch)
        => Path.Combine(HotelDirectory(hotel), batch.ToString("N", CultureInfo.InvariantCulture) + ".json");

    private async Task<BatchDocument> ReadBatchAsync(string hotel, Guid batch, CancellationToken ct)
    {
        var path = BatchPath(hotel, batch);
        if (!File.Exists(path)) throw new UnauthorizedAccessException("Import batch was not found for this hotel.");
        var text = await File.ReadAllTextAsync(path, ct);
        var doc = JsonSerializer.Deserialize<BatchDocument>(text, JsonOptions)
            ?? throw new InvalidOperationException("Import batch cannot be read.");
        if (doc.BatchId != batch || !string.Equals(doc.HotelId, hotel, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("The import batch belongs to another hotel.");
        return doc;
    }

    private async Task SaveBatchAsync(BatchDocument doc, CancellationToken ct)
    {
        var path = BatchPath(doc.HotelId, doc.BatchId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(doc, JsonOptions), ct);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private async Task<SqlConnection> OpenAsync(CancellationToken ct)
    {
        var cn = new SqlConnection(_cs);
        await cn.OpenAsync(ct);
        return cn;
    }

    // Superuser selector: list every hotel, not only the hotel currently in session.
    // Import operations still use the expressly selected hotel ID.
    public async Task<List<ImportDataHotelOption>> HotelsAsync(CancellationToken ct)
    {
        var hotels = new List<ImportDataHotelOption>();
        await using var cn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(@"
SELECT hotel_id, name, currency_sign
FROM dbo.HotelsSignUpTB
WHERE hotel_id IS NOT NULL
ORDER BY name, hotel_id;", cn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync(ct))
        {
            var id = Convert.ToString(reader["hotel_id"], CultureInfo.InvariantCulture)?.Trim() ?? "";
            if (id.Length == 0 || !seen.Add(id)) continue;
            var name = Convert.ToString(reader["name"], CultureInfo.InvariantCulture)?.Trim() ?? "";
            hotels.Add(new ImportDataHotelOption
            {
                HotelId = id,
                HotelName = name.Length == 0 ? id : name,
                Currency = Convert.ToString(reader["currency_sign"], CultureInfo.InvariantCulture)?.Trim() ?? ""
            });
        }
        return hotels;
    }

    public async Task<bool> HotelExistsAsync(string hotelId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(hotelId) || hotelId.Length > 50) return false;
        await using var cn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(@"
SELECT CASE WHEN EXISTS
    (SELECT 1 FROM dbo.HotelsSignUpTB WHERE hotel_id = @hotelId)
    THEN 1 ELSE 0 END;", cn);
        S(cmd, "@hotelId", hotelId.Trim(), 50);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture) == 1;
    }

    private static ImportDataBatchInfo Summarize(BatchDocument b) => new()
    {
        BatchId = b.BatchId, Kind = b.Kind, SourceSystem = b.SourceSystem,
        FileName = b.FileName, Status = b.Status, CreatedUtc = b.CreatedUtc,
        Ready = b.Rows.Count(r => r.Status == "Ready"),
        Invalid = b.Rows.Count(r => r.Status == "Invalid"),
        Imported = b.Rows.Count(r => r.Status == "Imported"),
        Failed = b.Rows.Count(r => r.Status == "Failed"),
        Duplicates = b.Rows.Count(r => r.Status == "Duplicate")
    };

    public async Task<List<ImportDataBatchInfo>> HistoryAsync(string hotel, CancellationToken ct)
    {
        var folder = HotelDirectory(hotel);
        if (!Directory.Exists(folder)) return new List<ImportDataBatchInfo>();
        var list = new List<ImportDataBatchInfo>();
        foreach (var file in Directory.EnumerateFiles(folder, "*.json")
                     .OrderByDescending(File.GetLastWriteTimeUtc).Take(100))
        {
            try
            {
                if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(file), "N", out var id)) continue;
                var doc = await ReadBatchAsync(hotel, id, ct);
                list.Add(Summarize(doc));
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Skipping unreadable ImportData batch file {File}", file);
            }
        }
        return list.OrderByDescending(x => x.CreatedUtc).Take(20).ToList();
    }

    public async Task<ImportDataPreviewResult> PreviewAsync(string hotel, string user, ImportDataKind kind,
        string source, IFormFile file, CancellationToken ct)
    {
        source = (source ?? "").Trim();
        if (source.Length is < 2 or > 80 || source.Any(char.IsControl))
            throw new ArgumentException("Enter a source system name (2 to 80 characters), e.g. HOP.");
        var rows = await ImportDataFileReader.ReadAsync(file, kind, ct);
        var doc = new BatchDocument
        {
            BatchId = Guid.NewGuid(), HotelId = hotel, CreatedBy = user,
            SourceSystem = source, Kind = kind.ToString(),
            FileName = Path.GetFileName(file.FileName), CreatedUtc = DateTime.UtcNow
        };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var cn = await OpenAsync(ct);
        for (var i = 0; i < rows.Count; i++)
        {
            var data = rows[i];
            var key = Key(kind, data);
            var issue = Validate(kind, data);
            var status = issue.Length > 0 ? "Invalid" : "Ready";
            if (status == "Ready" && !seen.Add(key))
            { status = "Duplicate"; issue = "Duplicate source key in this file."; }
            if (status == "Ready" && await AlreadyAppliedAsync(cn, null, hotel, source, kind, key, ct))
            { status = "Duplicate"; issue = "Already imported from this source."; }
            if (status == "Ready")
            {
                issue = await CheckDependencyAsync(cn, null, hotel, source, kind, data, ct);
                if (issue.Length > 0) status = "Invalid";
            }
            if (status == "Ready" && kind == ImportDataKind.CheckOuts && V(data,"CheckOutDateInferred") == "1")
                issue = "HOP Departure Date used as the checkout date; actual checkout time not provided. Historical Room Rent charges may be created, but no payments.";
            if (status == "Ready" && kind == ImportDataKind.NoShows && V(data,"NoShowDateInferred") == "1")
                issue = "HOP provides no no-show event timestamp or amount; arrival date is used only as a historical marker, and unknown revenue is not inferred.";
            doc.Rows.Add(new StoredRow { Number = i + 2, Key = key, Summary = Summary(kind, data),
                Status = status, Message = issue, Data = data });
        }
        await SaveBatchAsync(doc, ct);
        return ToPreview(doc);
    }

    private static ImportDataPreviewResult ToPreview(BatchDocument doc) => new()
    {
        BatchId = doc.BatchId, Kind = doc.Kind,
        Ready = doc.Rows.Count(r => r.Status == "Ready"),
        Invalid = doc.Rows.Count(r => r.Status == "Invalid"),
        Duplicates = doc.Rows.Count(r => r.Status == "Duplicate"),
        Rows = doc.Rows.Select(r => new ImportDataRowInfo
        { Number = r.Number, Key = r.Key, Summary = r.Summary, Status = r.Status,
          Message = r.Message, TargetRegId = r.TargetRegId }).ToList()
    };

    public async Task<ImportDataPreviewResult> GetBatchAsync(string hotel, Guid batch, CancellationToken ct)
        => ToPreview(await ReadBatchAsync(hotel, batch, ct));

    // Lightweight progress snapshot: includes only counters, never guest/payment fields.
    // Read the same atomic batch file used by the importer, so results reflect committed rows.
    public async Task<ImportDataProgress> GetProgressAsync(string hotel, Guid batch, CancellationToken ct)
    {
        var doc = await ReadBatchAsync(hotel, batch, ct);
        var reconciled = doc.Rows.Count(r => r.Status == "Imported" &&
            r.Message.StartsWith("Previously committed", StringComparison.Ordinal));
        var imported = doc.Rows.Count(r => r.Status == "Imported") - reconciled;
        var failed = doc.Rows.Count(r => r.Status == "Failed");
        var remaining = doc.Rows.Count(r => r.Status == "Ready");
        var invalid = doc.Rows.Count(r => r.Status == "Invalid");
        var duplicates = doc.Rows.Count(r => r.Status == "Duplicate");
        var processed = imported + reconciled + failed;
        var eligible = processed + remaining;
        return new ImportDataProgress
        {
            BatchId = doc.BatchId, Status = doc.Status, TotalRows = doc.Rows.Count,
            EligibleRows = eligible, Imported = imported, Failed = failed,
            Skipped = duplicates + reconciled,
            Invalid = invalid, Remaining = remaining, Processed = processed,
            Percent = eligible == 0 ? 0 : (int)Math.Floor(processed * 100.0 / eligible)
        };
    }

    public async Task<ImportDataCommitResult> CommitAsync(string hotel, string user, string userName,
        Guid batch, CancellationToken ct)
    {
        var gate = BatchGates.GetOrAdd(batch, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var doc = await ReadBatchAsync(hotel, batch, ct);
            if (!Enum.TryParse<ImportDataKind>(doc.Kind, out var kind) || kind == ImportDataKind.Categories)
                throw new InvalidOperationException("Use Rooms: missing categories are created automatically.");
            var result = new ImportDataCommitResult { BatchId = batch };
            // Short transactions per HTTP request let the UI show genuine progress
            // without a long request timing out mid-import.
            doc.Status = "Processing";
            await SaveBatchAsync(doc, ct);
            await using var cn = await OpenAsync(ct);
            foreach (var row in doc.Rows.Where(r => r.Status == "Ready").Take(20).ToList())
            {
                await using var tx = (SqlTransaction)await cn.BeginTransactionAsync(IsolationLevel.Serializable, ct);
                try
                {
                    // Transaction-owned application lock also protects simultaneous uploads
                    // on different web servers using the same SQL database.
                    await AcquireKeyLockAsync(cn, tx, hotel, doc.SourceSystem, kind, row.Key, ct);
                    if (await AlreadyAppliedAsync(cn, tx, hotel, doc.SourceSystem, kind, row.Key, ct))
                    {
                        row.Status = "Imported";
                        row.TargetRegId = kind == ImportDataKind.Reservations
                            ? await GetMappedRegAsync(cn, tx, hotel, doc.SourceSystem, V(row.Data,"ReservationID"), ct) : "";
                        row.Message = "Previously committed; reconciled from reservation audit.";
                        await tx.CommitAsync(ct);
                        result.Skipped++;
                    }
                    else
                    {
                        var issue = Validate(kind, row.Data);
                        if (issue.Length == 0) issue = await CheckDependencyAsync(cn, tx, hotel, doc.SourceSystem, kind, row.Data, ct);
                        if (issue.Length > 0) throw new InvalidOperationException(issue);
                        var reg = await ApplyAsync(cn, tx, hotel, user, userName, doc.SourceSystem, kind, batch, row.Data, ct);
                        // Reservations use two markers: one permanent source booking mapping
                        // (written by AddReservationAsync) and one marker per HOP room item.
                        if (kind != ImportDataKind.Reservations ||
                            !string.Equals(row.Key, V(row.Data,"ReservationID"),StringComparison.OrdinalIgnoreCase))
                            await WriteImportAuditAsync(cn, tx, hotel, reg, user, userName, doc.SourceSystem, kind, row.Key, ct);
                        await tx.CommitAsync(ct);
                        row.Status = "Imported";
                        row.TargetRegId = reg;
                        row.Message = kind == ImportDataKind.CheckOuts && V(row.Data,"CheckOutDateInferred") == "1"
                            ? "Imported using HOP Departure Date; actual checkout time unknown and no payment assumed." : "";
                        result.Imported++;
                    }
                    result.Rows.Add(new ImportDataRowInfo { Number = row.Number, Key = row.Key,
                        Status = row.Status, TargetRegId = row.TargetRegId, Message = row.Message });
                }
                catch (Exception ex) when (ex is SqlException or InvalidOperationException or ArgumentException)
                {
                    try { await tx.RollbackAsync(ct); } catch (Exception rollback) when (rollback is SqlException or InvalidOperationException) { }
                    _logger.LogWarning(ex, "ImportData row failed. Hotel={Hotel} Batch={Batch} Row={Row}", hotel, batch, row.Number);
                    row.Status = "Failed";
                    row.Message = ex is SqlException
                        ? "Database validation failed; see application server logs."
                        : ex.Message;
                    result.Failed++;
                    result.Rows.Add(new ImportDataRowInfo { Number = row.Number, Key = row.Key,
                        Status = row.Status, Message = row.Message });
                }
                // Persist status immediately after every row. If the process stops after a
                // SQL commit, the dbo.Reserv_log marker reconciles the row on the next run.
                await SaveBatchAsync(doc, ct);
            }
            result.Remaining = doc.Rows.Count(r => r.Status == "Ready");
            doc.Status = result.Remaining > 0 || doc.Rows.Any(r => r.Status is "Invalid" or "Failed")
                ? "Partial" : "Completed";
            doc.AppliedUtc = DateTime.UtcNow;
            await SaveBatchAsync(doc, ct);
            return result;
        }
        finally { gate.Release(); }
    }

    private static string ImportHash(string hotel, string source, ImportDataKind kind, string key)
    {
        var raw = hotel + "\u001f" + source.Trim().ToUpperInvariant() + "\u001f"
            + kind.ToString() + "\u001f" + key.Trim().ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
    }
    private static string AuditMarker(string hotel, string source, ImportDataKind kind, string key)
        => "IMPv1|" + kind.ToString() + "|" + ImportHash(hotel, source, kind, key);

    private static async Task AcquireKeyLockAsync(SqlConnection cn, SqlTransaction tx, string hotel,
        string source, ImportDataKind kind, string key, CancellationToken ct)
    {
        await using var cmd = Cmd(cn, tx, @"
DECLARE @lockResult int;
EXEC @lockResult = sys.sp_getapplock @Resource=@lockKey, @LockMode='Exclusive',
    @LockOwner='Transaction', @LockTimeout=10000;
SELECT @lockResult;");
        S(cmd, "@lockKey", "ImportData:" + ImportHash(hotel, source, kind, key), 100);
        var result = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
        if (result < 0) throw new InvalidOperationException("Import row is being processed by another request. Retry later.");
    }

    private static async Task WriteImportAuditAsync(SqlConnection cn, SqlTransaction tx, string hotel,
        string reg, string user, string userName, string source, ImportDataKind kind, string key, CancellationToken ct)
    {
        await using var cmd = Cmd(cn, tx, @"
INSERT INTO dbo.Reserv_log(reg_id,descp,user_id,username,hotel_id,status,ipAddress,systemname,createdat,date)
VALUES(@reg,@marker,@user,@username,@hotel,'ImportData','ImportData','ImportData',@now,@today)");
        S(cmd, "@reg", string.IsNullOrWhiteSpace(reg) ? "IMPORT" : reg, 100);
        S(cmd, "@marker", AuditMarker(hotel, source, kind, key), 120);
        S(cmd, "@user", user, 100); S(cmd, "@username", userName, 150); S(cmd, "@hotel", hotel, 100);
        var now = DateTime.UtcNow;
        D(cmd, "@now", now);
        S(cmd, "@today", now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), 10);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static string Key(ImportDataKind kind,Dictionary<string,string> x) =>
        (kind switch
        {
            ImportDataKind.Categories => V(x,"Category"),
            ImportDataKind.Rooms => V(x,"RoomNo"),
            ImportDataKind.Reservations or ImportDataKind.CheckIns => V(x,"ItemID").Length>0
                ? V(x,"ItemID") : V(x,"ReservationID"),
            ImportDataKind.Payments => V(x,"ReservationID")+"|"+V(x,"PaymentID"),
            ImportDataKind.Cancellations or ImportDataKind.NoShows =>
                V(x,"ReservationID")+"|"+V(x,"RoomNo"),
            ImportDataKind.CheckOuts => V(x,"ItemID").Length > 0 ? V(x,"ItemID") :
                V(x,"RoomNo").Length > 0 ? V(x,"ReservationID")+"|"+V(x,"RoomNo") : V(x,"ReservationID"),
            _ => V(x,"ReservationID")
        }).Trim().ToLowerInvariant();

    private static string Summary(ImportDataKind k,Dictionary<string,string> x) => k switch
    {
        ImportDataKind.Categories => V(x,"Category"),
        ImportDataKind.Rooms => V(x,"RoomNo")+" · "+V(x,"Category"),
        ImportDataKind.Payments => V(x,"ReservationID")+" · "+V(x,"Amount")+" · "+V(x,"Method"),
        ImportDataKind.Reservations => V(x,"ReservationID")+" / "+V(x,"ItemID")+" · "+V(x,"RoomNo")+" · "+V(x,"Arrival")+" → "+V(x,"Departure"),
        ImportDataKind.CheckOuts => V(x,"ReservationID")+" / "+V(x,"RoomNo")+" · "+V(x,"CheckOutAt")+
            (V(x,"CheckOutDateInferred")=="1" ? " (departure date only)" : ""),
        _ => V(x,"ReservationID")+" · "+(V(x,"CheckInAt")+V(x,"CheckOutAt")+V(x,"OccurredAt"))
    };

    private static string Validate(ImportDataKind kind,Dictionary<string,string> x)
    {
        // Downloaded templates include clearly labelled examples for guidance.
        // Never allow an example row to create live rooms, reservations or payments.
        if (new[] { "RoomNo", "ReservationID", "ItemID" }
            .Any(key => V(x,key).StartsWith("SAMPLE-", StringComparison.OrdinalIgnoreCase)))
            return "Template example row: delete it before importing real hotel data.";
        static bool Missing(Dictionary<string,string> d,string k)=>string.IsNullOrWhiteSpace(V(d,k));
        var required=kind switch
        {
            ImportDataKind.Categories=>new[]{"Category"},
            ImportDataKind.Rooms=>new[]{"RoomNo","Category"},
            ImportDataKind.Reservations=>new[]{"ReservationID","Guest","Arrival","Departure","RoomNo","Total"},
            ImportDataKind.CheckIns=>new[]{"ReservationID","CheckInAt"},
            ImportDataKind.CheckOuts=>new[]{"ReservationID"},
            ImportDataKind.Payments=>new[]{"PaymentID","ReservationID","PaymentAt","Amount","Method"},
            ImportDataKind.Cancellations=>new[]{"ReservationID","RoomNo","Guest","Arrival","Nights","Total","OccurredAt"},
            ImportDataKind.NoShows=>new[]{"ReservationID","RoomNo","Guest","Arrival","Nights","OccurredAt"},
            _=>new[]{"ReservationID","OccurredAt"}
        };
        foreach(var field in required)if(Missing(x,field))return field+" is required.";
        // HOP Reservation Number is used directly as ORA reg_id (varchar(50)).
        // Never truncate or regenerate it: block invalid identifiers in the preview.
        if(kind is ImportDataKind.Reservations or ImportDataKind.CheckIns or ImportDataKind.CheckOuts
            or ImportDataKind.Payments or ImportDataKind.Cancellations or ImportDataKind.NoShows)
        {
            var originalId=V(x,"ReservationID");
            if(originalId.Length>50 || originalId.Any(char.IsControl))
                return "HOP Reservation Number must be at most 50 characters without control characters.";
        }
        foreach(var val in x.Values)if(val.Length>500)return "A cell exceeds 500 characters.";
        if(Key(kind,x).Length>200)return "Source identifier exceeds 200 characters.";
        if(kind==ImportDataKind.Rooms && V(x,"Occupany").Length>0)
        {
            var occupancy=V(x,"Occupany").Split('.');
            if(occupancy.Length!=3 || occupancy.Any(v=>!int.TryParse(v,out var n)||n<0||n>30))
                return "Occupany must be in the HOP format 0.2.1 (cots.adults.children).";
        }
        if(kind==ImportDataKind.Reservations)
        {
            if(!TryDate(V(x,"Arrival"),out var a)||!TryDate(V(x,"Departure"),out var d)||d.Date<=a.Date)
                return "Arrival and Departure must be valid dates, with Departure after Arrival.";
            if((d.Date-a.Date).Days>730)return "A reservation cannot exceed 730 nights.";
            if(!TryMoney(V(x,"Total"),out var total)||total<0)return "Invalid reservation total.";
            if(!OptionalMoney(x,"GST",out var gst)||!OptionalMoney(x,"Bed",out var bed)||gst<0||bed<0||gst+bed>total)
                return "GST/Bed values must be non-negative and cannot exceed Total.";
            if(V(x,"Status").Length>0 && !string.Equals(V(x,"Status"),"Confirmed",StringComparison.OrdinalIgnoreCase))
                return "Only Confirmed reservations may be imported here.";
        }
        if(kind==ImportDataKind.Payments)
        {
            if(!TryMoney(V(x,"Amount"),out var amount)||amount<=0)return "Payment amount must be positive. Refunds must use the refund module.";
            if(!TryDate(V(x,"PaymentAt"),out _))return "Invalid payment date/time.";
            if(string.Equals(V(x,"HasPreAuth"),"True",StringComparison.OrdinalIgnoreCase))
                return "Pre-authorisation indicated: verify settlement before importing as a payment.";
        }
        if(kind==ImportDataKind.CheckIns)
        {
            if(!TryDate(V(x,"CheckInAt"),out _))return "Invalid check-in date/time.";
            if(V(x,"Status").Length>0 && !string.Equals(V(x,"Status"),"Checked In",StringComparison.OrdinalIgnoreCase))
                return "Only Checked In rows belong in this section.";
        }
        if(kind==ImportDataKind.CheckOuts)
        {
            if(V(x,"CheckOutAt").Length==0)
                return "Actual CheckOutAt is missing. For a HOP Reservations export, Status must be Checked Out and Departure Date must be valid.";
            if(!TryDate(V(x,"CheckOutAt"),out _)) return "Invalid checkout date/time.";
            if(V(x,"CheckOutDateInferred")=="1" && V(x,"RoomNo").Length==0)
                return "HOP checkout import requires Room Number to identify the room item.";
        }
        if(kind is ImportDataKind.Cancellations or ImportDataKind.NoShows)
        {
            if(!TryDate(V(x,"OccurredAt"),out _))return "Invalid status date/time.";
            if(!TryDate(V(x,"Arrival"),out _))return "Invalid DateOfArrival.";
            if(!int.TryParse(V(x,"Nights"),out var nights) || nights<1 || nights>730)
                return "NumberOfNights must be between 1 and 730.";
        }
        if(kind==ImportDataKind.Categories)
            foreach(var f in new[]{"Adults","Children","Cots"})
                if(V(x,f).Length>0&&(!int.TryParse(V(x,f),out var n)||n<0||n>100))return "Invalid "+f+" capacity.";
        return "";
    }

    private static bool OptionalMoney(Dictionary<string,string> x,string k,out decimal v)
    {v=0;return V(x,k).Length==0||TryMoney(V(x,k),out v);}
    private static bool TryMoney(string raw,out decimal value)
    {
        raw=raw.Trim().Replace("£","").Replace("$","").Replace("€","").Replace("Rs.","",StringComparison.OrdinalIgnoreCase)
            .Replace(",","").Trim();
        return decimal.TryParse(raw,NumberStyles.AllowLeadingSign|NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture,out value) && Math.Abs(value)<=1_000_000_000m;
    }
    private static bool TryDate(string raw,out DateTime result)
    {
        // A true datetime is retained; explicit formats avoid locale-dependent day/month swaps.
        string[] formats={"yyyy-MM-ddTHH:mm:ss","yyyy-MM-ddTHH:mm","yyyy-MM-dd HH:mm:ss","yyyy-MM-dd HH:mm",
            "yyyy-MM-dd","dd/MM/yyyy HH:mm:ss","dd/MM/yyyy HH:mm","dd/MM/yyyy","d/M/yyyy", "MM-dd-yyyy", "MM-dd-yyyy HH:mm:ss",
            "MMM d yyyy h:mmtt","MMM d yyyy h:mm tt","dd MMM yyyy HH:mm","dd MMM yyyy"};
        if(DateTime.TryParseExact(raw.Trim(),formats,CultureInfo.InvariantCulture,DateTimeStyles.AllowWhiteSpaces,out result))return true;
        if(double.TryParse(raw,CultureInfo.InvariantCulture,out var excel)&&excel>20000&&excel<90000)
        {try { result=DateTime.FromOADate(excel); return true; }catch(ArgumentException){}}
        result=default;return false;
    }
    private static string V(Dictionary<string,string> x,string name) => x.TryGetValue(name,out var text)?text?.Trim()??"":"";

    private static async Task<bool> AlreadyAppliedAsync(SqlConnection cn,SqlTransaction? tx,string hotel,string source,
        ImportDataKind kind,string key,CancellationToken ct)
    {
        await using var cmd=Cmd(cn,tx,@"SELECT TOP 1 1 FROM dbo.Reserv_log
WHERE hotel_id=@hotel AND status='ImportData' AND descp=@marker");
        S(cmd,"@hotel",hotel);S(cmd,"@marker",AuditMarker(hotel,source,kind,key),120);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    private static bool HasHistoricalCheckoutSnapshot(Dictionary<string,string> x) =>
        V(x,"CheckOutDateInferred") == "1" &&
        new[] {"ReservationID", "ItemID", "RoomNo", "Guest", "Arrival", "Departure", "Total"}
            .All(key => V(x,key).Length > 0) &&
        string.Equals(V(x,"Status"), "Checked Out", StringComparison.OrdinalIgnoreCase);

    private static string ValidateHistoricalCheckoutSnapshot(Dictionary<string,string> x)
    {
        if (!HasHistoricalCheckoutSnapshot(x))
            return "Import Reservations and Check-ins first, or supply a HOP Checked Out reservation row with room, guest, arrival, departure and total.";
        var snapshot = new Dictionary<string,string>(x, StringComparer.OrdinalIgnoreCase)
        {
            ["Status"] = "Confirmed"
        };
        var issue = Validate(ImportDataKind.Reservations, snapshot);
        if (issue.Length > 0) return issue;
        if (GetDate(x, "CheckOutAt").Date < GetDate(x, "Arrival").Date)
            return "Checkout date cannot precede arrival date.";
        return "";
    }

    private static async Task<bool> CheckOutAlreadyArchivedAsync(SqlConnection cn, SqlTransaction? tx,
        string hotel, string reg, CancellationToken ct)
        => await ExistsAsync(cn, tx, @"SELECT TOP 1 1 FROM dbo.GuestInformationLogTB
WHERE hotel_id=@h AND reg_id=@k AND LOWER(res_status) IN ('check out','checkout','checked out')",
            hotel, reg, ct);

    private static async Task<bool> CheckOutRoomExistsAsync(SqlConnection cn, SqlTransaction? tx,
        string hotel, string reg, string room, CancellationToken ct)
        => await ExistsAsync(cn, tx, @"SELECT TOP 1 1 FROM dbo.payments
WHERE hotel_id=@h AND reg_id=@k AND room_no=@room AND descr='Room Rent'",
            hotel, reg, ct, room);

    private static async Task<bool> RoomExistsAsync(SqlConnection cn, SqlTransaction? tx,
        string hotel, string room, CancellationToken ct)
        => await ExistsAsync(cn, tx, @"SELECT TOP 1 1 FROM dbo.RoomsTB
WHERE Hotel_id=@h AND room_no=@k", hotel, room, ct);

    private static async Task<bool> IsCheckInActiveAsync(SqlConnection cn, SqlTransaction? tx,
        string hotel, string reg, CancellationToken ct)
        => await ExistsAsync(cn, tx, @"SELECT TOP 1 1 FROM dbo.GuestInformationLogTB
WHERE hotel_id=@h AND reg_id=@k AND LOWER(res_status) IN ('check in','checked in')", hotel, reg, ct);

    private static async Task<string> CheckDependencyAsync(SqlConnection cn,SqlTransaction? tx,string hotel,string source,
        ImportDataKind kind,Dictionary<string,string> x,CancellationToken ct)
    {
        if(kind==ImportDataKind.Categories)
            return "Use the Rooms import: missing categories are created automatically.";
        if(kind==ImportDataKind.Rooms)
        {
            if(await ExistsAsync(cn,tx,@"SELECT TOP 1 1 FROM dbo.RoomsTB WHERE Hotel_id=@h AND room_no=@k",hotel,V(x,"RoomNo"),ct))
                return "Room number already exists in this hotel.";
            // An existing category is reused as-is. New categories need occupancy
            // from the room export; never invent guest capacities.
            if(!await ExistsAsync(cn,tx,@"SELECT TOP 1 1 FROM dbo.create_room WHERE hotel_id=@h AND LTRIM(RTRIM(description))=LTRIM(RTRIM(@k)) AND category='Room Rent'",hotel,V(x,"Category"),ct))
            {
                if(V(x,"Occupany").Length==0 && V(x,"Adults").Length==0)
                    return "New room category needs Occupany (example: 0.2.0) to set capacity.";
            }
            return "";
        }
        if(kind==ImportDataKind.Reservations)
        {
            if(!await ExistsAsync(cn,tx,@"SELECT TOP 1 1 FROM dbo.RoomsTB WHERE Hotel_id=@h AND room_no=@k",hotel,V(x,"RoomNo"),ct))
                return "Room not found. Import Rooms first.";
            var alreadyMapped=await GetMappedRegAsync(cn,tx,hotel,source,V(x,"ReservationID"),ct);
            if(alreadyMapped.Length>0)
            {
                if(V(x,"ItemID").Length==0)
                    return "A second room requires the HOP Reservation Item identifier.";
                if(await ExistsAsync(cn,tx,@"SELECT TOP 1 1 FROM dbo.payments WHERE hotel_id=@h AND reg_id=@k AND descr='Room Rent' AND room_no=@room",hotel,alreadyMapped,ct,V(x,"RoomNo")))
                    return "This room is already linked to the imported reservation.";
            }
            else if(await ReservationIdInUseAsync(cn,tx,hotel,OriginalRegId(x),ct))
                return "This HOP Reservation Number already exists in ORA PMS. Review it; no duplicate or replacement will be created.";
            return "";
        }
        var mapped=await GetMappedRegAsync(cn,tx,hotel,source,V(x,"ReservationID"),ct);
        if(mapped.Length==0)
        {
            if (kind == ImportDataKind.CheckOuts && HasHistoricalCheckoutSnapshot(x))
            {
                var historicalIssue = ValidateHistoricalCheckoutSnapshot(x);
                if (historicalIssue.Length > 0) return historicalIssue;
                if (!await RoomExistsAsync(cn,tx,hotel,V(x,"RoomNo"),ct)) return "Room not found. Import Rooms first.";
                if (await ReservationIdInUseAsync(cn,tx,hotel,OriginalRegId(x),ct))
                    return "HOP Reservation Number already exists in ORA PMS; not replacing it.";
                return "";
            }
            // Legacy HOP check-in files may include a complete booking snapshot.
            if(kind==ImportDataKind.CheckIns && V(x,"Guest").Length>0 && V(x,"RoomNo").Length>0 &&
               V(x,"Arrival").Length>0 && V(x,"Departure").Length>0 && V(x,"Total").Length>0)
            {
                var snapshot=new Dictionary<string,string>(x,StringComparer.OrdinalIgnoreCase)
                {
                    ["Status"]="Confirmed"
                };
                var validation=Validate(ImportDataKind.Reservations,snapshot);
                return validation.Length>0?validation:
                    await CheckDependencyAsync(cn,tx,hotel,source,ImportDataKind.Reservations,snapshot,ct);
            }
            if(kind is ImportDataKind.Cancellations or ImportDataKind.NoShows)
            {
                if(V(x,"Guest").Length==0 || V(x,"RoomNo").Length==0 ||
                   V(x,"Arrival").Length==0 || V(x,"Departure").Length==0)
                    return "HOP status export needs GuestOrBookerName, RoomNumber, DateOfArrival and NumberOfNights.";
                if(!TryDate(V(x,"Arrival"),out var arr)||!TryDate(V(x,"Departure"),out var dep)||dep.Date<=arr.Date)
                    return "Arrival date or nights is invalid.";
                if(kind==ImportDataKind.Cancellations &&
                    (!TryMoney(V(x,"Total"),out var amt)||amt<0))
                    return "Invalid cancelled booking amount.";
                if(await ReservationIdInUseAsync(cn,tx,hotel,OriginalRegId(x),ct))
                    return "This HOP Reservation Number already exists in ORA PMS. Review the existing record.";
                return "";
            }
            return "Reservation mapping missing. Import Reservations first with Source System HOP.";
        }
        if(kind==ImportDataKind.Payments)
        {
            if((await MatchPaymentMethodAsync(cn,tx,hotel,V(x,"Method"),ct)).Length==0)
                return "HOP payment method '"+V(x,"Method")+"' is not mapped to a method in this hotel's PaymentMethodTB.";
            await using(var balance=Cmd(cn,tx,@"SELECT TOP 1 TRY_CONVERT(decimal(18,2),remaining_amount)
FROM dbo.PaymentsUpdateTB WITH(UPDLOCK,HOLDLOCK) WHERE hotel_id=@hotel AND reg_id=@reg ORDER BY id DESC"))
            {
                S(balance,"@hotel",hotel);S(balance,"@reg",mapped);
                var raw=await balance.ExecuteScalarAsync(ct);
                if(raw is null || raw==DBNull.Value)return "Payment summary missing or invalid for this reservation.";
                var remaining=Convert.ToDecimal(raw,CultureInfo.InvariantCulture);
                if(TryMoney(V(x,"Amount"),out var amt) && amt>remaining+0.01m)
                    return "Payment exceeds the current unpaid balance. Reconcile this reservation first.";
            }
        }
        if(kind==ImportDataKind.CheckIns)
        {
            if(await ExistsAsync(cn,tx,@"SELECT TOP 1 1 FROM dbo.GuestInformationLogTB WHERE hotel_id=@h AND reg_id=@k AND LOWER(res_status) IN ('check in','checked in','check out')",hotel,mapped,ct))
            {
                if(V(x,"ItemID").Length==0 || V(x,"RoomNo").Length==0)
                    return "Already checked in; a different room needs the HOP Item and Room fields.";
                if(await ExistsAsync(cn,tx,@"SELECT TOP 1 1 FROM dbo.payments WHERE hotel_id=@h AND reg_id=@k AND room_no=@room AND descr='Room Rent' AND LOWER(res_status) IN ('check in','checked in','check out')",hotel,mapped,ct,V(x,"RoomNo")))
                    return "This room was already checked in on the same reservation.";
                return "";
            }
            if(!await ExistsAsync(cn,tx,@"SELECT TOP 1 1 FROM dbo.NewReservationsTB WHERE hotel_id=@h AND reg_id=@k",hotel,mapped,ct))
                return "Reservation is not active. Import Reservations first.";
        }
        if(kind==ImportDataKind.CheckOuts)
        {
            if (await CheckOutAlreadyArchivedAsync(cn,tx,hotel,mapped,ct))
            {
                var issue=ValidateHistoricalCheckoutSnapshot(x);
                if (issue.Length>0) return "Already checked-out reservation: " + issue;
                if(!await RoomExistsAsync(cn,tx,hotel,V(x,"RoomNo"),ct)) return "Room not found. Import Rooms first.";
                if(await CheckOutRoomExistsAsync(cn,tx,hotel,mapped,V(x,"RoomNo"),ct))
                    return "This room has already been recorded for the checked-out reservation.";
                return "";
            }
            if(!await IsCheckInActiveAsync(cn,tx,hotel,mapped,ct))
                return "Check-in record not found. Import Check-ins first.";
        }
        if(kind is ImportDataKind.Cancellations or ImportDataKind.NoShows)
        {
            var archiveTable=kind==ImportDataKind.Cancellations?"CancelledReservationsTB":"NoShowTB";
            // An archived HOP booking may include multiple room rows with one ReservationNumber.
            // Each RoomNumber is checked separately, and we never overwrite existing records.
            var archived=await ExistsAsync(cn,tx,$@"SELECT TOP 1 1 FROM dbo.{archiveTable}
WHERE hotel_id=@h AND reg_id=@k",hotel,mapped,ct);
            if(archived)
            {
                if(await ExistsAsync(cn,tx,$@"SELECT TOP 1 1 FROM dbo.{archiveTable}
WHERE hotel_id=@h AND reg_id=@k AND room_no=@room",hotel,mapped,ct,V(x,"RoomNo")))
                    return "This HOP room is already archived for the reservation.";
                return "";
            }
            if(!await ExistsAsync(cn,tx,@"SELECT TOP 1 1 FROM dbo.NewReservationsTB WHERE hotel_id=@h AND reg_id=@k",hotel,mapped,ct))
                return "Active or matching archived reservation not found.";
        }
        return "";
    }

    private static async Task<bool> ExistsAsync(SqlConnection cn,SqlTransaction? tx,string sql,string hotel,string key,CancellationToken ct,string? source=null)
    {
        await using var cmd=Cmd(cn,tx,sql);S(cmd,"@h",hotel);S(cmd,"@k",key);
        if(sql.Contains("@room",StringComparison.Ordinal))S(cmd,"@room",source??"",80);
        else if(source is not null)S(cmd,"@s",source,80);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }
    // The imported HOP Reservation Number is the actual ORA reservation ID.
    // All room items under the same HOP reservation use this one reg_id.
    private static string OriginalRegId(Dictionary<string,string> row)
    {
        var reg=V(row,"ReservationID");
        if(string.IsNullOrWhiteSpace(reg) || reg.Length>50 || reg.Any(char.IsControl))
            throw new InvalidOperationException("HOP Reservation Number is missing or exceeds 50 characters.");
        return reg;
    }

    private static Task<bool> ReservationIdInUseAsync(SqlConnection cn,SqlTransaction? tx,
        string hotel,string reg,CancellationToken ct) => ExistsAsync(cn,tx,@"
SELECT TOP 1 1 FROM dbo.NewReservationsTB WHERE hotel_id=@h AND (reg_id=@k OR CONVERT(varchar(200),booking_id)=@k)
UNION ALL SELECT TOP 1 1 FROM dbo.GuestInformationLogTB WHERE hotel_id=@h AND (reg_id=@k OR CONVERT(varchar(200),booking_id)=@k)
UNION ALL SELECT TOP 1 1 FROM dbo.CancelledReservationsTB WHERE hotel_id=@h AND reg_id=@k
UNION ALL SELECT TOP 1 1 FROM dbo.NoShowTB WHERE hotel_id=@h AND reg_id=@k
UNION ALL SELECT TOP 1 1 FROM dbo.payments WHERE hotel_id=@h AND reg_id=@k
UNION ALL SELECT TOP 1 1 FROM dbo.PaymentsLogTB WHERE hotel_id=@h AND reg_id=@k
UNION ALL SELECT TOP 1 1 FROM dbo.PaymentsUpdateTB WHERE hotel_id=@h AND reg_id=@k",
        hotel,reg,ct);

    private static async Task<string> GetMappedRegAsync(SqlConnection cn,SqlTransaction? tx,string hotel,string source,string external,CancellationToken ct)
    {
        await using var cmd=Cmd(cn,tx,@"SELECT TOP 1 reg_id FROM dbo.Reserv_log
WHERE hotel_id=@hotel AND status='ImportData' AND descp=@marker ORDER BY createdat DESC");
        S(cmd,"@hotel",hotel);S(cmd,"@marker",AuditMarker(hotel,source,ImportDataKind.Reservations,external),120);
        return Convert.ToString(await cmd.ExecuteScalarAsync(ct),CultureInfo.InvariantCulture)??"";
    }

    private async Task<string> ApplyAsync(SqlConnection cn,SqlTransaction tx,string hotel,string user,string userName,
        string source,ImportDataKind kind,Guid batch,Dictionary<string,string> x,CancellationToken ct)
    {
        switch(kind)
        {
            case ImportDataKind.Categories: throw new InvalidOperationException("Use Rooms to import categories automatically.");
            case ImportDataKind.Rooms: await AddRoomAsync(cn,tx,hotel,x,ct);return "";
            case ImportDataKind.Reservations: return await AddReservationAsync(cn,tx,hotel,user,userName,source,batch,x,ct);
            case ImportDataKind.CheckIns:
                {
                    var mapped=await GetMappedRegAsync(cn,tx,hotel,source,V(x,"ReservationID"),ct);
                    if(mapped.Length==0)
                        await AddReservationAsync(cn,tx,hotel,user,userName,source,batch,x,ct);
                    else if(V(x,"ItemID").Length>0 && V(x,"RoomNo").Length>0 &&
                        !await ExistsAsync(cn,tx,@"SELECT TOP 1 1 FROM dbo.payments WHERE hotel_id=@h AND reg_id=@k AND room_no=@room AND descr='Room Rent'",hotel,mapped,ct,V(x,"RoomNo")))
                        await AddAdditionalRoomAsync(cn,tx,hotel,user,mapped,x,ct);
                    mapped=await GetMappedRegAsync(cn,tx,hotel,source,V(x,"ReservationID"),ct);
                    if(await ExistsAsync(cn,tx,@"SELECT TOP 1 1 FROM dbo.GuestInformationLogTB WHERE hotel_id=@h AND reg_id=@k AND LOWER(res_status) IN ('check in','checked in')",hotel,mapped,ct))
                    {
                        await MarkRoomCheckedInAsync(cn,tx,hotel,mapped,V(x,"RoomNo"),_clock.GetHotelToday(hotel),ct);
                        return mapped;
                    }
                    return await CheckInAsync(cn,tx,hotel,user,source,x,_clock.GetHotelToday(hotel),ct);
                }
            case ImportDataKind.CheckOuts:
                {
                    var mapped=await GetMappedRegAsync(cn,tx,hotel,source,V(x,"ReservationID"),ct);
                    if(mapped.Length==0 || await CheckOutAlreadyArchivedAsync(cn,tx,hotel,mapped,ct))
                        return await AddHistoricalCheckOutAsync(cn,tx,hotel,user,userName,source,x,ct);
                    return await CheckOutAsync(cn,tx,hotel,source,x,_clock.GetHotelToday(hotel),ct);
                }
            case ImportDataKind.Payments: return await AddPaymentAsync(cn,tx,hotel,user,userName,source,x,ct);
            case ImportDataKind.Cancellations:
                {
                    var mapped=await GetMappedRegAsync(cn,tx,hotel,source,V(x,"ReservationID"),ct);
                    if(mapped.Length==0 || await ExistsAsync(cn,tx,@"SELECT TOP 1 1 FROM dbo.CancelledReservationsTB
WHERE hotel_id=@h AND reg_id=@k",hotel,mapped,ct))
                        return await AddStandaloneCancellationAsync(cn,tx,hotel,user,userName,source,x,ct);
                    return await CloseReservationAsync(cn,tx,hotel,user,source,x,"CancelledReservationsTB","cancelled",_clock.GetHotelToday(hotel),ct);
                }
            case ImportDataKind.NoShows:
                {
                    var mapped=await GetMappedRegAsync(cn,tx,hotel,source,V(x,"ReservationID"),ct);
                    if(mapped.Length==0 || await ExistsAsync(cn,tx,@"SELECT TOP 1 1 FROM dbo.NoShowTB
WHERE hotel_id=@h AND reg_id=@k",hotel,mapped,ct))
                        return await AddStandaloneNoShowAsync(cn,tx,hotel,user,userName,source,x,ct);
                    return await CloseReservationAsync(cn,tx,hotel,user,source,x,"NoShowTB","No Show",_clock.GetHotelToday(hotel),ct);
                }
            default:throw new InvalidOperationException("Unsupported import type.");
        }
    }

    private static string LocalId() => (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()*100000L+RandomNumberGenerator.GetInt32(10000,99999))
        .ToString(CultureInfo.InvariantCulture);

    private static async Task AddCategoryAsync(SqlConnection cn,SqlTransaction tx,string hotel,Dictionary<string,string> x,CancellationToken ct)
    {
        await using var cmd=Cmd(cn,tx,@"
INSERT INTO dbo.create_room(category,description,Adult_Spaces,Children_Spaces,Cot_Spaces,hotel_id,status,no_of_rooms,localcategoryid)
VALUES('Room Rent',@category,@adults,@children,@cots,@hotel,1,0,@id)");
        S(cmd,"@category",V(x,"Category"));S(cmd,"@hotel",hotel);
        S(cmd,"@id",LocalId());
        foreach(var field in new[]{("Adults","@adults"),("Children","@children"),("Cots","@cots")})
            cmd.Parameters.Add(field.Item2,SqlDbType.Int).Value=int.TryParse(V(x,field.Item1),out var n)?n:0;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>One HOP room import creates a missing category and links the new room to its real local ID.
    /// Room and category inserts use the same serializable SQL transaction as the import audit.</summary>
    private static async Task AddRoomAsync(SqlConnection cn, SqlTransaction tx, string hotel,
        Dictionary<string,string> x, CancellationToken ct)
    {
        var category=V(x,"Category");
        string catId="";
        await using(var read=Cmd(cn,tx,@"SELECT TOP 1 localcategoryid FROM dbo.create_room WITH(UPDLOCK,HOLDLOCK)
WHERE hotel_id=@hotel AND LTRIM(RTRIM(description))=LTRIM(RTRIM(@category)) AND category='Room Rent'"))
        {
            S(read,"@hotel",hotel);S(read,"@category",category);
            var value=await read.ExecuteScalarAsync(ct);
            if(value is not null && value!=DBNull.Value)catId=Convert.ToString(value,CultureInfo.InvariantCulture)??"";
        }
        if(catId.Length==0)
        {
            // A category with a missing ID is unsafe to rekey silently: it may
            // already be referenced by rooms or rate plans.
            if(await ExistsAsync(cn,tx,@"SELECT TOP 1 1 FROM dbo.create_room WHERE hotel_id=@h AND description=@k AND category='Room Rent'",hotel,category,ct))
                throw new InvalidOperationException("Category already exists but has no localcategoryid. Correct it before importing rooms.");
            if(V(x,"Occupany").Length==0 && V(x,"Adults").Length==0)
                throw new InvalidOperationException("New category needs Occupany (e.g. 0.2.0).");
            catId=LocalId();
            await using var insert=Cmd(cn,tx,@"INSERT INTO dbo.create_room
(category,description,Adult_Spaces,Children_Spaces,Cot_Spaces,hotel_id,status,no_of_rooms,localcategoryid)
VALUES('Room Rent',@category,@adults,@children,@cots,@hotel,1,0,@id)");
            S(insert,"@category",category);S(insert,"@hotel",hotel);S(insert,"@id",catId);
            insert.Parameters.Add("@adults",SqlDbType.Int).Value=I(x,"Adults",0);
            insert.Parameters.Add("@children",SqlDbType.Int).Value=I(x,"Children",0);
            insert.Parameters.Add("@cots",SqlDbType.Int).Value=I(x,"Cots",0);
            await insert.ExecuteNonQueryAsync(ct);
        }
        await using(var room=Cmd(cn,tx,@"INSERT INTO dbo.RoomsTB
(room_no,room_category,localcategoryid,Hotel_id,room_status,hkroomstatus,rate,isassigned,localroomid)
VALUES(@room,@category,@catId,@hotel,'Available','Clean',0,0,@roomId)"))
        {
            S(room,"@room",V(x,"RoomNo"));S(room,"@category",category);S(room,"@catId",catId);
            S(room,"@hotel",hotel);S(room,"@roomId",LocalId());
            await room.ExecuteNonQueryAsync(ct);
        }
        await using(var tally=Cmd(cn,tx,@"UPDATE dbo.create_room
SET no_of_rooms=ISNULL(TRY_CONVERT(int,no_of_rooms),0)+1
WHERE hotel_id=@hotel AND localcategoryid=@catId AND category='Room Rent'"))
        {
            S(tally,"@hotel",hotel);S(tally,"@catId",catId);
            if(await tally.ExecuteNonQueryAsync(ct)!=1)
                throw new InvalidOperationException("Category room-count could not be updated.");
        }
    }

    /// <summary>Additional HOP Reservation Item under the same external reservation number.
    /// One room charge per item in payments; the reservation/payment totals are increased
    /// only by the price of this additional room (no duplicate booking or payment log).</summary>
    private async Task<string> AddAdditionalRoomAsync(SqlConnection cn,SqlTransaction tx,string hotel,
        string user,string reg,Dictionary<string,string> x,CancellationToken ct)
    {
        if(V(x,"ItemID").Length==0)
            throw new InvalidOperationException("Multiple rooms require HOP Reservation Item.");
        if(!await ExistsAsync(cn,tx,@"SELECT TOP 1 1 FROM dbo.NewReservationsTB WHERE hotel_id=@h AND reg_id=@k
UNION ALL SELECT TOP 1 1 FROM dbo.GuestInformationLogTB WHERE hotel_id=@h AND reg_id=@k AND LOWER(res_status) IN ('check in','checked in')",hotel,reg,ct))
            throw new InvalidOperationException("Reservation is already archived or checked out; cannot attach another room.");
        var roomNo=V(x,"RoomNo");
        var arrival=GetDate(x,"Arrival");var departure=GetDate(x,"Departure");
        var nights=(departure.Date-arrival.Date).Days;
        var total=GetMoney(x,"Total");var gst=GetMoney(x,"GST");var bed=GetMoney(x,"Bed");
        string cat="",catId="";
        await using(var q=Cmd(cn,tx,@"SELECT TOP 1 room_category,localcategoryid FROM dbo.RoomsTB WITH(UPDLOCK,HOLDLOCK)
WHERE Hotel_id=@hotel AND room_no=@room"))
        {
            S(q,"@hotel",hotel);S(q,"@room",roomNo);
            await using var rd=await q.ExecuteReaderAsync(ct);
            if(!await rd.ReadAsync(ct))throw new InvalidOperationException("Room not found.");
            cat=Convert.ToString(rd[0])??"";catId=Convert.ToString(rd[1])??"";
        }
        if(await ExistsAsync(cn,tx,@"SELECT TOP 1 1 FROM dbo.payments WHERE hotel_id=@h AND reg_id=@k AND room_no=@room AND descr='Room Rent'",hotel,reg,ct,roomNo))
            throw new InvalidOperationException("Room already attached to this reservation.");
        if(V(x,"Category").Length>0 && !string.Equals(V(x,"Category"),cat,StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Room category does not match ORA PMS.");
        var plan=V(x,"RatePlan");var planId="";
        if(plan.Length>0)
        {
            await using var planCmd=Cmd(cn,tx,@"SELECT TOP 1 localplanid FROM dbo.category_plan
WHERE hotel_id=@hotel AND category=@category AND planname=@name");
            S(planCmd,"@hotel",hotel);S(planCmd,"@category",cat);S(planCmd,"@name",plan);
            planId=Convert.ToString(await planCmd.ExecuteScalarAsync(ct),CultureInfo.InvariantCulture)??"";
        }
        // Check overlaps against imported/per-room charges and active reservations.
        await EnsureRoomFreeAsync(cn,tx,hotel,roomNo,arrival,departure,ct);
        var isInHouse=await ExistsAsync(cn,tx,@"SELECT TOP 1 1 FROM dbo.GuestInformationLogTB WHERE hotel_id=@h AND reg_id=@k AND LOWER(res_status) IN ('check in','checked in')",hotel,reg,ct);
        await using(var charge=Cmd(cn,tx,@"INSERT INTO dbo.payments
(currentdate,deductioninfo,descr,Type,NumberOfRoom,Rate,Charge,GST,Bed,Nights,totalamount,
reg_id,payment_status,hid,cb_status,room_no,res_status,visit_id,hotel_id,ipAddress,systemUser,systemName,
discount,suffle_type,category_id,rateplan,rateplanname,ArrivalDate,DepartureDate,guestname,room_adults,room_children,room_infants)
VALUES(@date,'0','Room Rent',@cat,1,@rate,0,@gst,@bed,@nights,@total,@reg,'unpaid',@user,'1',@room,
@status,'',@hotel,'ImportData',@user,'ImportData','0','manual',@catId,@planId,@planName,@arr,@dep,@guest,@adults,@children,0)"))
        {
            D(charge,"@date",arrival);S(charge,"@cat",cat);M(charge,"@rate",Math.Round(total/nights,2));
            M(charge,"@gst",gst);M(charge,"@bed",bed);charge.Parameters.Add("@nights",SqlDbType.Int).Value=nights;
            M(charge,"@total",total);S(charge,"@reg",reg);S(charge,"@user",user);S(charge,"@room",roomNo);
            S(charge,"@status",isInHouse?"check in":"reservation");S(charge,"@hotel",hotel);S(charge,"@catId",catId);
            S(charge,"@planId",planId);S(charge,"@planName",plan);
            S(charge,"@arr",Legacy(arrival));S(charge,"@dep",Legacy(departure));S(charge,"@guest",V(x,"Guest"));
            charge.Parameters.Add("@adults",SqlDbType.Int).Value=I(x,"Adults",1);
            charge.Parameters.Add("@children",SqlDbType.Int).Value=I(x,"Children",0);
            await charge.ExecuteNonQueryAsync(ct);
        }
        // Keep summary at reservation level, with a distinct room charge in payments.
        await using(var update=Cmd(cn,tx,@"
UPDATE dbo.NewReservationsTB SET total_amount=ISNULL(TRY_CONVERT(decimal(18,2),total_amount),0)+@amount,
noofrooms=ISNULL(TRY_CONVERT(int,noofrooms),1)+1 WHERE hotel_id=@hotel AND reg_id=@reg;
UPDATE dbo.GuestInformationLogTB SET total_amount=ISNULL(TRY_CONVERT(decimal(18,2),total_amount),0)+@amount,
noofrooms=ISNULL(TRY_CONVERT(int,noofrooms),1)+1 WHERE hotel_id=@hotel AND reg_id=@reg;
UPDATE dbo.PaymentsUpdateTB SET grand_total=ISNULL(TRY_CONVERT(decimal(18,2),grand_total),0)+@amount,
payable=ISNULL(TRY_CONVERT(decimal(18,2),payable),0)+@amount,
remaining_amount=ISNULL(TRY_CONVERT(decimal(18,2),remaining_amount),0)+@amount
WHERE hotel_id=@hotel AND reg_id=@reg;"))
        {
            S(update,"@hotel",hotel);S(update,"@reg",reg);M(update,"@amount",total);
            await update.ExecuteNonQueryAsync(ct);
        }
        if(planId.Length>0)
        {
            var nightly=Math.Round(total/nights,2);
            for(var day=arrival.Date;day<departure.Date;day=day.AddDays(1))
            {
                var dayRate=day==departure.Date.AddDays(-1)?total-nightly*(nights-1):nightly;
                await using var rate=Cmd(cn,tx,@"IF EXISTS(SELECT 1 FROM dbo.NewReservationRate WITH(UPDLOCK,HOLDLOCK)
WHERE reg_id=@reg AND hotel_id=@hotel AND rate_date=@date AND plan_name=@plan)
UPDATE dbo.NewReservationRate SET rate=ISNULL(TRY_CONVERT(decimal(18,2),rate),0)+@rate
WHERE reg_id=@reg AND hotel_id=@hotel AND rate_date=@date AND plan_name=@plan;
ELSE INSERT INTO dbo.NewReservationRate(reg_id,rate_date,rate,hotel_id,plan_name)
VALUES(@reg,@date,@rate,@hotel,@plan)");
                S(rate,"@reg",reg);S(rate,"@hotel",hotel);
                S(rate,"@date",day.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture));
                S(rate,"@plan",planId);M(rate,"@rate",dayRate);
                await rate.ExecuteNonQueryAsync(ct);
            }
        }
        await AdjustAvailabilityAsync(cn,tx,hotel,catId,arrival,departure,-1,_clock.GetHotelToday(hotel),ct);
        if(isInHouse && arrival.Date<=_clock.GetHotelToday(hotel).Date && departure.Date>_clock.GetHotelToday(hotel).Date)
        {
            await using var occupied=Cmd(cn,tx,@"UPDATE dbo.RoomsTB SET room_status='Occupied' WHERE Hotel_id=@hotel AND room_no=@room");
            S(occupied,"@hotel",hotel);S(occupied,"@room",roomNo);
            await occupied.ExecuteNonQueryAsync(ct);
        }
        return reg;
    }

    private static async Task EnsureRoomFreeAsync(SqlConnection cn,SqlTransaction tx,string hotel,
        string room,DateTime arrival,DateTime departure,CancellationToken ct)
    {
        await using var q=Cmd(cn,tx,@"SELECT TOP 1 1 FROM dbo.payments p WITH(UPDLOCK,HOLDLOCK)
WHERE p.hotel_id=@hotel AND p.room_no=@room AND p.descr='Room Rent'
AND LOWER(ISNULL(p.res_status,'')) IN ('reservation','check in','checked in')
AND COALESCE(TRY_CONVERT(date,p.ArrivalDate,110),TRY_CONVERT(date,p.currentdate))<@dep
AND TRY_CONVERT(date,p.DepartureDate,110)>@arr
UNION ALL
SELECT TOP 1 1 FROM dbo.NewReservationsTB r WITH(UPDLOCK,HOLDLOCK)
WHERE r.hotel_id=@hotel AND r.room_no=@room AND LOWER(ISNULL(r.res_status,''))='reservation'
AND TRY_CONVERT(date,r.ArrivalDate,110)<@dep AND TRY_CONVERT(date,r.dept_date,110)>@arr
UNION ALL
SELECT TOP 1 1 FROM dbo.GuestInformationLogTB g WITH(UPDLOCK,HOLDLOCK)
WHERE g.hotel_id=@hotel AND g.room_no=@room AND LOWER(ISNULL(g.res_status,'')) IN ('check in','checked in')
AND TRY_CONVERT(date,g.ArrivalDate,110)<@dep AND TRY_CONVERT(date,g.DepartureDate,110)>@arr");
        S(q,"@hotel",hotel);S(q,"@room",room);D(q,"@arr",arrival.Date);D(q,"@dep",departure.Date);
        if(await q.ExecuteScalarAsync(ct) is not null)
            throw new InvalidOperationException("Room is occupied/reserved for overlapping dates.");
    }

    private static async Task<string> MatchPaymentMethodAsync(SqlConnection cn,SqlTransaction? tx,string hotel,string hop,CancellationToken ct)
    {
        var aliases=hop.Trim().ToLowerInvariant() switch
        {
            "creditcard" => new[] {"creditcard","credit card","card","online card"},
            "offlinecard" => new[] {"offlinecard","offline card","pdq","card terminal","card"},
            "cash" => new[] {"cash"},
            "banktransfer" => new[] {"banktransfer","bank transfer","bank"},
            _ => new[] {hop.Trim().ToLowerInvariant()}
        };
        // Prefer an exact hotel configured method. Never create a new method silently.
        foreach(var alias in aliases)
        {
            await using var cmd=Cmd(cn,tx,@"SELECT TOP 1 method FROM dbo.PaymentMethodTB
WHERE hotel_id=@hotel AND LOWER(LTRIM(RTRIM(method)))=@method");
            S(cmd,"@hotel",hotel);S(cmd,"@method",alias);
            var candidate=Convert.ToString(await cmd.ExecuteScalarAsync(ct),CultureInfo.InvariantCulture)??"";
            if(candidate.Length>0)return candidate;
        }
        return "";
    }

    private async Task<string> AddReservationAsync(SqlConnection cn,SqlTransaction tx,string hotel,string user,
        string userName,string source,Guid batch,Dictionary<string,string> x,CancellationToken ct)
    {
        var existing=await GetMappedRegAsync(cn,tx,hotel,source,V(x,"ReservationID"),ct);
        if(existing.Length>0)
            return await AddAdditionalRoomAsync(cn,tx,hotel,user,existing,x,ct);
        var arrival=GetDate(x,"Arrival");var departure=GetDate(x,"Departure");
        var nights=(departure.Date-arrival.Date).Days;
        var total=GetMoney(x,"Total");var gst=GetMoney(x,"GST");var bed=GetMoney(x,"Bed");
        var room=V(x,"RoomNo");
        string category,catId;
        await using(var q=Cmd(cn,tx,@"SELECT TOP 1 room_category,localcategoryid FROM dbo.RoomsTB WITH(UPDLOCK,HOLDLOCK)
WHERE Hotel_id=@hotel AND room_no=@room"))
        {
            S(q,"@hotel",hotel);S(q,"@room",room);
            await using var rd=await q.ExecuteReaderAsync(ct);
            if(!await rd.ReadAsync(ct))throw new InvalidOperationException("Room no longer exists.");
            category=Convert.ToString(rd[0])??"";catId=Convert.ToString(rd[1])??"";
        }
        if(V(x,"Category").Length>0 && !string.Equals(category,V(x,"Category"),StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Room category differs from the room's current category.");
        var plan=V(x,"RatePlan");
        string planId="";
        if(plan.Length>0)
        {
            await using var p=Cmd(cn,tx,@"SELECT TOP 1 localplanid FROM dbo.category_plan
WHERE hotel_id=@hotel AND category=@category AND planname=@name");
            S(p,"@hotel",hotel);S(p,"@category",category);S(p,"@name",plan);
            planId=Convert.ToString(await p.ExecuteScalarAsync(ct),CultureInfo.InvariantCulture)??"";
            // HOP rate-plan names are historical labels; keep them even when ORA
            // has not configured an identical plan. No rate plan is created here.
        }
        // Half-open room stays: checkout and next arrival may share the same day.
        await EnsureRoomFreeAsync(cn,tx,hotel,room,arrival,departure,ct);
        // Use the actual HOP Reservation Number as reg_id. Preserve RES-prefix,
        // and never allocate a new generated number or adopt an unrelated booking.
        var regId=OriginalRegId(x);
        // Use a hotel/ID-scoped SQL application lock to prevent two imports from
        // inserting the same reservation concurrently, even from different batches.
        await AcquireKeyLockAsync(cn,tx,hotel,"ORA",ImportDataKind.Reservations,"REG-ID:"+regId,ct);
        if(await ReservationIdInUseAsync(cn,tx,hotel,regId,ct))
            throw new InvalidOperationException("The HOP Reservation Number already exists for this hotel; import was stopped to prevent duplicates.");
        var guest=V(x,"Guest");var last=V(x,"LastName");
        await using(var ins=Cmd(cn,tx,@"
INSERT INTO dbo.NewReservationsTB(reg_id,GuestName,LastName,ArrivalDate,dept_date,Address,Email,PhoneNo,Agency,Status,
advance_paid,total_amount,room_no,room_category,rateplan_id,rateplanname,CreatedDate,CreatedAt,user_id,res_status,cb_status,hotel_id,
noofrooms,number_of_adult,number_of_minor,isupdateavailibilty,booking_id)
VALUES(@reg,@guest,@last,@arr,@dep,'',@email,@phone,@source,@source,0,@total,@room,@cat,@planId,@planName,
@created,@createdAt,@user,'reservation','1',@hotel,1,@adults,@children,1,@external)"))
        {
            S(ins,"@reg",regId);S(ins,"@guest",guest);S(ins,"@last",last);
            S(ins,"@arr",Legacy(arrival));S(ins,"@dep",Legacy(departure));
            S(ins,"@email",V(x,"Email"));S(ins,"@phone",V(x,"Phone"));
            S(ins,"@source",V(x,"Source").Length>0?V(x,"Source"):"Imported: "+source);
            M(ins,"@total",total);S(ins,"@room",room);S(ins,"@cat",category);
            S(ins,"@planId",planId);S(ins,"@planName",plan);
            var created=TryDate(V(x,"CreatedAt"),out var when)?when:arrival;
            S(ins,"@created",Legacy(created));D(ins,"@createdAt",created);
            // Never present an external PMS reservation ID as a live Channex booking ID.
            // The original external ID is mapped by a transactional Reserv_log marker.
            S(ins,"@user",user);S(ins,"@hotel",hotel);S(ins,"@external","");
            ins.Parameters.Add("@adults",SqlDbType.Int).Value=I(x,"Adults",1);
            ins.Parameters.Add("@children",SqlDbType.Int).Value=I(x,"Children",0);
            await ins.ExecuteNonQueryAsync(ct);
        }
        // Room charge is a charge, not a collected payment. Saved GST and bed tax are not invented.
        await using(var charge=Cmd(cn,tx,@"
INSERT INTO dbo.payments(currentdate,deductioninfo,descr,Type,NumberOfRoom,Rate,Charge,GST,Bed,Nights,totalamount,
reg_id,payment_status,hid,cb_status,room_no,res_status,visit_id,hotel_id,ipAddress,systemUser,systemName,discount,
suffle_type,category_id,rateplan,rateplanname,ArrivalDate,DepartureDate,guestname,room_adults,room_children,room_infants)
VALUES(@date,'0','Room Rent',@cat,1,@rate,0,@gst,@bed,@nights,@total,@reg,'unpaid',@user,'1',@room,
'reservation','',@hotel,'ImportData',@user,'ImportData','0','manual',@catId,@planId,@planName,@arr,@dep,@guest,@adults,@children,0)"))
        {
            D(charge,"@date",arrival);S(charge,"@cat",category);M(charge,"@rate",Math.Round(total/nights,2));
            M(charge,"@gst",gst);M(charge,"@bed",bed);charge.Parameters.Add("@nights",SqlDbType.Int).Value=nights;
            M(charge,"@total",total);S(charge,"@reg",regId);S(charge,"@user",user);
            S(charge,"@room",room);S(charge,"@hotel",hotel);S(charge,"@catId",catId);
            S(charge,"@planId",planId);S(charge,"@planName",plan);
            S(charge,"@arr",Legacy(arrival));S(charge,"@dep",Legacy(departure));S(charge,"@guest",guest);
            charge.Parameters.Add("@adults",SqlDbType.Int).Value=I(x,"Adults",1);
            charge.Parameters.Add("@children",SqlDbType.Int).Value=I(x,"Children",0);
            await charge.ExecuteNonQueryAsync(ct);
        }
        if(planId.Length>0)
        {
            var nightly=Math.Round(total/nights,2);
            for(var day=arrival.Date;day<departure.Date;day=day.AddDays(1))
            {
                await using var rate=Cmd(cn,tx,@"
INSERT INTO dbo.NewReservationRate(reg_id,rate_date,rate,hotel_id,plan_name)
VALUES(@reg,@date,@rate,@hotel,@plan)");
                var dayRate=day==departure.Date.AddDays(-1)?total-nightly*(nights-1):nightly;
                S(rate,"@reg",regId);S(rate,"@date",day.ToString("yyyy-MM-dd"));M(rate,"@rate",dayRate);
                S(rate,"@hotel",hotel);S(rate,"@plan",planId);
                await rate.ExecuteNonQueryAsync(ct);
            }
        }
        await using(var summary=Cmd(cn,tx,@"
INSERT INTO dbo.PaymentsUpdateTB(currentdate,name,grand_total,room_security,payable,paid_amount,remaining_amount,
payment_method,status,visit_id,user_id,hotel_id,systemUser,systemName,ipAddress,reg_id)
VALUES(@date,@name,@total,0,@total,0,@total,'','reservation','add',@user,@hotel,@user,'ImportData','ImportData',@reg)"))
        {
            D(summary,"@date",arrival);S(summary,"@name",(guest+" "+last).Trim());M(summary,"@total",total);
            S(summary,"@user",user);S(summary,"@hotel",hotel);S(summary,"@reg",regId);
            await summary.ExecuteNonQueryAsync(ct);
        }
        // This audit record preserves the HOP source identifier and supplies the
        // lookup for later payments and check-ins (reg_id equals the HOP number).
        await WriteImportAuditAsync(cn,tx,hotel,regId,user,userName,source,
            ImportDataKind.Reservations,V(x,"ReservationID"),ct);
        // Date-scoped availability only. No Channex API call or guest notification on import.
        await AdjustAvailabilityAsync(cn,tx,hotel,catId,arrival,departure,-1,_clock.GetHotelToday(hotel),ct);
        return regId;
    }

    private static async Task AdjustAvailabilityAsync(SqlConnection cn,SqlTransaction tx,string hotel,string catId,
        DateTime from,DateTime to,int delta,DateTime hotelToday,CancellationToken ct)
    {
        if(catId.Length==0)return;
        var start=from.Date<hotelToday.Date?hotelToday.Date:from.Date;
        for(var d=start;d<to.Date;d=d.AddDays(1))
        {
            await using var read=Cmd(cn,tx,@"SELECT TOP 1 TRY_CONVERT(int,availableroom)
FROM dbo.AvailabilityTB WITH(UPDLOCK,HOLDLOCK) WHERE hotel_id=@hotel AND category_id=@cat AND date=@date");
            S(read,"@hotel",hotel);S(read,"@cat",catId);S(read,"@date",d.ToString("yyyy-MM-dd"));
            var value=await read.ExecuteScalarAsync(ct);
            int available;
            if(value is null)
            {
                await using var count=Cmd(cn,tx,@"SELECT COUNT(*) FROM dbo.RoomsTB WHERE Hotel_id=@hotel AND localcategoryid=@cat");
                S(count,"@hotel",hotel);S(count,"@cat",catId);
                available=Convert.ToInt32(await count.ExecuteScalarAsync(ct),CultureInfo.InvariantCulture);
                if(available+delta<0)throw new InvalidOperationException("No availability left for "+d.ToString("yyyy-MM-dd"));
                await using var insert=Cmd(cn,tx,@"INSERT INTO dbo.AvailabilityTB(date,availableroom,category_id,hotel_id,username,systemName,ip)
VALUES(@date,@qty,@cat,@hotel,'ImportData','ImportData','ImportData')");
                S(insert,"@date",d.ToString("yyyy-MM-dd"));insert.Parameters.Add("@qty",SqlDbType.Int).Value=available+delta;
                S(insert,"@cat",catId);S(insert,"@hotel",hotel);
                await insert.ExecuteNonQueryAsync(ct);
            }
            else
            {
                available=value==DBNull.Value?0:Convert.ToInt32(value,CultureInfo.InvariantCulture);
                if(available+delta<0)throw new InvalidOperationException("No availability left for "+d.ToString("yyyy-MM-dd"));
                await using var update=Cmd(cn,tx,@"UPDATE dbo.AvailabilityTB SET availableroom=@qty WHERE hotel_id=@hotel AND category_id=@cat AND date=@date");
                update.Parameters.Add("@qty",SqlDbType.Int).Value=available+delta;
                S(update,"@date",d.ToString("yyyy-MM-dd"));S(update,"@cat",catId);S(update,"@hotel",hotel);
                await update.ExecuteNonQueryAsync(ct);
            }
        }
    }

    private static async Task MarkRoomCheckedInAsync(SqlConnection cn,SqlTransaction tx,string hotel,
        string reg,string room,DateTime today,CancellationToken ct)
    {
        if(room.Length==0)throw new InvalidOperationException("Room required for multi-room check-in.");
        await using(var cmd=Cmd(cn,tx,@"UPDATE dbo.payments SET res_status='check in'
WHERE hotel_id=@hotel AND reg_id=@reg AND room_no=@room AND descr='Room Rent'
AND LOWER(res_status)='reservation'"))
        {
            S(cmd,"@hotel",hotel);S(cmd,"@reg",reg);S(cmd,"@room",room);
            // A newly attached room is already 'check in', so zero affected rows
            // is allowed only if that matching Room Rent entry exists.
            if(await cmd.ExecuteNonQueryAsync(ct)==0 &&
               !await ExistsAsync(cn,tx,@"SELECT TOP 1 1 FROM dbo.payments WHERE hotel_id=@h AND reg_id=@k AND room_no=@room AND descr='Room Rent' AND LOWER(res_status)='check in'",hotel,reg,ct,room))
                throw new InvalidOperationException("Room Rent record not found for check-in.");
        }
        await using var occupied=Cmd(cn,tx,@"UPDATE dbo.RoomsTB SET room_status='Occupied'
WHERE Hotel_id=@hotel AND room_no=@room AND EXISTS(
SELECT 1 FROM dbo.payments p WHERE p.hotel_id=@hotel AND p.reg_id=@reg AND p.room_no=@room
AND TRY_CONVERT(date,p.ArrivalDate,110)<=@today AND TRY_CONVERT(date,p.DepartureDate,110)>@today)");
        S(occupied,"@hotel",hotel);S(occupied,"@room",room);S(occupied,"@reg",reg);D(occupied,"@today",today.Date);
        await occupied.ExecuteNonQueryAsync(ct);
    }

    // HOP cancellations may not exist in the confirmed-reservations export.
    // Create the archive directly: it must not block inventory or require a
    // payment charge, rate plan, or an overlapping active-room booking.
    private static async Task<string> AddStandaloneCancellationAsync(SqlConnection cn,SqlTransaction tx,string hotel,
        string user,string userName,string source,Dictionary<string,string> x,CancellationToken ct)
    {
        // Even a historical cancellation keeps the original HOP reservation number.
        var reg=OriginalRegId(x);
        await AcquireKeyLockAsync(cn,tx,hotel,"ORA",ImportDataKind.Reservations,"REG-ID:"+reg,ct);
        var mapped=await GetMappedRegAsync(cn,tx,hotel,source,reg,ct);
        if(mapped.Length==0)
        {
            if(await ReservationIdInUseAsync(cn,tx,hotel,reg,ct))
                throw new InvalidOperationException("The HOP Reservation Number already exists for this hotel; cancelled booking was not duplicated.");
        }
        else
        {
            if(!await ExistsAsync(cn,tx,@"SELECT TOP 1 1 FROM dbo.CancelledReservationsTB WHERE hotel_id=@h AND reg_id=@k",hotel,reg,ct))
                throw new InvalidOperationException("This reservation is not an archived cancellation.");
            if(await ExistsAsync(cn,tx,@"SELECT TOP 1 1 FROM dbo.CancelledReservationsTB WHERE hotel_id=@h AND reg_id=@k AND room_no=@room",hotel,reg,ct,V(x,"RoomNo")))
                throw new InvalidOperationException("This room is already included in the archived cancellation.");
        }
        var arrival=GetDate(x,"Arrival");var dep=GetDate(x,"Departure");var cancelled=GetDate(x,"OccurredAt");
        var total=GetMoney(x,"Total");
        await using var cmd=Cmd(cn,tx,@"INSERT INTO dbo.CancelledReservationsTB
(reg_id,GuestName,LastName,ArrivalDate,dept_date,Agency,Status,total_amount,room_no,rateplanname,
user_id,res_status,cb_status,hotel_id,noofrooms,room_category,number_of_adult,number_of_minor,reason,date)
VALUES(@reg,@guest,'',@arrival,@dep,@source,@source,@total,@room,@plan,
@user,'cancelled','1',@hotel,1,'',1,0,@reason,@cancelled)");
        S(cmd,"@reg",reg);S(cmd,"@guest",V(x,"Guest"));S(cmd,"@arrival",Legacy(arrival));
        S(cmd,"@dep",Legacy(dep));S(cmd,"@source","Imported: "+source);M(cmd,"@total",total);
        S(cmd,"@room",V(x,"RoomNo"));S(cmd,"@plan",V(x,"RatePlan"));S(cmd,"@user",user);
        S(cmd,"@hotel",hotel);S(cmd,"@reason",V(x,"Reason"));S(cmd,"@cancelled",Legacy(cancelled));
        await cmd.ExecuteNonQueryAsync(ct);
        if(mapped.Length==0)
            await WriteImportAuditAsync(cn,tx,hotel,reg,user,userName,source,ImportDataKind.Reservations,V(x,"ReservationID"),ct);
        return reg;
    }

    // The HOP No Shows export contains no event time, amount or cancellation reason.
    // Store a historical archive only, without inventing revenue/payments or blocking inventory.
    // No-show date is inferred as DateOfArrival, never represented as an actual event timestamp.
    private static async Task<string> AddStandaloneNoShowAsync(SqlConnection cn,SqlTransaction tx,string hotel,
        string user,string userName,string source,Dictionary<string,string> x,CancellationToken ct)
    {
        var reg=OriginalRegId(x);
        await AcquireKeyLockAsync(cn,tx,hotel,"ORA",ImportDataKind.Reservations,"REG-ID:"+reg,ct);
        var mapped=await GetMappedRegAsync(cn,tx,hotel,source,reg,ct);
        if(mapped.Length==0)
        {
            if(await ReservationIdInUseAsync(cn,tx,hotel,reg,ct))
                throw new InvalidOperationException("HOP reservation already exists. Review before importing a no-show.");
        }
        else
        {
            if(!await ExistsAsync(cn,tx,@"SELECT TOP 1 1 FROM dbo.NoShowTB WHERE hotel_id=@h AND reg_id=@k",hotel,reg,ct))
                throw new InvalidOperationException("This reservation is not an archived no-show.");
            if(await ExistsAsync(cn,tx,@"SELECT TOP 1 1 FROM dbo.NoShowTB WHERE hotel_id=@h AND reg_id=@k AND room_no=@room",hotel,reg,ct,V(x,"RoomNo")))
                throw new InvalidOperationException("This room is already included in the archived no-show.");
        }
        var arrival=GetDate(x,"Arrival"); var departure=GetDate(x,"Departure");
        if(departure.Date<=arrival.Date) throw new InvalidOperationException("Invalid no-show stay dates.");
        await using(var cmd=Cmd(cn,tx,@"INSERT INTO dbo.NoShowTB
(reg_id,GuestName,LastName,ArrivalDate,dept_date,Agency,Status,total_amount,room_no,rateplanname,
user_id,res_status,cb_status,hotel_id,noofrooms,room_category,number_of_adult,number_of_minor,reason)
VALUES(@reg,@guest,'',@arr,@dep,@source,@source,0,@room,@plan,
@user,'No Show','1',@hotel,1,'',1,0,@reason)"))
        {
            S(cmd,"@reg",reg);S(cmd,"@guest",V(x,"Guest"));S(cmd,"@arr",Legacy(arrival));
            S(cmd,"@dep",Legacy(departure));S(cmd,"@source","Imported: "+source);
            S(cmd,"@room",V(x,"RoomNo"));S(cmd,"@plan",V(x,"RatePlan"));
            S(cmd,"@user",user);S(cmd,"@hotel",hotel);
            S(cmd,"@reason","HOP historical no-show: amount and actual no-show time not supplied by source.");
            await cmd.ExecuteNonQueryAsync(ct);
        }
        if(mapped.Length==0)
            await WriteImportAuditAsync(cn,tx,hotel,reg,user,userName,source,ImportDataKind.Reservations,reg,ct);
        return reg;
    }

    private static async Task<string> CheckInAsync(SqlConnection cn,SqlTransaction tx,string hotel,string user,string source,
        Dictionary<string,string> x,DateTime hotelToday,CancellationToken ct)
    {
        var reg=await GetMappedRegAsync(cn,tx,hotel,source,V(x,"ReservationID"),ct);
        var when=GetDate(x,"CheckInAt");
        await using(var conflict=Cmd(cn,tx,@"
SELECT TOP 1 1 FROM dbo.GuestInformationLogTB g WITH(UPDLOCK,HOLDLOCK)
JOIN dbo.NewReservationsTB r ON r.hotel_id=g.hotel_id AND r.room_no=g.room_no
WHERE r.hotel_id=@hotel AND r.reg_id=@reg AND g.reg_id<>@reg
AND LOWER(ISNULL(g.res_status,'')) IN ('check in','checked in')
AND TRY_CONVERT(date,g.ArrivalDate,110)<TRY_CONVERT(date,r.dept_date,110)
AND TRY_CONVERT(date,g.DepartureDate,110)>TRY_CONVERT(date,r.ArrivalDate,110)"))
        {
            S(conflict,"@hotel",hotel);S(conflict,"@reg",reg);
            if(await conflict.ExecuteScalarAsync(ct) is not null)
                throw new InvalidOperationException("Room has a conflicting checked-in stay.");
        }
        await using var copy=Cmd(cn,tx,@"
INSERT INTO dbo.GuestInformationLogTB(reg_id,visit_id,GuestName,LastName,ArrivalDate,ArrivalTime,DepartureDate,DepartureTime,
Address,Country,City,NumberOfAdults,NumberOfMinors,Email,PhoneNo,Agency,Status,totalnights,
res_status,hotel_id,user_id,systemUser,systemName,ipAddress,council_id,room_category,
room_no,shuffle_type,cb_status,advance_paid,total_amount,payment_method,booking_id,noofrooms,rateplan_id,rateplanname)
SELECT TOP 1 reg_id,'add',GuestName,LastName,ArrivalDate,@time,dept_date,'',Address,'','',number_of_adult,number_of_minor,
Email,PhoneNo,Agency,Status,DATEDIFF(day,TRY_CONVERT(date,ArrivalDate,110),TRY_CONVERT(date,dept_date,110)),
'check in',hotel_id,@user,@user,'ImportData','ImportData',hotel_id,room_category,
room_no,'manual','1',advance_paid,total_amount,payment_method,booking_id,noofrooms,rateplan_id,rateplanname
FROM dbo.NewReservationsTB WHERE hotel_id=@hotel AND reg_id=@reg;
DELETE FROM dbo.NewReservationsTB WHERE hotel_id=@hotel AND reg_id=@reg;
UPDATE dbo.payments SET res_status='check in' WHERE hotel_id=@hotel AND reg_id=@reg
AND room_no=@room AND descr='Room Rent';
UPDATE dbo.PaymentsUpdateTB SET status='check in' WHERE hotel_id=@hotel AND reg_id=@reg;");
        S(copy,"@reg",reg);S(copy,"@hotel",hotel);S(copy,"@user",user);S(copy,"@room",V(x,"RoomNo"));
        S(copy,"@time",when.ToString("hh:mm tt",CultureInfo.InvariantCulture));
        await copy.ExecuteNonQueryAsync(ct);
        // Imported historical check-ins must not mark a room occupied today.
        await using(var room=Cmd(cn,tx,@"
UPDATE dbo.RoomsTB SET room_status='Occupied'
WHERE Hotel_id=@hotel AND room_no=(SELECT TOP 1 room_no FROM dbo.GuestInformationLogTB
WHERE hotel_id=@hotel AND reg_id=@reg)
AND EXISTS(SELECT 1 FROM dbo.GuestInformationLogTB
WHERE hotel_id=@hotel AND reg_id=@reg AND TRY_CONVERT(date,ArrivalDate,110)<=@today
AND TRY_CONVERT(date,DepartureDate,110)>@today)"))
        {
            S(room,"@hotel",hotel);S(room,"@reg",reg);D(room,"@today",hotelToday.Date);
            await room.ExecuteNonQueryAsync(ct);
        }
        return reg;
    }

    // HOP historical checked-out reservations can be imported directly from its
    // Reservations export. No fake payment is inserted, and no past availability
    // is modified. Each room item creates one Room Rent row in dbo.payments.
    private static async Task<string> AddHistoricalCheckOutAsync(SqlConnection cn,SqlTransaction tx,
        string hotel,string user,string userName,string source,Dictionary<string,string> x,CancellationToken ct)
    {
        var issue=ValidateHistoricalCheckoutSnapshot(x);
        if(issue.Length>0)throw new InvalidOperationException(issue);
        var originalReg=OriginalRegId(x);
        var mapped=await GetMappedRegAsync(cn,tx,hotel,source,originalReg,ct);
        var first=mapped.Length==0;
        var reg=first?originalReg:mapped;
        var room=V(x,"RoomNo");
        if(!first && !await CheckOutAlreadyArchivedAsync(cn,tx,hotel,reg,ct))
            throw new InvalidOperationException("Reservation is not archived as checked out.");
        if(!await RoomExistsAsync(cn,tx,hotel,room,ct))
            throw new InvalidOperationException("Room not found. Import Rooms first.");
        if(!first && await CheckOutRoomExistsAsync(cn,tx,hotel,reg,room,ct))
            throw new InvalidOperationException("Room already imported under this reservation.");
        var arrival=GetDate(x,"Arrival");
        var departure=GetDate(x,"Departure");
        var checkOut=GetDate(x,"CheckOutAt");
        var nights=(departure.Date-arrival.Date).Days;
        var amount=GetMoney(x,"Total");
        var gst=GetMoney(x,"GST");
        var bed=GetMoney(x,"Bed");
        var guest=V(x,"Guest");
        var plan=V(x,"RatePlan");
        if(nights<1 || nights>730 || amount<0 || gst<0 || bed<0 || gst+bed>amount)
            throw new InvalidOperationException("Invalid nights, amount or tax in historical checkout.");
        string category, categoryId;
        await using(var read=Cmd(cn,tx,@"SELECT TOP 1 room_category,localcategoryid FROM dbo.RoomsTB WITH(UPDLOCK,HOLDLOCK)
WHERE Hotel_id=@hotel AND room_no=@room"))
        {
            S(read,"@hotel",hotel); S(read,"@room",room);
            await using var rd=await read.ExecuteReaderAsync(ct);
            if(!await rd.ReadAsync(ct)) throw new InvalidOperationException("Room no longer exists.");
            category=Convert.ToString(rd[0],CultureInfo.InvariantCulture)??"";
            categoryId=Convert.ToString(rd[1],CultureInfo.InvariantCulture)??"";
        }
        var planId="";
        if(plan.Length>0)
        {
            await using var planQuery=Cmd(cn,tx,@"SELECT TOP 1 localplanid FROM dbo.category_plan
WHERE hotel_id=@hotel AND category=@category AND planname=@plan");
            S(planQuery,"@hotel",hotel);S(planQuery,"@category",category);S(planQuery,"@plan",plan);
            planId=Convert.ToString(await planQuery.ExecuteScalarAsync(ct),CultureInfo.InvariantCulture)??"";
        }
        // Lock the reservation identifier across multiple uploads and servers.
        await AcquireKeyLockAsync(cn,tx,hotel,"ORA",ImportDataKind.Reservations,"REG-ID:"+reg,ct);
        if(first)
        {
            if(await ReservationIdInUseAsync(cn,tx,hotel,reg,ct))
                throw new InvalidOperationException("HOP Reservation Number already exists in ORA PMS.");
            await using(var guestInsert=Cmd(cn,tx,@"
INSERT INTO dbo.GuestInformationLogTB
(reg_id,visit_id,GuestName,LastName,ArrivalDate,ArrivalTime,DepartureDate,DepartureTime,
Address,Country,City,NumberOfAdults,NumberOfMinors,Email,PhoneNo,Agency,Status,totalnights,
res_status,hotel_id,user_id,systemUser,systemName,ipAddress,council_id,room_category,
room_no,shuffle_type,cb_status,advance_paid,total_amount,payment_method,booking_id,noofrooms,rateplan_id,rateplanname)
VALUES(@reg,'add',@guest,'',@arr,'',@dep,'','', '', '',@adults,@children,
@email,@phone,@source,@source,@nights,'check out',@hotel,@user,@user,'ImportData','ImportData',@hotel,
@category,@room,'manual','1',0,@total,'','',1,@planId,@plan)"))
            {
                S(guestInsert,"@reg",reg);S(guestInsert,"@guest",guest);
                S(guestInsert,"@arr",Legacy(arrival));S(guestInsert,"@dep",Legacy(departure));
                S(guestInsert,"@email",V(x,"Email"));S(guestInsert,"@phone",V(x,"Phone"));
                S(guestInsert,"@source",V(x,"Source").Length>0?V(x,"Source"):"Imported: "+source);
                guestInsert.Parameters.Add("@adults",SqlDbType.Int).Value=I(x,"Adults",1);
                guestInsert.Parameters.Add("@children",SqlDbType.Int).Value=I(x,"Children",0);
                guestInsert.Parameters.Add("@nights",SqlDbType.Int).Value=nights;
                S(guestInsert,"@hotel",hotel);S(guestInsert,"@user",user);
                S(guestInsert,"@category",category);S(guestInsert,"@room",room);
                M(guestInsert,"@total",amount);S(guestInsert,"@planId",planId);S(guestInsert,"@plan",plan);
                await guestInsert.ExecuteNonQueryAsync(ct);
            }
            await using(var summary=Cmd(cn,tx,@"
INSERT INTO dbo.PaymentsUpdateTB
(currentdate,name,grand_total,room_security,payable,paid_amount,remaining_amount,payment_method,
status,visit_id,user_id,hotel_id,systemUser,systemName,ipAddress,reg_id)
VALUES(@date,@guest,@amount,0,@amount,0,@amount,'','check out','add',@user,@hotel,@user,
'ImportData','ImportData',@reg)"))
            {
                D(summary,"@date",arrival);S(summary,"@guest",guest);M(summary,"@amount",amount);
                S(summary,"@user",user);S(summary,"@hotel",hotel);S(summary,"@reg",reg);
                await summary.ExecuteNonQueryAsync(ct);
            }
            await WriteImportAuditAsync(cn,tx,hotel,reg,user,userName,source,
                ImportDataKind.Reservations,originalReg,ct);
        }
        else
        {
            await using var summary=Cmd(cn,tx,@"
UPDATE dbo.GuestInformationLogTB SET total_amount=ISNULL(TRY_CONVERT(decimal(18,2),total_amount),0)+@amount,
noofrooms=ISNULL(TRY_CONVERT(int,noofrooms),1)+1 WHERE hotel_id=@hotel AND reg_id=@reg;
UPDATE dbo.PaymentsUpdateTB SET grand_total=ISNULL(TRY_CONVERT(decimal(18,2),grand_total),0)+@amount,
payable=ISNULL(TRY_CONVERT(decimal(18,2),payable),0)+@amount,
remaining_amount=ISNULL(TRY_CONVERT(decimal(18,2),remaining_amount),0)+@amount
WHERE hotel_id=@hotel AND reg_id=@reg;");
            S(summary,"@hotel",hotel);S(summary,"@reg",reg);M(summary,"@amount",amount);
            await summary.ExecuteNonQueryAsync(ct);
        }
        // Saved GST / Bed Tax are zero when HOP did not provide them; no new VAT
        // and no artificial cash/card transaction is created.
        await using(var charge=Cmd(cn,tx,@"
INSERT INTO dbo.payments
(currentdate,deductioninfo,descr,Type,NumberOfRoom,Rate,Charge,GST,Bed,Nights,totalamount,
reg_id,payment_status,hid,cb_status,room_no,res_status,visit_id,hotel_id,ipAddress,systemUser,systemName,
discount,suffle_type,category_id,rateplan,rateplanname,ArrivalDate,DepartureDate,guestname,
room_adults,room_children,room_infants)
VALUES(@date,'0','Room Rent',@category,1,@rate,0,@gst,@bed,@nights,@amount,
@reg,'unpaid',@user,'1',@room,'check out','',@hotel,'ImportData',@user,'ImportData',
'0','manual',@categoryId,@planId,@plan,@arr,@dep,@guest,@adults,@children,0)"))
        {
            D(charge,"@date",arrival);S(charge,"@category",category);
            M(charge,"@rate",Math.Round(amount/nights,2));M(charge,"@gst",gst);M(charge,"@bed",bed);
            charge.Parameters.Add("@nights",SqlDbType.Int).Value=nights;
            M(charge,"@amount",amount);S(charge,"@reg",reg);S(charge,"@user",user);
            S(charge,"@room",room);S(charge,"@hotel",hotel);
            S(charge,"@categoryId",categoryId);S(charge,"@planId",planId);S(charge,"@plan",plan);
            S(charge,"@arr",Legacy(arrival));S(charge,"@dep",Legacy(departure));
            S(charge,"@guest",guest);
            charge.Parameters.Add("@adults",SqlDbType.Int).Value=I(x,"Adults",1);
            charge.Parameters.Add("@children",SqlDbType.Int).Value=I(x,"Children",0);
            await charge.ExecuteNonQueryAsync(ct);
        }
        // The source gives a checkout status but no verified actual time.
        // Store the HOP departure date and its provenance in the existing Guest Log,
        // not in payments.actual_checkout_date (which means an actual event date).
        await using(var audit=Cmd(cn,tx,@"
INSERT INTO dbo.Reserv_log(reg_id,descp,user_id,username,hotel_id,status,ipAddress,systemname,createdat,date)
VALUES(@reg,@description,@user,@username,@hotel,'ImportData','ImportData','ImportData',@now,@today)"))
        {
            S(audit,"@reg",reg);
            S(audit,"@description","HOP checked-out import: Departure Date "+
                checkOut.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture)+" (actual checkout time unknown)",150);
            S(audit,"@user",user);S(audit,"@username",userName);S(audit,"@hotel",hotel);
            var now=DateTime.UtcNow;D(audit,"@now",now);
            S(audit,"@today",now.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture),10);
            await audit.ExecuteNonQueryAsync(ct);
        }
        return reg;
    }

    private static async Task<string> CheckOutAsync(SqlConnection cn,SqlTransaction tx,string hotel,string source,
        Dictionary<string,string> x,DateTime hotelToday,CancellationToken ct)
    {
        var reg=await GetMappedRegAsync(cn,tx,hotel,source,V(x,"ReservationID"),ct);
        var when=GetDate(x,"CheckOutAt");
        var inferred = V(x,"CheckOutDateInferred") == "1";
        var roomNo=V(x,"RoomNo");
        // A HOP Reservation Item / Room identifies the precise Room Rent row.
        // Without one, accept only a booking that contains exactly one room charge.
        if(roomNo.Length==0)
        {
            await using var rooms=Cmd(cn,tx,@"SELECT COUNT(*) FROM dbo.payments
WHERE hotel_id=@hotel AND reg_id=@reg AND descr='Room Rent'");
            S(rooms,"@hotel",hotel);S(rooms,"@reg",reg);
            if(Convert.ToInt32(await rooms.ExecuteScalarAsync(ct),CultureInfo.InvariantCulture)!=1)
                throw new InvalidOperationException("Multi-room checkout requires the HOP Room identifier.");
            await using var one=Cmd(cn,tx,@"SELECT TOP 1 room_no FROM dbo.payments
WHERE hotel_id=@hotel AND reg_id=@reg AND descr='Room Rent'");
            S(one,"@hotel",hotel);S(one,"@reg",reg);
            roomNo=Convert.ToString(await one.ExecuteScalarAsync(ct),CultureInfo.InvariantCulture)??"";
        }
        await using(var cmd=Cmd(cn,tx,@"UPDATE dbo.payments
SET res_status='check out',
    DepartureTime=CASE WHEN @inferred=1 THEN DepartureTime ELSE @time END,
    actual_checkout_date=CASE WHEN @inferred=1 THEN actual_checkout_date ELSE @date END
WHERE hotel_id=@hotel AND reg_id=@reg AND room_no=@room AND descr='Room Rent'
AND LOWER(res_status) IN ('check in','checked in');"))
        {
            S(cmd,"@reg",reg);S(cmd,"@hotel",hotel);S(cmd,"@room",roomNo);
            S(cmd,"@time",when.ToString("hh:mm tt",CultureInfo.InvariantCulture));D(cmd,"@date",when.Date);
            cmd.Parameters.Add("@inferred", SqlDbType.Bit).Value = inferred;
            if(await cmd.ExecuteNonQueryAsync(ct)==0)
                throw new InvalidOperationException("This Room Rent is not checked in or is already checked out.");
        }
        var stillInhouse=await ExistsAsync(cn,tx,@"SELECT TOP 1 1 FROM dbo.payments
WHERE hotel_id=@h AND reg_id=@k AND descr='Room Rent'
AND LOWER(res_status) IN ('check in','checked in')",hotel,reg,ct);
        if(!stillInhouse)
        {
            await using var complete=Cmd(cn,tx,@"UPDATE dbo.GuestInformationLogTB SET res_status='check out',
    DepartureTime=CASE WHEN @inferred=1 THEN DepartureTime ELSE @time END
WHERE hotel_id=@hotel AND reg_id=@reg;
UPDATE dbo.PaymentsLogTB SET status='check out' WHERE hotel_id=@hotel AND reg_id=@reg;
UPDATE dbo.PaymentsUpdateTB SET status='check out' WHERE hotel_id=@hotel AND reg_id=@reg;");
            S(complete,"@hotel",hotel);S(complete,"@reg",reg);
            S(complete,"@time",when.ToString("hh:mm tt",CultureInfo.InvariantCulture));
            complete.Parameters.Add("@inferred", SqlDbType.Bit).Value = inferred;
            await complete.ExecuteNonQueryAsync(ct);
        }
        // Release room status only when there is no other live in-house charge.
        await using var release=Cmd(cn,tx,@"UPDATE dbo.RoomsTB SET room_status='Available'
WHERE Hotel_id=@hotel AND room_no=@room AND room_status='Occupied'
AND @date<=@today AND NOT EXISTS(
SELECT 1 FROM dbo.payments p WHERE p.hotel_id=@hotel AND p.room_no=@room
AND p.descr='Room Rent' AND LOWER(p.res_status) IN ('check in','checked in')
AND TRY_CONVERT(date,p.ArrivalDate,110)<=@today AND TRY_CONVERT(date,p.DepartureDate,110)>@today)");
        S(release,"@hotel",hotel);S(release,"@room",roomNo);D(release,"@today",hotelToday.Date);
        D(release,"@date",when.Date);
        await release.ExecuteNonQueryAsync(ct);
        return reg;
    }

    private static async Task<string> AddPaymentAsync(SqlConnection cn,SqlTransaction tx,string hotel,string user,string userName,
        string source,Dictionary<string,string> x,CancellationToken ct)
    {
        var reg=await GetMappedRegAsync(cn,tx,hotel,source,V(x,"ReservationID"),ct);
        var when=GetDate(x,"PaymentAt");var amount=GetMoney(x,"Amount");
        var method=await MatchPaymentMethodAsync(cn,tx,hotel,V(x,"Method"),ct);
        if(method.Length==0)throw new InvalidOperationException("Payment method not configured for this hotel.");
        await using(var payment=Cmd(cn,tx,@"
INSERT INTO dbo.PaymentsLogTB(visit_id,status,reg_id,currentdate,name,paid_amount,payment_method,user_id,hotel_id,
cb_status,ipAddress,systemUser,systemName,CardType)
VALUES('add',COALESCE((SELECT TOP 1 res_status FROM dbo.GuestInformationLogTB WHERE hotel_id=@hotel AND reg_id=@reg),'reservation'),
@reg,@when,@name,@amount,@method,@user,@hotel,'1','ImportData',@username,'ImportData',@card)"))
        {
            S(payment,"@reg",reg);D(payment,"@when",when);S(payment,"@name",V(x,"Guest"));
            M(payment,"@amount",amount);S(payment,"@method",method);S(payment,"@user",user);
            S(payment,"@hotel",hotel);S(payment,"@username",userName);S(payment,"@card",V(x,"CardType"));
            await payment.ExecuteNonQueryAsync(ct);
        }
        // Update only the latest payment summary, which is authoritative in ORA PMS.
        // Do not move its currentdate: historical payment reports use payment log dates.
        await using(var update=Cmd(cn,tx,@"
UPDATE dbo.PaymentsUpdateTB WITH(UPDLOCK)
SET paid_amount=CONVERT(decimal(18,2),ISNULL(TRY_CONVERT(decimal(18,2),paid_amount),0)+@amount),
    remaining_amount=CONVERT(decimal(18,2),ISNULL(TRY_CONVERT(decimal(18,2),remaining_amount),0)-@amount),
    payment_method=@method
WHERE id=(SELECT TOP 1 id FROM dbo.PaymentsUpdateTB WITH(UPDLOCK,HOLDLOCK)
          WHERE hotel_id=@hotel AND reg_id=@reg ORDER BY id DESC)
  AND hotel_id=@hotel AND reg_id=@reg"))
        {
            S(update,"@reg",reg);S(update,"@hotel",hotel);S(update,"@method",method);M(update,"@amount",amount);
            if(await update.ExecuteNonQueryAsync(ct)==0)throw new InvalidOperationException("Payment summary missing for this reservation.");
        }
        return reg;
    }

    private static async Task<string> CloseReservationAsync(SqlConnection cn,SqlTransaction tx,string hotel,string user,string source,
        Dictionary<string,string> x,string table,string status,DateTime hotelToday,CancellationToken ct)
    {
        // table is hard-coded by enum dispatch, never supplied by the client.
        if(table is not ("CancelledReservationsTB" or "NoShowTB"))throw new InvalidOperationException("Invalid status target.");
        var reg=await GetMappedRegAsync(cn,tx,hotel,source,V(x,"ReservationID"),ct);
        var hasCancelDate = table=="CancelledReservationsTB";
        var dateColumn = hasCancelDate ? ",date" : "";
        var dateValue = hasCancelDate ? ",@eventDate" : "";
        await using(var archive=Cmd(cn,tx,$@"
INSERT INTO dbo.{table}(reg_id,GuestName,LastName,ArrivalDate,dept_date,Agency,Status,total_amount,room_no,rateplanname,
user_id,res_status,cb_status,hotel_id,noofrooms,room_category,number_of_adult,number_of_minor,reason{dateColumn})
SELECT TOP 1 reg_id,GuestName,LastName,ArrivalDate,dept_date,Agency,@status,total_amount,room_no,rateplanname,
@user,@status,'1',hotel_id,noofrooms,room_category,number_of_adult,number_of_minor,@reason{dateValue}
FROM dbo.NewReservationsTB WHERE hotel_id=@hotel AND reg_id=@reg;"))
        {
            S(archive,"@hotel",hotel);S(archive,"@reg",reg);S(archive,"@user",user);
            S(archive,"@status",status);S(archive,"@reason",V(x,"Reason"));
            if(hasCancelDate)S(archive,"@eventDate",Legacy(GetDate(x,"OccurredAt")));
            if(await archive.ExecuteNonQueryAsync(ct)==0)throw new InvalidOperationException("Reservation is no longer active.");
        }
        // Release the future availability held by EVERY imported room charge.
        // A multi-room reservation may contain different categories or date ranges.
        var roomsToRelease=new List<(DateTime arrival,DateTime departure,string category)>();
        await using(var availability=Cmd(cn,tx,@"SELECT p.ArrivalDate,p.DepartureDate,p.category_id,
r.ArrivalDate AS HeaderArrival,r.dept_date AS HeaderDeparture
FROM dbo.payments p INNER JOIN dbo.NewReservationsTB r ON r.hotel_id=p.hotel_id AND r.reg_id=p.reg_id
WHERE p.hotel_id=@hotel AND p.reg_id=@reg AND p.descr='Room Rent'"))
        {
            S(availability,"@hotel",hotel);S(availability,"@reg",reg);
            await using var reader=await availability.ExecuteReaderAsync(ct);
            while(await reader.ReadAsync(ct))
            {
                var arrival=Convert.ToString(reader[0]==DBNull.Value?reader[3]:reader[0],CultureInfo.InvariantCulture)??"";
                var departure=Convert.ToString(reader[1]==DBNull.Value?reader[4]:reader[1],CultureInfo.InvariantCulture)??"";
                var cat=reader[2]==DBNull.Value?"":Convert.ToString(reader[2],CultureInfo.InvariantCulture)??"";
                if(TryDate(arrival,out var a)&&TryDate(departure,out var d))
                    roomsToRelease.Add((a,d,cat));
            }
        }
        foreach(var (arrival,departure,category) in roomsToRelease)
            await AdjustAvailabilityAsync(cn,tx,hotel,category,arrival,departure,+1,hotelToday,ct);
        // Do not delete financial history: payments and room charges are kept for audit.
        await using(var end=Cmd(cn,tx,@"
UPDATE dbo.payments SET res_status=@status WHERE hotel_id=@hotel AND reg_id=@reg;
UPDATE dbo.PaymentsUpdateTB SET status=@status WHERE hotel_id=@hotel AND reg_id=@reg;
DELETE FROM dbo.NewReservationsTB WHERE hotel_id=@hotel AND reg_id=@reg;"))
        {
            S(end,"@hotel",hotel);S(end,"@reg",reg);S(end,"@status",status);
            await end.ExecuteNonQueryAsync(ct);
        }
        return reg;
    }

    private static string Legacy(DateTime d)=>d.ToString("MM-dd-yyyy",CultureInfo.InvariantCulture);
    private static DateTime GetDate(Dictionary<string,string> x,string key)=>TryDate(V(x,key),out var date)?date:
        throw new InvalidOperationException("Invalid "+key+" date.");
    private static decimal GetMoney(Dictionary<string,string> x,string key)=>V(x,key).Length==0?0:
        TryMoney(V(x,key),out var value)?value:throw new InvalidOperationException("Invalid "+key+" amount.");
    private static int I(Dictionary<string,string> x,string key,int fallback)=>int.TryParse(V(x,key),out var v)?v:fallback;
    private static async Task<object?> ScalarAsync(SqlConnection cn,SqlTransaction tx,string sql,CancellationToken ct)
    {await using var cmd=Cmd(cn,tx,sql);return await cmd.ExecuteScalarAsync(ct);}
    private static SqlCommand Cmd(SqlConnection cn,SqlTransaction? tx,string sql)=>new(sql,cn,tx){CommandTimeout=60};
    private static void S(SqlCommand cmd,string key,string value,int size=200)
        =>cmd.Parameters.Add(key,SqlDbType.NVarChar,size).Value=(object)(value??"");
    private static void M(SqlCommand cmd,string key,decimal value)
    {var p=cmd.Parameters.Add(key,SqlDbType.Decimal);p.Precision=18;p.Scale=2;p.Value=value;}
    private static void D(SqlCommand cmd,string key,DateTime value)=>cmd.Parameters.Add(key,SqlDbType.DateTime).Value=value;
}
