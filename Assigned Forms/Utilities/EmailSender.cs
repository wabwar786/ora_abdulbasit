using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Data.SqlClient;
using System.IO;
using System.Net;
using System.Net.Mail;
using System.Text.RegularExpressions;
using System.Web;

namespace hotelsoftware.Utilities
{
    public static class EmailSender
    {
        private static string CS => ConfigurationManager.ConnectionStrings["con"].ConnectionString;
        // =========================
        // DTOs
        // =========================
        public class EmailConfig
        {
            public string FromEmail { get; set; }
            public string FromName { get; set; }
            public string SmtpHost { get; set; }
            public int SmtpPort { get; set; }
            public bool UseSsl { get; set; }
            public string SmtpUser { get; set; }
            public string SmtpPassPlain { get; set; } // decrypted in memory only
        }
        private class HotelInfo
        {
            public string Name { get; set; }
            public string LogoUrl { get; set; }
            public string Phone { get; set; }
            public string WebsiteUrl { get; set; }
            public string CheckInPolicy { get; set; }
            public string Email { get; set; }
        }
        private class GuestWelcomeInfo
        {
            public string FullName { get; set; }
            public string Email { get; set; }
            public string RoomNo { get; set; }
            public string RoomType { get; set; }       // optional: room_category
            public string ArrivalDateRaw { get; set; } // can be date/varchar
            public string ArrivalTimeRaw { get; set; } // can be time/varchar
            public string Status { get; set; }         // Status (optional)
            public string ResStatus { get; set; }      // res_status (important)
        }
        private class BookingRoomInfo
        {
            public int Quantity { get; set; }
            public string Category { get; set; }
            public string RoomNo { get; set; }
            public string RatePlan { get; set; }
            public string ArrivalDateRaw { get; set; }
            public string DepartureDateRaw { get; set; }
            public decimal TotalAmount { get; set; }
            public decimal Discount { get; set; }
        }

        private class BookingInfo
        {
            public string RegId { get; set; }
            public string GuestName { get; set; }
            public string Email { get; set; }
            public string Phone { get; set; }
            public string ArrivalDateRaw { get; set; }
            public string DepartureDateRaw { get; set; }
            public string ResStatus { get; set; }
            public string BookingSource { get; set; }
            public string Company { get; set; }
            public string PaymentMethod { get; set; }
            public string ReservationType { get; set; }
            public string ShuffleType { get; set; }
            public decimal AdvancePaid { get; set; }
            public List<BookingRoomInfo> Rooms { get; set; } = new List<BookingRoomInfo>();

            public decimal TotalAmount
            {
                get
                {
                    decimal total = 0m;
                    if (Rooms != null)
                    {
                        foreach (var room in Rooms) total += room.TotalAmount;
                    }
                    return total;
                }
            }

            public decimal Balance
            {
                get { return Math.Max(0m, TotalAmount - AdvancePaid); }
            }
        }

