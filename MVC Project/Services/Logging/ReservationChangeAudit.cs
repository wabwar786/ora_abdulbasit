using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Data.SqlClient;

namespace Orapmshms.Services.Logging;

/// <summary>
/// Captures an explicit BEFORE image in the SAME transaction as a reservation change,
/// then calculates a field-level AFTER difference for the existing Reserv_log row.
/// This is NOT a change-tracking table or a SQL trigger. Captures are isolated by
/// SqlTransaction and hotel/reg_id so concurrently edited hotels cannot cross-contaminate.
/// Never stores or prints credit-card, token, password or passport numbers.
/// </summary>
public static class ReservationChangeAudit
{
    private sealed class TransactionImages
    {
        public readonly Dictionary<string, Snapshot> Items = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class Snapshot
    {
        public readonly Dictionary<string, Dictionary<string, string>> Rows =
            new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> TruncatedTables = new(StringComparer.OrdinalIgnoreCase);
    }

    private static readonly ConditionalWeakTable<SqlTransaction, TransactionImages> Images = new();

    // These are allowlisted operational fields. In particular, no payment credentials,
    // passport/CNIC numbers, tokens, freeform security notes or password hashes enter the log.
    private static readonly (string Table, string[] Fields)[] Sources =
    {
        ("GuestInformationLogTB", new[] {
            "GuestName", "LastName", "ArrivalDate", "DepartureDate", "ArrivalTime", "DepartureTime",
            "Address", "Country", "City", "Email", "PhoneNo", "NumberOfAdults", "NumberOfMinors",
            "Agency", "Status", "res_status", "vatno", "notes", "CNIC", "VisaPassportNo" }),
        ("NewReservationsTB", new[] {
            "GuestName", "LastName", "ArrivalDate", "DepartureDate", "Address", "Country", "City",
            "Email", "PhoneNo", "number_of_adult", "number_of_minor", "Agency", "Status", "res_status",
            "notes", "cnic", "visa" }),
        ("payments", new[] {
            "room_no", "descr", "Type", "ArrivalDate", "DepartureDate", "Rate", "Charge", "GST",
            "Bed", "Nights", "totalamount", "discount", "res_status", "guestname", "rateplan",
            "rateplanname", "room_adults", "room_children", "room_infants" }),
        ("PaymentsLogTB", new[] { "name", "paid_amount", "payment_method", "status" }),
        ("PaymentsUpdateTB", new[] { "grand_total", "room_security", "payable", "paid_amount", "remaining_amount", "status" }),
        ("LaundryTB", new[] { "category", "subcategory", "item", "quantity", "rate", "totalamount", "status" }),
        ("GuestNoteBook", new[] { "description" })
    };

    private static string Key(string hotelId, string regId) => (hotelId.Trim() + "\u001f" + regId.Trim());

    public static async Task CaptureBeforeAsync(SqlConnection connection, SqlTransaction transaction,
        string hotelId, string regId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(regId) || string.IsNullOrWhiteSpace(hotelId)) return;
        var images = Images.GetValue(transaction, _ => new TransactionImages());
        var key = Key(hotelId, regId);
        if (images.Items.ContainsKey(key)) return;
        // Capture failures must reject the mutation; otherwise it would commit without an accurate old value.
        images.Items.Add(key, await ReadAsync(connection, transaction, hotelId, regId, cancellationToken));
    }

