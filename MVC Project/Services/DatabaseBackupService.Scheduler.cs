using System.Data;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace Orapmshms.Services;

internal sealed partial class DatabaseBackupService
{
    // Internal hosted worker only; no public HTTP endpoint or API key.
    internal bool SchedulerConfigured => config.GetValue<bool>("DatabaseManagement:Enabled")
        && config.GetValue<bool>("DatabaseBackupScheduler:Enabled");
    internal bool RetentionConfigured => config.GetValue<bool>("DatabaseBackupScheduler:RetentionEnabled");

    // Retention uses the saved backup folder, never a second configured path.
    // Fail closed when the SQL host differs from the application host or the local directory is unsafe.
    internal async Task<bool> RetentionMatchesFolderAsync(SqlConnection cn, string folder, CancellationToken ct)
    {
        if (!RetentionConfigured || string.IsNullOrWhiteSpace(folder)) return false;
        try
        {
            using var cmd = new SqlCommand("SELECT CONVERT(nvarchar(128), SERVERPROPERTY('ComputerNamePhysicalNetBIOS'));", cn);
            var host = Convert.ToString(await cmd.ExecuteScalarAsync(ct));
            if (string.IsNullOrWhiteSpace(host) || !string.Equals(host, Environment.MachineName, StringComparison.OrdinalIgnoreCase)) return false;
            return IsSafeLocalDirectory(folder);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { log.LogWarning(ex, "Backup retention location could not be confirmed"); return false; }
    }

    private static bool IsSafeLocalDirectory(string folder)
    {
        if (!Path.IsPathFullyQualified(folder) || !Directory.Exists(folder)) return false;
        for (DirectoryInfo? dir = new DirectoryInfo(Path.GetFullPath(folder)); dir != null; dir = dir.Parent)
            if ((dir.Attributes & FileAttributes.ReparsePoint) != 0) return false;
        return true;
    }

    internal static bool ValidSettings(string? frequency, string? backupTime, int retentionDays, out TimeSpan time)
    {
        time = default;
        return (frequency is "6h" or "12h" or "Daily" or "Weekly")
            && new[] {7,15,30,60,90}.Contains(retentionDays)
            && TimeSpan.TryParseExact(backupTime, @"hh\:mm", CultureInfo.InvariantCulture, out time)
            && time >= TimeSpan.Zero && time < TimeSpan.FromDays(1);
    }

    // SQL Server local wall-clock schedule. Weekly = Monday; 6/12-hour cycles
    // repeat from the selected time. Persisted local slots prevent DST duplicates.
    internal static DateTime LatestSlot(DateTime now, string frequency, TimeSpan time)
    {
        if (frequency == "Weekly")
        {
            int days = ((int)now.DayOfWeek + 6) % 7;
            var slot = now.Date.AddDays(-days).Add(time);
            return slot <= now ? slot : slot.AddDays(-7);
        }
        int hours = frequency switch { "6h" => 6, "12h" => 12, "Daily" => 24,
            _ => throw new InvalidOperationException("Unknown backup frequency.") };
        var anchor = now.Date.Add(time);
        return anchor.AddHours(Math.Floor((now-anchor).TotalHours / hours) * hours);
    }
    internal static DateTime NextSlot(DateTime now, string frequency, TimeSpan time) =>
        LatestSlot(now, frequency, time).AddHours(frequency switch { "6h" => 6, "12h" => 12, "Daily" => 24, "Weekly" => 168,
            _ => throw new InvalidOperationException("Unknown backup frequency.") });

    private static async Task<DateTime> ServerClockAsync(SqlConnection cn, CancellationToken ct)
    {
        using var cmd = new SqlCommand("SELECT CONVERT(datetime2(0), GETDATE());", cn);
        return (DateTime)(await cmd.ExecuteScalarAsync(ct))!;
    }

    internal async Task<DatabaseBackupSchedule> ReadScheduleAsync(SqlConnection cn, CancellationToken ct)
    {
        using var cmd = new SqlCommand(@"SELECT s.Enabled,s.Frequency,s.BackupTime,s.RetentionDays,s.NextDueLocal,
 r.State,r.StartedUtc,r.FileName,r.Error
FROM dbo.DatabaseBackupSettingsTB s
OUTER APPLY (SELECT TOP (1) State,StartedUtc,FileName,Error FROM dbo.DatabaseBackupRunTB ORDER BY RunId DESC) r
WHERE s.Id=1;", cn);
        using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) throw new InvalidOperationException("Run the scheduler migration first.");
        return new(r.GetBoolean(0),r.GetString(1),r.GetTimeSpan(2),r.GetInt32(3),
            r.IsDBNull(4)?null:r.GetDateTime(4),r.IsDBNull(5)?null:r.GetString(5),
            r.IsDBNull(6)?null:r.GetDateTime(6),r.IsDBNull(7)?null:r.GetString(7),r.IsDBNull(8)?null:r.GetString(8));
    }

    // Caller owns the shared application lock. A retention-only change does not reset the next run.
    internal async Task SaveScheduleAsync(SqlConnection cn, bool enabled, string frequency, TimeSpan time,
        int retentionDays, string actor, CancellationToken ct)
    {
        var old = await ReadScheduleAsync(cn, ct);
        var now = await ServerClockAsync(cn, ct);
        DateTime? next = !enabled ? null : old.Enabled && old.Frequency == frequency && old.BackupTime == time && old.NextDueLocal.HasValue
            ? old.NextDueLocal : NextSlot(now, frequency, time);
        using var cmd = new SqlCommand(@"UPDATE dbo.DatabaseBackupSettingsTB SET Enabled=@enabled,Frequency=@frequency,
BackupTime=@time,RetentionDays=@days,NextDueLocal=@next,UpdatedUtc=SYSUTCDATETIME(),UpdatedBy=@actor WHERE Id=1;", cn);
        cmd.Parameters.Add("@enabled",SqlDbType.Bit).Value=enabled;
        cmd.Parameters.Add("@frequency",SqlDbType.VarChar,10).Value=frequency;
        cmd.Parameters.Add("@time",SqlDbType.Time).Value=time;
        cmd.Parameters.Add("@days",SqlDbType.Int).Value=retentionDays;
        cmd.Parameters.Add("@next",SqlDbType.DateTime2).Value=(object?)next??DBNull.Value;
        cmd.Parameters.Add("@actor",SqlDbType.NVarChar,128).Value=actor;
        if (await cmd.ExecuteNonQueryAsync(ct) != 1) throw new InvalidOperationException("Missing scheduler settings.");
    }

    internal sealed record RunResult(string Status, DateTime? NextDue = null, string? File = null, int Pruned = 0, bool RetentionWarning = false);

    internal async Task<RunResult> RunDueAsync(CancellationToken ct)
    {
        await using var cn = await OpenAsync(ct);
        // Fast path: avoid locking manual backups when automatic mode is disabled.
        var settings = await ReadScheduleAsync(cn,ct);
        var now = await ServerClockAsync(cn,ct);
        if (!settings.Enabled) return new("disabled");
        if (!await TryLockAsync(cn,ct)) return new("busy",settings.NextDueLocal);
        // Read again under the shared lock, including when a prior process was interrupted.
        settings = await ReadScheduleAsync(cn,ct);
        now = await ServerClockAsync(cn,ct);
        if (!settings.Enabled) return new("disabled");
        // An interrupted execution may have completed the backup. Never replay its slot automatically.
        // Acquiring the same SQL session lock proves no module backup is still holding it.
        using (var interrupted = new SqlCommand(@"UPDATE dbo.DatabaseBackupRunTB SET State='Unconfirmed',FinishedUtc=SYSUTCDATETIME(),
 Error=N'Previous execution interrupted. Check SQL backup history and verify the file before retrying.' WHERE State='Running';",cn))
            await interrupted.ExecuteNonQueryAsync(ct);
        if (!settings.NextDueLocal.HasValue) return new("configuration_required");
        if (settings.NextDueLocal.Value > now) return new("not_due",settings.NextDueLocal);
        var slot = LatestSlot(now,settings.Frequency,settings.BackupTime);
        var next = NextSlot(now,settings.Frequency,settings.BackupTime);
        if (slot < settings.NextDueLocal.Value) return new("not_due",settings.NextDueLocal);
        var name = NewFileName();
        var path = await BackupPathAsync(cn,name,ct);
        // Atomically claim one slot and move the schedule forward before performing backup.
        // A retry or app restart cannot launch a second backup for that slot.
        using (var claim = new SqlCommand(@"SET XACT_ABORT ON;
BEGIN TRANSACTION;
DECLARE @claimed bit=0;
IF NOT EXISTS(SELECT 1 FROM dbo.DatabaseBackupRunTB WHERE SlotLocal=@slot)
BEGIN
  INSERT dbo.DatabaseBackupRunTB(SlotLocal,FileName,BackupPath,State) VALUES(@slot,@name,@path,'Running');
  SET @claimed=1;
END;
UPDATE dbo.DatabaseBackupSettingsTB SET NextDueLocal=@next WHERE Id=1;
COMMIT;
SELECT @claimed;",cn))
        {
            claim.Parameters.Add("@slot",SqlDbType.DateTime2).Value=slot;
            claim.Parameters.Add("@next",SqlDbType.DateTime2).Value=next;
            claim.Parameters.Add("@name",SqlDbType.VarChar,80).Value=name;
            claim.Parameters.Add("@path",SqlDbType.NVarChar,260).Value=path;
            if (!(bool)(await claim.ExecuteScalarAsync(ct))!) return new("already_claimed",next);
        }
        bool backupCompleted=false;
        try
        {
            log.LogInformation("Automatic backup started for slot {Slot}: {File}",slot,name);
            await ExecuteBackupAsync(cn,path,name,true,ct);
            backupCompleted=true;
            await VerifyAsync(cn,path,ct);
            await SetStateAsync(cn,name,"Verified",null,ct);
            log.LogInformation("Automatic backup verified for slot {Slot}: {File}",slot,name);
        }
        catch (Exception ex)
        {
            log.LogError(ex,"Automatic backup not confirmed: {File}",name);
            string state = backupCompleted ? "VerificationFailed" : "Unconfirmed";
            string error = ex is SqlException sql ? "SQL error " + string.Join(", ",sql.Errors.Cast<SqlError>().Select(e=>e.Number).Distinct()) : "Backup or verification interrupted.";
            try { if (cn.State==ConnectionState.Open) await SetStateAsync(cn,name,state,error,CancellationToken.None); }
            catch (Exception updateError) { log.LogError(updateError,"Unable to persist backup failure for {File}",name); }
            return new(state=="VerificationFailed"?"verification_failed":"unconfirmed",next,name);
        }
        int pruned=0;
        bool retentionWarning=false;
        if (await RetentionMatchesFolderAsync(cn, path[..^name.Length], ct))
        {
            try { var result=await PruneAsync(cn,path,name,settings.RetentionDays,ct); pruned=result.Count; retentionWarning=result.Warning; }
            catch(Exception ex) { log.LogWarning(ex,"Backup verified but retention cleanup failed"); retentionWarning=true; }
        }
        return new("verified",next,name,pruned,retentionWarning);
    }

    private static async Task SetStateAsync(SqlConnection cn,string name,string state,string? error,CancellationToken ct)
    {
        using var cmd=new SqlCommand(@"UPDATE dbo.DatabaseBackupRunTB SET State=@state,FinishedUtc=SYSUTCDATETIME(),Error=@error WHERE FileName=@name;",cn);
        cmd.Parameters.Add("@state",SqlDbType.VarChar,24).Value=state;
        cmd.Parameters.Add("@error",SqlDbType.NVarChar,400).Value=(object?)error??DBNull.Value;
        cmd.Parameters.Add("@name",SqlDbType.VarChar,80).Value=name;
        if(await cmd.ExecuteNonQueryAsync(ct)!=1) throw new InvalidOperationException("Backup execution history missing.");
    }

    // Explicit opt-in. Same-host saved SQL folder only. Never scan or
    // delete arbitrary files, old manual backups, failed/unverified backups, or the new backup.
    private async Task<(int Count,bool Warning)> PruneAsync(SqlConnection cn,string currentPath,string currentName,int days,CancellationToken ct)
    {
        string root = currentPath[..^currentName.Length];
        if (!IsSafeLocalDirectory(root)) return (0,true);
        root=System.IO.Path.GetFullPath(root);
        var currentFile=System.IO.Path.Combine(root,currentName);
        if (!File.Exists(currentFile) || (File.GetAttributes(currentFile)&FileAttributes.ReparsePoint)!=0 || new FileInfo(currentFile).Length==0)
            return (0,true); // Mapping must contain the freshly verified file before any deletion.
        string sqlFolder=currentPath[..^currentName.Length];
        var old=new List<(string Name,string Path)>();
        using(var cmd=new SqlCommand(@"SELECT TOP(100) FileName,BackupPath FROM dbo.DatabaseBackupRunTB
WHERE State='Verified' AND FinishedUtc < DATEADD(day,-@days,SYSUTCDATETIME()) AND FileName<>@current ORDER BY FinishedUtc;",cn))
        {
            cmd.Parameters.Add("@days",SqlDbType.Int).Value=days;
            cmd.Parameters.Add("@current",SqlDbType.VarChar,80).Value=currentName;
            using var r=await cmd.ExecuteReaderAsync(ct);
            while(await r.ReadAsync(ct)) old.Add((r.GetString(0),r.GetString(1)));
        }
        int count=0; bool warning=false;
        foreach(var item in old)
        {
            ct.ThrowIfCancellationRequested();
            if(!Regex.IsMatch(item.Name,@"\AORA_\d{8}_\d{6}_[a-f0-9]{32}\.bak\z") || !string.Equals(item.Path,sqlFolder+item.Name,StringComparison.Ordinal)) continue;
            try
            {
                var file=System.IO.Path.Combine(root,item.Name);
                // Missing files are not recorded as deleted by this application.
                if (!File.Exists(file) || (File.GetAttributes(file)&FileAttributes.ReparsePoint)!=0) { warning=true; continue; }
                File.Delete(file);
                using var mark=new SqlCommand("UPDATE dbo.DatabaseBackupRunTB SET State='Pruned',DeletedUtc=SYSUTCDATETIME() WHERE FileName=@name AND State='Verified';",cn);
                mark.Parameters.Add("@name",SqlDbType.VarChar,80).Value=item.Name;
                await mark.ExecuteNonQueryAsync(ct);
                count++;
                log.LogInformation("Expired automatic backup removed: {File}",item.Name);
            }
            catch(Exception ex) when (ex is not OperationCanceledException) { warning=true; log.LogWarning(ex,"Retention could not remove {File}",item.Name); }
        }
        return (count,warning);
    }
}
