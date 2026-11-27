using System.Linq;
using System.Threading;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Data;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Orapmshms.Services;

namespace Orapmshms.Controllers;

[Authorize]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[Route("DatabaseManagement")]
public sealed class DatabaseManagementController : Controller
{
    private readonly IConfiguration config;
    private readonly ILogger<DatabaseManagementController> log;
    private const string Prefix = DatabaseBackupService.Prefix;
    public DatabaseManagementController(IConfiguration config, ILogger<DatabaseManagementController> log)
    { this.config = config; this.log = log; }
    private bool Allowed() => DatabaseManagementAccess.Allowed(HttpContext, config);
    // Return a real HTTP 403 instead of invoking cookie AccessDeniedPath (/LoginHMS).
    // Authentication, the global PMS master filter and the SuperUser role check remain in force.
    private IActionResult AccessDeniedResponse()
    {
        var message = !config.GetValue<bool>("DatabaseManagement:Enabled")
            ? "Database Management is disabled. Contact the system administrator."
            : "Database Management is restricted to the SuperUser role.";
        return new ContentResult
        {
            StatusCode = 403,
            ContentType = "text/plain; charset=utf-8",
            Content = message
        };
    }

    private string Actor => HttpContext.Session.GetString("UserId") ?? "unknown";
    public sealed record BackupRow(int Id, DateTime Finished, long Bytes, string Path, int Position, long OriginalBytes, DateTime Started, string Source = "Manual", string State = "Recorded")
    {
        public string FileName => Path.Replace('\\', '/').Split('/').Last();
    }
    public sealed record Page(string Database, IReadOnlyList<BackupRow> Rows, bool Ready, string? Error = null,
        string Folder = "", DateTime? ServerNow = null, int FreshnessHours = 24, bool Compression = false,
        bool Connected = false, bool HistoryAvailable = false,
        long? DataFileBytes = null, long? LogFileBytes = null,
        long? TableReservedBytes = null, long? TableUsedBytes = null,
        DatabaseBackupSchedule? Schedule = null, bool SchedulerConfigured = false, bool RetentionConfigured = false,
        bool ScheduleAvailable = false, bool RetentionMatches = false)
    {
        public long? DatabaseBytes => DataFileBytes.HasValue && LogFileBytes.HasValue
            ? DataFileBytes.Value + LogFileBytes.Value : null;
    }

