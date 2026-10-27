using System.Data;
using Microsoft.Data.SqlClient;

namespace Orapmshms.Services.Logging;

/// <summary>
/// Writes a single, legacy-compatible Reserv_log row for an actual reservation mutation.
/// Call BEFORE committing the business transaction, using its connection and transaction.
/// An audit write failure rolls back the mutation instead of leaving an unlogged change.
/// </summary>
public static class ReservationAuditWriter
{
    public static async Task WriteAsync(
        SqlConnection connection, SqlTransaction? transaction,
        string hotelId, string regId, string userId, string userName,
        string ipAddress, string action, string details, DateTime hotelNow,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(hotelId))
            throw new ArgumentException("Hotel ID is required for reservation audit.", nameof(hotelId));
        if (string.IsNullOrWhiteSpace(regId))
            throw new ArgumentException("Registration ID is required for reservation audit.", nameof(regId));

        const string sql = @"
INSERT INTO dbo.Reserv_log
    (reg_id, descp, user_id, username, hotel_id, status, ipAddress, systemname, createdat, date)
VALUES
    (@reg_id, @descp, @user_id, @username, @hotel_id, @status, @ipAddress, @systemname, @createdat, @date);";

        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@reg_id", SqlDbType.VarChar, 100).Value = regId.Trim();
        command.Parameters.Add("@descp", SqlDbType.VarChar, -1).Value = details ?? string.Empty;
        command.Parameters.Add("@user_id", SqlDbType.VarChar, 100).Value = userId ?? string.Empty;
        command.Parameters.Add("@username", SqlDbType.VarChar, 150).Value = userName ?? string.Empty;
        command.Parameters.Add("@hotel_id", SqlDbType.VarChar, 100).Value = hotelId.Trim();
        command.Parameters.Add("@status", SqlDbType.VarChar, 150).Value = action ?? string.Empty;
        command.Parameters.Add("@ipAddress", SqlDbType.VarChar, 100).Value = ipAddress ?? string.Empty;
        command.Parameters.Add("@systemname", SqlDbType.VarChar, 150).Value = Environment.MachineName ?? string.Empty;
        command.Parameters.Add("@createdat", SqlDbType.DateTime).Value = hotelNow;
        command.Parameters.Add("@date", SqlDbType.VarChar, 10).Value = hotelNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
