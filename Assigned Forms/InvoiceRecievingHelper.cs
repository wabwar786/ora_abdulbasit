#nullable enable

using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Reflection;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Orapmshms.Models;

namespace Orapmshms.Services;

/// <summary>
/// hotelsoftware.Utilities.InvoiceHelper, ported to ASP.NET Core.
///
/// The four queries below are that class's own SQL, byte for byte. Nothing was
/// rewritten, reordered or "improved": same CTEs, same COALESCE chains, same
/// TRY_CONVERT styles, same ORDER BY, same @hid / @reg VarChar(50) parameters.
///
/// Only the two methods that cannot cross to Core were rebuilt, and only in
/// their plumbing:
///
///   BuildPayNowUrl   used HttpContext.Current (System.Web, which does not
///                    exist in Core). It now takes the base address from the
///                    current request through IHttpContextAccessor. The query
///                    string it produces is identical - same keys, same order,
///                    same base64url encoding, same PayNow.aspx target.
///
///   GenerateQrDataUrl  used QRCoder with System.Drawing. Both are called
///                      through reflection here, so this module adds no
///                      package reference and the project's .csproj stays
///                      untouched. When QRCoder is not in the project the QR
///                      panel simply hides, exactly as it did whenever a QR
///                      could not be produced.
/// </summary>
public sealed class InvoiceRecievingHelper : IInvoiceRecievingHelper
{
    private readonly string _connectionString;
    private readonly IHttpContextAccessor? _http;
    private readonly ILogger? _logger;

    private const int CommandTimeoutSeconds = 60;

    public InvoiceRecievingHelper(
        IConfiguration configuration,
        IHttpContextAccessor? http = null,
        ILogger<InvoiceRecievingHelper>? logger = null)
    {
        // The same connection string name the class used: ConnectionStrings["con"].
        _connectionString =
            configuration.GetConnectionString("con")
            ?? configuration["ConnectionStrings:con"]
            ?? "";

        _http = http;
        _logger = logger;
    }

    // ---------------------------------------------------------------- hotel

    private const string SqlHotel = @"
SELECT TOP 1
  COALESCE(NULLIF(name,''),'Hotel') AS name,
  COALESCE(address,'') +
    CASE WHEN city IS NULL OR city='' THEN '' ELSE ', ' + city END +
    CASE WHEN postcode IS NULL OR postcode='' THEN '' ELSE ' ' + postcode END AS full_address,
  COALESCE(phone_no,'') AS phone,
  COALESCE(contact_email,email,'') AS email,
  COALESCE(website_url,'') AS website_url,
  COALESCE(logo,'') AS logo,
  COALESCE(currency_sign,'£') AS currency_sign
FROM dbo.HotelsSignUpTB
WHERE hotel_id=@hid;";

    public InvoiceRecievingHotel GetHotel(string hotelId)
    {
        using var con = new SqlConnection(_connectionString);
        using var cmd = new SqlCommand(SqlHotel, con) { CommandTimeout = CommandTimeoutSeconds };

        cmd.Parameters.Add("@hid", SqlDbType.VarChar, 50).Value = hotelId;

        con.Open();
        using SqlDataReader r = cmd.ExecuteReader();

        if (r.Read())
        {
            return new InvoiceRecievingHotel
            {
                Name = Str(r["name"]),
                Address = Str(r["full_address"]),
                Phone = Str(r["phone"]),
                Email = Str(r["email"]),
                WebsiteUrl = Str(r["website_url"]),
                Logo = Str(r["logo"]),
                CurrencySign = Str(r["currency_sign"])
            };
        }

        // The class's own fallback tuple.
        return new InvoiceRecievingHotel { Name = "Hotel", CurrencySign = "£" };
    }

    // ---------------------------------------------------------------- guest