        // =========================
        // Email Validation (better)
        // =========================
        public static bool IsValidEmail(string email)
        {
            if (string.IsNullOrWhiteSpace(email)) return false;

            email = email.Trim();
            if (email.Length > 200) return false;

            try
            {
                var addr = new MailAddress(email);
                if (!addr.Address.Contains("@")) return false;

                // basic "must have dot in domain" (prevents "a@b")
                var parts = addr.Address.Split('@');
                if (parts.Length != 2) return false;
                if (!parts[1].Contains(".")) return false;

                return true;
            }
            catch { return false; }
        }
        // =========================
        // Get Guest Info (Email + Room + Arrival)
        // =========================
        private static GuestWelcomeInfo GetGuestWelcomeInfo(string hotelId, string regId)
        {
            using (var con = new SqlConnection(CS))
            using (var cmd = new SqlCommand(@"
SELECT TOP 1
    GuestName,
    LastName,
    Email,
    room_no,
    room_category,
    ArrivalDate,
    ArrivalTime,
    Status,
    res_status
FROM dbo.GuestInformationLogTB
WHERE hotel_id=@hotel_id
  AND reg_id=@reg_id
ORDER BY ID DESC;", con))
            {
                cmd.Parameters.AddWithValue("@hotel_id", hotelId);
                cmd.Parameters.AddWithValue("@reg_id", regId);
                con.Open();
                using (var r = cmd.ExecuteReader(CommandBehavior.SingleRow))
                {
                    if (!r.Read()) return null;

                    var fn = r["GuestName"]?.ToString() ?? "";
                    var ln = r["LastName"]?.ToString() ?? "";
                    var full = (fn + " " + ln).Trim();
                    if (string.IsNullOrWhiteSpace(full)) full = "Guest";

                    return new GuestWelcomeInfo
                    {
                        FullName = full,
                        Email = (r["Email"] == DBNull.Value ? "" : (r["Email"]?.ToString() ?? "")).Trim(),
                        RoomNo = (r["room_no"] == DBNull.Value ? "" : (r["room_no"]?.ToString() ?? "")).Trim(),
                        RoomType = (r["room_category"] == DBNull.Value ? "" : (r["room_category"]?.ToString() ?? "")).Trim(),
                        ArrivalDateRaw = r["ArrivalDate"] == DBNull.Value ? "" : (r["ArrivalDate"]?.ToString() ?? ""),
                        ArrivalTimeRaw = r["ArrivalTime"] == DBNull.Value ? "" : (r["ArrivalTime"]?.ToString() ?? ""),
                        Status = r["Status"] == DBNull.Value ? "" : (r["Status"]?.ToString() ?? ""),
                        ResStatus = r["res_status"] == DBNull.Value ? "" : (r["res_status"]?.ToString() ?? "")
                    };
                }
            }
        }
        // Keep your old method for compatibility (if you call it elsewhere)
        public static string GetGuestEmail(string hotelId, string regId)
        {
            var g = GetGuestWelcomeInfo(hotelId, regId);
            return g?.Email ?? "";
        }
        // ✅ Only send when res_status = check in
        private static bool IsCheckedIn(string resStatus)
        {
            var s = (resStatus ?? "").Trim().ToLowerInvariant();
            return s == "check in" || s == "checkin" || s == "checked in";
        }
        // =========================
        // Log (one email per reg_id)
        // =========================
        public static bool TryCreateWelcomeLog(string hotelId, string regId, string toEmail, string status, string message)
        {
            try
            {
                using (var con = new SqlConnection(CS))
                using (var cmd = new SqlCommand(@"
INSERT INTO dbo.EmailSendLogTB(hotel_id, reg_id, email_type, ToEmail, Status, Message,CreatedAt)
VALUES(@hotel_id, @reg_id, 'WELCOME', @to, @st, @msg,@CreatedAt);", con))
                {
                    cmd.Parameters.AddWithValue("@hotel_id", hotelId);
                    cmd.Parameters.AddWithValue("@reg_id", regId);
                    cmd.Parameters.AddWithValue("@to", (object)(toEmail ?? "") ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@st", status ?? "Sent");
                    cmd.Parameters.AddWithValue("@msg", (object)(message ?? "") ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@CreatedAt", HotelTimeHelper.GetHotelTime(hotelId));

                    con.Open();
                    cmd.ExecuteNonQuery();
                    return true;
                }
            }
            catch (SqlException ex)
            {
                // 2601/2627 = unique constraint violation => already exists
                if (ex.Number == 2601 || ex.Number == 2627) return false;
                throw;
            }
        }

        public static void UpdateWelcomeLogStatus(string hotelId, string regId, string status, string message)
        {
            using (var con = new SqlConnection(CS))
            using (var cmd = new SqlCommand(@"
UPDATE dbo.EmailSendLogTB
SET Status=@st, Message=@msg
WHERE hotel_id=@hotel_id AND reg_id=@reg_id AND email_type='WELCOME';", con))
            {
                cmd.Parameters.AddWithValue("@hotel_id", hotelId);
                cmd.Parameters.AddWithValue("@reg_id", regId);
                cmd.Parameters.AddWithValue("@st", status ?? "Sent");
                cmd.Parameters.AddWithValue("@msg", (object)(message ?? "") ?? DBNull.Value);

                con.Open();
                cmd.ExecuteNonQuery();
            }
        }
        // =========================
        // SMTP Config
        // =========================
        public static EmailConfig GetActiveConfig(string hotelId)
        {
            using (var con = new SqlConnection(CS))
            using (var cmd = new SqlCommand(@"
SELECT TOP 1 FromEmail, FromName, SmtpHost, SmtpPort, UseSsl, SmtpUser, SmtpPassEnc
FROM dbo.EmailSettingsTB
WHERE IsActive = 1
  AND hotel_id IN (@hotel_id, '-1')
ORDER BY 
    CASE 
        WHEN hotel_id = @hotel_id THEN 0
        WHEN hotel_id = '-1' THEN 1
        ELSE 2
    END,
    ID DESC;", con))
            {
                cmd.Parameters.AddWithValue("@hotel_id", hotelId);
                con.Open();

                using (var r = cmd.ExecuteReader(CommandBehavior.SingleRow))
                {
                    if (!r.Read()) return null;

                    var enc = r["SmtpPassEnc"]?.ToString() ?? "";
                    var pass = EmailCrypto.DecryptFromBase64(enc);

                    return new EmailConfig
                    {
                        FromEmail = r["FromEmail"]?.ToString(),
                        FromName = r["FromName"] == DBNull.Value ? "" : r["FromName"].ToString(),
                        SmtpHost = r["SmtpHost"]?.ToString(),
                        SmtpPort = Convert.ToInt32(r["SmtpPort"]),
                        UseSsl = Convert.ToBoolean(r["UseSsl"]),
                        SmtpUser = r["SmtpUser"]?.ToString(),
                        SmtpPassPlain = pass
                    };
                }
            }
        }
        // =========================
        // Hotel Info (brand)
        // =========================
        private static HotelInfo GetHotelInfo(string hotelId)
        {
            using (var con = new SqlConnection(CS))
            using (var cmd = new SqlCommand(@"
SELECT TOP 1
  name,
  logo,
  phone_no,
  website_url,
  CheckIn_Policy,
  email
FROM dbo.HotelsSignUpTB
WHERE hotel_id=@hotel_id;", con))
            {
                cmd.Parameters.AddWithValue("@hotel_id", hotelId);

                con.Open();
                using (var r = cmd.ExecuteReader(CommandBehavior.SingleRow))
                {
                    if (!r.Read()) return null;
                    return new HotelInfo
                    {
                        Name = r["name"]?.ToString(),
                        LogoUrl = r["logo"]?.ToString(),
                        Phone = r["phone_no"]?.ToString(),
                        WebsiteUrl = r["website_url"]?.ToString(),
                        CheckInPolicy = r["CheckIn_Policy"] == DBNull.Value ? "" : r["CheckIn_Policy"].ToString(),
                        Email = r["email"] == DBNull.Value ? "" : r["email"].ToString()
                    };
                }
            }
        }
        // =========================
        // Date/Time formatting (safe)
        // =========================
        private static string FormatAsDdMmYyyy(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            raw = raw.Trim();

            DateTime dt;
            if (DateTime.TryParse(raw, out dt))
                return dt.ToString("dd/MM/yyyy");

            return raw; // fallback
        }
        private static string FormatAsTime12(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            raw = raw.Trim();

            DateTime dt;
            if (DateTime.TryParse(raw, out dt))
                return dt.ToString("hh:mm tt");
            return raw; // fallback
        }
        // =========================
        // ✅ Your provided HTML template (unchanged)
        // =========================
        private static string BuildWelcomeTemplate_YourHtml(
            string hotelName,
            string hotelLogoUrl,
            GuestWelcomeInfo g
        )
        {
            hotelName = string.IsNullOrWhiteSpace(hotelName) ? "Hotel" : hotelName.Trim();
            hotelLogoUrl = hotelLogoUrl ?? "";

            string guestName = string.IsNullOrWhiteSpace(g?.FullName) ? "Guest" : g.FullName.Trim();
            string roomNo = string.IsNullOrWhiteSpace(g?.RoomNo) ? "-" : g.RoomNo.Trim();
            string roomType = string.IsNullOrWhiteSpace(g?.RoomType) ? "" : g.RoomType.Trim();

            string checkInDate = FormatAsDdMmYyyy(g?.ArrivalDateRaw);
            string checkInTime = FormatAsTime12(g?.ArrivalTimeRaw);
            string year = DateTime.Now.Year.ToString();

            string html = @"<!doctype html>
<html>
<head>
  <meta charset=""utf-8"">
  <meta name=""viewport"" content=""width=device-width,initial-scale=1"">
  <meta name=""x-apple-disable-message-reformatting"">
  <title>Check-in Details</title>
  <style>
    @media (max-width:600px){
      .w{width:100%!important}
      .px{padding-left:16px!important;padding-right:16px!important}
      .center{text-align:center!important}
      .stack{display:block!important;width:100%!important}
      .pt{padding-top:10px!important}
    }
  </style>
</head>

<body style=""margin:0;padding:0;"">
  <!-- preheader -->
  <div style=""display:none;max-height:0;overflow:hidden;opacity:0;color:#ffffff;font-size:1px;line-height:1px;"">
    Your check-in is on {{CheckInDate}} at {{CheckInTime}} — Room {{RoomNo}}.
  </div>

  <!-- FULL BACKGROUND GRADIENT -->
  <table role=""presentation"" width=""100%"" cellpadding=""0"" cellspacing=""0"" border=""0"">
    <tr>
      <td align=""center"" style=""padding:44px 14px;"">
        <!-- WHITE CARD -->
        <table role=""presentation"" width=""600"" cellpadding=""0"" cellspacing=""0"" border=""0"" class=""w""
               style=""width:600px;max-width:600px;background:#ffffff;border-radius:22px;overflow:hidden;
                      box-shadow:0 20px 55px rgba(0,0,0,.22);"">
          <!-- Header -->
          <tr>
            <td class=""px center"" style=""padding:26px 34px 16px;background:#f8fafc;"">
             
               <div  style=""display:block;margin:0 auto;border:0;max-width:140px;height:auto;font-weight:bold;"" >{{HotelName}}</div>
              <div style=""margin-top:12px;display:inline-block;padding:7px 12px;border-radius:999px;
                          background:#ffffff;border:1px solid #e5e7eb;
                          font-family:Arial,Helvetica,sans-serif;font-size:12px;font-weight:800;color:#0f172a;"">
                Check-in Details
              </div>
            </td>
          </tr>

          <!-- Title -->
          <tr>
            <td class=""px center"" style=""padding:22px 34px 10px;"">
              <div style=""font-family:Arial,Helvetica,sans-serif;font-size:26px;line-height:1.15;
                          font-weight:900;color:#0f172a;letter-spacing:-0.3px;"">
                Welcome, {{GuestName}}
              </div>
              <div style=""margin-top:8px;font-family:Arial,Helvetica,sans-serif;font-size:14px;line-height:1.75;color:#475569;"">
                We look forward to hosting you at <strong style=""color:#0f172a;"">{{HotelName}}</strong>.
              </div>
            </td>
          </tr>

          <!-- DETAILS STRIP -->
          <tr>
            <td class=""px"" style=""padding:12px 34px 0;"">
              <table role=""presentation"" width=""100%"" cellpadding=""0"" cellspacing=""0"" border=""0""
                     style=""background:#ffffff;border-radius:16px;overflow:hidden;"">
                <tr>
                  <td class=""stack center"" style=""width:60%;padding:14px 16px;vertical-align:top;"">
                    <div style=""font-family:Arial,Helvetica,sans-serif;font-size:11px;color:#64748b;font-weight:800;letter-spacing:.4px;"">
                      CHECK-IN
                    </div>
                    <div style=""margin-top:6px;font-family:Arial,Helvetica,sans-serif;font-size:16px;color:#0f172a;font-weight:900;"">
                      {{CheckInDate}}
                    </div>
                  </td>

                  <td class=""stack center pt"" style=""width:40%;padding:14px 16px;vertical-align:top;border-left:1px solid #e5e7eb;"">
                    <div style=""font-family:Arial,Helvetica,sans-serif;font-size:11px;color:#64748b;font-weight:800;letter-spacing:.4px;"">
                      ROOM
                    </div>
                    <div style=""margin-top:6px;font-family:Arial,Helvetica,sans-serif;font-size:22px;color:#0f172a;font-weight:900;"">
                      {{RoomNo}}
                    </div>
                    <div style=""margin-top:2px;font-family:Arial,Helvetica,sans-serif;font-size:12px;color:#64748b;"">
                      {{RoomType}}
                    </div>
                  </td>
                </tr>
              </table>

              <!-- Accent line -->
            </td>
          </tr>

          <!-- Note -->
          <tr>
            <td class=""px center"" style=""padding:18px 34px 26px;"">

              <div style=""margin-top:14px;font-family:Arial,Helvetica,sans-serif;font-size:12px;line-height:1.6;color:#94a3b8;"">
                Warm regards,<br>
                <strong style=""color:#0f172a;"">{{HotelName}} Team</strong>
              </div>
            </td>
          </tr>

          <!-- Footer -->
          <tr>
            <td class=""px center"" style=""padding:14px 34px;background:#f8fafc;border-top:1px solid #e5e7eb;"">
              <div style=""font-family:Arial,Helvetica,sans-serif;font-size:11px;color:#94a3b8;"">
                © {{Year}} {{HotelName}}
              </div>
            </td>
          </tr>

        </table>
        <!-- /WHITE CARD -->

      </td>
    </tr>
  </table>
</body>
</html>";
            // ✅ replace tokens (encode values)
            html = html.Replace("{{HotelName}}", HttpUtility.HtmlEncode(hotelName));
            html = html.Replace("{{HotelLogoUrl}}", HttpUtility.HtmlAttributeEncode(hotelLogoUrl));
            html = html.Replace("{{GuestName}}", HttpUtility.HtmlEncode(guestName));
            html = html.Replace("{{CheckInDate}}", HttpUtility.HtmlEncode(checkInDate));
            html = html.Replace("{{CheckInTime}}", HttpUtility.HtmlEncode(checkInTime));
            html = html.Replace("{{RoomNo}}", HttpUtility.HtmlEncode(roomNo));
            html = html.Replace("{{RoomType}}", HttpUtility.HtmlEncode(roomType));
            html = html.Replace("{{Year}}", HttpUtility.HtmlEncode(year));
            return html;
        }
        public static bool SendPaymentLinkEmailOnce(
    string hotelId,
    string regId,
    string toEmail,
    string payUrl,
    string customMessage,
    out string resultMessage
)
        {
            resultMessage = "";
            if (string.IsNullOrWhiteSpace(hotelId) || string.IsNullOrWhiteSpace(regId))
            {
                resultMessage = "hotelId/regId missing.";
                return false;
            }
            if (!IsValidEmail(toEmail))
            {
                resultMessage = "Invalid guest email.";
                return false;
            }
            if (string.IsNullOrWhiteSpace(payUrl))
            {
                resultMessage = "Payment link missing.";
                return false;
            }
            try
            {
                var cfg = GetActiveConfig(hotelId);
                if (cfg == null)
                {
                    resultMessage = "No active SMTP configuration.";
                    return false;
                }
                var hotelInfo = GetHotelInfo(hotelId);
                string hotelName = hotelInfo?.Name ?? "Hotel";
                var g = GetGuestWelcomeInfo(hotelId, regId);
                string guestName = g?.FullName ?? "Guest";
                string subject = "Payment Link";
                string html = BuildPayLinkTemplate(hotelName, guestName, payUrl, customMessage);
                Send(cfg, toEmail.Trim(), subject, html);
                resultMessage = "Payment link sent successfully.";
                return true;
            }
            catch (Exception ex)
            {
                resultMessage = "Email failed: " + ex.Message;
                return false;
            }
        }

        private static string BuildPayLinkTemplate(string hotelName, string guestName, string payUrl, string customMessage)
        {
            hotelName = string.IsNullOrWhiteSpace(hotelName) ? "Hotel" : hotelName.Trim();
            guestName = string.IsNullOrWhiteSpace(guestName) ? "Guest" : guestName.Trim();

            string safeUrl = HttpUtility.HtmlAttributeEncode(payUrl ?? "");
            string safeHotel = HttpUtility.HtmlEncode(hotelName);
            string safeGuest = HttpUtility.HtmlEncode(guestName);

            // ✅ allow multi-line message typed by user
            string msg = (customMessage ?? "").Trim();
            msg = HttpUtility.HtmlEncode(msg).Replace("\r\n", "<br/>").Replace("\n", "<br/>");

            if (string.IsNullOrWhiteSpace(msg))
                msg = $"Hello {safeGuest},<br/>Please use the secure payment link below:";

            return $@"<!doctype html>
<html><body style='font-family:Arial;background:#f5f7fb;margin:0;padding:0;'>
  <div style='max-width:600px;margin:24px auto;background:#fff;border-radius:14px;padding:18px;'>
    <h2 style='margin:0 0 8px 0;color:#0f172a;'>{safeHotel}</h2>

    <div style='color:#334155;line-height:1.6;margin:10px 0 14px 0;'>
      {msg}
    </div>

    
  </div>
</body></html>";
        }

        public static bool SendInvoicePdfEmailOnce(
         string hotelId,
         string regId,
         string toEmail,
         byte[] pdfBytes,
         string pdfFileName,
         string customMessage,
         out string msg,
         string subject = null
     )
        {
            msg = "";

            if (string.IsNullOrWhiteSpace(hotelId) || string.IsNullOrWhiteSpace(regId))
            {
                msg = "hotelId/regId missing.";
                return false;
            }
            if (!IsValidEmail(toEmail))
            {
                msg = "Invalid guest email.";
                return false;
            }
            if (pdfBytes == null || pdfBytes.Length == 0)
            {
                msg = "Invoice PDF is empty.";
                return false;
            }
            if (string.IsNullOrWhiteSpace(pdfFileName))
                pdfFileName = "Invoice-" + regId + ".pdf";

            if (string.IsNullOrWhiteSpace(subject))
                subject = "Invoice PDF - " + regId;

            // prevent duplicates (one INVPDF per regId)
            bool created = TryCreateEmailLog(hotelId, regId, "INVPDF", toEmail, "Pending", "Sending invoice PDF...");
            if (!created)
            {
                msg = "Invoice PDF already sent before.";
                return false;
            }

            try
            {
                var cfg = GetActiveConfig(hotelId);
                if (cfg == null)
                {
                    UpdateEmailLogStatus(hotelId, regId, "INVPDF", "Failed", "No active SMTP configuration");
                    msg = "No active SMTP configuration.";
                    return false;
                }

                var hotelInfo = GetHotelInfo(hotelId);
                string hotelName = hotelInfo != null && !string.IsNullOrWhiteSpace(hotelInfo.Name) ? hotelInfo.Name : "Hotel";

                var g = GetGuestWelcomeInfo(hotelId, regId);
                string guestName = g != null && !string.IsNullOrWhiteSpace(g.FullName) ? g.FullName : "Guest";

                string html = BuildInvoicePdfTemplate(hotelName, guestName, customMessage);

                using (var mail = new MailMessage())
                {
                    mail.From = new MailAddress(
                        cfg.FromEmail,
                        string.IsNullOrWhiteSpace(cfg.FromName) ? cfg.FromEmail : cfg.FromName
                    );
                    mail.To.Add(toEmail.Trim());
                    mail.Subject = subject;
                    mail.Body = html;
                    mail.IsBodyHtml = true;

                    // attach PDF
                    var ms = new MemoryStream(pdfBytes);
                    var att = new Attachment(ms, pdfFileName, "application/pdf");
                    mail.Attachments.Add(att);

                    using (var smtp = new SmtpClient(cfg.SmtpHost, cfg.SmtpPort))
                    {
                        smtp.EnableSsl = cfg.UseSsl;
                        smtp.Credentials = new NetworkCredential(cfg.SmtpUser, cfg.SmtpPassPlain);
                        smtp.DeliveryMethod = SmtpDeliveryMethod.Network;
                        smtp.Timeout = 30000;
                        smtp.Send(mail);
                    }
                }

                UpdateEmailLogStatus(hotelId, regId, "INVPDF", "Sent", "OK");
                msg = "Invoice PDF sent successfully.";
                return true;
            }
            catch (Exception ex)
            {
                UpdateEmailLogStatus(hotelId, regId, "INVPDF", "Failed", ex.Message);
                msg = "Invoice PDF failed: " + ex.Message;
                return false;
            }
        }

        // ✅ Sends Invoice Link Email (no PDF)
        public static bool SendInvoiceEmailOnce(
     string hotelId,
     string regId,
     string toEmail,
     string invoiceUrl,
     string customMessage,
     out string msg,
     string payUrl = null,
     string subject = null
 )
        {
            msg = "";

            if (string.IsNullOrWhiteSpace(hotelId) || string.IsNullOrWhiteSpace(regId))
            {
                msg = "hotelId/regId missing.";
                return false;
            }
            if (!IsValidEmail(toEmail))
            {
                msg = "Invalid guest email.";
                return false;
            }
            if (string.IsNullOrWhiteSpace(invoiceUrl))
            {
                msg = "Invoice URL missing.";
                return false;
            }

            // default subject
            if (string.IsNullOrWhiteSpace(subject))
                subject = "Invoice - " + regId;

            // prevent duplicates (one INVOICE per regId)
            bool created = TryCreateEmailLog(hotelId, regId, "INVOICE", toEmail, "Pending", "Sending invoice link...");
            if (!created)
            {
                msg = "Invoice email already sent before.";
                return false;
            }

            try
            {
                var cfg = GetActiveConfig(hotelId);
                if (cfg == null)
                {
                    UpdateEmailLogStatus(hotelId, regId, "INVOICE", "Failed", "No active SMTP configuration");
                    msg = "No active SMTP configuration.";
                    return false;
                }

                var hotelInfo = GetHotelInfo(hotelId);
                string hotelName = hotelInfo != null && !string.IsNullOrWhiteSpace(hotelInfo.Name) ? hotelInfo.Name : "Hotel";

                var g = GetGuestWelcomeInfo(hotelId, regId);
                string guestName = g != null && !string.IsNullOrWhiteSpace(g.FullName) ? g.FullName : "Guest";

                string html = BuildInvoiceLinkTemplate(hotelName, guestName, invoiceUrl, payUrl, customMessage);

                Send(cfg, toEmail.Trim(), subject, html);

                UpdateEmailLogStatus(hotelId, regId, "INVOICE", "Sent", "OK");
                msg = "Invoice email sent successfully.";
                return true;
            }
            catch (Exception ex)
            {
                UpdateEmailLogStatus(hotelId, regId, "INVOICE", "Failed", ex.Message);
                msg = "Invoice email failed: " + ex.Message;
                return false;
            }
        }

        // =========================================================
        // BOOKING CONFIRMATION - CUSTOMER + HOTEL
        // =========================================================
        public static bool SendBookingConfirmationEmailsOnce(
            string hotelId,
            string regId,
            string customerEmailFromTextbox,
            out string resultMessage)
        {
            resultMessage = string.Empty;
            hotelId = (hotelId ?? string.Empty).Trim();
            regId = (regId ?? string.Empty).Trim();
            customerEmailFromTextbox = (customerEmailFromTextbox ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(hotelId) || string.IsNullOrWhiteSpace(regId))
            {
                resultMessage = "hotelId/regId missing.";
                return false;
            }

            BookingInfo booking;
            HotelInfo hotel;
            EmailConfig cfg;

            try
            {
                booking = GetBookingInfo(hotelId, regId);
                hotel = GetHotelInfo(hotelId);
                cfg = GetActiveConfig(hotelId);
            }
            catch (Exception ex)
            {
                resultMessage = "Unable to prepare booking emails: " + ex.Message;
                return false;
            }

            if (booking == null)
            {
                resultMessage = "Booking not found for email.";
                return false;
            }

            string hotelName = hotel != null && !string.IsNullOrWhiteSpace(hotel.Name)
                ? hotel.Name.Trim()
                : "Hotel";

            bool provisional = IsProvisionalBooking(booking.ResStatus);

            string guestSubject = provisional
                ? "Provisional Booking - " + hotelName + " - " + regId
                : "Booking Confirmation - " + hotelName + " - " + regId;

            string hotelSubject = provisional
                ? "New Provisional Booking Received - " + booking.GuestName + " - " + regId
                : "New Booking Received - " + booking.GuestName + " - " + regId;

            string guestHtml = BuildCustomerBookingConfirmationTemplate(hotel, booking, provisional);
            string hotelHtml = BuildHotelBookingNotificationTemplate(hotel, booking, provisional);

            string guestResult;
            bool guestOk = SendOneBookingEmail(
                cfg,
                hotelId,
                regId,
                "BOOKING_GUEST",
                customerEmailFromTextbox,
                guestSubject,
                guestHtml,
                out guestResult);

            string hotelEmail = hotel == null ? string.Empty : (hotel.Email ?? string.Empty).Trim();
            string hotelResult;
            bool hotelOk = SendOneBookingEmail(
                cfg,
                hotelId,
                regId,
                "BOOKING_HOTEL",
                hotelEmail,
                hotelSubject,
                hotelHtml,
                out hotelResult);

            resultMessage = "Customer: " + guestResult + " | Hotel: " + hotelResult;
            return guestOk && hotelOk;
        }

        private static bool SendOneBookingEmail(
            EmailConfig cfg,
            string hotelId,
            string regId,
            string emailType,
            string toEmail,
            string subject,
            string html,
            out string result)
        {
            result = string.Empty;
            toEmail = (toEmail ?? string.Empty).Trim();

            if (!IsValidEmail(toEmail))
            {
                try
                {
                    TryCreateEmailLog(
                        hotelId,
                        regId,
                        emailType,
                        toEmail,
                        "Skipped",
                        "Invalid or missing recipient email");
                }
                catch { }

                result = "Skipped - invalid or missing email.";
                return false;
            }

            bool logCreated;
            try
            {
                logCreated = TryCreateEmailLog(
                    hotelId,
                    regId,
                    emailType,
                    toEmail,
                    "Pending",
                    "Preparing booking confirmation email...");
            }
            catch (Exception ex)
            {
                result = "Email log failed: " + ex.Message;
                return false;
            }

            if (!logCreated)
            {
                result = "Already processed before.";
                return true;
            }

            if (cfg == null)
            {
                try
                {
                    UpdateEmailLogStatus(
                        hotelId,
                        regId,
                        emailType,
                        "Failed",
                        "No active SMTP configuration");
                }
                catch { }

                result = "No active SMTP configuration.";
                return false;
            }

            try
            {
                Send(cfg, toEmail, subject, html);

                UpdateEmailLogStatus(
                    hotelId,
                    regId,
                    emailType,
                    "Sent",
                    "OK");

                result = "Sent successfully.";
                return true;
            }
            catch (Exception ex)
            {
                try
                {
                    UpdateEmailLogStatus(
                        hotelId,
                        regId,
                        emailType,
                        "Failed",
                        ex.Message);
                }
                catch { }

                result = "Failed: " + ex.Message;
                return false;
            }
        }

        private static BookingInfo GetBookingInfo(string hotelId, string regId)
        {
            BookingInfo booking = null;

            using (var con = new SqlConnection(CS))
            {
                con.Open();

                using (var cmd = new SqlCommand(@"
SELECT TOP 1
    reg_id,
    GuestName,
    LastName,
    Email,
    PhoneNo,
    ArrivalDate,
    dept_date,
    res_status,
    Agency,
    Status,
    advance_paid,
    payment_method,
    reservtype,
    shuffle_type
FROM dbo.NewReservationsTB
WHERE hotel_id=@hotel_id
  AND reg_id=@reg_id;", con))
                {
                    cmd.Parameters.AddWithValue("@hotel_id", hotelId);
                    cmd.Parameters.AddWithValue("@reg_id", regId);

                    using (var r = cmd.ExecuteReader(CommandBehavior.SingleRow))
                    {
                        if (!r.Read()) return null;

                        string firstName = SafeDbString(r["GuestName"]);
                        string lastName = SafeDbString(r["LastName"]);
                        string fullName = (firstName + " " + lastName).Trim();

                        booking = new BookingInfo
                        {
                            RegId = SafeDbString(r["reg_id"]),
                            GuestName = string.IsNullOrWhiteSpace(fullName) ? "Guest" : fullName,
                            Email = SafeDbString(r["Email"]),
                            Phone = SafeDbString(r["PhoneNo"]),
                            ArrivalDateRaw = SafeDbString(r["ArrivalDate"]),
                            DepartureDateRaw = SafeDbString(r["dept_date"]),
                            ResStatus = SafeDbString(r["res_status"]),
                            BookingSource = SafeDbString(r["Agency"]),
                            Company = SafeDbString(r["Status"]),
                            AdvancePaid = SafeDbDecimal(r["advance_paid"]),
                            PaymentMethod = SafeDbString(r["payment_method"]),
                            ReservationType = SafeDbString(r["reservtype"]),
                            ShuffleType = SafeDbString(r["shuffle_type"])
                        };
                    }
                }

                using (var cmd = new SqlCommand(@"
SELECT
    NumberOfRoom,
    Type,
    room_no,
    rateplanname,
    ArrivalDate,
    DepartureDate,
    totalamount,
    discount
FROM dbo.payments
WHERE hotel_id=@hotel_id
  AND reg_id=@reg_id
  AND LTRIM(RTRIM(descr))='Room Rent'
ORDER BY TRY_CONVERT(INT, room_no), room_no;", con))
                {
                    cmd.Parameters.AddWithValue("@hotel_id", hotelId);
                    cmd.Parameters.AddWithValue("@reg_id", regId);

                    using (var r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                        {
                            booking.Rooms.Add(new BookingRoomInfo
                            {
                                Quantity = SafeDbInt(r["NumberOfRoom"], 1),
                                Category = SafeDbString(r["Type"]),
                                RoomNo = SafeDbString(r["room_no"]),
                                RatePlan = SafeDbString(r["rateplanname"]),
                                ArrivalDateRaw = SafeDbString(r["ArrivalDate"]),
                                DepartureDateRaw = SafeDbString(r["DepartureDate"]),
                                TotalAmount = SafeDbDecimal(r["totalamount"]),
                                Discount = SafeDbDecimal(r["discount"])
                            });
                        }
                    }
                }
            }

            return booking;
        }

        private static string SafeDbString(object value)
        {
            return value == null || value == DBNull.Value
                ? string.Empty
                : Convert.ToString(value).Trim();
        }

        private static decimal SafeDbDecimal(object value)
        {
            if (value == null || value == DBNull.Value) return 0m;

            decimal number;
            string raw = Convert.ToString(value)
                .Replace(",", string.Empty)
                .Replace("£", string.Empty)
                .Replace("$", string.Empty)
                .Replace("€", string.Empty)
                .Trim();

            return decimal.TryParse(raw, out number) ? number : 0m;
        }

        private static int SafeDbInt(object value, int fallback)
        {
            if (value == null || value == DBNull.Value) return fallback;
            int number;
            return int.TryParse(Convert.ToString(value), out number) ? number : fallback;
        }

        private static bool IsProvisionalBooking(string status)
        {
            return string.Equals(
                (status ?? string.Empty).Trim(),
                "provisional",
                StringComparison.OrdinalIgnoreCase);
        }

        private static string BookingDisplayStatus(bool provisional)
        {
            return provisional ? "Provisional" : "Confirmed";
        }

        private static string DisplayRoomNoForGuest(string roomNo)
        {
            string value = (roomNo ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(value) ||
                value.Equals("UNASSIGNED", StringComparison.OrdinalIgnoreCase))
                return "To be assigned";

            return value;
        }

        private static int CalculateBookingNights(string arrivalRaw, string departureRaw)
        {
            DateTime arrival;
            DateTime departure;

            if (DateTime.TryParse(arrivalRaw, out arrival) &&
                DateTime.TryParse(departureRaw, out departure) &&
                departure.Date > arrival.Date)
            {
                return (departure.Date - arrival.Date).Days;
            }

            return 0;
        }

        private static string BuildCustomerBookingConfirmationTemplate(
            HotelInfo hotel,
            BookingInfo booking,
            bool provisional)
        {
            string hotelName = hotel != null && !string.IsNullOrWhiteSpace(hotel.Name)
                ? hotel.Name.Trim()
                : "Hotel";

            string safeHotel = HttpUtility.HtmlEncode(hotelName);
            string safeGuest = HttpUtility.HtmlEncode(booking.GuestName ?? "Guest");
            string safeReg = HttpUtility.HtmlEncode(booking.RegId ?? string.Empty);
            string safeArrival = HttpUtility.HtmlEncode(FormatAsDdMmYyyy(booking.ArrivalDateRaw));
            string safeDeparture = HttpUtility.HtmlEncode(FormatAsDdMmYyyy(booking.DepartureDateRaw));
            string safePhone = HttpUtility.HtmlEncode(hotel == null ? string.Empty : (hotel.Phone ?? string.Empty));
            string safeWebsite = HttpUtility.HtmlEncode(hotel == null ? string.Empty : (hotel.WebsiteUrl ?? string.Empty));
            string status = BookingDisplayStatus(provisional);
            int nights = CalculateBookingNights(booking.ArrivalDateRaw, booking.DepartureDateRaw);

            var roomRows = new System.Text.StringBuilder();
            if (booking.Rooms != null && booking.Rooms.Count > 0)
            {
                foreach (var room in booking.Rooms)
                {
                    roomRows.Append(@"<tr>");
                    roomRows.Append("<td style='padding:10px;border-bottom:1px solid #e5e7eb;'>" + HttpUtility.HtmlEncode(room.Category ?? "-") + "</td>");
                    roomRows.Append("<td style='padding:10px;border-bottom:1px solid #e5e7eb;text-align:center;'>" + room.Quantity + "</td>");
                    roomRows.Append("<td style='padding:10px;border-bottom:1px solid #e5e7eb;'>" + HttpUtility.HtmlEncode(DisplayRoomNoForGuest(room.RoomNo)) + "</td>");
                    roomRows.Append("<td style='padding:10px;border-bottom:1px solid #e5e7eb;'>" + HttpUtility.HtmlEncode(room.RatePlan ?? "-") + "</td>");
                    roomRows.Append(@"</tr>");
                }
            }
            else
            {
                roomRows.Append("<tr><td colspan='4' style='padding:12px;color:#64748b;'>Room details will be updated by the hotel.</td></tr>");
            }

            string policyBlock = string.Empty;
            if (hotel != null && !string.IsNullOrWhiteSpace(hotel.CheckInPolicy))
            {
                policyBlock = "<div style='margin-top:18px;padding:14px 16px;background:#f8fafc;border:1px solid #e5e7eb;border-radius:12px;'>" +
                              "<div style='font-size:12px;font-weight:700;color:#0d2742;margin-bottom:6px;'>CHECK-IN INFORMATION</div>" +
                              "<div style='font-size:13px;line-height:1.6;color:#526476;'>" + HttpUtility.HtmlEncode(hotel.CheckInPolicy) + "</div></div>";
            }

            return @"<!doctype html>
<html>
<head>
<meta charset='utf-8'>
<meta name='viewport' content='width=device-width,initial-scale=1'>
<title>Booking Confirmation</title>
</head>
<body style='margin:0;padding:0;background:#eef3f8;font-family:Arial,Helvetica,sans-serif;'>
<table role='presentation' width='100%' cellpadding='0' cellspacing='0' border='0' style='background:#eef3f8;'>
<tr><td align='center' style='padding:36px 14px;'>
<table role='presentation' width='640' cellpadding='0' cellspacing='0' border='0' style='width:640px;max-width:100%;background:#ffffff;border-radius:18px;overflow:hidden;box-shadow:0 16px 44px rgba(13,39,66,.12);'>
<tr><td style='padding:30px 34px;background:#0d2742;color:#ffffff;'>
<div style='font-size:24px;font-weight:800;'>" + safeHotel + @"</div>
<div style='margin-top:8px;font-size:12px;letter-spacing:1.5px;color:#d7a84a;font-weight:700;'>" + status.ToUpperInvariant() + @" BOOKING</div>
</td></tr>
<tr><td style='padding:30px 34px;'>
<div style='font-size:20px;font-weight:700;color:#0d2742;'>Dear " + safeGuest + @",</div>
<div style='margin-top:10px;font-size:14px;line-height:1.7;color:#526476;'>" +
                (provisional
                    ? "Your provisional booking has been received. Please keep the booking reference below for any communication with the hotel."
                    : "Thank you for your reservation. Your booking has been confirmed. Please keep the booking reference below for your records.") + @"</div>

<table width='100%' cellpadding='0' cellspacing='0' border='0' style='margin-top:22px;background:#f8fafc;border:1px solid #e5e7eb;border-radius:12px;'>
<tr>
<td style='padding:14px 16px;'><div style='font-size:11px;color:#64748b;font-weight:700;'>BOOKING REFERENCE</div><div style='margin-top:5px;font-size:17px;color:#0d2742;font-weight:800;'>" + safeReg + @"</div></td>
<td style='padding:14px 16px;border-left:1px solid #e5e7eb;'><div style='font-size:11px;color:#64748b;font-weight:700;'>STATUS</div><div style='margin-top:5px;font-size:17px;color:#0d2742;font-weight:800;'>" + status + @"</div></td>
</tr>
</table>

<table width='100%' cellpadding='0' cellspacing='0' border='0' style='margin-top:16px;border:1px solid #e5e7eb;border-radius:12px;'>
<tr><td style='padding:13px 16px;color:#64748b;font-size:12px;'>Check-in</td><td style='padding:13px 16px;text-align:right;color:#0d2742;font-weight:700;'>" + safeArrival + @"</td></tr>
<tr><td style='padding:13px 16px;border-top:1px solid #e5e7eb;color:#64748b;font-size:12px;'>Check-out</td><td style='padding:13px 16px;border-top:1px solid #e5e7eb;text-align:right;color:#0d2742;font-weight:700;'>" + safeDeparture + @"</td></tr>
<tr><td style='padding:13px 16px;border-top:1px solid #e5e7eb;color:#64748b;font-size:12px;'>Nights</td><td style='padding:13px 16px;border-top:1px solid #e5e7eb;text-align:right;color:#0d2742;font-weight:700;'>" + nights + @"</td></tr>
</table>

<div style='margin-top:22px;font-size:13px;font-weight:800;color:#0d2742;'>ROOM DETAILS</div>
<table width='100%' cellpadding='0' cellspacing='0' border='0' style='margin-top:8px;border:1px solid #e5e7eb;border-radius:12px;overflow:hidden;font-size:12px;color:#334155;'>
<tr style='background:#f8fafc;color:#64748b;font-weight:700;'><th align='left' style='padding:10px;'>Room Type</th><th style='padding:10px;'>Qty</th><th align='left' style='padding:10px;'>Room</th><th align='left' style='padding:10px;'>Rate Plan</th></tr>
" + roomRows + @"
</table>

<div style='margin-top:22px;font-size:13px;font-weight:800;color:#0d2742;'>PAYMENT SUMMARY</div>
<table width='100%' cellpadding='0' cellspacing='0' border='0' style='margin-top:8px;border:1px solid #e5e7eb;border-radius:12px;'>
<tr><td style='padding:12px 16px;color:#64748b;'>Booking Total</td><td style='padding:12px 16px;text-align:right;font-weight:700;color:#0d2742;'>" + booking.TotalAmount.ToString("N2") + @"</td></tr>
<tr><td style='padding:12px 16px;border-top:1px solid #e5e7eb;color:#64748b;'>Paid / Advance</td><td style='padding:12px 16px;border-top:1px solid #e5e7eb;text-align:right;font-weight:700;color:#0d2742;'>" + booking.AdvancePaid.ToString("N2") + @"</td></tr>
<tr><td style='padding:12px 16px;border-top:1px solid #e5e7eb;color:#64748b;'>Balance</td><td style='padding:12px 16px;border-top:1px solid #e5e7eb;text-align:right;font-weight:800;color:#0d2742;'>" + booking.Balance.ToString("N2") + @"</td></tr>
</table>
" + policyBlock + @"

<div style='margin-top:24px;font-size:13px;line-height:1.7;color:#526476;'>If you need to make any changes to your booking, please contact the hotel and quote booking reference <strong style='color:#0d2742;'>" + safeReg + @"</strong>.</div>
<div style='margin-top:18px;font-size:13px;line-height:1.6;color:#0d2742;font-weight:700;'>Warm regards,<br>" + safeHotel + @" Team</div>
</td></tr>
<tr><td align='center' style='padding:18px 24px;background:#f8fafc;border-top:1px solid #e5e7eb;font-size:11px;line-height:1.7;color:#8a98a7;'>" +
                (!string.IsNullOrWhiteSpace(safePhone) ? "Phone: " + safePhone + "<br>" : string.Empty) +
                (!string.IsNullOrWhiteSpace(safeWebsite) ? safeWebsite + "<br>" : string.Empty) +
                "© " + DateTime.Now.Year + " " + safeHotel + @"
</td></tr>
</table>
</td></tr>
</table>
</body>
</html>";
        }

        private static string BuildHotelBookingNotificationTemplate(
            HotelInfo hotel,
            BookingInfo booking,
            bool provisional)
        {
            string hotelName = hotel != null && !string.IsNullOrWhiteSpace(hotel.Name)
                ? hotel.Name.Trim()
                : "Hotel";

            string safeHotel = HttpUtility.HtmlEncode(hotelName);
            string safeGuest = HttpUtility.HtmlEncode(booking.GuestName ?? "Guest");
            string safeReg = HttpUtility.HtmlEncode(booking.RegId ?? string.Empty);
            string safeEmail = HttpUtility.HtmlEncode(booking.Email ?? string.Empty);
            string safePhone = HttpUtility.HtmlEncode(booking.Phone ?? string.Empty);
            string safeSource = HttpUtility.HtmlEncode(string.IsNullOrWhiteSpace(booking.BookingSource) ? "Walk In" : booking.BookingSource);
            string safeCompany = HttpUtility.HtmlEncode(string.IsNullOrWhiteSpace(booking.Company) ? "-" : booking.Company);
            string safePayment = HttpUtility.HtmlEncode(string.IsNullOrWhiteSpace(booking.PaymentMethod) ? "-" : booking.PaymentMethod);
            string safeReservationType = HttpUtility.HtmlEncode(string.IsNullOrWhiteSpace(booking.ReservationType) ? "Individual" : booking.ReservationType);
            string status = BookingDisplayStatus(provisional);

            var roomRows = new System.Text.StringBuilder();
            if (booking.Rooms != null && booking.Rooms.Count > 0)
            {
                foreach (var room in booking.Rooms)
                {
                    roomRows.Append("<tr>");
                    roomRows.Append("<td style='padding:9px;border-bottom:1px solid #e5e7eb;'>" + HttpUtility.HtmlEncode(room.Category ?? "-") + "</td>");
                    roomRows.Append("<td style='padding:9px;border-bottom:1px solid #e5e7eb;text-align:center;'>" + room.Quantity + "</td>");
                    roomRows.Append("<td style='padding:9px;border-bottom:1px solid #e5e7eb;'>" + HttpUtility.HtmlEncode(string.IsNullOrWhiteSpace(room.RoomNo) ? "-" : room.RoomNo) + "</td>");
                    roomRows.Append("<td style='padding:9px;border-bottom:1px solid #e5e7eb;'>" + HttpUtility.HtmlEncode(room.RatePlan ?? "-") + "</td>");
                    roomRows.Append("<td style='padding:9px;border-bottom:1px solid #e5e7eb;text-align:right;'>" + room.TotalAmount.ToString("N2") + "</td>");
                    roomRows.Append("</tr>");
                }
            }
            else
            {
                roomRows.Append("<tr><td colspan='5' style='padding:12px;color:#64748b;'>No room rows found.</td></tr>");
            }

            return @"<!doctype html>
<html>
<head><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'><title>New Booking</title></head>
<body style='margin:0;padding:0;background:#eef3f8;font-family:Arial,Helvetica,sans-serif;'>
<table role='presentation' width='100%' cellpadding='0' cellspacing='0' border='0'><tr><td align='center' style='padding:34px 14px;'>
<table role='presentation' width='680' cellpadding='0' cellspacing='0' border='0' style='width:680px;max-width:100%;background:#ffffff;border-radius:18px;overflow:hidden;box-shadow:0 16px 44px rgba(13,39,66,.12);'>
<tr><td style='padding:28px 32px;background:#0d2742;color:#ffffff;'>
<div style='font-size:22px;font-weight:800;'>" + safeHotel + @"</div>
<div style='margin-top:7px;font-size:12px;letter-spacing:1.4px;color:#d7a84a;font-weight:700;'>NEW " + status.ToUpperInvariant() + @" BOOKING RECEIVED</div>
</td></tr>
<tr><td style='padding:28px 32px;'>
<table width='100%' cellpadding='0' cellspacing='0' border='0' style='background:#f8fafc;border:1px solid #e5e7eb;border-radius:12px;'>
<tr><td style='padding:13px 15px;color:#64748b;font-size:12px;'>Booking Reference</td><td style='padding:13px 15px;text-align:right;color:#0d2742;font-weight:800;'>" + safeReg + @"</td></tr>
<tr><td style='padding:13px 15px;border-top:1px solid #e5e7eb;color:#64748b;font-size:12px;'>Status</td><td style='padding:13px 15px;border-top:1px solid #e5e7eb;text-align:right;color:#0d2742;font-weight:700;'>" + status + @"</td></tr>
<tr><td style='padding:13px 15px;border-top:1px solid #e5e7eb;color:#64748b;font-size:12px;'>Reservation Type</td><td style='padding:13px 15px;border-top:1px solid #e5e7eb;text-align:right;color:#0d2742;font-weight:700;'>" + safeReservationType + @"</td></tr>
</table>

<div style='margin-top:20px;font-size:13px;font-weight:800;color:#0d2742;'>GUEST DETAILS</div>
<table width='100%' cellpadding='0' cellspacing='0' border='0' style='margin-top:8px;border:1px solid #e5e7eb;border-radius:12px;'>
<tr><td style='padding:11px 14px;color:#64748b;'>Guest</td><td style='padding:11px 14px;text-align:right;font-weight:700;color:#0d2742;'>" + safeGuest + @"</td></tr>
<tr><td style='padding:11px 14px;border-top:1px solid #e5e7eb;color:#64748b;'>Email</td><td style='padding:11px 14px;border-top:1px solid #e5e7eb;text-align:right;color:#0d2742;'>" + (string.IsNullOrWhiteSpace(safeEmail) ? "-" : safeEmail) + @"</td></tr>
<tr><td style='padding:11px 14px;border-top:1px solid #e5e7eb;color:#64748b;'>Phone</td><td style='padding:11px 14px;border-top:1px solid #e5e7eb;text-align:right;color:#0d2742;'>" + (string.IsNullOrWhiteSpace(safePhone) ? "-" : safePhone) + @"</td></tr>
<tr><td style='padding:11px 14px;border-top:1px solid #e5e7eb;color:#64748b;'>Booking Source</td><td style='padding:11px 14px;border-top:1px solid #e5e7eb;text-align:right;color:#0d2742;'>" + safeSource + @"</td></tr>
<tr><td style='padding:11px 14px;border-top:1px solid #e5e7eb;color:#64748b;'>Company</td><td style='padding:11px 14px;border-top:1px solid #e5e7eb;text-align:right;color:#0d2742;'>" + safeCompany + @"</td></tr>
</table>

<div style='margin-top:20px;font-size:13px;font-weight:800;color:#0d2742;'>STAY DETAILS</div>
<table width='100%' cellpadding='0' cellspacing='0' border='0' style='margin-top:8px;border:1px solid #e5e7eb;border-radius:12px;'>
<tr><td style='padding:11px 14px;color:#64748b;'>Arrival</td><td style='padding:11px 14px;text-align:right;font-weight:700;color:#0d2742;'>" + HttpUtility.HtmlEncode(FormatAsDdMmYyyy(booking.ArrivalDateRaw)) + @"</td></tr>
<tr><td style='padding:11px 14px;border-top:1px solid #e5e7eb;color:#64748b;'>Departure</td><td style='padding:11px 14px;border-top:1px solid #e5e7eb;text-align:right;font-weight:700;color:#0d2742;'>" + HttpUtility.HtmlEncode(FormatAsDdMmYyyy(booking.DepartureDateRaw)) + @"</td></tr>
<tr><td style='padding:11px 14px;border-top:1px solid #e5e7eb;color:#64748b;'>Nights</td><td style='padding:11px 14px;border-top:1px solid #e5e7eb;text-align:right;font-weight:700;color:#0d2742;'>" + CalculateBookingNights(booking.ArrivalDateRaw, booking.DepartureDateRaw) + @"</td></tr>
</table>

<div style='margin-top:20px;font-size:13px;font-weight:800;color:#0d2742;'>ROOM / RATE DETAILS</div>
<table width='100%' cellpadding='0' cellspacing='0' border='0' style='margin-top:8px;border:1px solid #e5e7eb;border-radius:12px;overflow:hidden;font-size:12px;color:#334155;'>
<tr style='background:#f8fafc;color:#64748b;font-weight:700;'><th align='left' style='padding:9px;'>Room Type</th><th style='padding:9px;'>Qty</th><th align='left' style='padding:9px;'>Room</th><th align='left' style='padding:9px;'>Rate Plan</th><th align='right' style='padding:9px;'>Amount</th></tr>
" + roomRows + @"
</table>

<div style='margin-top:20px;font-size:13px;font-weight:800;color:#0d2742;'>PAYMENT DETAILS</div>
<table width='100%' cellpadding='0' cellspacing='0' border='0' style='margin-top:8px;border:1px solid #e5e7eb;border-radius:12px;'>
<tr><td style='padding:11px 14px;color:#64748b;'>Total</td><td style='padding:11px 14px;text-align:right;font-weight:800;color:#0d2742;'>" + booking.TotalAmount.ToString("N2") + @"</td></tr>
<tr><td style='padding:11px 14px;border-top:1px solid #e5e7eb;color:#64748b;'>Advance Paid</td><td style='padding:11px 14px;border-top:1px solid #e5e7eb;text-align:right;font-weight:700;color:#0d2742;'>" + booking.AdvancePaid.ToString("N2") + @"</td></tr>
<tr><td style='padding:11px 14px;border-top:1px solid #e5e7eb;color:#64748b;'>Balance</td><td style='padding:11px 14px;border-top:1px solid #e5e7eb;text-align:right;font-weight:800;color:#0d2742;'>" + booking.Balance.ToString("N2") + @"</td></tr>
<tr><td style='padding:11px 14px;border-top:1px solid #e5e7eb;color:#64748b;'>Payment Method</td><td style='padding:11px 14px;border-top:1px solid #e5e7eb;text-align:right;color:#0d2742;'>" + safePayment + @"</td></tr>
</table>

<div style='margin-top:20px;padding:13px 15px;background:#fff7e6;border:1px solid #f1d28a;border-radius:10px;font-size:12px;line-height:1.6;color:#6b4d12;'>This is an internal hotel notification. Please review room allocation, payment information and any operational requirements for this booking.</div>
</td></tr>
<tr><td align='center' style='padding:16px 22px;background:#f8fafc;border-top:1px solid #e5e7eb;font-size:11px;color:#8a98a7;'>Automated booking notification from ORA PMS</td></tr>
</table>
</td></tr></table>
</body>
</html>";
        }

        // =========================
        // MAIN: Send welcome once
        // =========================
        public static bool SendWelcomeEmailOnce(string hotelId, string userId, string regId, string roomNoIgnored, out string resultMessage)
        {
            resultMessage = "";
            if (string.IsNullOrWhiteSpace(hotelId) || string.IsNullOrWhiteSpace(regId))
            {
                resultMessage = "hotelId/regId missing";
                return false;
            }
            // 1) get guest info (includes room_no and res_status)
            var g = GetGuestWelcomeInfo(hotelId, regId);
            if (g == null)
            {
                resultMessage = "Guest not found.";
                return false;
            }
            // 2) must be checked-in
            if (!IsCheckedIn(g.ResStatus))
            {
                // log once (optional). If you don't want to log in this case, remove this block.
                var created = TryCreateWelcomeLog(hotelId, regId, g.Email, "Skipped", "Not checked-in");
                resultMessage = created ? "Skipped (not checked-in)" : "Skipped (already processed)";
                return false;
            }
            var toEmail = g.Email;
            // 3) email validation
            if (!IsValidEmail(toEmail))
            {
                var created = TryCreateWelcomeLog(hotelId, regId, toEmail, "Skipped", "Invalid or missing email");
                resultMessage = created ? "Skipped (invalid email)" : "Skipped (already processed)";
                return false;
            }
            // 4) create log first (prevents duplicates)
            bool logCreated = TryCreateWelcomeLog(hotelId, regId, toEmail, "Pending", "Sending...");
            if (!logCreated)
            {
                resultMessage = "Already processed before.";
                return false;
            }
            try
            {
                // 5) SMTP config
                var cfg = GetActiveConfig(hotelId);
                if (cfg == null)
                {
                    UpdateWelcomeLogStatus(hotelId, regId, "Failed", "No active SMTP configuration");
                    resultMessage = "No active SMTP configuration.";
                    return false;
                }
                // 6) hotel info for logo/name
                var hotelInfo = GetHotelInfo(hotelId);
                string subject = "Check-in Details";
                // ✅ build YOUR exact template (room_no from GuestInformationLogTB)
                string html = BuildWelcomeTemplate_YourHtml(
                    hotelName: hotelInfo?.Name,
                    hotelLogoUrl: hotelInfo?.LogoUrl,
                    g: g
                );
                Send(cfg, toEmail.Trim(), subject, html);
                UpdateWelcomeLogStatus(hotelId, regId, "Sent", "OK");
                resultMessage = "Welcome email sent.";
                return true;
            }
            catch (Exception ex)
            {
                UpdateWelcomeLogStatus(hotelId, regId, "Failed", ex.Message);
                resultMessage = "Email failed: " + ex.Message;
                return false;
            }
        }

        // =========================
        // SEND (SMTP)
        // =========================
        public static void Send(EmailConfig cfg, string toEmail, string subject, string htmlBody)
        {
            if (cfg == null) throw new Exception("Email config is missing.");
            if (string.IsNullOrWhiteSpace(toEmail)) throw new Exception("To email is required.");
            if (string.IsNullOrWhiteSpace(cfg.FromEmail)) throw new Exception("FromEmail is missing in config.");
            if (string.IsNullOrWhiteSpace(cfg.SmtpHost)) throw new Exception("SmtpHost is missing in config.");
            if (cfg.SmtpPort <= 0) throw new Exception("SmtpPort is invalid in config.");
            using (var msg = new MailMessage())
            {
                msg.From = new MailAddress(
                    cfg.FromEmail,
                    string.IsNullOrWhiteSpace(cfg.FromName) ? cfg.FromEmail : cfg.FromName
                );
                msg.To.Add(toEmail);
                msg.Subject = subject ?? "";
                msg.Body = htmlBody ?? "";
                msg.IsBodyHtml = true;
                using (var smtp = new SmtpClient(cfg.SmtpHost, cfg.SmtpPort))
                {
                    smtp.EnableSsl = cfg.UseSsl;
                    smtp.Credentials = new NetworkCredential(cfg.SmtpUser, cfg.SmtpPassPlain);
                    smtp.DeliveryMethod = SmtpDeliveryMethod.Network;
                    smtp.Timeout = 30000;
                    smtp.Send(msg);
                }
            }
        }
        private static string GetFeedbackPageUrl()
        {
            HttpContext context = HttpContext.Current;

            if (context == null || context.Request == null)
            {
                throw new InvalidOperationException(
                    "The current HTTP request is not available.");
            }

            HttpRequest request = context.Request;

            string relativePath = VirtualPathUtility.ToAbsolute("~/Feedback.aspx");

            Uri baseUri = new Uri(
                request.Url.GetLeftPart(UriPartial.Authority));

            Uri fullUri = new Uri(baseUri, relativePath);

            return fullUri.AbsoluteUri;
        }
        private static bool IsValidAbsoluteHttpUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return false;

            Uri uri;

            if (!Uri.TryCreate(url, UriKind.Absolute, out uri))
                return false;

            return uri.Scheme.Equals(
                       Uri.UriSchemeHttp,
                       StringComparison.OrdinalIgnoreCase)
                   ||
                   uri.Scheme.Equals(
                       Uri.UriSchemeHttps,
                       StringComparison.OrdinalIgnoreCase);
        }
        public static bool SendCheckoutThanksEmailOnce(
       string hotelId,
       string regId,
       string feedbackPageUrl,
       out string resultMessage)
        {
            resultMessage = string.Empty;

            hotelId = (hotelId ?? string.Empty).Trim();
            regId = (regId ?? string.Empty).Trim();
            feedbackPageUrl = (feedbackPageUrl ?? string.Empty).Trim();

            string visitId = "1";

            if (string.IsNullOrWhiteSpace(hotelId) ||
                string.IsNullOrWhiteSpace(regId))
            {
                resultMessage = "hotelId/regId missing.";
                return false;
            }

            if (!IsValidAbsoluteHttpUrl(feedbackPageUrl))
            {
                resultMessage = "A valid feedback page URL was not provided.";
                return false;
            }

            GuestWelcomeInfo guest = GetGuestWelcomeInfo(hotelId, regId);

            if (guest == null)
            {
                resultMessage = "Guest not found.";
                return false;
            }

            string toEmail = (guest.Email ?? string.Empty).Trim();

            if (!IsValidEmail(toEmail))
            {
                TryCreateEmailLog(
                    hotelId,
                    regId,
                    "CHECKOUT",
                    toEmail,
                    "Skipped",
                    "Invalid or missing email");

                resultMessage = "Skipped (invalid email).";
                return false;
            }

            bool logCreated = TryCreateEmailLog(
                hotelId,
                regId,
                "CHECKOUT",
                toEmail,
                "Pending",
                "Preparing checkout feedback email...");

            if (!logCreated)
            {
                resultMessage = "Checkout email already sent before.";
                return false;
            }

            try
            {
                EmailConfig config = GetActiveConfig(hotelId);

                if (config == null)
                {
                    UpdateEmailLogStatus(
                        hotelId,
                        regId,
                        "CHECKOUT",
                        "Failed",
                        "No active SMTP configuration");

                    resultMessage = "No active SMTP configuration.";
                    return false;
                }

                HotelInfo hotelInfo = GetHotelInfo(hotelId);

                string hotelName =
                    hotelInfo != null &&
                    !string.IsNullOrWhiteSpace(hotelInfo.Name)
                        ? hotelInfo.Name.Trim()
                        : "Hotel";

                string guestName =
                    !string.IsNullOrWhiteSpace(guest.FullName)
                        ? guest.FullName.Trim()
                        : "Guest";

                var feedbackService =
                    new global::hotelsoftware.HotelFeedbackLinkService(
                        CS,
                        feedbackPageUrl);

                string feedbackUrl = feedbackService.CreateFeedbackUrl(
                    hotelId: hotelId,
                    regId: regId,
                    visitId: visitId,
                    hotelName: hotelName,
                    hotelLogoUrl: hotelInfo == null
                        ? null
                        : hotelInfo.LogoUrl,
                    guestName: guestName,
                    guestEmail: toEmail,
                    bookingNumber: regId,
                    expiryDays: 30);

                string feedbackSection =
                    global::hotelsoftware.CheckoutFeedbackEmailIntegration
                        .BuildFeedbackEmailSection(
                            feedbackUrl,
                            hotelName);

                string subject =
                    "How was your stay at " + hotelName + "?";

                string html = BuildCheckoutThanksTemplate(
                    hotelName,
                    guestName,
                    feedbackSection);

                Send(config, toEmail, subject, html);

                UpdateEmailLogStatus(
                    hotelId,
                    regId,
                    "CHECKOUT",
                    "Sent",
                    "Checkout email with feedback link sent successfully.");

                resultMessage = "Checkout feedback email sent.";
                return true;
            }
            catch (Exception ex)
            {
                UpdateEmailLogStatus(
                    hotelId,
                    regId,
                    "CHECKOUT",
                    "Failed",
                    ex.Message);

                resultMessage = "Email failed: " + ex.Message;
                return false;
            }
        }

        // ------------------------
        // helpers (generic log)
        // ------------------------
        private static bool TryCreateEmailLog(string hotelId, string regId, string emailType, string toEmail, string status, string message)
        {
            try
            {
                using (var con = new SqlConnection(CS))
                using (var cmd = new SqlCommand(@"
INSERT INTO dbo.EmailSendLogTB(hotel_id, reg_id, email_type, ToEmail, Status, Message)
VALUES(@hotel_id, @reg_id, @type, @to, @st, @msg);", con))
                {
                    cmd.Parameters.AddWithValue("@hotel_id", hotelId);
                    cmd.Parameters.AddWithValue("@reg_id", regId);
                    cmd.Parameters.AddWithValue("@type", emailType ?? "");
                    cmd.Parameters.AddWithValue("@to", (object)(toEmail ?? "") ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@st", status ?? "Sent");
                    cmd.Parameters.AddWithValue("@msg", (object)(message ?? "") ?? DBNull.Value);
                    con.Open();
                    cmd.ExecuteNonQuery();
                    return true;
                }
            }
            catch (SqlException ex)
            {
                // already exists
                if (ex.Number == 2601 || ex.Number == 2627) return false;
                throw;
            }
        }

        private static void UpdateEmailLogStatus(string hotelId, string regId, string emailType, string status, string message)
        {
            using (var con = new SqlConnection(CS))
            using (var cmd = new SqlCommand(@"
UPDATE dbo.EmailSendLogTB
SET Status=@st, Message=@msg
WHERE hotel_id=@hotel_id AND reg_id=@reg_id AND email_type=@type;", con))
            {
                cmd.Parameters.AddWithValue("@hotel_id", hotelId);
                cmd.Parameters.AddWithValue("@reg_id", regId);
                cmd.Parameters.AddWithValue("@type", emailType ?? "");
                cmd.Parameters.AddWithValue("@st", status ?? "Sent");
                cmd.Parameters.AddWithValue("@msg", (object)(message ?? "") ?? DBNull.Value);

                con.Open();
                cmd.ExecuteNonQuery();
            }
        }

        // ------------------------
        // template
        // ------------------------
        private static string BuildCheckoutThanksTemplate(
            string hotelName,
            string guestName,
            string feedbackSection)
        {
            hotelName = string.IsNullOrWhiteSpace(hotelName)
                ? "Hotel"
                : hotelName.Trim();

            guestName = string.IsNullOrWhiteSpace(guestName)
                ? "Guest"
                : guestName.Trim();

            string safeHotel = HttpUtility.HtmlEncode(hotelName);
            string safeGuest = HttpUtility.HtmlEncode(guestName);
            string year = DateTime.UtcNow.Year.ToString();

            return @"<!doctype html>
<html>
<head>
<meta charset='utf-8'>
<meta name='viewport' content='width=device-width,initial-scale=1'>
<meta name='x-apple-disable-message-reformatting'>
<title>Share Your Stay Experience</title>
<style>
@media only screen and (max-width:620px){
    .email-card{width:100%!important;}
    .email-padding{padding-left:20px!important;padding-right:20px!important;}
}
</style>
</head>
<body style='margin:0;padding:0;background:#eef3f8;font-family:Arial,Helvetica,sans-serif;'>
<div style='display:none;max-height:0;overflow:hidden;opacity:0;color:transparent;'>
Thank you for staying with " + safeHotel + @". Please rate your experience.
</div>
<table role='presentation' width='100%' cellpadding='0' cellspacing='0' border='0' style='background:#eef3f8;'>
<tr>
<td align='center' style='padding:38px 14px;'>
<table role='presentation' width='600' cellpadding='0' cellspacing='0' border='0' class='email-card'
style='width:600px;max-width:600px;background:#ffffff;border-radius:22px;overflow:hidden;box-shadow:0 18px 48px rgba(13,39,66,.14);'>
<tr>
<td align='center' style='padding:34px 30px;background:#0d2742;'>
<div style='font-size:24px;line-height:1.25;font-weight:800;color:#ffffff;'>" + safeHotel + @"</div>
<div style='margin-top:9px;font-size:11px;line-height:1.4;font-weight:700;letter-spacing:2px;color:#d7a84a;'>THANK YOU FOR YOUR STAY</div>
</td>
</tr>
<tr>
<td class='email-padding' style='padding:36px 38px 22px;'>
<div style='font-size:18px;line-height:1.4;font-weight:700;color:#0d2742;'>Dear " + safeGuest + @",</div>
<div style='margin-top:14px;font-size:15px;line-height:1.75;color:#526476;'>
Thank you for choosing <strong style='color:#0d2742;'>" + safeHotel + @"</strong>.
We hope your stay was comfortable and memorable.
</div>
<div style='margin-top:12px;font-size:15px;line-height:1.75;color:#526476;'>
Your rating takes less than a minute and helps the hotel improve future guest experiences.
</div>
</td>
</tr>
<tr>
<td class='email-padding' style='padding:0 38px 8px;'>" + (feedbackSection ?? string.Empty) + @"</td>
</tr>
<tr>
<td class='email-padding' style='padding:20px 38px 32px;'>
<div style='font-size:14px;line-height:1.6;color:#0d2742;font-weight:700;'>
Warm regards,<br>" + safeHotel + @" Team
</div>
</td>
</tr>
<tr>
<td align='center' style='padding:18px 24px;background:#f8fafc;border-top:1px solid #e5e7eb;'>
<div style='font-size:11px;line-height:1.6;color:#8a98a7;'>
This rating link is securely connected to your completed stay and expires after 30 days.<br>
© " + year + @" " + safeHotel + @". All rights reserved.
</div>
</td>
</tr>
</table>
</td>
</tr>
</table>
</body>
</html>";
        }

        private static string BuildInvoiceLinkTemplate(string hotelName, string guestName, string invoiceUrl, string payUrl, string customMessage)
        {
            hotelName = string.IsNullOrWhiteSpace(hotelName) ? "Hotel" : hotelName.Trim();
            guestName = string.IsNullOrWhiteSpace(guestName) ? "Guest" : guestName.Trim();

            string safeHotel = HttpUtility.HtmlEncode(hotelName);
            string safeGuest = HttpUtility.HtmlEncode(guestName);

            // string safeInvoiceUrlAttr = HttpUtility.HtmlAttributeEncode(invoiceUrl ?? "");
            //string safeInvoiceUrlText = HttpUtility.HtmlEncode(invoiceUrl ?? "");

            string safePayUrlAttr = HttpUtility.HtmlAttributeEncode(payUrl ?? "");
            string safePayUrlText = HttpUtility.HtmlEncode(payUrl ?? "");

            string msg = (customMessage ?? "").Trim();
            msg = HttpUtility.HtmlEncode(msg).Replace("\r\n", "<br/>").Replace("\n", "<br/>");

            if (string.IsNullOrWhiteSpace(msg))
            {
                msg = "Hello " + safeGuest + ",<br/><br/>Please find your invoice below.";
            }

            string payBlock = "";
            //  if (!string.IsNullOrWhiteSpace(payUrl))
            //  {
            //      payBlock = @"
            //<div style='margin-top:14px;'>
            //  <a href='" + safePayUrlAttr + @"' style='display:inline-block;background:#16a34a;color:#fff;text-decoration:none;
            //    padding:12px 16px;border-radius:10px;font-weight:800;'>Pay Online</a>
            //  <div style='font-size:12px;color:#64748b;margin-top:8px;word-break:break-all;'>Pay link: " + safePayUrlText + @"</div>
            //</div>";
            //  }
            return @"<!doctype html>
<html><body style='font-family:Arial;background:#f5f7fb;margin:0;padding:0;'>
  <div style='max-width:640px;margin:22px auto;background:#fff;border-radius:14px;padding:18px;box-shadow:0 10px 30px rgba(0,0,0,.08);'>
    <h2 style='margin:0 0 6px 0;color:#0f172a;'>" + safeHotel + @"</h2>
    <div style='color:#334155;line-height:1.7;margin:10px 0 14px 0;'>" + msg + @"</div>
    <div style='margin-top:18px;color:#64748b;font-size:12px;'>
      Regards,<br/><b>" + safeHotel + @" Team</b>
    </div>
  </div>
</body></html>";
        }

        private static string BuildInvoicePdfTemplate(string hotelName, string guestName, string customMessage)
        {
            hotelName = string.IsNullOrWhiteSpace(hotelName) ? "Hotel" : hotelName.Trim();
            guestName = string.IsNullOrWhiteSpace(guestName) ? "Guest" : guestName.Trim();

            string safeHotel = HttpUtility.HtmlEncode(hotelName);
            string safeGuest = HttpUtility.HtmlEncode(guestName);

            string msg = (customMessage ?? "").Trim();
            msg = HttpUtility.HtmlEncode(msg).Replace("\r\n", "<br/>").Replace("\n", "<br/>");

            if (string.IsNullOrWhiteSpace(msg))
                msg = "Hello " + safeGuest + ",<br/><br/>Please find your invoice PDF attached.";

            return @"<!doctype html>
<html><body style='font-family:Arial;background:#f5f7fb;margin:0;padding:0;'>
  <div style='max-width:640px;margin:22px auto;background:#fff;border-radius:14px;padding:18px;box-shadow:0 10px 30px rgba(0,0,0,.08);'>
    <h2 style='margin:0 0 6px 0;color:#0f172a;'>" + safeHotel + @"</h2>
    <div style='color:#334155;line-height:1.7;margin:10px 0 14px 0;'>" + msg + @"</div>

    <div style='margin-top:14px;padding:12px;border:1px dashed #cbd5e1;border-radius:12px;background:#f8fafc;color:#334155;'>
      📎 Invoice PDF is attached with this email.
    </div>

    <div style='margin-top:18px;color:#64748b;font-size:12px;'>
      Regards,<br/><b>" + safeHotel + @" Team</b>
    </div>
  </div>
</body></html>";
        }


    }
}
