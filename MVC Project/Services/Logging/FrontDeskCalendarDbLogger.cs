using System.Data;
using Microsoft.Data.SqlClient;
using Orapmshms.Services;

namespace Orapmshms.Services.Logging;

/// <summary>
/// Database logger for the Front Desk Calendar.
/// Mirrors the legacy WebForms Log_helper tables without requiring Program.cs changes.
/// Logging is deliberately best-effort: it must never break the calendar workflow.
/// </summary>
public sealed class FrontDeskCalendarDbLogger
{
    private readonly string _connectionString;
    private readonly IHotelClock _hotelClock;
    private readonly IAppLogger _appLogger;

    public FrontDeskCalendarDbLogger(string connectionString, IHotelClock hotelClock, IAppLogger appLogger)
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        _hotelClock = hotelClock ?? throw new ArgumentNullException(nameof(hotelClock));
        _appLogger = appLogger ?? throw new ArgumentNullException(nameof(appLogger));
    }

    public async Task LogActionAsync(
        string module,
        string action,
        string hotelId,
        string userId,
        string description,
        string ip,
        CancellationToken ct = default)
    {
        try
        {
            await using var cn = new SqlConnection(_connectionString);
            await cn.OpenAsync(ct);
            await LogActionTransactionalAsync(cn, null, module, action, hotelId, userId, description, ip, ct);
        }
        catch (Exception ex)
        {
            _appLogger.Debug(ex, "Front Desk Calendar ActionLogTB insert skipped.");
        }
    }

    public async Task LogActionTransactionalAsync(
        SqlConnection cn,
        SqlTransaction? tx,
        string module,
        string action,
        string hotelId,
        string userId,
        string description,
        string ip,
        CancellationToken ct = default)
    {
        try
        {
            var hotelNow = SafeHotelNow(hotelId);
            await using var cmd = new SqlCommand(@"
IF OBJECT_ID('dbo.ActionLogTB','U') IS NOT NULL
BEGIN
    INSERT INTO dbo.ActionLogTB
    (module, action, userid, [date], [time], pc, ip, hotel_id, description)
    VALUES
    (@module, @action, @userid, @date, @time, @pc, @ip, @hotel_id, @description);
END;", cn, tx);

            cmd.Parameters.Add("@module", SqlDbType.VarChar, 200).Value = module ?? string.Empty;
            cmd.Parameters.Add("@action", SqlDbType.VarChar, 250).Value = action ?? string.Empty;
            cmd.Parameters.Add("@userid", SqlDbType.VarChar, 100).Value = userId ?? string.Empty;
            cmd.Parameters.Add("@date", SqlDbType.VarChar, 20).Value = hotelNow.ToString("MM-dd-yyyy");
            cmd.Parameters.Add("@time", SqlDbType.VarChar, 20).Value = hotelNow.ToString("HH:mm:ss");
            cmd.Parameters.Add("@pc", SqlDbType.VarChar, 150).Value = Environment.MachineName ?? string.Empty;
            cmd.Parameters.Add("@ip", SqlDbType.VarChar, 100).Value = ip ?? string.Empty;
            cmd.Parameters.Add("@hotel_id", SqlDbType.VarChar, 100).Value = hotelId ?? string.Empty;
            cmd.Parameters.Add("@description", SqlDbType.VarChar, -1).Value = description ?? string.Empty;
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (Exception ex)
        {
            _appLogger.Debug(ex, "Front Desk Calendar ActionLogTB insert skipped.");
        }
    }

    public async Task InsertReservationLogTransactionalAsync(
        SqlConnection cn,
        SqlTransaction? tx,
        string regId,
        string description,
        string userId,
        string userName,
        string hotelId,
        string status,
        string ip,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(regId)) return;
        try
        {
            var hotelNow = SafeHotelNow(hotelId);
            await using var cmd = new SqlCommand(@"
IF OBJECT_ID('dbo.Reserv_log','U') IS NOT NULL
BEGIN
    INSERT INTO dbo.Reserv_log
    (reg_id, descp, user_id, username, hotel_id, status, ipAddress, systemname, createdat, date)
    VALUES
    (@reg_id, @descp, @user_id, @username, @hotel_id, @status, @ipAddress, @systemname, @createdat, @date);
END;", cn, tx);

            cmd.Parameters.Add("@reg_id", SqlDbType.VarChar, 100).Value = regId.Trim();
            cmd.Parameters.Add("@descp", SqlDbType.VarChar, -1).Value = description ?? string.Empty;
            cmd.Parameters.Add("@user_id", SqlDbType.VarChar, 100).Value = userId ?? string.Empty;
            cmd.Parameters.Add("@username", SqlDbType.VarChar, 150).Value = userName ?? string.Empty;
            cmd.Parameters.Add("@hotel_id", SqlDbType.VarChar, 100).Value = hotelId ?? string.Empty;
            cmd.Parameters.Add("@status", SqlDbType.VarChar, 150).Value = status ?? string.Empty;
            cmd.Parameters.Add("@ipAddress", SqlDbType.VarChar, 100).Value = ip ?? string.Empty;
            cmd.Parameters.Add("@systemname", SqlDbType.VarChar, 150).Value = Environment.MachineName ?? string.Empty;
            cmd.Parameters.Add("@createdat", SqlDbType.DateTime).Value = hotelNow;
            cmd.Parameters.Add("@date", SqlDbType.VarChar, 10).Value = hotelNow.ToString("yyyy-MM-dd");
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (Exception ex)
        {
            _appLogger.Debug(ex, "Front Desk Calendar Reserv_log insert skipped for {RegId}.", regId);
        }
    }

    public async Task<string> LogExceptionAsync(
        Exception ex,
        string pageName,
        string actionName,
        string hotelId,
        string userId,
        string userName,
        string ip,
        CancellationToken ct = default)
    {
        var errorRef = Guid.NewGuid().ToString("N");
        try
        {
            var hotelNow = SafeHotelNow(hotelId);
            var description = BuildExceptionDescription(ex, actionName, errorRef);
            await using var cn = new SqlConnection(_connectionString);
            await cn.OpenAsync(ct);
            await using var cmd = new SqlCommand(@"
IF OBJECT_ID('dbo.ErrorLogTB','U') IS NOT NULL
BEGIN
    INSERT INTO dbo.ErrorLogTB
    (page_name, description, date, hotel_id, ip, system, username)
    VALUES
    (@page_name, @description, @date, @hotel_id, @ip, @system, @username);
END;", cn);

            cmd.Parameters.Add("@page_name", SqlDbType.NVarChar, 250).Value = Trim(pageName, 250);
            cmd.Parameters.Add("@description", SqlDbType.NVarChar, -1).Value = description;
            cmd.Parameters.Add("@date", SqlDbType.DateTime).Value = hotelNow;
            cmd.Parameters.Add("@hotel_id", SqlDbType.NVarChar, 100).Value = Trim(hotelId, 100);
            cmd.Parameters.Add("@ip", SqlDbType.NVarChar, 100).Value = Trim(ip, 100);
            cmd.Parameters.Add("@system", SqlDbType.NVarChar, 150).Value = Trim(Environment.MachineName, 150);
            cmd.Parameters.Add("@username", SqlDbType.NVarChar, 150).Value = Trim(!string.IsNullOrWhiteSpace(userName) ? userName : userId, 150);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (Exception logEx)
        {
            _appLogger.Debug(logEx, "Front Desk Calendar ErrorLogTB insert skipped. ErrorRef {ErrorRef}.", errorRef);
        }
        return errorRef;
    }

    private DateTime SafeHotelNow(string hotelId)
    {
        try { return _hotelClock.GetHotelNow(hotelId); }
        catch { return DateTime.Now; }
    }

    private static string BuildExceptionDescription(Exception? ex, string actionName, string errorRef)
    {
        if (ex == null) return $"ErrorRef: {errorRef}\nAction: {actionName}\nNo exception object.";
        var text = $"ErrorRef: {errorRef}\nAction: {actionName}\nMessage: {ex.Message}\nSource: {ex.Source}\nStackTrace: {ex.StackTrace}";
        if (ex.InnerException != null)
            text += $"\nInnerMessage: {ex.InnerException.Message}\nInnerStackTrace: {ex.InnerException.StackTrace}";
        return text;
    }

    private static string Trim(string? value, int maxLength)
    {
        var v = (value ?? string.Empty).Trim();
        return v.Length <= maxLength ? v : v.Substring(0, maxLength);
    }
}