    private const string SqlGuest = @"
;WITH G AS (
  SELECT TOP 1
      LTRIM(RTRIM(G.GuestName)) + ' ' + LTRIM(RTRIM(G.LastName)) AS full_name,
      COALESCE(G.Address,'') AS addr,
      COALESCE(G.City,'') AS city,
      COALESCE(G.Country,'') AS country,
      TRY_CONVERT(datetime, NULLIF(G.ArrivalDate,''), 120) AS arr,
      TRY_CONVERT(datetime, NULLIF(G.DepartureDate,''), 120) AS dep,
      G.BookID
  FROM dbo.GuestInformationLogTB G
  WHERE G.hotel_id=@hid AND G.reg_id=@reg
  ORDER BY G.ID DESC
),
NR AS (
  SELECT TOP 1
      LTRIM(RTRIM(NR.GuestName)) + ' ' + LTRIM(RTRIM(NR.LastName)) AS full_name,
      COALESCE(NR.Address,'') AS addr,
      COALESCE(NR.City,'') AS city,
      COALESCE(NR.Country,'') AS country,
      TRY_CONVERT(datetime, NULLIF(NR.ArrivalDate,''), 120) AS arr,
      TRY_CONVERT(datetime, NULLIF(NR.dept_date,''), 120) AS dep,
      NR.BookID
  FROM dbo.NewReservationsTB NR
  WHERE NR.hotel_id=@hid AND NR.reg_id=@reg
  ORDER BY NR.id DESC
)
SELECT TOP 1 * FROM (
  SELECT 1 AS pref, * FROM G
  UNION ALL
  SELECT 2 AS pref, * FROM NR
) x ORDER BY pref;";

    public InvoiceRecievingGuest GetGuest(string hotelId, string regId)
    {
        using var con = new SqlConnection(_connectionString);
        using var cmd = new SqlCommand(SqlGuest, con) { CommandTimeout = CommandTimeoutSeconds };

        cmd.Parameters.Add("@hid", SqlDbType.VarChar, 50).Value = hotelId;
        cmd.Parameters.Add("@reg", SqlDbType.VarChar, 50).Value = regId;

        con.Open();
        using SqlDataReader r = cmd.ExecuteReader();

        if (r.Read())
            return new InvoiceRecievingGuest { FullName = Str(r["full_name"]) };

        return new InvoiceRecievingGuest();
    }

    // ---------------------------------------------------------------- lines

    private const string SqlLines = @"
WITH p AS (
    SELECT *
    FROM dbo.payments
    WHERE hotel_id = @hid
      AND reg_id = @reg
),
px AS (
    SELECT
        p.*,
        tx.vat AS vat_pct,
        tx.isincludeinrate
    FROM p
    OUTER APPLY (
        SELECT TOP 1 t.vat, t.isincludeinrate
        FROM dbo.taxes t
        WHERE t.hotel_id = p.hotel_id
        ORDER BY t.id DESC
    ) AS tx
)
SELECT
    TRY_CONVERT(datetime, px.currentdate, 120) AS line_date,
    TRY_CONVERT(date, px.ArrivalDate, 101) AS arrival_date,
    TRY_CONVERT(date, px.DepartureDate, 101) AS departure_date,

    CONCAT(
        COALESCE(NULLIF(px.descr, ''), px.rateplanname, px.rateplan, 'Misc'),
        ' (Room ', COALESCE(NULLIF(px.room_no, ''), '?'),
        ', Cat ', COALESCE(NULLIF(px.Type, ''), '?'),
        ', Nights ', COALESCE(NULLIF(px.Nights, ''), '?'),
        ')'
    ) AS descr,

    COALESCE(px.PaymentMethod, '') AS PaymentMethod,

    CASE
        WHEN TRY_CONVERT(decimal(18,2), NULLIF(px.Charge,'')) > 0
            THEN TRY_CONVERT(decimal(18,2), NULLIF(px.Charge,''))

        WHEN TRY_CONVERT(decimal(18,2), NULLIF(px.totalamount,'')) > 0
             AND TRY_CONVERT(decimal(18,2), NULLIF(px.GST,'')) > 0
            THEN
                TRY_CONVERT(decimal(18,2), NULLIF(px.totalamount,''))
                -
                TRY_CONVERT(decimal(18,2), NULLIF(px.GST,''))

        WHEN TRY_CONVERT(decimal(18,2), NULLIF(px.totalamount,'')) > 0
            THEN TRY_CONVERT(decimal(18,2), NULLIF(px.totalamount,''))

        WHEN TRY_CONVERT(decimal(18,2), NULLIF(px.Rate,'')) > 0
            THEN TRY_CONVERT(decimal(18,2), NULLIF(px.Rate,''))

        ELSE 0
    END AS net_amount,

    CASE
        WHEN TRY_CONVERT(decimal(18,2), NULLIF(px.GST,'')) > 0
            THEN TRY_CONVERT(decimal(18,2), NULLIF(px.GST,''))

        WHEN COALESCE(px.isincludeinrate, 0) = 0
             AND TRY_CONVERT(decimal(18,4), NULLIF(px.Rate,'')) > 0
             AND COALESCE(px.vat_pct, 0) > 0
            THEN
                ROUND(
                    TRY_CONVERT(decimal(18,4), px.Rate)
                    -
                    (
                        TRY_CONVERT(decimal(18,4), px.Rate)
                        / (1 + (px.vat_pct / 100.0))
                    ),
                    2
                )

