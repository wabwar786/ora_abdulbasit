#nullable enable

using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Orapmshms.Models;

namespace Orapmshms.Services;

/// <summary>
/// InvoiceRecieving, ported from the Web Forms code-behind.
///
/// Every rule below - what counts as a room-rent line, when the VAT column is
/// shown, how the tax-rate label is chosen, how descriptions are cleaned, how
/// money is formatted - is the page's own logic, moved across unchanged. The
/// three SQL statements are byte-for-byte the ones the page ran.
///
/// Performance notes:
///   - three queries per invoice, the same three the page ran, now issued
///     asynchronously and in parallel where they do not depend on each other;
///   - one pass over the payment lines, with the deduction lookup pre-built
///     into a dictionary exactly as before;
///   - line values are read from typed properties instead of per-row
///     reflection, which is the one thing here that is faster than the original.
/// </summary>
public sealed class InvoiceRecievingService : IInvoiceRecievingService
{
    private readonly string _connectionString;
    private readonly IInvoiceRecievingHelper _helper;
    private readonly ILogger<InvoiceRecievingService> _logger;


    private const int CommandTimeoutSeconds = 60;

    public InvoiceRecievingService(
        IConfiguration configuration,
        ILogger<InvoiceRecievingService> logger,
        IInvoiceRecievingHelper? helper = null,
        IHttpContextAccessor? httpContextAccessor = null)
    {
        // Same connection string name the page used: ConnectionStrings["con"].
        _connectionString =
            configuration.GetConnectionString("con")
            ?? configuration["ConnectionStrings:con"]
            ?? "";

        // The project's own InvoiceHelper when it is reachable; otherwise the
        // port of it in this module, which runs the same SQL.
        _helper = helper
                  ?? (InvoiceRecievingHelperBridge.IsAvailable
                          ? new InvoiceRecievingHelperBridge()
                          : new InvoiceRecievingHelper(configuration, httpContextAccessor));
        _logger = logger;

    }


    // ------------------------------------------------------------------ SQL

    private const string SqlShowVatOnInvoice = @"
SELECT TOP (1)
    ISNULL(show_vat_on_invoice, 0)
FROM dbo.HotelsSignUpTB
WHERE hotel_id = @hotel_id;";

    private const string SqlHotelNtn = @"
SELECT TOP (1)
    LTRIM(RTRIM(ISNULL(ntn, ''))) AS ntn
FROM dbo.HotelsSignUpTB
WHERE hotel_id = @hotel_id;";

    private const string SqlDeductionInfo = @"
SELECT
    LTRIM(RTRIM(ISNULL(descr, ''))) AS descr,
    LTRIM(RTRIM(ISNULL(deductioninfo, ''))) AS deductioninfo,
    ISNULL(totalamount, 0) AS totalamount,
    ISNULL(ID, 0) AS ID
FROM dbo.payments
WHERE hotel_id = @hotel_id
  AND reg_id = @reg_id
  AND LTRIM(RTRIM(ISNULL(descr, ''))) <> 'Room Rent'
ORDER BY ID;
";

    private const string SqlArrivalDeparture = @"
SELECT
    MIN(TRY_CONVERT(date, ArrivalDate, 101)) AS ArrivalDate,
    MAX(TRY_CONVERT(date, DepartureDate, 101)) AS DepartureDate
FROM dbo.payments
WHERE hotel_id = @hotel_id
  AND reg_id = @reg_id
  AND ArrivalDate IS NOT NULL
  AND DepartureDate IS NOT NULL
  AND LTRIM(RTRIM(ISNULL(ArrivalDate, ''))) <> ''
  AND LTRIM(RTRIM(ISNULL(DepartureDate, ''))) <> '';
";

    // --------------------------------------------------------------- public

