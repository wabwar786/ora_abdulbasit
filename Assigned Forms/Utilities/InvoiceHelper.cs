using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Data;
using System.Linq;
using System.Web;
using System.Configuration;
using QRCoder;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;

namespace hotelsoftware.Utilities
{
    public static class InvoiceHelper
    {
       static string connectionString = ConfigurationManager.ConnectionStrings["con"].ConnectionString;
        // ===================== DATA ACCESS METHODS =====================

        public static (string Name, string Address, string Phone, string Email, string WebsiteUrl, string Logo, string CurrencySign) GetHotel(string hotelId)
        {
            const string sql = @"
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

            using (var con = new SqlConnection(connectionString))
            using (var cmd = new SqlCommand(sql, con))
            {
                cmd.Parameters.Add("@hid", SqlDbType.VarChar, 50).Value = hotelId;
                con.Open();
                using (var r = cmd.ExecuteReader())
                {
                    if (r.Read())
                    {
                        return (
                            Convert.ToString(r["name"]),
                            Convert.ToString(r["full_address"]),
                            Convert.ToString(r["phone"]),
                            Convert.ToString(r["email"]),
                            Convert.ToString(r["website_url"]),
                            Convert.ToString(r["logo"]),
                            Convert.ToString(r["currency_sign"])
                        );
                    }
                }
            }
            return ("Hotel", "", "", "", "", "", "£");
        }

        public  class GuestModel
        {
            public string FullName { get; set; }
            public string Address { get; set; }
            public string City { get; set; }
            public string Country { get; set; }
            public DateTime? Arrival { get; set; }
            public DateTime? Departure { get; set; }
            public string BookID { get; set; }
        }

        public static GuestModel GetGuest(string hotelId, string regId)
        {
            const string sql = @"
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

            using (var con = new SqlConnection(connectionString))
            using (var cmd = new SqlCommand(sql, con))
            {
                cmd.Parameters.Add("@hid", SqlDbType.VarChar, 50).Value = hotelId;
                cmd.Parameters.Add("@reg", SqlDbType.VarChar, 50).Value = regId;
                con.Open();
                using (var r = cmd.ExecuteReader())
                {
                    if (r.Read())
                    {
                        return new GuestModel
                        {
                            FullName = Convert.ToString(r["full_name"]),
                            Address = Convert.ToString(r["addr"]),
                            City = Convert.ToString(r["city"]),
                            Country = Convert.ToString(r["country"]),
                            Arrival = r["arr"] == DBNull.Value ? (DateTime?)null : Convert.ToDateTime(r["arr"]),
                            Departure = r["dep"] == DBNull.Value ? (DateTime?)null : Convert.ToDateTime(r["dep"]),
                            BookID = Convert.ToString(r["BookID"])
                        };
                    }
                }
            }
            return new GuestModel();
        }

        public class LineModel
        {
            public DateTime Date { get; set; }
            public string Descr { get; set; }
            public string PaymentMethod { get; set; }

            public DateTime? ArrivalDate { get; set; }
            public DateTime? DepartureDate { get; set; }

            public decimal Net { get; set; }
            public decimal Vat { get; set; }
            public decimal Gross { get; set; }
        }


        public static List<LineModel> GetPaymentLines(string hotelId, string regId)
        {
            const string sql = @"
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

            var list = new List<LineModel>();

            using (var con = new SqlConnection(connectionString))
            using (var cmd = new SqlCommand(sql, con))
            {
                cmd.Parameters.Add("@hid", SqlDbType.VarChar, 50).Value = hotelId;
                cmd.Parameters.Add("@reg", SqlDbType.VarChar, 50).Value = regId;

                con.Open();

                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        var dt = r["line_date"] == DBNull.Value
                            ? DateTime.Today
                            : Convert.ToDateTime(r["line_date"]);

                        var arrival = r["arrival_date"] == DBNull.Value
                            ? (DateTime?)null
                            : Convert.ToDateTime(r["arrival_date"]);

                        var departure = r["departure_date"] == DBNull.Value
                            ? (DateTime?)null
                            : Convert.ToDateTime(r["departure_date"]);

                        var descr = Convert.ToString(r["descr"]);
                        var pm = Convert.ToString(r["PaymentMethod"]);

                        var net = r["net_amount"] == DBNull.Value ? 0m : Convert.ToDecimal(r["net_amount"]);
                        var vat = r["vat_amount"] == DBNull.Value ? 0m : Convert.ToDecimal(r["vat_amount"]);
                        var gross = r["gross_amount"] == DBNull.Value ? 0m : Convert.ToDecimal(r["gross_amount"]);

                        if (net <= 0m && gross > 0m && vat > 0m)
                            net = gross - vat;

                        if (gross <= 0m && net > 0m)
                            gross = net + vat;

                        list.Add(new LineModel
                        {
                            Date = dt,
                            ArrivalDate = arrival,
                            DepartureDate = departure,
                            Descr = descr,
                            PaymentMethod = pm,
                            Net = net,
                            Vat = vat,
                            Gross = gross
                        });
                    }
                }
            }