        ELSE 0
    END AS vat_amount,

    CASE
        WHEN TRY_CONVERT(decimal(18,2), NULLIF(px.totalamount,'')) > 0
            THEN TRY_CONVERT(decimal(18,2), NULLIF(px.totalamount,''))

        WHEN TRY_CONVERT(decimal(18,2), NULLIF(px.Rate,'')) > 0
            THEN TRY_CONVERT(decimal(18,2), NULLIF(px.Rate,''))

        WHEN TRY_CONVERT(decimal(18,2), NULLIF(px.Charge,'')) > 0
             AND TRY_CONVERT(decimal(18,2), NULLIF(px.GST,'')) > 0
            THEN
                TRY_CONVERT(decimal(18,2), NULLIF(px.Charge,''))
                +
                TRY_CONVERT(decimal(18,2), NULLIF(px.GST,''))

        ELSE 0
    END AS gross_amount

FROM px
ORDER BY
    TRY_CONVERT(date, px.ArrivalDate, 101) ASC,
    TRY_CONVERT(date, px.DepartureDate, 101) ASC,
    TRY_CONVERT(datetime, px.currentdate, 120) ASC,
    px.id ASC;";

    public IReadOnlyList<InvoiceRecievingPaymentLine>? GetPaymentLines(string hotelId, string regId)
    {
        var list = new List<InvoiceRecievingPaymentLine>();

        using var con = new SqlConnection(_connectionString);
        using var cmd = new SqlCommand(SqlLines, con) { CommandTimeout = CommandTimeoutSeconds };

        cmd.Parameters.Add("@hid", SqlDbType.VarChar, 50).Value = hotelId;
        cmd.Parameters.Add("@reg", SqlDbType.VarChar, 50).Value = regId;

        con.Open();
        using SqlDataReader r = cmd.ExecuteReader();

        while (r.Read())
        {
            DateTime dt = r["line_date"] == DBNull.Value
                ? DateTime.Today
                : Convert.ToDateTime(r["line_date"], CultureInfo.InvariantCulture);

            DateTime? arrival = r["arrival_date"] == DBNull.Value
                ? null
                : Convert.ToDateTime(r["arrival_date"], CultureInfo.InvariantCulture);

            DateTime? departure = r["departure_date"] == DBNull.Value
                ? null
                : Convert.ToDateTime(r["departure_date"], CultureInfo.InvariantCulture);

            string descr = Str(r["descr"]);

            decimal net = r["net_amount"] == DBNull.Value ? 0m : Convert.ToDecimal(r["net_amount"], CultureInfo.InvariantCulture);
            decimal vat = r["vat_amount"] == DBNull.Value ? 0m : Convert.ToDecimal(r["vat_amount"], CultureInfo.InvariantCulture);
            decimal gross = r["gross_amount"] == DBNull.Value ? 0m : Convert.ToDecimal(r["gross_amount"], CultureInfo.InvariantCulture);

            // The class's own two corrections, kept as they are.
            if (net <= 0m && gross > 0m && vat > 0m)
                net = gross - vat;

            if (gross <= 0m && net > 0m)
                gross = net + vat;

            list.Add(new InvoiceRecievingPaymentLine
            {
                Date = dt,
                ArrivalDate = arrival,
                DepartureDate = departure,
                Descr = descr,
                Gross = gross,
                Vat = vat,

                // LineModel carried no stored rate property, so the page's
                // rate lookup found none and computed it from net and vat.
                StoredTaxRatePercent = 0m
            });
        }

        return list;
    }

    // -------------------------------------------------------------- summary

    private const string SqlSummary = @"
;WITH p AS (
  SELECT
      TRY_CONVERT(decimal(18,2), NULLIF(grand_total,'')) AS grand_total,
      TRY_CONVERT(decimal(18,2), NULLIF(paid_amount,'')) AS paid_amount,
      TRY_CONVERT(decimal(18,2), NULLIF(remaining_amount,'')) AS remaining_amount,
      currentdate, id
  FROM dbo.PaymentsUpdateTB
  WHERE hotel_id=@hid AND reg_id=@reg
),
p_rank AS (
  SELECT *, ROW_NUMBER() OVER (ORDER BY ISNULL(TRY_CONVERT(datetime,currentdate,120),GETDATE()) DESC, id DESC) rn
  FROM p
)
SELECT TOP 1 grand_total, paid_amount, remaining_amount FROM p_rank WHERE rn=1;";