    public async Task<InvoiceRecievingPageViewModel> GetInvoiceAsync(
        string? hotelId,
        string? regId,
        string? src,
        CancellationToken ct = default)
    {
        var model = new InvoiceRecievingPageViewModel();

        // The page returned without binding anything when either was missing.
        if (string.IsNullOrWhiteSpace(hotelId) || string.IsNullOrWhiteSpace(regId))
            return model;

        hotelId = hotelId.Trim();
        regId = regId.Trim();

        InvoiceRecievingHotel hotel = _helper.GetHotel(hotelId);
        InvoiceRecievingGuest guest = _helper.GetGuest(hotelId, regId);
        IReadOnlyList<InvoiceRecievingPaymentLine>? lines = _helper.GetPaymentLines(hotelId, regId);
        InvoiceRecievingPaymentSummary? summary = _helper.GetLatestPaymentSummary(hotelId, regId);

        // These four touch different tables and nothing shared; run them together.
        Task<bool> allowVatTask = IsVatAllowedOnInvoiceAsync(hotelId, ct);
        Task<string> ntnTask = GetHotelNtnAsync(hotelId, ct);
        Task<Dictionary<string, Queue<string>>> deductionsTask = BuildDeductionInfoQueuesAsync(hotelId, regId, ct);
        Task<(DateTime? Arrival, DateTime? Departure)> stayTask = GetPaymentArrivalDepartureAsync(hotelId, regId, ct);

        await Task.WhenAll(allowVatTask, ntnTask, deductionsTask, stayTask).ConfigureAwait(false);

        bool allowVatOnInvoice = allowVatTask.Result;
        Dictionary<string, Queue<string>> deductionQueues = deductionsTask.Result;
        (DateTime? arrival, DateTime? departure) = stayTask.Result;

        model.HasData = true;
        model.Hotel = hotel;
        model.GuestName = guest.FullName;
        model.HotelNtn = ntnTask.Result;

        model.InvoiceNo = "INV-" + regId;
        model.InvoiceDate = DateTime.Today.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
        model.ReservationNo = regId;
        model.ArrivalDate = arrival.HasValue ? arrival.Value.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) : "";
        model.DepartureDate = departure.HasValue ? departure.Value.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) : "";

        string currency = string.IsNullOrWhiteSpace(hotel.CurrencySign) ? "£" : hotel.CurrencySign;

        decimal net = 0m;
        decimal vat = 0m;
        decimal gross = 0m;

        var rows = new List<InvoiceRecievingRow>();
        var explicitTaxRates = new List<decimal>();

        if (lines is not null)
        {
            model.ShowVatColumn = allowVatOnInvoice &&
                                  lines.Any(x => IsRoomRentLine(x.Descr) && x.Vat > 0m);

            string lastDateGroup = "";
            bool extrasHeaderAdded = false;
            int serial = 0;

            foreach (InvoiceRecievingPaymentLine l in lines
                .OrderBy(x => IsRoomRentLine(x.Descr) ? 0 : 1)
                .ThenBy(x => x.ArrivalDate)
                .ThenBy(x => x.DepartureDate)
                .ThenBy(x => x.Date))
            {
                bool isRoomRent = IsRoomRentLine(l.Descr);

                decimal lineGross = l.Gross;
                decimal lineVat = 0m;
                decimal lineNet = lineGross;

                if (isRoomRent && allowVatOnInvoice)
                {
                    lineVat = l.Vat;

                    if (lineVat > 0m)
                        lineNet = lineGross - lineVat;

                    if (lineVat > 0m)
                        AddExplicitTaxRate(explicitTaxRates, l);
                }

                gross += lineGross;
                vat += lineVat;
                net += lineNet;

                if (isRoomRent)
                {
                    string currentDateGroup = "";

                    if (l.ArrivalDate.HasValue && l.DepartureDate.HasValue)
                    {
                        currentDateGroup =
                            l.ArrivalDate.Value.ToString("dd MMM, yyyy", CultureInfo.InvariantCulture) +
                            " → " +
                            l.DepartureDate.Value.ToString("dd MMM, yyyy", CultureInfo.InvariantCulture);
                    }

                    if (currentDateGroup != "" && currentDateGroup != lastDateGroup)
                    {
                        rows.Add(new InvoiceRecievingRow { IsGroup = true, DateGroup = currentDateGroup });
                        lastDateGroup = currentDateGroup;
                    }
                }
                else
                {
                    if (!extrasHeaderAdded)
                    {
                        rows.Add(new InvoiceRecievingRow { IsGroup = true, DateGroup = "Extras" });
                        extrasHeaderAdded = true;
                    }
                }

                string descr = BuildInvoiceDescription(l, deductionQueues);
                (string main, string sub) = SplitDescription(descr, isRoomRent);

                rows.Add(new InvoiceRecievingRow
                {
                    IsGroup = false,
                    Serial = ++serial,
                    LineDate = l.Date,
                    Descr = descr,
                    DescrMain = main,
                    DescrSub = sub,
                    RateFmt = FormatMoney(lineNet, currency),
                    VatFmt = isRoomRent && lineVat > 0m ? FormatMoney(lineVat, currency) : "",
                    GrossFmt = FormatMoney(lineGross, currency)
                });
            }
        }
        else
        {
            model.ShowVatColumn = false;
        }

        string taxRateLabel = GetDisplayTaxRateLabel(lines, explicitTaxRates);
        model.VatColumnHeaderText = string.IsNullOrWhiteSpace(taxRateLabel)
            ? "VAT"
            : "VAT (" + taxRateLabel + ")";
        model.VatTotalLabelText = "Total VAT";

        model.Rows = rows;

        model.NetFmt = FormatMoney(net, currency);
        model.VatFmt = FormatMoney(vat, currency);
        model.GrossFmt = FormatMoney(gross, currency);
        model.DueFmt = summary is not null
            ? FormatMoney(summary.Remaining, currency)
            : FormatMoney(gross, currency);

        ApplyQr(model, hotelId, regId, src, summary, arrival, departure);

        return model;
    }

    // ------------------------------------------------------------------ QR

    private void ApplyQr(
        InvoiceRecievingPageViewModel model,
        string hotelId,
        string regId,
        string? src,
        InvoiceRecievingPaymentSummary? summary,
        DateTime? arrival,
        DateTime? departure)
    {
        // The page hid the QR whenever nothing was left to pay, and swallowed
        // any failure rather than breaking the invoice. Both kept.
        try
        {
            if (summary is null || summary.Remaining <= 0m)
            {
                model.ShowQr = false;
                return;
            }

            string fullName = string.IsNullOrWhiteSpace(model.GuestName) ? "Guest" : model.GuestName.Trim();

            string arrivalText = arrival.HasValue ? arrival.Value.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) : "";
            string departureText = departure.HasValue ? departure.Value.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) : "";

            string payUrl = _helper.BuildPayNowUrl(
                hotelId,
                regId,
                fullName,
                summary.Remaining,
                arrivalText,
                departureText,
                src ?? "",
                "");

            if (string.IsNullOrWhiteSpace(payUrl))
            {
                model.ShowQr = false;
                return;
            }

            model.PayUrl = payUrl;
            model.QrDataUrl = _helper.GenerateQrDataUrl(payUrl);
            model.ShowQr = !string.IsNullOrWhiteSpace(model.QrDataUrl);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "InvoiceRecieving: QR could not be generated for {RegId}.", regId);
            model.ShowQr = false;
        }
    }

    // ------------------------------------------------------------- queries

    private async Task<bool> IsVatAllowedOnInvoiceAsync(string hotelId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(hotelId)) return false;

        try
        {
            await using var con = new SqlConnection(_connectionString);
            await using var cmd = new SqlCommand(SqlShowVatOnInvoice, con) { CommandTimeout = CommandTimeoutSeconds };

            cmd.Parameters.Add("@hotel_id", SqlDbType.NVarChar, 50).Value = hotelId.Trim();

            await con.OpenAsync(ct).ConfigureAwait(false);
            object? result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);

            return result is not null &&
                   result != DBNull.Value &&
                   Convert.ToBoolean(result, CultureInfo.InvariantCulture);
        }
        catch (Exception ex)
        {
            // The page logged this one and carried on with VAT off.
            _logger.LogError(ex, "InvoiceRecieving: show_vat_on_invoice lookup failed for {HotelId}.", hotelId);
            return false;
        }
    }

    private async Task<string> GetHotelNtnAsync(string hotelId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(hotelId)) return "";

        try
        {
            await using var con = new SqlConnection(_connectionString);
            await using var cmd = new SqlCommand(SqlHotelNtn, con) { CommandTimeout = CommandTimeoutSeconds };

            cmd.Parameters.Add("@hotel_id", SqlDbType.NVarChar, 50).Value = hotelId;

            await con.OpenAsync(ct).ConfigureAwait(false);
            object? result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);

            return result is null || result == DBNull.Value
                ? ""
                : (Convert.ToString(result, CultureInfo.InvariantCulture) ?? "").Trim();
        }
        catch
        {
            // Keep invoice rendering unchanged even if NTN cannot be loaded.
            return "";
        }
    }

    private async Task<Dictionary<string, Queue<string>>> BuildDeductionInfoQueuesAsync(
        string hotelId,
        string regId,
        CancellationToken ct)
    {
        var result = new Dictionary<string, Queue<string>>(StringComparer.OrdinalIgnoreCase);

        try
        {
            await using var con = new SqlConnection(_connectionString);
            await using var cmd = new SqlCommand(SqlDeductionInfo, con) { CommandTimeout = CommandTimeoutSeconds };

            cmd.Parameters.Add("@hotel_id", SqlDbType.NVarChar, 50).Value = hotelId;
            cmd.Parameters.Add("@reg_id", SqlDbType.NVarChar, 50).Value = regId;

            await con.OpenAsync(ct).ConfigureAwait(false);

            await using SqlDataReader dr = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

            int oDescr = dr.GetOrdinal("descr");
            int oInfo = dr.GetOrdinal("deductioninfo");
            int oAmount = dr.GetOrdinal("totalamount");

            while (await dr.ReadAsync(ct).ConfigureAwait(false))
            {
                string descr = dr.IsDBNull(oDescr) ? "" : dr.GetString(oDescr);
                string deductionInfo = dr.IsDBNull(oInfo) ? "" : dr.GetString(oInfo);
                decimal amount = dr.IsDBNull(oAmount) ? 0m : Convert.ToDecimal(dr.GetValue(oAmount), CultureInfo.InvariantCulture);

                string value = !string.IsNullOrWhiteSpace(deductionInfo)
                    ? deductionInfo.Trim()
                    : GetBaseDescription(descr);

                string key = MakeDeductionKey(GetBaseDescription(descr), amount);

                if (!result.TryGetValue(key, out Queue<string>? queue))
                {
                    queue = new Queue<string>();
                    result[key] = queue;
                }

                queue.Enqueue(value);
            }
        }
        catch (Exception ex)
        {
            // Descriptions fall back to the line's own text, as they did when
            // no deduction row matched.
            _logger.LogError(ex, "InvoiceRecieving: deduction info could not be read for {RegId}.", regId);
        }

        return result;
    }

    private async Task<(DateTime? Arrival, DateTime? Departure)> GetPaymentArrivalDepartureAsync(
        string hotelId,
        string regId,
        CancellationToken ct)
    {
        try
        {
            await using var con = new SqlConnection(_connectionString);
            await using var cmd = new SqlCommand(SqlArrivalDeparture, con) { CommandTimeout = CommandTimeoutSeconds };

            cmd.Parameters.Add("@hotel_id", SqlDbType.NVarChar, 50).Value = hotelId;
            cmd.Parameters.Add("@reg_id", SqlDbType.NVarChar, 50).Value = regId;

            await con.OpenAsync(ct).ConfigureAwait(false);

            await using SqlDataReader dr = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

            if (await dr.ReadAsync(ct).ConfigureAwait(false))
            {
                DateTime? arrival = dr["ArrivalDate"] == DBNull.Value
                    ? null
                    : Convert.ToDateTime(dr["ArrivalDate"], CultureInfo.InvariantCulture);

                DateTime? departure = dr["DepartureDate"] == DBNull.Value
                    ? null
                    : Convert.ToDateTime(dr["DepartureDate"], CultureInfo.InvariantCulture);

                return (arrival, departure);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "InvoiceRecieving: stay dates could not be read for {RegId}.", regId);
        }

        return (null, null);
    }

    // ---------------------------------------------------------------- rules

    private static bool IsRoomRentLine(string? descr) =>
        !string.IsNullOrWhiteSpace(descr) &&
        descr.Trim().StartsWith("Room Rent", StringComparison.OrdinalIgnoreCase);

    private static string BuildInvoiceDescription(
        InvoiceRecievingPaymentLine line,
        Dictionary<string, Queue<string>> deductionQueues)
    {
        if (line is null) return "";

        string descr = line.Descr ?? "";
        decimal gross = line.Gross;

        if (IsRoomRentLine(descr))
        {
            descr = descr.Replace("Cat ", "");
            return CleanQuestionMarks(descr);
        }

        string baseDescr = GetBaseDescription(descr);
        string key = MakeDeductionKey(baseDescr, gross);

        if (deductionQueues is not null &&
            deductionQueues.TryGetValue(key, out Queue<string>? queue) &&
            queue.Count > 0)
        {
            string deductionInfo = queue.Dequeue();

            if (!string.IsNullOrWhiteSpace(deductionInfo))
                return CleanQuestionMarks(deductionInfo.Trim());
        }

        return CleanQuestionMarks(baseDescr);
    }

    /// <summary>
    /// Splits the room-rent description the page produced
    /// ("Room Rent (Room G01, Standard Double Room, Nights 1)") into the two
    /// lines the redesigned table shows: the room category on top and the room
    /// number with its night count underneath. Nothing new is read from the
    /// database - this is the same text, laid out differently. Anything that
    /// does not parse falls back to the original single line.
    /// </summary>
    private static (string Main, string Sub) SplitDescription(string descr, bool isRoomRent)
    {
        if (!isRoomRent || string.IsNullOrWhiteSpace(descr))
            return (descr ?? "", "");

        int open = descr.IndexOf('(');
        int close = descr.LastIndexOf(')');

        if (open < 0 || close <= open)
            return (descr, "");

        string inside = descr.Substring(open + 1, close - open - 1);

        string[] parts = inside
            .Split(',')
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .ToArray();

        if (parts.Length == 0) return (descr, "");

        string room = parts.FirstOrDefault(p => p.StartsWith("Room", StringComparison.OrdinalIgnoreCase)) ?? "";
        string nights = parts.FirstOrDefault(p => p.StartsWith("Night", StringComparison.OrdinalIgnoreCase)) ?? "";

        string category = parts.FirstOrDefault(p =>
            !ReferenceEquals(p, null) &&
            !p.Equals(room, StringComparison.OrdinalIgnoreCase) &&
            !p.Equals(nights, StringComparison.OrdinalIgnoreCase)) ?? "";

        if (string.IsNullOrWhiteSpace(category))
            return (descr, "");

        string sub = room;

        // "Nights 1" -> "x 1", so the cell reads "Room G01 x 1".
        string nightCount = new string(nights.Where(char.IsDigit).ToArray());
        if (!string.IsNullOrWhiteSpace(nightCount))
            sub = string.IsNullOrWhiteSpace(sub) ? "x " + nightCount : sub + " x " + nightCount;

        return (category, sub);
    }

    private static string GetBaseDescription(string? descr)
    {
        if (string.IsNullOrWhiteSpace(descr)) return "";

        int idx = descr.IndexOf("(", StringComparison.Ordinal);
        if (idx > -1)
            return descr.Substring(0, idx).Trim();

        return descr.Trim();
    }

    private static string MakeDeductionKey(string? descr, decimal amount) =>
        (descr ?? "").Trim().ToLowerInvariant() + "|" +
        amount.ToString("0.00", CultureInfo.InvariantCulture);

    private static string CleanQuestionMarks(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";

        return text
            .Replace("Room ?,", "")
            .Replace("Room ?", "")
            .Replace("?,", "")
            .Replace("?", "")
            .Replace("Cat ", "")
            .Replace("  ", " ")
            .Trim();
    }

    private static decimal GetTaxRatePercent(InvoiceRecievingPaymentLine line, decimal lineNet, decimal lineVat)
    {
        decimal storedRate = line.StoredTaxRatePercent;
        if (storedRate > 0m)
            return storedRate;

        if (lineVat > 0m && lineNet > 0m)
            return Math.Round((lineVat / lineNet) * 100m, 4, MidpointRounding.AwayFromZero);

        return 0m;
    }

    private static void AddExplicitTaxRate(ICollection<decimal> rates, InvoiceRecievingPaymentLine line)
    {
        if (rates is null || line is null) return;

        decimal storedRate = line.StoredTaxRatePercent;
        if (storedRate > 0m)
            rates.Add(decimal.Round(storedRate, 2, MidpointRounding.AwayFromZero));
    }

    private static string GetDisplayTaxRateLabel(
        IReadOnlyList<InvoiceRecievingPaymentLine>? lines,
        IEnumerable<decimal>? explicitTaxRates)
    {
        var explicitList = (explicitTaxRates ?? Enumerable.Empty<decimal>())
            .Where(x => x > 0m)
            .Select(x => decimal.Round(x, 2, MidpointRounding.AwayFromZero))
            .ToList();

        decimal chosenRate = ChoosePreferredTaxRate(explicitList);
        if (chosenRate > 0m)
            return FormatTaxRate(chosenRate);

        if (lines is not null)
        {
            var computedRates = new List<decimal>();

            foreach (InvoiceRecievingPaymentLine line in lines)
            {
                if (line is null) continue;
                if (!IsRoomRentLine(line.Descr)) continue;

                decimal gross = line.Gross;
                decimal vat = line.Vat;
                decimal net = gross - vat;

                decimal rate = GetTaxRatePercent(line, net, vat);
                if (rate > 0m)
                    computedRates.Add(decimal.Round(rate, 2, MidpointRounding.AwayFromZero));
            }

            chosenRate = ChoosePreferredTaxRate(computedRates);
            if (chosenRate > 0m)
                return FormatTaxRate(chosenRate);
        }

        return "";
    }

    private static decimal ChoosePreferredTaxRate(IEnumerable<decimal>? rates)
    {
        var ranked = (rates ?? Enumerable.Empty<decimal>())
            .Where(x => x > 0m)
            .GroupBy(x => decimal.Round(x, 2, MidpointRounding.AwayFromZero))
            .Select(g => new { Rate = g.Key, Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .ThenBy(x => x.Rate)
            .ToList();

        return ranked.Count == 0 ? 0m : ranked[0].Rate;
    }

    private static string FormatTaxRate(decimal rate)
    {
        if (rate <= 0m) return "";

        return rate.ToString("0.##", CultureInfo.InvariantCulture) + "%";
    }

    private static string FormatMoney(decimal val, string currency) =>
        currency + val.ToString("N2", CultureInfo.InvariantCulture);
}
