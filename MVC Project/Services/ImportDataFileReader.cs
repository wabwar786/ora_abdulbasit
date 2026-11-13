using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualBasic.FileIO;
using Orapmshms.Models;

namespace Orapmshms.Services;

/// <summary>Dependency-free CSV / xlsx reader; no ACE/OLEDB provider and no public upload folder.</summary>
public static class ImportDataFileReader
{
    public const long MaxBytes = 5 * 1024 * 1024;
    public const int MaxRows = 5000;

    public static async Task<List<Dictionary<string, string>>> ReadAsync(IFormFile file, ImportDataKind kind, CancellationToken ct)
    {
        if (file is null || file.Length == 0 || file.Length > MaxBytes)
            throw new ArgumentException("Upload a CSV or XLSX file under 5 MB.");
        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (ext != ".csv" && ext != ".xlsx")
            throw new ArgumentException("Supported formats: .csv and .xlsx. Save old .xls files as .xlsx first.");
        await using var stream = new MemoryStream();
        await file.CopyToAsync(stream, ct);
        stream.Position = 0;
        var raw = ext == ".csv" ? ReadCsv(stream) : ReadXlsx(stream);
        if (raw.Count == 0) throw new ArgumentException("The uploaded file contains no data rows.");
        if (raw.Count > MaxRows) throw new ArgumentException("Maximum 5,000 data rows per import. Split the file.");
        var columns = ImportDataTemplates.Fields[kind];
        var result = new List<Dictionary<string, string>>(raw.Count);
        foreach (var source in raw)
        {
            var mapped = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var key in columns)
            {
                mapped[key] = string.Empty;
                var aliases = ImportDataTemplates.Aliases.TryGetValue(key, out var a) ? a : new[] { key };
                foreach (var alias in aliases)
                {
                    var normalized = Normalize(alias);
                    var hit = source.FirstOrDefault(kvp => Normalize(kvp.Key) == normalized);
                    if (hit.Key is not null) { mapped[key] = hit.Value?.Trim() ?? ""; break; }
                }
            }
            result.Add(mapped);
        }
        // Room exports supported:
        // - Existing ORA/HOP CSV: Occupany = cots.adults.children (e.g. 0.2.1)
        // - HOP Room Management: Occupancy (Max/Adult/Child) = max / adult / child (e.g. 3 / 2 / 1)
        // The latter has no cot count, so use zero cots and preserve the adult/child counts.
        // A single Rooms import creates missing categories and assigns their localcategoryid.
        if (kind is ImportDataKind.Categories or ImportDataKind.Rooms)
        {
            for (var i = 0; i < result.Count; i++)
            {
                var item = result[i];
                var occupancy = raw[i].FirstOrDefault(kvp =>
                    Normalize(kvp.Key) is "occupany" or "occupancy" or "occupancymaxadultchild").Value ?? "";
                occupancy = occupancy.Trim();
                if (occupancy.Contains('/'))
                {
                    // The source explicitly labels the three numbers Max/Adult/Child.
                    var counts = occupancy.Split('/');
                    if (counts.Length != 3 ||
                        !int.TryParse(counts[0].Trim(), out var maximum) ||
                        !int.TryParse(counts[1].Trim(), out var adults) ||
                        !int.TryParse(counts[2].Trim(), out var children) ||
                        maximum < 0 || adults < 0 || children < 0 ||
                        maximum < adults + children)
                        throw new ArgumentException($"Invalid Max/Adult/Child occupancy in room row {i + 2}: '{occupancy}'.");
                    item["Occupany"] = $"0.{adults}.{children}";
                    item["Cots"] = "0";
                    item["Adults"] = adults.ToString(CultureInfo.InvariantCulture);
                    item["Children"] = children.ToString(CultureInfo.InvariantCulture);
                }
                else
                {
                    // Keep the existing cots.adults.children CSV behaviour unchanged.
                    var parts = occupancy.Split('.');
                    item["Occupany"] = occupancy;
                    if (parts.Length >= 3)
                    {
                        if (string.IsNullOrWhiteSpace(item["Cots"])) item["Cots"] = parts[0].Trim();
                        if (string.IsNullOrWhiteSpace(item["Adults"])) item["Adults"] = parts[1].Trim();
                        if (string.IsNullOrWhiteSpace(item["Children"])) item["Children"] = parts[2].Trim();
                    }
                }
            }
        }
        // WebForms/HOP exports contain reservation detail in the Check In sheet,
        // with Arrival/Nights instead of a separate check-in timestamp.
        if (kind == ImportDataKind.CheckIns)
        {
            for (var i = 0; i < result.Count; i++)
            {
                var item = result[i];
                var original = raw[i];
                var guestCount = original.FirstOrDefault(kvp => Normalize(kvp.Key) == "aci").Value ?? "";
                var counts = guestCount.Split('/');
                if (counts.Length >= 2)
                {
                    if (string.IsNullOrWhiteSpace(item["Adults"])) item["Adults"] = counts[0].Trim();
                    if (string.IsNullOrWhiteSpace(item["Children"])) item["Children"] = counts[1].Trim();
                }
                if (string.IsNullOrWhiteSpace(item["CheckInAt"])) item["CheckInAt"] = item["Arrival"];
                // PDF / HOP In House listing has a date but not an actual check-in time.
                // Store the date at midnight; never infer an unprovided check-in time.
                var dateValid = DateTime.TryParseExact(item["Arrival"],
                    new[] { "dd/MM/yyyy", "d/M/yyyy", "yyyy-MM-dd", "MM-dd-yyyy" },
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var start);
                if (!dateValid && double.TryParse(item["Arrival"], NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var excelDate) && excelDate > 20000 && excelDate < 90000)
                {
                    start = DateTime.FromOADate(excelDate);
                    dateValid = true;
                }
                if (string.IsNullOrWhiteSpace(item["Departure"]) && dateValid &&
                    int.TryParse(item["Nights"], out var nights) && nights > 0 && nights <= 730)
                    item["Departure"] = start.AddDays(nights).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }
        }
        // A HOP Reservations export reports Status=Checked Out with Departure Date,
        // but has no actual checkout timestamp. Accept this historical date only when
        // HOP explicitly marks the item Checked Out; keep a flag so the audit and UI
        // never misrepresent a scheduled departure as a verified checkout time.
        if (kind == ImportDataKind.CheckOuts)
        {
            foreach (var item in result)
            {
                item["CheckOutDateInferred"] = "0";
                if (!string.IsNullOrWhiteSpace(item["CheckOutAt"])) continue;
                var status = item["Status"].Trim();
                if (!new[] { "Checked Out", "Check Out", "Checkout" }
                        .Any(x => string.Equals(x, status, StringComparison.OrdinalIgnoreCase))) continue;
                var rawDate = item["Departure"];
                // CheckOutAt holds only the date from HOP; no actual time is inferred.
                if (DateTime.TryParseExact(rawDate,
                        new[] { "dd/MM/yyyy", "d/M/yyyy", "yyyy-MM-dd", "MM-dd-yyyy" },
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out var depart))
                {
                    item["CheckOutAt"] = depart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    item["CheckOutDateInferred"] = "1";
                }
                else if (double.TryParse(rawDate, NumberStyles.Float, CultureInfo.InvariantCulture, out var excel) &&
                         excel > 20000 && excel < 90000)
                {
                    item["CheckOutAt"] = DateTime.FromOADate(excel).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    item["CheckOutDateInferred"] = "1";
                }
            }
        }
        if (kind == ImportDataKind.Cancellations)
        {
            // HOP cancellations have DateOfArrival + NumberOfNights, not a departure column.
            // Derive checkout only for historical reporting, not a new room reservation.
            foreach (var item in result)
            {
                if (!string.IsNullOrWhiteSpace(item["Departure"])) continue;
                if (DateTime.TryParseExact(item["Arrival"],
                        new[] { "dd/MM/yyyy", "d/M/yyyy", "yyyy-MM-dd", "MM-dd-yyyy" },
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out var arrival) &&
                    int.TryParse(item["Nights"], out var nights) && nights>0 && nights<=730)
                    item["Departure"] = arrival.AddDays(nights).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }
        }
        if (kind == ImportDataKind.NoShows)
        {
            // HOP's No Shows.csv has DateOfArrival but does not have a no-show
            // action timestamp. Keep a provenance marker; never invent a time.
            foreach (var item in result)
            {
                item["NoShowDateInferred"] = "0";
                if (!string.IsNullOrWhiteSpace(item["OccurredAt"])) continue;
                var raw1 = item["Arrival"];
                if (DateTime.TryParseExact(raw1,
                    new[] { "dd/MM/yyyy", "d/M/yyyy", "yyyy-MM-dd", "MM-dd-yyyy" },
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var arrival))
                {
                    item["OccurredAt"] = arrival.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    item["NoShowDateInferred"] = "1";
                    if (string.IsNullOrWhiteSpace(item["Departure"]) &&
                        int.TryParse(item["Nights"], out var nights) && nights>0 && nights<=730)
                        item["Departure"] = arrival.AddDays(nights).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                }
            }
        }
        if (kind == ImportDataKind.Payments)
        {
            // Legacy HOP payment files do not include unique payment IDs.
            // Identical rows are ranked to distinguish legitimate repeated payments;
            // stable row hashes prevent replay of a previously imported file.
            var seen = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var item in result)
            {
                // Never invent a payment method: it must be mapped to a real ORA method.
                if (!string.IsNullOrWhiteSpace(item["PaymentID"])) continue;
                var content = string.Join("|", new[] { "ReservationID", "PaymentAt", "Amount", "Method", "CardType", "Guest", "RoomNo", "Description" }
                    .Select(key => item[key].Trim().ToLowerInvariant()));
                var index = seen.GetValueOrDefault(content, 0) + 1;
                seen[content] = index;
                item["PaymentID"] = "legacy-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                    Encoding.UTF8.GetBytes(content)))[..22] + "-" + index.ToString(CultureInfo.InvariantCulture);
            }
        }
        return result;
    }

    private static string Normalize(string text) => new(text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static List<Dictionary<string, string>> ReadCsv(Stream stream)
    {
        using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        using var parser = new TextFieldParser(reader);
        parser.TextFieldType = FieldType.Delimited;
        parser.SetDelimiters(",");
        parser.HasFieldsEnclosedInQuotes = true;
        var rows = new List<Dictionary<string, string>>();
        if (parser.EndOfData) return rows;
        var headers = parser.ReadFields() ?? Array.Empty<string>();
        if (headers.Length == 0 || headers.Length > 100 || headers.Distinct(StringComparer.OrdinalIgnoreCase).Count() != headers.Length)
            throw new ArgumentException("Invalid or duplicate CSV column headers.");
        while (!parser.EndOfData)
        {
            if (rows.Count >= MaxRows + 1) break;
            string[] fields;
            try { fields = parser.ReadFields() ?? Array.Empty<string>(); }
            catch (MalformedLineException ex) { throw new ArgumentException("Invalid CSV quoting: " + ex.Message); }
            if (fields.All(string.IsNullOrWhiteSpace)) continue;
            if (fields.Length != headers.Length)
                throw new ArgumentException($"CSV row {rows.Count + 2}: expected {headers.Length} columns, found {fields.Length}.");
            rows.Add(headers.Select((h, i) => (h, i)).ToDictionary(x => x.h.Trim().Trim('\uFEFF'), x => fields[x.i], StringComparer.OrdinalIgnoreCase));
        }
        return rows;
    }

    private static List<Dictionary<string, string>> ReadXlsx(Stream stream)
    {
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        if (zip.Entries.Count > 200 || zip.Entries.Sum(e => e.Length) > 35 * 1024 * 1024)
            throw new ArgumentException("Workbook is too large to process safely.");
        var wb = LoadXml(zip.GetEntry("xl/workbook.xml") ?? throw new ArgumentException("Invalid XLSX workbook."));
        var rels = LoadXml(zip.GetEntry("xl/_rels/workbook.xml.rels") ?? throw new ArgumentException("Invalid XLSX relationships."));
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        XNamespace r = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        var first = wb.Descendants(ns + "sheet").FirstOrDefault()
            ?? throw new ArgumentException("Workbook contains no worksheet.");
        var relId = (string?)first.Attribute(r + "id") ?? "";
        var target = rels.Descendants().FirstOrDefault(x => (string?)x.Attribute("Id") == relId)?.Attribute("Target")?.Value;
        if (string.IsNullOrEmpty(target)) throw new ArgumentException("Worksheet path not found.");
        var path = target.TrimStart('/');
        if (!path.StartsWith("xl/", StringComparison.OrdinalIgnoreCase)) path = "xl/" + path;
        if (path.Contains("..", StringComparison.Ordinal)) throw new ArgumentException("Invalid worksheet path.");
        var sheet = LoadXml(zip.GetEntry(path) ?? throw new ArgumentException("Worksheet not found."));
        var strings = new List<string>();
        if (zip.GetEntry("xl/sharedStrings.xml") is { } shared)
        {
            var ss = LoadXml(shared);
            strings.AddRange(ss.Descendants(ns + "si").Select(si => string.Concat(si.Descendants(ns + "t").Select(t => t.Value))));
        }
        var lines = new List<Dictionary<int, string>>();
        foreach (var row in sheet.Descendants(ns + "sheetData").Elements(ns + "row"))
        {
            if (lines.Count > MaxRows + 1) break;
            var cells = new Dictionary<int, string>();
            foreach (var c in row.Elements(ns + "c"))
            {
                var index = ColumnIndex((string?)c.Attribute("r") ?? "");
                if (index < 0 || index >= 100) continue;
                var raw = c.Element(ns + "v")?.Value ?? c.Element(ns + "is")?.Value ?? "";
                if ((string?)c.Attribute("t") == "s" && int.TryParse(raw, out var idx))
                    raw = idx >= 0 && idx < strings.Count ? strings[idx] : "";
                cells[index] = raw;
            }
            if (cells.Count > 0) lines.Add(cells);
        }
        if (lines.Count < 2) return new();
        var headers = lines[0];
        if (headers.Values.Distinct(StringComparer.OrdinalIgnoreCase).Count() != headers.Count)
            throw new ArgumentException("Duplicate XLSX headers.");
        var result = new List<Dictionary<string, string>>();
        foreach (var line in lines.Skip(1))
        {
            if (line.Values.All(string.IsNullOrWhiteSpace)) continue;
            result.Add(headers.ToDictionary(h => h.Value.Trim(), h => line.GetValueOrDefault(h.Key, ""), StringComparer.OrdinalIgnoreCase));
        }
        return result;
    }

    private static int ColumnIndex(string cellReference)
    {
        var n = 0;
        foreach (var ch in cellReference)
        {
            if (!char.IsLetter(ch)) break;
            n = n * 26 + (char.ToUpperInvariant(ch) - 'A' + 1);
        }
        return n - 1;
    }

    private static XDocument LoadXml(ZipArchiveEntry entry)
    {
        using var input = entry.Open();
        using var xml = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 35 * 1024 * 1024 });
        return XDocument.Load(xml);
    }
}