    public InvoiceRecievingPaymentSummary? GetLatestPaymentSummary(string hotelId, string regId)
    {
        using var con = new SqlConnection(_connectionString);
        using var cmd = new SqlCommand(SqlSummary, con) { CommandTimeout = CommandTimeoutSeconds };

        cmd.Parameters.Add("@hid", SqlDbType.VarChar, 50).Value = hotelId;
        cmd.Parameters.Add("@reg", SqlDbType.VarChar, 50).Value = regId;

        con.Open();
        using SqlDataReader r = cmd.ExecuteReader();

        if (!r.Read()) return null;

        return new InvoiceRecievingPaymentSummary
        {
            Remaining = r["remaining_amount"] == DBNull.Value
                ? 0m
                : Convert.ToDecimal(r["remaining_amount"], CultureInfo.InvariantCulture)
        };
    }

    // ------------------------------------------------------------- pay link

    /// <summary>
    /// The same PayNow.aspx link, built from the current request instead of
    /// HttpContext.Current: same keys in the same order, each base64url encoded
    /// the way Utilities.Convertion.B64UrlEncode encodes them.
    /// </summary>
    public string BuildPayNowUrl(
        string hotelId,
        string regId,
        string fullName,
        decimal amount,
        string arrivalText,
        string departureText,
        string src,
        string userid)
    {
        HttpRequest? req = _http?.HttpContext?.Request;
        if (req is null) return "";

        string origin = req.Scheme + "://" + req.Host.Value;
        string app = (req.PathBase.HasValue ? req.PathBase.Value : "").TrimEnd('/');
        string baseUri = origin + app + "/";

        var qs = new StringBuilder();

        Append(qs, "reg_id", B64UrlEncode(regId));
        Append(qs, "hotel_id", B64UrlEncode(hotelId));
        Append(qs, "name", B64UrlEncode(fullName));
        Append(qs, "amount", B64UrlEncode(amount.ToString(CultureInfo.InvariantCulture)));
        Append(qs, "arrival", B64UrlEncode(arrivalText));
        Append(qs, "depart", B64UrlEncode(departureText));
        Append(qs, "src", B64UrlEncode(src));
        Append(qs, "userid", B64UrlEncode(userid));

        return baseUri + "PayNow.aspx?" + qs;
    }

    private static void Append(StringBuilder qs, string key, string value)
    {
        if (qs.Length > 0) qs.Append('&');

        qs.Append(Uri.EscapeDataString(key))
          .Append('=')
          .Append(Uri.EscapeDataString(value));
    }

    private static string B64UrlEncode(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";

        return Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
                      .TrimEnd('=')
                      .Replace('+', '-')
                      .Replace('/', '_');
    }

    // ------------------------------------------------------------------- QR

    private static readonly Lazy<Type?> QrGeneratorType = new(
        () => FindType("QRCoder.QRCodeGenerator"), isThreadSafe: true);

    /// <summary>
    /// QRCoder through reflection, so no package reference is added to the
    /// project. Returns "" when QRCoder is not there, which hides the panel.
    /// </summary>
    public string GenerateQrDataUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "";

        Type? generatorType = QrGeneratorType.Value;
        if (generatorType is null) return "";

        try
        {
            object? generator = Activator.CreateInstance(generatorType);
            if (generator is null) return "";

            // ECCLevel.M, the level the original used.
            Type? eccType = generatorType.GetNestedType("ECCLevel");
            object? ecc = eccType is null ? null : Enum.Parse(eccType, "M");

            MethodInfo? create = generatorType.GetMethod("CreateQrCode", new[] { typeof(string), eccType! });
            object? data = create?.Invoke(generator, new[] { url, ecc });
            if (data is null) return "";

            // PngByteQRCode needs no System.Drawing and ships with QRCoder.
            Type? pngType = FindType("QRCoder.PngByteQRCode");
            if (pngType is not null)
            {
                object? png = Activator.CreateInstance(pngType, data);
                MethodInfo? graphic = pngType.GetMethod("GetGraphic", new[] { typeof(int) });

                if (png is not null && graphic?.Invoke(png, new object[] { 10 }) is byte[] bytes)
                    return "data:image/png;base64," + Convert.ToBase64String(bytes);
            }

            return "";
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "InvoiceRecieving: QR could not be generated.");
            return "";
        }
    }

    private static Type? FindType(string fullName)
    {
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type? type = assembly.GetType(fullName, throwOnError: false);
            if (type is not null) return type;
        }

        return null;
    }

    // ------------------------------------------------------------- helpers

    private static string Str(object? value) =>
        value is null || value == DBNull.Value
            ? ""
            : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
}
