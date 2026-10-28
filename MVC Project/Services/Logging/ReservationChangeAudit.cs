using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
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

    /// <summary>
    /// Returns only meaningful, human-readable old/new changes for the Guest History.
    /// Internal SQL table names, row IDs, ledger recalculations and whole-row snapshots
    /// are never placed into the visible Reserv_log description.
    /// </summary>
    public static async Task<string> ChangesSinceCaptureAsync(SqlConnection connection, SqlTransaction? transaction,
        string hotelId, string regId, CancellationToken cancellationToken = default, string? action = null)
    {
        if (transaction == null || string.IsNullOrWhiteSpace(regId) ||
            !Images.TryGetValue(transaction, out var images) ||
            !images.Items.TryGetValue(Key(hotelId, regId), out var before)) return string.Empty;

        var after = await ReadAsync(connection, transaction, hotelId, regId, cancellationToken);
        // When multiple actions share a transaction, do not log the same mutation twice.
        images.Items[Key(hotelId, regId)] = after;

        var preferredFields = PreferredFields(action);
        var changed = new List<(int Rank, string Text)>();
        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var keys = before.Rows.Keys.Union(after.Rows.Keys, StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase);

        foreach (var key in keys)
        {
            var table = key.Split('#')[0];
            if (before.TruncatedTables.Contains(table) || after.TruncatedTables.Contains(table)) continue;
            if (!before.Rows.TryGetValue(key, out var oldRow) ||
                !after.Rows.TryGetValue(key, out var newRow))
            {
                // ADD ROOM, DELETE ROOM, PAYMENT and LAUNDRY already describe the
                // addition/removal in their action details. Never dump the SQL row.
                continue;
            }

            foreach (var field in preferredFields)
            {
                if (!oldRow.TryGetValue(field, out var prior) ||
                    !newRow.TryGetValue(field, out var current) ||
                    string.Equals(prior, current, StringComparison.Ordinal)) continue;
                if (!AllowedSource(table, field)) continue;
                if (IsPrivate(field))
                {
                    // Record that sensitive notes/address were updated, not their contents.
                    var redacted = FieldLabel(field) + " updated";
                    if (unique.Add(redacted)) changed.Add((preferredFields.Length + 1, redacted));
                    continue;
                }
                var line = $"{FieldLabel(field)}: {FriendlyValue(field, prior)} → {FriendlyValue(field, current)}";
                if (unique.Add(line))
                    changed.Add((Array.IndexOf(preferredFields, field), line));
            }
        }
        if (changed.Count == 0) return string.Empty;

        // A brief operational log rather than a complete database change-data dump.
        // The original business transaction remains the source of truth for values.
        var ordered = changed.OrderBy(x => x.Rank).ThenBy(x => x.Text, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Text).ToList();
        var selected = new List<string>();
        var length = 0;
        foreach (var line in ordered)
        {
            if (selected.Count >= 4 || length + line.Length > 270) break;
            selected.Add(line);
            length += line.Length + 2;
        }
        var remainder = ordered.Count - selected.Count;
        if (remainder > 0) selected.Add($"{remainder} more field(s) updated");
        return " | " + string.Join("; ", selected);
    }

    private static readonly string[] GuestFields =
    {
        "GuestName", "LastName", "Email", "PhoneNo", "ArrivalDate", "DepartureDate",
        "NumberOfAdults", "NumberOfMinors", "number_of_adult", "number_of_minor",
        "Agency", "Status", "Country", "City", "notes", "Address"
    };

    private static readonly string[] RoomFields =
    {
        "room_no", "Type", "ArrivalDate", "DepartureDate", "Nights", "rateplanname",
        "rateplan", "room_adults", "room_children", "room_infants"
    };

    private static string[] PreferredFields(string? action)
    {
        var a = (action ?? string.Empty).ToUpperInvariant();
        if (a.Contains("GUEST NAME")) return new[] { "GuestName", "LastName", "guestname" };
        if (a.Contains("UPDATE GUEST")) return GuestFields;
        if (a.Contains("ROOM CHANGE") || a.Contains("ROOM MOVE") || a.Contains("ROOM SWAP") ||
            a.Contains("CALENDAR MOVE")) return Array.Empty<string>(); // Room before/after is in action details.
        if (a.Contains("RATE")) return Array.Empty<string>(); // Rate action already records old/new nightly price.
        if (a.Contains("TAX")) return new[] { "GST", "Bed", "totalamount" };
        if (a.Contains("EXTEND") || a.Contains("SHRINK") || a.Contains("DATE"))
            return new[] { "ArrivalDate", "DepartureDate", "Nights" };
        if (a.Contains("CHECK IN") || a.Contains("CHECK OUT") || a.Contains("STATUS"))
            return new[] { "res_status" };
        if (a.Contains("ADD ROOM") || a.Contains("DELETE ROOM") ||
            a.Contains("ADD SERVICE") || a.Contains("DELETE SERVICE") ||
            a.Contains("PAYMENT") || a.Contains("REFUND") || a.Contains("LAUNDRY") ||
            a.Contains("SECURITY")) return Array.Empty<string>();
        return GuestFields.Concat(RoomFields).Concat(new[] { "res_status", "Rate", "discount" })
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static bool AllowedSource(string table, string field)
    {
        if (table.Equals("PaymentsUpdateTB", StringComparison.OrdinalIgnoreCase) ||
            table.Equals("PaymentsLogTB", StringComparison.OrdinalIgnoreCase) ||
            table.Equals("LaundryTB", StringComparison.OrdinalIgnoreCase) ||
            table.Equals("GuestNoteBook", StringComparison.OrdinalIgnoreCase)) return false;
        var payment = table.Equals("payments", StringComparison.OrdinalIgnoreCase);
        return payment
            ? RoomFields.Contains(field, StringComparer.OrdinalIgnoreCase) ||
              new[] { "Rate", "Charge", "discount", "GST", "Bed", "totalamount", "res_status", "guestname" }
                  .Contains(field, StringComparer.OrdinalIgnoreCase)
            : GuestFields.Contains(field, StringComparer.OrdinalIgnoreCase) ||
              field.Equals("res_status", StringComparison.OrdinalIgnoreCase);
    }

    private static string FieldLabel(string field) => field.ToLowerInvariant() switch
    {
        "guestname" => "Guest name", "lastname" => "Last name", "email" => "Email",
        "phoneno" => "Phone", "arrivaldate" => "Arrival", "departuredate" => "Departure",
        "numberofadults" or "number_of_adult" or "room_adults" => "Adults",
        "numberofminors" or "number_of_minor" or "room_children" => "Children",
        "room_infants" => "Infants", "agency" => "Company", "status" => "Booking source",
        "res_status" => "Reservation status", "country" => "Country", "city" => "City",
        "address" => "Address", "notes" => "Guest notes", "room_no" => "Room",
        "type" => "Room type", "nights" => "Nights", "rateplan" or "rateplanname" => "Rate plan",
        "rate" => "Rate", "charge" => "Charge", "discount" => "Discount", "gst" => "GST",
        "bed" => "Bed tax", "totalamount" => "Total", _ => field
    };

    private static string FriendlyValue(string field, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "Not set";
        if ((field.Contains("Date", StringComparison.OrdinalIgnoreCase)) &&
            DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return date.ToString("dd MMM yyyy", CultureInfo.InvariantCulture);
        var clean = value.Replace('\r', ' ').Replace('\n', ' ').Replace('|', '/').Trim();
        return clean.Length <= 55 ? clean : clean[..52] + "...";
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

}
