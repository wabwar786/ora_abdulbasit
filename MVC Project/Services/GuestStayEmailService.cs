using System.Data;
using System.Globalization;
using System.Net;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;

namespace Orapmshms.Services;

/// <summary>
/// Port of the WebForms EmailSender welcome / checkout notifications.
/// All sends happen AFTER the check-in/checkout transaction commits.
/// Uses the existing EmailSettingsTB + EmailSendLogTB (WELCOME / CHECKOUT).
/// Does not fabricate legacy Feedback.aspx tokens: those require the separate
/// WebForms HotelFeedbackLinkService, which is not part of this MVC project.
/// </summary>
public sealed class GuestStayEmailService
{
    private readonly string _connectionString;
    private readonly ILogger _logger;

    public GuestStayEmailService(string connectionString, ILogger logger)
    {
        _connectionString = connectionString;
        _logger = logger;
    }

    private sealed class Guest
    {
        public string Name { get; init; } = "Guest";
        public string Email { get; init; } = "";
        public string Room { get; init; } = "";
        public string Category { get; init; } = "";
        public string ArrivalDate { get; init; } = "";
        public string ArrivalTime { get; init; } = "";
        public string Status { get; init; } = "";
        public string CreatedRole { get; init; } = "";
        public string Agency { get; init; } = "";
        public string CategoryId { get; init; } = "";
    }

    private sealed class Hotel
    {
        public string Name { get; init; } = "Hotel";
        public string LogoUrl { get; init; } = "";
    }

    private sealed class SmtpSettings
    {
        public string FromEmail { get; init; } = "";
        public string FromName { get; init; } = "";
        public string Host { get; init; } = "";
        public int Port { get; init; }
        public bool Ssl { get; init; }
        public string User { get; init; } = "";
        public string Password { get; init; } = "";
    }

    public async Task<string> SendWelcomeOnceAsync(string hotelId, string regId,
        IReadOnlyCollection<string> checkedInRooms)
    {
        return await TrySendAsync(hotelId, regId, "WELCOME", checkedInRooms);
    }

    public async Task<string> SendCheckoutOnceAsync(string hotelId, string regId)
    {
        return await TrySendAsync(hotelId, regId, "CHECKOUT", Array.Empty<string>());
    }