            return list;
        }
        public class PaymentSummary
        {
            public decimal GrandTotal { get; set; }
            public decimal Paid { get; set; }
            public decimal Remaining { get; set; }
        }
        public static PaymentSummary GetLatestPaymentSummary(string hotelId, string regId)
        {
            const string sql = @"
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

            using (var con = new SqlConnection(connectionString))
            using (var cmd = new SqlCommand(sql, con))
            {
                cmd.Parameters.Add("@hid", SqlDbType.VarChar, 50).Value = hotelId;
                cmd.Parameters.Add("@reg", SqlDbType.VarChar, 50).Value = regId;
                con.Open();
                using (var r = cmd.ExecuteReader())
                {
                    if (!r.Read()) return null;
                    return new PaymentSummary
                    {
                        GrandTotal = r["grand_total"] == DBNull.Value ? 0m : Convert.ToDecimal(r["grand_total"]),
                        Paid = r["paid_amount"] == DBNull.Value ? 0m : Convert.ToDecimal(r["paid_amount"]),
                        Remaining = r["remaining_amount"] == DBNull.Value ? 0m : Convert.ToDecimal(r["remaining_amount"])
                    };
                }
            }
        }
        /// <summary>
        /// Generates the payment QR (data URL) and assigns it to imgQR.ImageUrl.
        /// Call after you’ve bound guest name, dates, and totals.
        /// </summary>
        public static string BuildPayNowUrl(string hotelId, string regId, string fullName,
                            decimal amount, string arrival, string depart, string src,string userid)
        {
            var req = HttpContext.Current.Request;
            string origin = req.Url.GetLeftPart(UriPartial.Authority);
            string app = req.ApplicationPath.TrimEnd('/');
            string baseUri = origin + app + "/";
            var qs = HttpUtility.ParseQueryString(string.Empty);
            qs["reg_id"] = Utilities.Convertion.B64UrlEncode(regId);
            qs["hotel_id"] = Utilities.Convertion.B64UrlEncode(hotelId);
            qs["name"] = Utilities.Convertion.B64UrlEncode(fullName);
            qs["amount"] = Utilities.Convertion.B64UrlEncode(amount.ToString(CultureInfo.InvariantCulture));
            qs["arrival"] = Utilities.Convertion.B64UrlEncode(arrival);
            qs["depart"] = Utilities.Convertion.B64UrlEncode(depart);
            qs["src"] = Utilities.Convertion.B64UrlEncode(src);
            qs["userid"] = Utilities.Convertion.B64UrlEncode(userid);
            return baseUri + "PayNow.aspx?" + qs.ToString();
        }


        /// <summary>
        /// Encodes the given URL as a PNG QR and returns a data URI.
        /// </summary>
        public static string GenerateQrDataUrl(string url)
        {
            using (var generator = new QRCodeGenerator())
            using (var data = generator.CreateQrCode(url, QRCodeGenerator.ECCLevel.M))
            using (var code = new QRCode(data))
            using (var bmp = code.GetGraphic(10)) // 10 = scale; adjust for size
            using (var ms = new MemoryStream())
            {
                bmp.Save(ms, ImageFormat.Png);
                string b64 = Convert.ToBase64String(ms.ToArray());
                return "data:image/png;base64," + b64;
            }
        }
    }
}