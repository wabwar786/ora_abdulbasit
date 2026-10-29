using System.Data;
using Microsoft.Data.SqlClient;

namespace Orapmshms.Services;

/// <summary>
/// Persistence for the existing HotelsSignUpTB.invoice_instructions field.
/// Never accepts a hotel id from the browser: the controller supplies the
/// validated selected hotel from the authenticated session.
/// </summary>
public sealed class InvoiceFooterSettingsService
{
    private readonly string _connectionString;

    public InvoiceFooterSettingsService(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("con")
            ?? throw new InvalidOperationException("ConnectionStrings:con is missing.");
    }

    public async Task<InvoiceFooterSettingsData?> GetAsync(
        string hotelId, CancellationToken cancellationToken = default)
    {
        const string sql = @"
SELECT TOP (1) [name], invoice_instructions
FROM dbo.HotelsSignUpTB
WHERE hotel_id = @HotelId;";

        await using var connection = new SqlConnection(_connectionString);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@HotelId", SqlDbType.VarChar, 50).Value = hotelId;
        await connection.OpenAsync(cancellationToken);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken);

        if (!await reader.ReadAsync(cancellationToken)) return null;
        var hotelName = reader.IsDBNull(0) ? string.Empty : Convert.ToString(reader.GetValue(0)) ?? string.Empty;
        var instructions = reader.IsDBNull(1) ? string.Empty : Convert.ToString(reader.GetValue(1)) ?? string.Empty;
        return new InvoiceFooterSettingsData(hotelName, instructions);
    }

    /// <returns>True only if the selected hotel row was updated.</returns>
    public async Task<bool> SaveAsync(
        string hotelId, string instructions, CancellationToken cancellationToken = default)
    {
        // No whitespace trim: formatting in invoice instructions is significant.
        var normalized = (instructions ?? string.Empty).Replace("\r\n", "\n").Replace("\r", "\n");
        if (normalized.Length > 5000)
            throw new ArgumentOutOfRangeException(nameof(instructions), "Instructions cannot exceed 5,000 characters.");

        const string sql = @"
UPDATE dbo.HotelsSignUpTB
SET invoice_instructions = @Instructions
WHERE hotel_id = @HotelId;";

        await using var connection = new SqlConnection(_connectionString);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@HotelId", SqlDbType.VarChar, 50).Value = hotelId;
        command.Parameters.Add("@Instructions", SqlDbType.NVarChar, -1).Value =
            string.IsNullOrWhiteSpace(normalized) ? DBNull.Value : normalized;
        await connection.OpenAsync(cancellationToken);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }
}

public sealed record InvoiceFooterSettingsData(string HotelName, string Instructions);