    /// <summary>Returns details to APPEND to the normal reservation action entry, not an extra log row.</summary>
    public static async Task<string> ChangesSinceCaptureAsync(SqlConnection connection, SqlTransaction? transaction,
        string hotelId, string regId, CancellationToken cancellationToken = default)
    {
        if (transaction == null || string.IsNullOrWhiteSpace(regId) ||
            !Images.TryGetValue(transaction, out var images) ||
            !images.Items.TryGetValue(Key(hotelId, regId), out var before)) return string.Empty;

        var after = await ReadAsync(connection, transaction, hotelId, regId, cancellationToken);
        // Multiple action messages in one transaction should not repeat the same before/after details.
        images.Items[Key(hotelId, regId)] = after;
        var changes = new List<string>();
        var keys = before.Rows.Keys.Union(after.Rows.Keys, StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase);
        foreach (var rowKey in keys)
        {
            var oldExists = before.Rows.TryGetValue(rowKey, out var oldRow);
            var newExists = after.Rows.TryGetValue(rowKey, out var newRow);
            var table = rowKey.Split('#')[0];
            if (before.TruncatedTables.Contains(table) || after.TruncatedTables.Contains(table)) continue;
            var display = rowKey.Replace("#", " #", StringComparison.Ordinal);
            if (!oldExists)
            {
                changes.Add($"ADDED {display}: {Summary(newRow!)}");
                continue;
            }
            if (!newExists)
            {
                changes.Add($"DELETED {display}: previous {Summary(oldRow!)}");
                continue;
            }
            foreach (var field in oldRow!.Keys.Union(newRow!.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(x => x))
            {
                var prior = oldRow.GetValueOrDefault(field, "");
                var current = newRow.GetValueOrDefault(field, "");
                if (string.Equals(prior, current, StringComparison.Ordinal)) continue;
                var protectedField = IsPrivate(field);
                changes.Add(protectedField
                    ? $"{display}.{field}: [previous value withheld] -> [updated value withheld]"
                    : $"{display}.{field}: '{Display(prior)}' -> '{Display(current)}'");
            }
        }
        foreach (var name in before.TruncatedTables.Union(after.TruncatedTables))
            changes.Add($"{name}: more than 500 rows; detailed comparison not available");
        if (changes.Count == 0) return "";
        // Reserv_log.descp is varchar(max); cap message size for a readable Guest Log timeline.
        var result = new StringBuilder(" | CHANGES (OLD -> NEW): ");
        for (var i = 0; i < changes.Count; i++)
        {
            if (result.Length + changes[i].Length > 9500)
            {
                result.Append($"; ... {changes.Count - i} additional changes");
                break;
            }
            if (i > 0) result.Append("; ");
            result.Append(changes[i]);
        }
        return result.ToString();
    }

    private static async Task<Snapshot> ReadAsync(SqlConnection connection, SqlTransaction transaction,
        string hotelId, string regId, CancellationToken cancellationToken)
    {
        // One SQL round trip for all operational tables. Tables/columns are fixed code literals.
        // Each capture uses the transaction's own connection, so uncommitted changes are visible.
        var sql = string.Join("\n", Sources.Select(x =>
            $"SELECT TOP (501) * FROM dbo.[{x.Table}] WHERE hotel_id=@hotel AND reg_id=@reg;"));
        var snap = new Snapshot();
        await using var cmd = new SqlCommand(sql, connection, transaction);
        cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 100).Value = hotelId.Trim();
        cmd.Parameters.Add("@reg", SqlDbType.VarChar, 100).Value = regId.Trim();
        await using var rd = await cmd.ExecuteReaderAsync(cancellationToken);
        for (var t = 0; t < Sources.Length; t++)
        {
            var src = Sources[t];
            var ordinals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var c = 0; c < rd.FieldCount; c++) ordinals[rd.GetName(c)] = c;
            var rowNum = 0;
            while (await rd.ReadAsync(cancellationToken))
            {
                rowNum++;
                if (rowNum > 500) { snap.TruncatedTables.Add(src.Table); continue; }
                var id = ordinals.TryGetValue("ID", out var idOrdinal) && !rd.IsDBNull(idOrdinal)
                    ? Convert.ToString(rd.GetValue(idOrdinal), CultureInfo.InvariantCulture)
                    : rowNum.ToString(CultureInfo.InvariantCulture);
                var key = src.Table + "#" + id;
                var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var name in src.Fields)
                {
                    if (!ordinals.TryGetValue(name, out var index)) continue;
                    var value = rd.IsDBNull(index) ? string.Empty : rd.GetValue(index);
                    fields[name] = value switch
                    {
                        DateTime date => date.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                        decimal number => number.ToString("0.################", CultureInfo.InvariantCulture),
                        _ => Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty
                    };
                }
                snap.Rows[key] = fields;
            }
            if (t < Sources.Length - 1 && !await rd.NextResultAsync(cancellationToken))
                throw new InvalidOperationException("Incomplete reservation audit snapshot result set.");
        }
        return snap;
    }

    private static bool IsPrivate(string field) =>
        field.Equals("notes", StringComparison.OrdinalIgnoreCase) ||
        field.Equals("description", StringComparison.OrdinalIgnoreCase) ||
        field.Equals("CNIC", StringComparison.OrdinalIgnoreCase) ||
        field.Equals("visa", StringComparison.OrdinalIgnoreCase) ||
        field.Equals("VisaPassportNo", StringComparison.OrdinalIgnoreCase) ||
        field.Equals("Address", StringComparison.OrdinalIgnoreCase);

    private static string Display(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "(empty)";
        var normalized = value.Replace('\n', ' ').Replace('\r', ' ').Replace('|', '/').Trim();
        return normalized.Length > 140 ? normalized[..140] + "..." : normalized;
    }

    private static string Summary(Dictionary<string, string> fields) =>
        string.Join(", ", fields.Where(kv => !IsPrivate(kv.Key) && !string.IsNullOrEmpty(kv.Value))
            .Take(14).Select(kv => kv.Key + "=" + Display(kv.Value)));
}