    private DatabaseBackupService BackupService => new(config, log);
    private Task<SqlConnection> OpenAsync(CancellationToken ct) => BackupService.OpenAsync(ct);
    private Task<string> FolderAsync(SqlConnection cn, CancellationToken ct) => BackupService.FolderAsync(cn, ct);
    private static Task<bool> CanBackupAsync(SqlConnection cn, CancellationToken ct) => DatabaseBackupService.CanBackupAsync(cn, ct);
    // SQL Server reports file sizes as allocated 8 KB pages, not used space.
    // Query the connected database only; no backup-folder or msdb access required.
    private static async Task<(long DataBytes, long LogBytes)> DatabaseSizeAsync(SqlConnection cn, CancellationToken ct)
    {
        using var cmd = new SqlCommand(@"SELECT
    COALESCE(SUM(CASE WHEN type = 0 THEN CONVERT(bigint, size) ELSE CONVERT(bigint, 0) END), 0) * 8192 AS DataBytes,
    COALESCE(SUM(CASE WHEN type = 1 THEN CONVERT(bigint, size) ELSE CONVERT(bigint, 0) END), 0) * 8192 AS LogBytes
FROM sys.database_files
WHERE type IN (0, 1);", cn);
        using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) throw new InvalidOperationException("Database file size was not returned.");
        return (r.GetInt64(0), r.GetInt64(1));
    }
    // Match the table-wise report: user tables, all indexes, all partitions.
    // Keep allocated database files separate from table reservations.
    private static async Task<(long ReservedBytes, long UsedBytes)> TableSizeAsync(SqlConnection cn, CancellationToken ct)
    {
        using var cmd = new SqlCommand(@"SELECT
    COALESCE(SUM(CONVERT(bigint, p.reserved_page_count)), 0) * 8192,
    COALESCE(SUM(CONVERT(bigint, p.used_page_count)), 0) * 8192
FROM sys.tables t
INNER JOIN sys.dm_db_partition_stats p ON t.object_id = p.object_id
WHERE t.is_ms_shipped = 0;", cn);
        using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) throw new InvalidOperationException("Table size was not returned.");
        return (r.GetInt64(0), r.GetInt64(1));
    }
    private async Task<List<BackupRow>> HistoryAsync(SqlConnection cn, int? id, CancellationToken ct)
    {
        using var cmd = new SqlCommand(@"SELECT TOP (100) b.backup_set_id, b.backup_finish_date,
CONVERT(bigint,COALESCE(b.compressed_backup_size,b.backup_size)), f.physical_device_name, CONVERT(int,b.position), CONVERT(bigint,b.backup_size), b.backup_start_date, b.name
FROM msdb.dbo.backupset b
JOIN msdb.dbo.backupmediafamily f ON b.media_set_id=f.media_set_id
JOIN msdb.dbo.backupmediaset m ON b.media_set_id=m.media_set_id
WHERE b.database_name=@db AND b.type='D' AND b.is_copy_only=1 AND b.has_backup_checksums=1
AND b.backup_finish_date IS NOT NULL AND b.name LIKE @prefix AND m.media_family_count=1 AND f.device_type=2
AND (@id IS NULL OR b.backup_set_id=@id)
ORDER BY b.backup_finish_date DESC, b.backup_set_id DESC;", cn);
        cmd.Parameters.Add("@db",SqlDbType.NVarChar,128).Value=cn.Database;
        cmd.Parameters.Add("@prefix",SqlDbType.NVarChar,128).Value=Prefix+"%";
        cmd.Parameters.Add("@id",SqlDbType.Int).Value=(object?)id ?? DBNull.Value;
        var rows = new List<BackupRow>();
        using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while(await r.ReadAsync(ct))
            {
                var path = r.GetString(3);
                // Retain history from earlier saved directories. Only module-generated, local paths
                // with the exact expected backup name and label are accepted. No file access here.
                int split = Math.Max(path.LastIndexOf('\\'), path.LastIndexOf('/'));
                if (split < 0) continue;
                var file = path[(split + 1)..];
                if (!Regex.IsMatch(file, @"\AORA_\d{8}_\d{6}_[a-f0-9]{32}\.bak\z")) continue;
                try { _ = DatabaseBackupService.NormalizeFolder(path[..(split + 1)]); }
                catch (InvalidOperationException) { continue; }
                var label = r.GetString(7);
                if (label != Prefix + file && label != Prefix + "Automatic " + file) continue;
                rows.Add(new(r.GetInt32(0),r.GetDateTime(1),r.GetInt64(2),path,r.GetInt32(4),r.GetInt64(5),r.GetDateTime(6), r.GetString(7).StartsWith(Prefix+"Automatic ", StringComparison.Ordinal) ? "Automatic" : "Manual"));
            }
        }
        // Optional schedule state enrichment; never hide manual history if migration is absent.
        var automatic = rows.Where(x => x.Source == "Automatic").ToList();
        if (automatic.Count > 0)
        {
            try
            {
                using var states = new SqlCommand("SELECT FileName,State FROM dbo.DatabaseBackupRunTB WHERE FileName IN (" +
                    string.Join(",", automatic.Select((_,i) => "@f"+i)) + ");",cn);
                for (int i=0;i<automatic.Count;i++) states.Parameters.Add("@f"+i,SqlDbType.VarChar,80).Value=automatic[i].FileName;
                var map = new Dictionary<string,string>(StringComparer.Ordinal);
                using (var r = await states.ExecuteReaderAsync(ct))
                    while (await r.ReadAsync(ct)) map[r.GetString(0)] = r.GetString(1);
                for (int i=0;i<rows.Count;i++)
                    if (rows[i].Source=="Automatic" && map.TryGetValue(rows[i].FileName,out var state)) rows[i] = rows[i] with { State=state };
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { log.LogWarning(ex,"Automatic backup status lookup failed"); }
        }
        return rows;
    }
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        if (!Allowed()) return AccessDeniedResponse();
        var database = "—";
        var folder = "";
        bool retentionMatches = false;
        DateTime? now = null;
        var connected = false;
        var ready = false;
        var historyAvailable = false;
        long? dataFileBytes = null;
        long? logFileBytes = null;
        long? tableReservedBytes = null;
        long? tableUsedBytes = null;
        var rows = new List<BackupRow>();
        var errors = new List<string>();
        DatabaseBackupSchedule? schedule = null;
        bool scheduleAvailable = false;
        try
        {
            await using var cn = await OpenAsync(ct);
            connected = true;
            database = cn.Database;
            try { schedule = await BackupService.ReadScheduleAsync(cn, ct); scheduleAvailable = true; }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { log.LogWarning(ex, "Backup schedule lookup failed"); }
            try
            {
                var sizes = await DatabaseSizeAsync(cn, ct);
                dataFileBytes = sizes.DataBytes;
                logFileBytes = sizes.LogBytes;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Database size is informational; its failure must not disable backup or history.
                log.LogWarning(ex, "Database size lookup failed for {Actor}", Actor);
            }
            try
            {
                var tables = await TableSizeAsync(cn, ct);
                tableReservedBytes = tables.ReservedBytes;
                tableUsedBytes = tables.UsedBytes;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Missing DMV permissions must not disable backup or history.
                log.LogWarning(ex, "Database table size lookup failed for {Actor}", Actor);
            }
            using var clock = new SqlCommand("SELECT GETDATE();", cn);
            now = (DateTime)(await clock.ExecuteScalarAsync(ct))!;
            try
            {
                folder = await FolderAsync(cn, ct);
                ready = await CanBackupAsync(cn, ct);
                retentionMatches = await BackupService.RetentionMatchesFolderAsync(cn, folder, ct);
                if (!ready) errors.Add("The SQL login does not have BACKUP DATABASE permission.");
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Database backup folder or permission check failed for {Actor}", Actor);
                errors.Add("Save a valid SQL Server backup folder in Backup storage. If already saved, check the SQL migration and backup login.");
            }
            {
                try
                {
                    rows = await HistoryAsync(cn, null, ct);
                    historyAvailable = true;
                }
                catch (Exception ex)
                {
                    log.LogError(ex, "Database management history failed for {Actor}", Actor);
                    errors.Add("Backup history is unavailable. Check msdb read permissions. Backup creation can still work if enabled.");
                }
            }
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Database backup connection failed for {Actor}", Actor);
            errors.Add("Unable to open the backup database connection. Check DatabaseBackup (or UseExistingConnection) and ensure it targets the live PMS database.");
        }
        return View(new Page(database, rows, ready, errors.Count == 0 ? null : string.Join(" ", errors),
            Folder: folder, ServerNow: now,
            FreshnessHours: Math.Clamp(config.GetValue<int?>("DatabaseManagement:FreshnessHours") ?? 24, 1, 720),
            Compression: config.GetValue<bool>("DatabaseManagement:Compression"), Connected: connected, HistoryAvailable: historyAvailable,
            DataFileBytes: dataFileBytes, LogFileBytes: logFileBytes,
            TableReservedBytes: tableReservedBytes, TableUsedBytes: tableUsedBytes,
            Schedule: schedule, SchedulerConfigured: BackupService.SchedulerConfigured,
            RetentionConfigured: BackupService.RetentionConfigured, ScheduleAvailable: scheduleAvailable,
            RetentionMatches: retentionMatches));
    }
    // Active request only; no SQL credentials, file paths or other users' progress are exposed.
    // The SQL application lock remains the authoritative cross-process duplicate guard.
    private static readonly ConcurrentDictionary<(string Actor, Guid Operation), ManualProgress> ActiveManualBackups = new();
    private sealed class ManualProgress
    {
        private int percent = -1;
        private int started;
        public int Percent => Volatile.Read(ref percent);
        public bool Started => Volatile.Read(ref started) == 1;
        public void Start() => Volatile.Write(ref started, 1);
        public void Report(int value)
        {
            int old;
            do { old = Percent; if (value <= old) return; }
            while (Interlocked.CompareExchange(ref percent, value, old) != old);
        }
    }
    [HttpGet("BackupProgress")]
    public IActionResult BackupProgress(Guid operationId)
    {
        if (!Allowed()) return AccessDeniedResponse();
        if (!ActiveManualBackups.TryGetValue((Actor, operationId), out var progress))
            return Json(new { active = false });
        var pct = progress.Percent;
        return Json(new { active = true, started = progress.Started,
            percent = pct < 0 ? (int?)null : pct, finalizing = pct == 100 });
    }
    [HttpPost("Backup"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Backup(Guid? operationId, CancellationToken ct)
    {
        if (!Allowed()) return AccessDeniedResponse();
        var ajax = string.Equals(Request.Headers["X-ORA-Backup-Progress"].ToString(), "1", StringComparison.Ordinal);
        if (ajax && (!operationId.HasValue || operationId == Guid.Empty))
            return BadRequest(new { success = false, message = "Missing backup operation ID." });
        var name = DatabaseBackupService.NewFileName();
        var key = (Actor, operationId.GetValueOrDefault());
        var progress = new ManualProgress();
        var registered = ajax && ActiveManualBackups.TryAdd(key, progress);
        if (ajax && !registered)
            return Conflict(new { success = false, message = "This backup request is already running. Check history before retrying." });
        IActionResult Finish(bool success, string message, int status = 200)
        {
            if (ajax) return StatusCode(status, new { success, message });
            TempData[success ? "DatabaseMessage" : "DatabaseError"] = message;
            return RedirectToAction(nameof(Index));
        }
        try
        {
            await using var cn = await OpenAsync(ct);
            if (!await DatabaseBackupService.TryLockAsync(cn, ct))
                return Finish(false, "Another backup or schedule update is running. Try later.", 409);
            var path = await BackupService.BackupPathAsync(cn, name, ct);
            log.LogInformation("Database backup requested by {Actor}: {File}",Actor,name);
            // SQL Server STATS messages are informational only; do not change error handling.
            // Never treat the 100% processed message as successful completion of the command.
            SqlInfoMessageEventHandler handler = (_, args) =>
            {
                foreach (SqlError error in args.Errors)
                {
                    var match = Regex.Match(error.Message, @"\b(\d{1,3})\s+percent\s+processed\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                    if (match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var pct) && pct >= 0 && pct <= 100)
                        progress.Report(pct);
                }
            };
            progress.Start();
            if (ajax) cn.InfoMessage += handler;
            try { await BackupService.ExecuteBackupAsync(cn, path, name, false, ct); }
            finally { if (ajax) cn.InfoMessage -= handler; }
            log.LogInformation("Database backup completed by {Actor}: {File}",Actor,name);
            return Finish(true, "Backup file created on SQL Server: " + path);
        }
        catch (SqlException ex)
        {
            log.LogError(ex, "Database backup did not confirm completion for {Actor}: {File}", Actor, name);
            var numbers = string.Join(", ", ex.Errors.Cast<SqlError>().Select(e => e.Number).Distinct());
            var hint = "Check the SQL Server error log and backup history before retrying.";
            if (ex.Errors.Cast<SqlError>().Any(e => e.Number == 3201 || e.Number == 18210 || e.Number == 3202))
            {
                hint = "SQL Server could not open or write the backup device. Check the configured folder on the SQL Server host, service-account write access and free disk space.";
                var messages = string.Join(" ", ex.Errors.Cast<SqlError>().Select(e => e.Message));
                if (messages.Contains("Operating system error 3(", StringComparison.OrdinalIgnoreCase) || messages.Contains("Operating system error 3 ", StringComparison.OrdinalIgnoreCase))
                    hint = "The backup folder was not found on the SQL Server host (OS error 3). Create it there or configure an existing backup folder.";
                else if (messages.Contains("Operating system error 5(", StringComparison.OrdinalIgnoreCase) || messages.Contains("Operating system error 5 ", StringComparison.OrdinalIgnoreCase))
                    hint = "Access denied to the backup file (OS error 5). Grant the SQL Server service account write access to the configured folder.";
                else if (messages.Contains("Operating system error 112(", StringComparison.OrdinalIgnoreCase) || messages.Contains("Operating system error 112 ", StringComparison.OrdinalIgnoreCase))
                    hint = "The backup volume has insufficient free space (OS error 112). Check available disk space on the SQL Server host.";
            }
            else if (ex.Errors.Cast<SqlError>().Any(e => e.Number == 262 || e.Number == 229))
                hint = "The backup SQL login lacks a required permission. Ask the DBA to check the backup and application-lock permissions.";
            else if (ex.Errors.Cast<SqlError>().Any(e => e.Number == 50001))
                hint = "Another backup may be running. Check its status before retrying.";
            return Finish(false, $"Backup not confirmed (SQL error {numbers}). {hint} File: {name}", 500);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Database backup did not confirm completion for {Actor}: {File}", Actor, name);
            return Finish(false, "Backup not confirmed. Check the application log and backup history before retrying. File: " + name, 500);
        }
        finally
        {
            if (registered) ActiveManualBackups.TryRemove(key, out _);
        }
    }
    [HttpPost("SaveFolder"), ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveFolder(string? backupFolder, CancellationToken ct)
    {
        if (!Allowed()) return AccessDeniedResponse();
        if (!ModelState.IsValid || string.IsNullOrWhiteSpace(backupFolder) || backupFolder.Length > 260)
        { TempData["DatabaseError"] = "Enter an absolute local SQL Server backup folder."; return RedirectToAction(nameof(Index)); }
        try
        {
            await using var cn = await OpenAsync(ct);
            if (!await DatabaseBackupService.TryLockAsync(cn, ct))
            { TempData["DatabaseError"] = "A backup is running. Change the folder after it finishes."; return RedirectToAction(nameof(Index)); }
            await BackupService.SaveFolderAsync(cn, backupFolder, Actor, ct);
            log.LogInformation("Backup folder changed by {Actor}", Actor);
            TempData["DatabaseMessage"] = "Backup folder saved for manual and automatic backups. Test Backup Now to confirm SQL Server write access.";
        }
        catch (InvalidOperationException ex)
        { log.LogWarning(ex, "Backup folder change rejected for {Actor}", Actor); TempData["DatabaseError"] = "Enter a valid absolute local SQL Server backup folder. Check the SQL migration."; }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { log.LogError(ex, "Backup folder save failed for {Actor}", Actor); TempData["DatabaseError"] = "Unable to save backup folder. Check the SQL migration and permissions."; }
        return RedirectToAction(nameof(Index));
    }
    [HttpPost("SaveSchedule"), ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveSchedule(bool enabled, string frequency, string backupTime, int retentionDays, CancellationToken ct)
    {
        if (!Allowed()) return AccessDeniedResponse();
        if (!ModelState.IsValid || !DatabaseBackupService.ValidSettings(frequency, backupTime, retentionDays, out var time))
        { TempData["DatabaseError"] = "Select a valid frequency, backup time and retention period."; return RedirectToAction(nameof(Index)); }
        try
        {
            await using var cn = await OpenAsync(ct);
            if (!await DatabaseBackupService.TryLockAsync(cn, ct))
            { TempData["DatabaseError"] = "A backup is running. Save the settings again after it finishes."; return RedirectToAction(nameof(Index)); }
            await BackupService.SaveScheduleAsync(cn, enabled, frequency, time, retentionDays, Actor, ct);
            log.LogInformation("Database schedule updated by {Actor}", Actor);
            TempData["DatabaseMessage"] = "Automatic backup settings saved.";
        }
        catch (Exception ex)
        { log.LogError(ex,"Backup schedule save failed for {Actor}",Actor); TempData["DatabaseError"] = "Unable to save schedule. Check scheduler migration and SQL permissions."; }
        return RedirectToAction(nameof(Index));
    }
    [HttpGet("HistoryCsv")]
    public async Task<IActionResult> HistoryCsv(CancellationToken ct)
    {
        if (!Allowed()) return AccessDeniedResponse();
        try
        {
            await using var cn=await OpenAsync(ct);
            var rows=await HistoryAsync(cn,null,ct);
            var csv=new StringBuilder("Backup ID,File,Started (SQL server time),Finished (SQL server time),Stored bytes,Original bytes,Source,Status\r\n");
            foreach(var row in rows)
                csv.AppendLine(FormattableString.Invariant($"{row.Id},{row.FileName},{row.Started:yyyy-MM-dd HH:mm:ss},{row.Finished:yyyy-MM-dd HH:mm:ss},{row.Bytes},{row.OriginalBytes},{row.Source},{row.State}"));
            log.LogInformation("Backup history exported by {Actor}",Actor);
            return File(Encoding.UTF8.GetBytes(csv.ToString()),"text/csv","ORA_Backup_History.csv");
        }
        catch(Exception ex) { log.LogError(ex,"Backup history export failed for {Actor}",Actor); TempData["DatabaseError"]="Unable to export backup history."; return RedirectToAction(nameof(Index)); }
    }
    [HttpPost("VerifyPlan"), ValidateAntiForgeryToken]
    public async Task<IActionResult> VerifyPlan(int id, CancellationToken ct)
    {
        if (!Allowed()) return AccessDeniedResponse();
        try
        {
            await using var cn=await OpenAsync(ct);
            var row=(await HistoryAsync(cn,id,ct)).SingleOrDefault();
            if(row is null || row.State=="Pruned") return NotFound();
            var path="N'"+row.Path.Replace("'","''")+"'";
            var sql=$@"-- DBA: run on a trusted SQL Server with access to this backup file.
-- This does not restore or overwrite a database.
USE master;
RESTORE VERIFYONLY FROM DISK = {path} WITH FILE = {row.Position}, CHECKSUM;
RESTORE HEADERONLY FROM DISK = {path} WITH FILE = {row.Position};
-- VERIFYONLY does not replace a test restore and DBCC CHECKDB.
";
            log.LogInformation("Verification script requested by {Actor} for backup {Id}",Actor,id);
            return File(Encoding.UTF8.GetBytes(sql),"application/octet-stream",$"ORA_Verify_{id}.sql");
        }
        catch(Exception ex) { log.LogError(ex,"Verification script failed for {Actor}",Actor); TempData["DatabaseError"]="Unable to prepare verification script."; return RedirectToAction(nameof(Index)); }
    }
    [HttpPost("RestorePlan"), ValidateAntiForgeryToken]
    public async Task<IActionResult> RestorePlan(int id, CancellationToken ct)
    {
        if (!Allowed()) return AccessDeniedResponse();
        try
        {
            await using var cn=await OpenAsync(ct);
            var row=(await HistoryAsync(cn,id,ct)).SingleOrDefault();
            if(row is null || row.State=="Pruned") return NotFound();
            static string Literal(string s) => "N'"+s.Replace("'","''")+"'";
            var path=Literal(row.Path);
            var target="ORA_Recovery_"+DateTime.UtcNow.ToString("yyyyMMdd_HHmmss",System.Globalization.CultureInfo.InvariantCulture)+"_"+Guid.NewGuid().ToString("N")[..8];
            var sql=$@"-- DBA ONLY: Run on an approved isolated recovery SQL Server, not live PMS.
-- Copy the backup securely first and adjust its path if needed.
-- Backup history is not proof that a file still exists or is recoverable.
USE master;
RESTORE VERIFYONLY FROM DISK = {path} WITH FILE = {row.Position}, CHECKSUM;
RESTORE FILELISTONLY FROM DISK = {path} WITH FILE = {row.Position};
-- After successful checks, replace placeholders for EVERY logical file and use unique paths.
-- The following is intentionally commented out. Review before executing.
/*
IF DB_ID(N'{target}') IS NOT NULL THROW 50002, 'Recovery database already exists.', 1;
RESTORE DATABASE [{target}] FROM DISK = {path}
WITH FILE = {row.Position}, CHECKSUM, RECOVERY,
MOVE N'<logical data name>' TO N'<unique recovery data path>',
MOVE N'<logical log name>' TO N'<unique recovery log path>';
DBCC CHECKDB ([{target}]) WITH NO_INFOMSGS;
*/
-- Do not use WITH REPLACE, overwrite live files, or attach PMS workers to recovery.
-- Live cutover is a separate DBA-led maintenance operation.
";
            log.LogInformation("Recovery plan requested by {Actor} for backup {Id}",Actor,id);
            return File(Encoding.UTF8.GetBytes(sql),"application/octet-stream",target+".sql");
        }
        catch(Exception ex) { log.LogError(ex,"Recovery plan failed for {Actor}",Actor); TempData["DatabaseError"]="Unable to prepare recovery script."; return RedirectToAction(nameof(Index)); }
    }
}