    private async Task<string> TrySendAsync(string hotelId, string regId,
        string type, IReadOnlyCollection<string> checkedInRooms)
    {
        // Never propagate an email exception back into the committed reservation action.
        try
        {
            var guest = await GetGuestAsync(hotelId, regId);
            if (guest is null) return "Skipped: guest record missing";

            if (type == "WELCOME" && !IsCheckedIn(guest.Status))
            {
                await ClaimAsync(hotelId, regId, type, guest.Email, "Skipped", "Guest not checked in");
                return "Skipped: not checked in";
            }

            string reviewUrl = "";
            string reviewStatus = "";
            if (type == "CHECKOUT")
            {
                if (guest.CreatedRole.Equals("Channex", StringComparison.OrdinalIgnoreCase))
                {
                    if (!guest.Agency.Equals("bnbuk", StringComparison.OrdinalIgnoreCase))
                    {
                        await ClaimAsync(hotelId, regId, type, guest.Email, "Skipped", "Channex booking is not BNBUK");
                        return "Skipped: channel reservation excluded by legacy rule";
                    }
                    if (string.IsNullOrWhiteSpace(guest.CategoryId))
                    {
                        await ClaimAsync(hotelId, regId, type, guest.Email, "Skipped", "BNBUK category id missing");
                        return "Skipped: BNBUK category missing";
                    }
                    reviewUrl = "https://www.bnbuk.co.uk/review/" +
                        Base64Url(regId) + "/" + Base64Url(guest.CategoryId) + "/" + Base64Url(hotelId);
                }
                else
                {
                    // The WebForms path invokes HotelFeedbackLinkService to create a
                    // 30-day Feedback.aspx token. That service/schema was not supplied;
                    // the MVC project must never invent a link or expose an unprotected ID.
                    // Send a thank-you email without a feedback button for PMS bookings.
                    reviewStatus = " (without review link: legacy token service unavailable)";
                }
            }

            if (!ValidEmail(guest.Email))
            {
                await ClaimAsync(hotelId, regId, type, guest.Email, "Skipped", "Invalid or missing customer email");
                return "Skipped: guest email missing or invalid";
            }

            // Claim BEFORE SMTP. The transaction + UPDLOCK/HOLDLOCK makes this
            // idempotent even when the live table lacks a unique index.
            if (!await ClaimAsync(hotelId, regId, type, guest.Email, "Pending", "Preparing email"))
                return "Already processed";

            try
            {
                var (hotel, smtp) = await GetHotelAndSmtpAsync(hotelId);
                if (smtp is null || string.IsNullOrWhiteSpace(smtp.Host) || smtp.Port <= 0 || !ValidEmail(smtp.FromEmail))
                    throw new InvalidOperationException("No active/valid SMTP settings for this hotel or global fallback");

                string subject = type == "WELCOME"
                    ? "Check-in Details"
                    : "How was your stay at " + hotel.Name + "?";
                string html = type == "WELCOME"
                    ? BuildWelcomeHtml(hotel.Name, hotel.LogoUrl, guest,
                        string.Join(", ", checkedInRooms.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct()))
                    : BuildCheckoutHtml(hotel.Name, guest.Name, reviewUrl);

                using var msg = new MailMessage
                {
                    From = new MailAddress(smtp.FromEmail,
                        string.IsNullOrWhiteSpace(smtp.FromName) ? smtp.FromEmail : smtp.FromName),
                    Subject = subject,
                    Body = html,
                    IsBodyHtml = true
                };
                msg.To.Add(new MailAddress(guest.Email));
                using var client = new SmtpClient(smtp.Host, smtp.Port)
                {
                    EnableSsl = smtp.Ssl,
                    Credentials = new NetworkCredential(smtp.User, smtp.Password),
                    DeliveryMethod = SmtpDeliveryMethod.Network,
                    Timeout = 30000
                };
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await client.SendMailAsync(msg, timeout.Token);
                await SetStatusAsync(hotelId, regId, type, "Sent", "SMTP accepted message" + reviewStatus);
                return "Sent" + reviewStatus;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "{EmailType} email failed for hotel {HotelId} / booking {RegId}.", type, hotelId, regId);
                try { await SetStatusAsync(hotelId, regId, type, "Failed", ex.Message); }
                catch (Exception logEx) { _logger.LogError(logEx, "Unable to update email log for {RegId}.", regId); }
                return "Failed: " + ex.Message;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{EmailType} email preparation failed for {RegId}.", type, regId);
            return "Failed: email preparation error";
        }
    }

