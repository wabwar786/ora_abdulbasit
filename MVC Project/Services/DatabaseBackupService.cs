using System.Data;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace Orapmshms.Services;

public sealed record DatabaseBackupSchedule(bool Enabled, string Frequency, TimeSpan BackupTime,
    int RetentionDays, DateTime? NextDueLocal, string? LastState, DateTime? LastRunUtc,
    string? LastFile, string? LastError);

internal sealed partial class DatabaseBackupService
{
    internal const string Prefix = "ORA Database Management ";
    private const string LockName = "ORA.DatabaseManagement.Backup";
    private readonly IConfiguration config;
    private readonly ILogger log;
    internal DatabaseBackupService(IConfiguration config, ILogger log)
    { this.config = config; this.log = log; }

    internal async Task<SqlConnection> OpenAsync(CancellationToken ct)
    {
        var cs = config.GetConnectionString("DatabaseBackup");
        var live = config.GetConnectionString("con");
        // Prefer the dedicated backup login. The existing PMS connection is an explicit opt-in only.
        if (string.IsNullOrWhiteSpace(cs) && config.GetValue<bool>("DatabaseManagement:UseExistingConnection")) cs = live;
        if (string.IsNullOrWhiteSpace(cs) || string.IsNullOrWhiteSpace(live)) throw new InvalidOperationException("Configure the DatabaseBackup connection, or explicitly enable UseExistingConnection.");
        var backup = new SqlConnectionStringBuilder(cs);
        var pms = new SqlConnectionStringBuilder(live);
        if (string.IsNullOrWhiteSpace(pms.InitialCatalog) || !string.Equals(backup.InitialCatalog, pms.InitialCatalog, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(backup.DataSource, pms.DataSource, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Backup connection must target the same server and database as PMS.");
        backup.Pooling = false;
        backup.ConnectRetryCount = 0; // Never reconnect silently after acquiring a session lock.
        var cn = new SqlConnection(backup.ConnectionString);
        try
        {
            await cn.OpenAsync(ct);
            if (!string.Equals(cn.Database, pms.InitialCatalog, StringComparison.OrdinalIgnoreCase)
                || new[]{"master","model","msdb","tempdb"}.Contains(cn.Database, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException("Unexpected database target.");
            return cn;
        }
        catch { await cn.DisposeAsync(); throw; }
    }
    // SuperUser selects a private directory on the SQL Server host. OS permissions remain the security boundary.
    // Reject UNC/device paths, traversal, relative paths and alternate data streams.
    internal static string NormalizeFolder(string? raw)
    {
        var folder = raw?.Trim() ?? "";
        bool unix = folder.StartsWith('/') && !folder.StartsWith("//", StringComparison.Ordinal);
        bool windows = folder.Length >= 3 && char.IsAsciiLetter(folder[0]) && folder[1] == ':' && folder[2] == '\\';
        if (!unix && !windows) throw new InvalidOperationException("Enter an absolute local SQL Server folder (no network or device paths).");
        char separator = unix ? '/' : '\\';
        var body = folder[(unix ? 1 : 3)..].TrimEnd(separator);
        var parts = body.Split(separator);
        if (parts.Any(x => string.IsNullOrWhiteSpace(x) || x == "." || x == ".." || x != x.Trim() || x.EndsWith('.') ||
            x.Any(c => char.IsControl(c) || "<>:\"|?*".Contains(c) || c == (unix ? '\\' : '/'))))
            throw new InvalidOperationException("Enter a private backup directory, not a drive root or traversal path.");
        var result = folder[..(unix ? 1 : 3)] + string.Join(separator, parts) + separator;
        if (result.Length + NewFileName().Length > 259) throw new InvalidOperationException("Backup folder path is too long.");
        return result;
    }
    // The saved database value is the only backup destination. Never fall back to appsettings.
    internal async Task<string> FolderAsync(SqlConnection cn, CancellationToken ct)
    {
        using var cmd = new SqlCommand("SELECT BackupFolder FROM dbo.DatabaseBackupSettingsTB WHERE Id=1;", cn);
        var saved = (await cmd.ExecuteScalarAsync(ct)) as string;
        if (string.IsNullOrWhiteSpace(saved))
            throw new InvalidOperationException("Save a backup folder in Database Management first.");
        return NormalizeFolder(saved);
    }
    // Caller holds the shared backup lock; access is restricted to SuperUser by the controller.
    internal async Task SaveFolderAsync(SqlConnection cn, string? input, string actor, CancellationToken ct)
    {
        var folder = NormalizeFolder(input);
        using var cmd = new SqlCommand(@"UPDATE dbo.DatabaseBackupSettingsTB SET BackupFolder=@folder,
UpdatedUtc=SYSUTCDATETIME(),UpdatedBy=@actor WHERE Id=1;", cn);
        cmd.Parameters.Add("@folder", SqlDbType.NVarChar, 260).Value = folder;
        cmd.Parameters.Add("@actor", SqlDbType.NVarChar,128).Value = actor;
        if (await cmd.ExecuteNonQueryAsync(ct) != 1) throw new InvalidOperationException("Backup settings row is missing.");
    }

    internal static async Task<bool> CanBackupAsync(SqlConnection cn, CancellationToken ct)
    {
        using var cmd = new SqlCommand("SELECT HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'BACKUP DATABASE');", cn);
        return await cmd.ExecuteScalarAsync(ct) is int permission && permission == 1;
    }

    internal static string NewFileName() => "ORA_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture)
        + "_" + Guid.NewGuid().ToString("N") + ".bak";

    // Same SQL session, same database and same lock for manual, automatic and settings writes.
    internal static async Task<bool> TryLockAsync(SqlConnection cn, CancellationToken ct)
    {
        using var cmd = new SqlCommand(@"DECLARE @result int;
EXEC @result=sys.sp_getapplock @Resource=@resource, @LockMode='Exclusive',
    @LockOwner='Session', @LockTimeout=0, @DbPrincipal='public'; SELECT @result;", cn);
        cmd.Parameters.Add("@resource",SqlDbType.NVarChar,255).Value=LockName;
        var result = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
        if (result >= 0) return true;
        if (result == -1) return false;
        throw new InvalidOperationException("Unable to acquire database backup lock: " + result);
    }

    // Connections are deliberately not pooled; disposal releases the session lock even on error.
    internal async Task<string> BackupPathAsync(SqlConnection cn, string name, CancellationToken ct)
    {
        if (!Regex.IsMatch(name, @"\AORA_\d{8}_\d{6}_[a-f0-9]{32}\.bak\z"))
            throw new InvalidOperationException("Unexpected backup filename.");
        if (!await CanBackupAsync(cn, ct)) throw new InvalidOperationException("BACKUP DATABASE permission required.");
        var path = await FolderAsync(cn, ct) + name;
        if (path.Length > 259) throw new InvalidOperationException("Backup path too long.");
        return path;
    }

    // Caller owns the session application lock. Never start another SQL connection for the backup.
    internal async Task ExecuteBackupAsync(SqlConnection cn, string path, string name, bool automatic, CancellationToken ct)
    {
        var sql = "BACKUP DATABASE @db TO DISK=@path WITH COPY_ONLY, CHECKSUM, STATS=10, NAME=@label;";
        if (config.GetValue<bool>("DatabaseManagement:Compression"))
            sql = sql.Replace("CHECKSUM,", "CHECKSUM, COMPRESSION,", StringComparison.Ordinal);
        using var cmd = new SqlCommand(sql, cn) { CommandTimeout = 0 };
        cmd.Parameters.Add("@db",SqlDbType.NVarChar,128).Value=cn.Database;
        cmd.Parameters.Add("@path",SqlDbType.NVarChar,260).Value=path;
        cmd.Parameters.Add("@label",SqlDbType.NVarChar,128).Value=Prefix+(automatic ? "Automatic " : "")+name;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    internal static async Task VerifyAsync(SqlConnection cn, string path, CancellationToken ct)
    {
        using var cmd = new SqlCommand("RESTORE VERIFYONLY FROM DISK=@path WITH FILE=1, CHECKSUM;", cn) { CommandTimeout=0 };
        cmd.Parameters.Add("@path",SqlDbType.NVarChar,260).Value=path;
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
