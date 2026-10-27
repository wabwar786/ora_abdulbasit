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
    private static string H(string value) => WebUtility.HtmlEncode(value ?? "");

    private static string BuildWelcomeHtml(string hotelName, string hotelLogoUrl, Guest guest, string checkedInRooms)
    {
        var name = string.IsNullOrWhiteSpace(guest.Name) ? "Guest" : guest.Name;
        var room = checkedInRooms.Length > 0 ? checkedInRooms : guest.Room;
        var date = FormatLegacyDate(guest.ArrivalDate);
        var time = guest.ArrivalTime;
        // The HTML below is ported from EmailSender.BuildWelcomeTemplate_YourHtml.
        const string template = @"<!doctype html>
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
        return template.Replace("{{HotelName}}", H(hotelName))
            .Replace("{{HotelLogoUrl}}", H(hotelLogoUrl))
            .Replace("{{GuestName}}", H(name))
            .Replace("{{CheckInDate}}", H(date))
            .Replace("{{CheckInTime}}", H(time))
            .Replace("{{RoomNo}}", H(room.Length > 0 ? room : "-"))
            .Replace("{{RoomType}}", H(guest.Category))
            .Replace("{{Year}}", DateTime.UtcNow.Year.ToString(CultureInfo.InvariantCulture));
    }

    private static string FormatLegacyDate(string date)
    {
        if (DateTime.TryParse(date, CultureInfo.GetCultureInfo("en-GB"), DateTimeStyles.None, out var parsed))
            return parsed.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
        return date;
    }

    private static string BuildCheckoutHtml(string hotelName, string guestName, string reviewUrl)
    {
        var title = H(hotelName);
        var name = H(string.IsNullOrWhiteSpace(guestName) ? "Guest" : guestName);
        var feedback = reviewUrl.Length > 0
            ? $"<p style='padding:18px 0'><a href='{H(reviewUrl)}' style='background:#0d2742;color:#fff;padding:14px 22px;border-radius:9px;text-decoration:none;font-weight:bold'>Share your stay experience</a></p>"
            : string.Empty;
        return $"""
<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"></head>
<body style="background:#eef3f8;margin:0;padding:28px 12px;font-family:Arial,Helvetica,sans-serif;color:#0d2742">
<table role="presentation" style="width:100%;max-width:600px;margin:0 auto;background:white;border-radius:16px;overflow:hidden;border-collapse:collapse">
<tr><td style="background:#0d2742;color:#fff;text-align:center;padding:30px 22px"><div style="font-size:24px;font-weight:bold">{title}</div><div style="font-size:12px;margin-top:8px">THANK YOU FOR YOUR STAY</div></td></tr>
<tr><td style="padding:30px"><h2 style="margin:0">Dear {name},</h2><p style="line-height:1.6;color:#475569">Thank you for choosing {title}. We hope your stay was comfortable and memorable.</p>{feedback}<p>Warm regards,<br><strong>{title} Team</strong></p></td></tr>
<tr><td style="padding:16px;text-align:center;color:#64748b;background:#f8fafc;font-size:12px">&copy; {DateTime.UtcNow.Year} {title}</td></tr>
</table></body></html>
""";
    }
}