    private async Task<Guest?> GetGuestAsync(string hotelId, string regId)
    {
        await using var cn = new SqlConnection(_connectionString);
        await cn.OpenAsync(CancellationToken.None);
        await using var cmd = new SqlCommand(@"
SELECT TOP (1) GuestName,LastName,Email,room_no,room_category,ArrivalDate,ArrivalTime,
    res_status,createdRole,Agency,cetogory_id
FROM dbo.GuestInformationLogTB WHERE hotel_id=@hotel AND reg_id=@reg ORDER BY ID DESC;", cn);
        cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
        cmd.Parameters.Add("@reg", SqlDbType.VarChar, 100).Value = regId;
        await using var r = await cmd.ExecuteReaderAsync(CancellationToken.None);
        if (!await r.ReadAsync(CancellationToken.None)) return null;
        string S(string field) => Convert.ToString(r[field])?.Trim() ?? "";
        var name = (S("GuestName") + " " + S("LastName")).Trim();
        return new Guest
        {
            Name = name.Length > 0 ? name : "Guest",
            Email = S("Email"), Room = S("room_no"), Category = S("room_category"),
            ArrivalDate = S("ArrivalDate"), ArrivalTime = S("ArrivalTime"),
            Status = S("res_status"), CreatedRole = S("createdRole"),
            Agency = S("Agency"), CategoryId = S("cetogory_id")
        };
    }

    private async Task<(Hotel Hotel, SmtpSettings? Smtp)> GetHotelAndSmtpAsync(string hotelId)
    {
        await using var cn = new SqlConnection(_connectionString);
        await cn.OpenAsync(CancellationToken.None);
        Hotel hotel = new();
        await using (var cmd = new SqlCommand(@"
SELECT TOP (1) name,logo FROM dbo.HotelsSignUpTB WHERE hotel_id=@hotel;", cn))
        {
            cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
            await using var r = await cmd.ExecuteReaderAsync(CancellationToken.None);
            if (await r.ReadAsync(CancellationToken.None))
                hotel = new Hotel { Name = Convert.ToString(r["name"])?.Trim() is { Length: > 0 } n ? n : "Hotel",
                    LogoUrl = Convert.ToString(r["logo"])?.Trim() ?? "" };
        }
        await using (var cmd = new SqlCommand(@"
SELECT TOP (1) FromEmail,FromName,SmtpHost,SmtpPort,UseSsl,SmtpUser,SmtpPassEnc
FROM dbo.EmailSettingsTB WHERE IsActive=1 AND hotel_id IN (@hotel,'-1')
ORDER BY CASE WHEN hotel_id=@hotel THEN 0 ELSE 1 END, ID DESC;", cn))
        {
            cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
            await using var r = await cmd.ExecuteReaderAsync(CancellationToken.None);
            if (!await r.ReadAsync(CancellationToken.None)) return (hotel, null);
            string S(string field) => Convert.ToString(r[field])?.Trim() ?? "";
            var enc = S("SmtpPassEnc");
            // Same Windows DPAPI/LocalMachine entropy as WebForms EmailCrypto.cs.
            var pass = enc.Length == 0 ? "" : Encoding.UTF8.GetString(ProtectedData.Unprotect(
                Convert.FromBase64String(enc), Encoding.UTF8.GetBytes("ORA_PMS_EMAIL_SETTINGS_V1"),
                DataProtectionScope.LocalMachine));
            return (hotel, new SmtpSettings
            {
                FromEmail = S("FromEmail"), FromName = S("FromName"), Host = S("SmtpHost"),
                Port = int.TryParse(S("SmtpPort"), out var port) ? port : 0,
                Ssl = bool.TryParse(S("UseSsl"), out var ssl) && ssl,
                User = S("SmtpUser"), Password = pass
            });
        }
    }

    private async Task<bool> ClaimAsync(string hotelId, string regId, string type,
        string recipient, string status, string message)
    {
        await using var cn = new SqlConnection(_connectionString);
        await cn.OpenAsync(CancellationToken.None);
        await using var tx = (SqlTransaction)await cn.BeginTransactionAsync(IsolationLevel.Serializable, CancellationToken.None);
        try
        {
            await using var cmd = new SqlCommand(@"
IF NOT EXISTS (SELECT 1 FROM dbo.EmailSendLogTB WITH (UPDLOCK,HOLDLOCK)
    WHERE hotel_id=@hotel AND reg_id=@reg AND email_type=@type)
BEGIN
    INSERT INTO dbo.EmailSendLogTB (hotel_id,reg_id,email_type,ToEmail,Status,Message)
    VALUES (@hotel,@reg,@type,@to,@status,@msg);
    SELECT CAST(1 AS bit);
END
ELSE SELECT CAST(0 AS bit);", cn, tx);
            cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
            cmd.Parameters.Add("@reg", SqlDbType.VarChar, 100).Value = regId;
            cmd.Parameters.Add("@type", SqlDbType.VarChar, 30).Value = type;
            cmd.Parameters.Add("@to", SqlDbType.VarChar, 250).Value = recipient;
            cmd.Parameters.Add("@status", SqlDbType.VarChar, 30).Value = status;
            cmd.Parameters.Add("@msg", SqlDbType.NVarChar, 1000).Value = message;
            var created = Convert.ToBoolean(await cmd.ExecuteScalarAsync(CancellationToken.None));
            await tx.CommitAsync(CancellationToken.None);
            return created;
        }
        catch (SqlException ex) when (ex.Number is 2601 or 2627)
        {
            await tx.RollbackAsync(CancellationToken.None);
            return false;
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private async Task SetStatusAsync(string hotelId, string regId, string type, string status, string message)
    {
        await using var cn = new SqlConnection(_connectionString);
        await cn.OpenAsync(CancellationToken.None);
        await using var cmd = new SqlCommand(@"
UPDATE dbo.EmailSendLogTB SET Status=@status, Message=@msg
WHERE hotel_id=@hotel AND reg_id=@reg AND email_type=@type;", cn);
        cmd.Parameters.Add("@hotel", SqlDbType.VarChar, 50).Value = hotelId;
        cmd.Parameters.Add("@reg", SqlDbType.VarChar, 100).Value = regId;
        cmd.Parameters.Add("@type", SqlDbType.VarChar, 30).Value = type;
        cmd.Parameters.Add("@status", SqlDbType.VarChar, 30).Value = status;
        cmd.Parameters.Add("@msg", SqlDbType.NVarChar, 1000).Value = message.Length > 950 ? message[..950] : message;
        await cmd.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private static bool IsCheckedIn(string value) => (value ?? "").Trim().ToLowerInvariant() is "check in" or "checkin" or "checked in";
    private static bool ValidEmail(string value)
    {
        try { var mail = new MailAddress(value); return mail.Address == value && mail.Host.Contains('.'); }
        catch { return false; }
    }
    private static string Base64Url(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value.Trim()))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static string EscapeEmailHtml(string? value) =>
        WebUtility.HtmlEncode(value ?? string.Empty)
            .Replace("{", "&#123;", StringComparison.Ordinal)
            .Replace("}", "&#125;", StringComparison.Ordinal);

    private static string BuildWelcomeHtml(string hotelName, string hotelLogoUrl,
        Guest guest, string checkedInRooms)
    {
        var name = string.IsNullOrWhiteSpace(guest.Name) ? "Guest" : guest.Name;
        var room = checkedInRooms.Length > 0 ? checkedInRooms : guest.Room;
        var date = FormatLegacyDate(guest.ArrivalDate);
        const string template = """
        <!doctype html>
        <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"><meta name="x-apple-disable-message-reformatting"><title>CHECK-IN DETAILS — {{HotelName}}</title>
        <style>
          @media screen and (max-width:620px) {
            .email-wrap { width:100%!important; }
            .email-pad { padding-left:22px!important; padding-right:22px!important; }
            .mobile-block { display:block!important;width:100%!important;padding-right:0!important;box-sizing:border-box!important;padding-bottom:9px!important; }
            .mobile-title {font-size:26px!important;line-height:33px!important;}
            .outer-pad {padding:0!important;}
            .small-table th,.small-table td {font-size:11px!important;padding:10px 5px!important;}
          }
        </style></head>
        <body style="margin:0;padding:0;background:#EEF3F6;font-family:Arial,Helvetica,sans-serif;color:#142638;-webkit-text-size-adjust:100%;">
        <div style="display:none!important;font-size:1px;color:#f0f4f7;line-height:1px;opacity:0;max-height:0;max-width:0;overflow:hidden;mso-hide:all">Your check-in details for {{HotelName}}.</div>
        <table role="presentation" border="0" cellpadding="0" cellspacing="0" width="100%" style="background:#EEF3F6;border-collapse:collapse;"><tr><td class="outer-pad" style="padding:38px 14px 52px;" align="center">
        <table role="presentation" border="0" cellpadding="0" cellspacing="0" width="600" class="email-wrap" style="width:600px;max-width:600px;background:#fff;border-collapse:separate;border-spacing:0;border-radius:18px;overflow:hidden;box-shadow:0 12px 34px rgba(23,49,70,.07);text-align:left;">
        <tr><td style="height:5px;background:#2FAA8F;font-size:0;line-height:5px">&nbsp;</td></tr>
        <tr><td class="email-pad" style="background:#10273E;padding:28px 35px 35px;">
        <table width="100%" cellpadding="0" cellspacing="0" role="presentation"><tr><td style="vertical-align:middle"><p style="margin:0;color:white;font-size:20px;line-height:26px;font-family:Arial,Helvetica,sans-serif;font-weight:800;letter-spacing:-.4px">{{HotelName}}</p></td><td align="right" style="vertical-align:middle;color:#9DB8C8;font-family:Arial,Helvetica,sans-serif;font-size:10px;font-weight:700;letter-spacing:1.2px;white-space:nowrap">ORA PMS</td></tr></table>
        <div style="height:1px;background:#2E465B;margin:22px 0 27px"></div>
        <p style="margin:0 0 9px;color:#7DDBBE;letter-spacing:1.8px;font-weight:800;font-size:11px;line-height:16px;text-transform:uppercase">CHECK-IN DETAILS</p>
        <h1 class="mobile-title" style="margin:0;color:#fff;font-family:Arial,Helvetica,sans-serif;font-size:30px;line-height:39px;letter-spacing:-.8px;font-weight:800">Welcome to your stay.</h1>
        <p style="margin:12px 0 0;color:#C7D7E2;font-size:14px;line-height:22px">Everything you need for your arrival, in one place.</p>
        </td></tr>
        <tr><td class="email-pad" style="padding:30px 35px 35px;background:#fff;">
        <p style="margin:0 0 9px;font-size:16px;font-weight:700;color:#142638">Welcome, {{GuestName}}.</p>
        <p style="margin:0 0 24px;color:#617386;font:14px/23px Arial,Helvetica,sans-serif">We look forward to hosting you at <strong style="color:#142638">{{HotelName}}</strong>. Here are your check-in details.</p>
        <table role="presentation" cellpadding="0" cellspacing="0" width="100%" style="width:100%;border-collapse:collapse;"><tr><td class="mobile-block" width="50%" style="width:50%;vertical-align:top;padding:0 5px 0 0"><div style="background:#F4F8FA;border-radius:12px;padding:17px 18px;min-height:67px;"><div style="font-family:Arial,Helvetica,sans-serif;font-size:11px;line-height:16px;font-weight:700;letter-spacing:1.1px;text-transform:uppercase;color:#60768b;">Check-in date</div><div style="margin-top:7px;font-family:Arial,Helvetica,sans-serif;font-size:16px;line-height:24px;font-weight:700;color:#122e45;">{{CheckInDate}}</div></div></td><td class="mobile-block" width="50%" style="width:50%;vertical-align:top;padding:0 5px 0 0"><div style="background:#F4F8FA;border-radius:12px;padding:17px 18px;min-height:67px;"><div style="font-family:Arial,Helvetica,sans-serif;font-size:11px;line-height:16px;font-weight:700;letter-spacing:1.1px;text-transform:uppercase;color:#60768b;">Arrival time</div><div style="margin-top:7px;font-family:Arial,Helvetica,sans-serif;font-size:16px;line-height:24px;font-weight:700;color:#122e45;">{{CheckInTime}}</div></div></td></tr></table>
        <div style="height:15px"></div>
        <table role="presentation" width="100%" cellspacing="0" cellpadding="0" style="background:#EAF8F3;border:1px solid #CDE8DC;border-radius:12px;border-collapse:separate;"><tr><td style="padding:19px 22px"><div style="font-family:Arial,Helvetica,sans-serif;font-size:11px;line-height:16px;font-weight:700;letter-spacing:1.1px;text-transform:uppercase;color:#60768b;">Assigned room</div><div style="margin-top:7px;font-family:Arial,Helvetica,sans-serif;font-size:16px;line-height:24px;font-weight:700;color:#122e45;">{{RoomNo}}</div><div style="margin-top:4px;color:#4B716C;font-size:13px">{{RoomType}}</div></td></tr></table>
        <div style="margin-top:26px;padding-top:21px;border-top:1px solid #E2EAF0"><p style="margin:0;font:13px/22px Arial,Helvetica,sans-serif;color:#617386">We wish you a comfortable and pleasant stay.<br><br>Warm regards,<br><strong style="color:#142638">{{HotelName}} Team</strong></p></div>
        </td></tr>
        <tr><td class="email-pad" style="background:#F6F9FB;padding:22px 35px 26px;border-top:1px solid #E7EEF3;">
        <p style="font:12px/19px Arial,Helvetica,sans-serif;color:#627789;margin:0 0 8px">This is an automated email from {{HotelName}}.</p>
        <p style="font:11px/18px Arial,Helvetica,sans-serif;color:#8CA0AE;margin:0">&copy; {{Year}} {{HotelName}} &nbsp;&middot;&nbsp; Powered by ORA PMS</p>
        </td></tr>
        </table>
        </td></tr></table></body></html>
        """;
        return template
            .Replace("{{HotelName}}", EscapeEmailHtml(hotelName), StringComparison.Ordinal)
            .Replace("{{GuestName}}", EscapeEmailHtml(name), StringComparison.Ordinal)
            .Replace("{{CheckInDate}}", EscapeEmailHtml(date), StringComparison.Ordinal)
            .Replace("{{CheckInTime}}", EscapeEmailHtml(guest.ArrivalTime), StringComparison.Ordinal)
            .Replace("{{RoomNo}}", EscapeEmailHtml(room.Length > 0 ? room : "-"), StringComparison.Ordinal)
            .Replace("{{RoomType}}", EscapeEmailHtml(guest.Category), StringComparison.Ordinal)
            .Replace("{{Year}}", DateTime.UtcNow.Year.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    private static string FormatLegacyDate(string date)
    {
        if (DateTime.TryParse(date, CultureInfo.GetCultureInfo("en-GB"), DateTimeStyles.None, out var parsed))
            return parsed.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
        return date;
    }

    private static string BuildCheckoutHtml(string hotelName, string guestName, string reviewUrl)
    {
        // Existing policy: only generated BNBUK review URLs are passed to this
        // method; other reservations get a thank-you with no review button.
        const string withoutReviewHtml = """
        <!doctype html>
        <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"><meta name="x-apple-disable-message-reformatting"><title>THANK YOU FOR STAYING — {{HotelName}}</title>
        <style>
          @media screen and (max-width:620px) {
            .email-wrap { width:100%!important; }
            .email-pad { padding-left:22px!important; padding-right:22px!important; }
            .mobile-block { display:block!important;width:100%!important;padding-right:0!important;box-sizing:border-box!important;padding-bottom:9px!important; }
            .mobile-title {font-size:26px!important;line-height:33px!important;}
            .outer-pad {padding:0!important;}
            .small-table th,.small-table td {font-size:11px!important;padding:10px 5px!important;}
          }
        </style></head>
        <body style="margin:0;padding:0;background:#EEF3F6;font-family:Arial,Helvetica,sans-serif;color:#142638;-webkit-text-size-adjust:100%;">
        <div style="display:none!important;font-size:1px;color:#f0f4f7;line-height:1px;opacity:0;max-height:0;max-width:0;overflow:hidden;mso-hide:all">Thank you for staying with {{HotelName}}.</div>
        <table role="presentation" border="0" cellpadding="0" cellspacing="0" width="100%" style="background:#EEF3F6;border-collapse:collapse;"><tr><td class="outer-pad" style="padding:38px 14px 52px;" align="center">
        <table role="presentation" border="0" cellpadding="0" cellspacing="0" width="600" class="email-wrap" style="width:600px;max-width:600px;background:#fff;border-collapse:separate;border-spacing:0;border-radius:18px;overflow:hidden;box-shadow:0 12px 34px rgba(23,49,70,.07);text-align:left;">
        <tr><td style="height:5px;background:#2FAA8F;font-size:0;line-height:5px">&nbsp;</td></tr>
        <tr><td class="email-pad" style="background:#10273E;padding:28px 35px 35px;">
        <table width="100%" cellpadding="0" cellspacing="0" role="presentation"><tr><td style="vertical-align:middle"><p style="margin:0;color:white;font-size:20px;line-height:26px;font-family:Arial,Helvetica,sans-serif;font-weight:800;letter-spacing:-.4px">{{HotelName}}</p></td><td align="right" style="vertical-align:middle;color:#9DB8C8;font-family:Arial,Helvetica,sans-serif;font-size:10px;font-weight:700;letter-spacing:1.2px;white-space:nowrap">ORA PMS</td></tr></table>
        <div style="height:1px;background:#2E465B;margin:22px 0 27px"></div>
        <p style="margin:0 0 9px;color:#7DDBBE;letter-spacing:1.8px;font-weight:800;font-size:11px;line-height:16px;text-transform:uppercase">THANK YOU FOR STAYING</p>
        <h1 class="mobile-title" style="margin:0;color:#fff;font-family:Arial,Helvetica,sans-serif;font-size:30px;line-height:39px;letter-spacing:-.8px;font-weight:800">Until next time.</h1>
        <p style="margin:12px 0 0;color:#C7D7E2;font-size:14px;line-height:22px">We hope you enjoyed your visit.</p>
        </td></tr>
        <tr><td class="email-pad" style="padding:30px 35px 35px;background:#fff;">
        <p style="margin:0 0 9px;font-size:17px;font-weight:800;color:#142638">Dear {{GuestName}},</p>
        <p style="margin:0;color:#617386;font:14px/25px Arial,Helvetica,sans-serif">Thank you for choosing <strong style="color:#142638">{{HotelName}}</strong>. We hope your stay was comfortable and memorable.</p>
        
        <div style="margin-top:27px;padding-top:22px;border-top:1px solid #E2EAF0"><p style="margin:0;font:13px/23px Arial,Helvetica,sans-serif;color:#617386">We hope to welcome you again soon.<br><br>Warm regards,<br><strong style="color:#142638">{{HotelName}} Team</strong></p></div>
        </td></tr>
        <tr><td class="email-pad" style="background:#F6F9FB;padding:22px 35px 26px;border-top:1px solid #E7EEF3;">
        <p style="font:12px/19px Arial,Helvetica,sans-serif;color:#627789;margin:0 0 8px">This is an automated email from {{HotelName}}.</p>
        <p style="font:11px/18px Arial,Helvetica,sans-serif;color:#8CA0AE;margin:0">&copy; {{Year}} {{HotelName}} &nbsp;&middot;&nbsp; Powered by ORA PMS</p>
        </td></tr>
        </table>
        </td></tr></table></body></html>
        """;
        const string withReviewHtml = """
        <!doctype html>
        <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"><meta name="x-apple-disable-message-reformatting"><title>THANK YOU FOR STAYING — {{HotelName}}</title>
        <style>
          @media screen and (max-width:620px) {
            .email-wrap { width:100%!important; }
            .email-pad { padding-left:22px!important; padding-right:22px!important; }
            .mobile-block { display:block!important;width:100%!important;padding-right:0!important;box-sizing:border-box!important;padding-bottom:9px!important; }
            .mobile-title {font-size:26px!important;line-height:33px!important;}
            .outer-pad {padding:0!important;}
            .small-table th,.small-table td {font-size:11px!important;padding:10px 5px!important;}
          }
        </style></head>
        <body style="margin:0;padding:0;background:#EEF3F6;font-family:Arial,Helvetica,sans-serif;color:#142638;-webkit-text-size-adjust:100%;">
        <div style="display:none!important;font-size:1px;color:#f0f4f7;line-height:1px;opacity:0;max-height:0;max-width:0;overflow:hidden;mso-hide:all">We would love to hear about your stay at {{HotelName}}.</div>
        <table role="presentation" border="0" cellpadding="0" cellspacing="0" width="100%" style="background:#EEF3F6;border-collapse:collapse;"><tr><td class="outer-pad" style="padding:38px 14px 52px;" align="center">
        <table role="presentation" border="0" cellpadding="0" cellspacing="0" width="600" class="email-wrap" style="width:600px;max-width:600px;background:#fff;border-collapse:separate;border-spacing:0;border-radius:18px;overflow:hidden;box-shadow:0 12px 34px rgba(23,49,70,.07);text-align:left;">
        <tr><td style="height:5px;background:#2FAA8F;font-size:0;line-height:5px">&nbsp;</td></tr>
        <tr><td class="email-pad" style="background:#10273E;padding:28px 35px 35px;">
        <table width="100%" cellpadding="0" cellspacing="0" role="presentation"><tr><td style="vertical-align:middle"><p style="margin:0;color:white;font-size:20px;line-height:26px;font-family:Arial,Helvetica,sans-serif;font-weight:800;letter-spacing:-.4px">{{HotelName}}</p></td><td align="right" style="vertical-align:middle;color:#9DB8C8;font-family:Arial,Helvetica,sans-serif;font-size:10px;font-weight:700;letter-spacing:1.2px;white-space:nowrap">ORA PMS</td></tr></table>
        <div style="height:1px;background:#2E465B;margin:22px 0 27px"></div>
        <p style="margin:0 0 9px;color:#7DDBBE;letter-spacing:1.8px;font-weight:800;font-size:11px;line-height:16px;text-transform:uppercase">THANK YOU FOR STAYING</p>
        <h1 class="mobile-title" style="margin:0;color:#fff;font-family:Arial,Helvetica,sans-serif;font-size:30px;line-height:39px;letter-spacing:-.8px;font-weight:800">Until next time.</h1>
        <p style="margin:12px 0 0;color:#C7D7E2;font-size:14px;line-height:22px">We hope you enjoyed your visit.</p>
        </td></tr>
        <tr><td class="email-pad" style="padding:30px 35px 35px;background:#fff;">
        <p style="margin:0 0 9px;font-size:17px;font-weight:800;color:#142638">Dear {{GuestName}},</p>
        <p style="margin:0;color:#617386;font:14px/25px Arial,Helvetica,sans-serif">Thank you for choosing <strong style="color:#142638">{{HotelName}}</strong>. We hope your stay was comfortable and memorable.</p>
        <div style="padding:20px 22px;border:1px solid #D7EDE5;border-radius:12px;background:#F1FAF6;margin:26px 0"><p style="font-size:15px;font-weight:800;color:#142638;margin:0 0 8px">We'd love your feedback</p><p style="margin:0 0 19px;color:#617386;font:13px/21px Arial,Helvetica,sans-serif">Your experience helps us make every stay better.</p><table role="presentation" cellspacing="0" cellpadding="0" style="border-collapse:separate;"><tr><td style="border-radius:9px;background:#2FAA8F;text-align:center"><a href="{{ReviewUrl}}" style="display:inline-block;text-decoration:none;border:1px solid #2FAA8F;border-radius:9px;color:#fff;font:700 14px Arial,Helvetica,sans-serif;padding:15px 24px;">Share your stay experience &nbsp;&rsaquo;</a></td></tr></table></div>
        <div style="margin-top:27px;padding-top:22px;border-top:1px solid #E2EAF0"><p style="margin:0;font:13px/23px Arial,Helvetica,sans-serif;color:#617386">We hope to welcome you again soon.<br><br>Warm regards,<br><strong style="color:#142638">{{HotelName}} Team</strong></p></div>
        </td></tr>
        <tr><td class="email-pad" style="background:#F6F9FB;padding:22px 35px 26px;border-top:1px solid #E7EEF3;">
        <p style="font:12px/19px Arial,Helvetica,sans-serif;color:#627789;margin:0 0 8px">This is an automated email from {{HotelName}}.</p>
        <p style="font:11px/18px Arial,Helvetica,sans-serif;color:#8CA0AE;margin:0">&copy; {{Year}} {{HotelName}} &nbsp;&middot;&nbsp; Powered by ORA PMS</p>
        </td></tr>
        </table>
        </td></tr></table></body></html>
        """;
        var template = reviewUrl.Length > 0 ? withReviewHtml : withoutReviewHtml;
        return template
            .Replace("{{HotelName}}", EscapeEmailHtml(hotelName), StringComparison.Ordinal)
            .Replace("{{GuestName}}", EscapeEmailHtml(string.IsNullOrWhiteSpace(guestName) ? "Guest" : guestName), StringComparison.Ordinal)
            .Replace("{{ReviewUrl}}", EscapeEmailHtml(reviewUrl), StringComparison.Ordinal)
            .Replace("{{Year}}", DateTime.UtcNow.Year.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }
}
