using System.Data;
using System.Net;
using System.Net.Mail;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Orapmshms.Models;

namespace Orapmshms.Services;

public sealed class LoginService : ILoginService
{
    private const int LoginMaxAttempts = 5;
    private const int LoginLockMinutes = 15;
    private const int ForgotMaxAttempts = 3;
    private const int ForgotLockMinutes = 20;

    private readonly string _connectionString;
    private readonly IConfiguration _configuration;
    private readonly IMemoryCache _cache;
   // private readonly SmtpOptions _smtp;
    private readonly ILegacyUrlSigner _urlSigner;
    private readonly ILoginBackgroundQueue _backgroundQueue;
    private readonly ILogger<LoginService> _logger;

    public LoginService(
        IConfiguration configuration,
        IMemoryCache cache,
       // IOptions<SmtpOptions> smtp,
        ILegacyUrlSigner urlSigner,
        ILoginBackgroundQueue backgroundQueue,
        ILogger<LoginService> logger)
    {
        _configuration = configuration;
        _connectionString = configuration.GetConnectionString("con")
            ?? throw new InvalidOperationException("ConnectionStrings:con is missing from configuration.");
        _cache = cache;
       // _smtp = smtp.Value;
        _urlSigner = urlSigner;
        _backgroundQueue = backgroundQueue;
        _logger = logger;
    }

    public async Task<LoginResult> AuthenticateAsync(
        string loginText,
        string plainPassword,
        string clientIp,
        string? returnUrl,
        CancellationToken cancellationToken = default)
    {
        loginText = CleanInput(loginText, 256);
        plainPassword ??= string.Empty;
        clientIp = CleanInput(clientIp, 100);

        if (string.IsNullOrWhiteSpace(loginText))
            return Fail("Please enter username or email.");

        if (string.IsNullOrWhiteSpace(plainPassword))
            return Fail("Please enter password.");

        if (plainPassword.Length > 100)
            return Fail("User does not exist. Please insert the correct credentials.");

        if (IsTemporarilyBlocked("login", loginText, clientIp, LoginMaxAttempts))
            return Fail("Too many failed attempts. Please try again after a few minutes.");

        var encodedPassword = Convert.ToBase64String(Encoding.UTF8.GetBytes(plainPassword));

        // Match the latest Web Forms login implementation: authenticate the account and
        // resolve RolePagesTB.page_name in one SQL round trip.
        var user = await GetLoginUserAndLandingPageAsync(
            loginText,
            encodedPassword,
            cancellationToken);

        if (user is null)
        {
            RecordAttempt("login", loginText, clientIp, LoginLockMinutes);
            return Fail("User does not exist. Please insert the correct credentials.");
        }

        if (!string.Equals(user.ActiveStatus, "ACTIVE", StringComparison.Ordinal))
        {
            RecordAttempt("login", loginText, clientIp, LoginLockMinutes);
            return Fail("This account is expired.");
        }

        ClearAttempts("login", loginText, clientIp);

        // Latest Web Forms behavior: only queue the inactive-status maintenance when the
        // logged-in account is not council and its expiry string is exactly today's value.
        if (!string.Equals(user.Role, "council", StringComparison.Ordinal) &&
            string.Equals(user.ExpiryDate, DateTime.Now.ToString("MM-dd-yyyy"), StringComparison.Ordinal))
        {
            _backgroundQueue.QueueExpiryMaintenance(user.HotelId);
        }

        var defaultUrl = BuildLegacyLoginUrl(user.LandingPage, user);
        var safeReturnUrl = GetSafeReturnUrl(returnUrl, user);
        var redirectUrl = string.IsNullOrWhiteSpace(safeReturnUrl) ? defaultUrl : safeReturnUrl;

        // Preserve audit logging without storing the password (Base64 is not a security boundary).
        _backgroundQueue.QueueAudit(
            $"(Login Details),{loginText},{DateTime.Now}",
            user.UserName,
            clientIp,
            Environment.MachineName);

        // Same successful-login side effect as Web Forms, now executed by a hosted queue so
        // it cannot delay the Login response and duplicate concurrent triggers are prevented.
        _backgroundQueue.QueueAutoChargeWorker();

        return new LoginResult(true, string.Empty, user, redirectUrl);
    }

    public async Task<PasswordResetResult> SendPasswordResetLinkAsync(
        string email,
        string clientIp,
        CancellationToken cancellationToken = default)
    {
        email = CleanInput(email, 256);
        clientIp = CleanInput(clientIp, 100);

        if (string.IsNullOrWhiteSpace(email))
            return ResetFail("Please enter e-mail first.");

        if (!Regex.IsMatch(email, @"^[^@\s]+@[^@\s]+\.[^@\s]+$"))
            return ResetFail("Invalid email format. Please provide a valid email address.");

        if (IsTemporarilyBlocked("forgot", email, clientIp, ForgotMaxAttempts))
            return ResetFail("Too many reset requests. Please try again after a few minutes.");

        RecordAttempt("forgot", email, clientIp, ForgotLockMinutes);

        var user = await GetForgotPasswordUserAsync(email, cancellationToken);
        if (user is null)
            return ResetFail("Account not found.");

        //if (!HasSmtpConfiguration())
        //    return ResetFail("Email configuration is missing. Please contact administrator.");

        var resetBaseUrl = _configuration["Login:PasswordResetUrl"];
        if (string.IsNullOrWhiteSpace(resetBaseUrl))
            resetBaseUrl = "https://www.smartpmspro.com/forgotpassword.aspx";

        var base64Email = Convert.ToBase64String(Encoding.UTF8.GetBytes(email));
        var forgotPasswordUrl = QueryHelpers.AddQueryString(resetBaseUrl, "rd", base64Email);

        try
        {
            await SendPasswordResetEmailAsync(
                user.UserName,
                user.Email,
                forgotPasswordUrl,
                cancellationToken);

            var formattedDate = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            await InsertResetPasswordLogAsync(user.Email, clientIp, formattedDate, cancellationToken);

            _backgroundQueue.QueueAudit(
                $"(insert ResetPasswordRequest),{user.Email} , 1 , {formattedDate},{clientIp}",
                user.UserName,
                clientIp,
                Environment.MachineName);

            ClearAttempts("forgot", email, clientIp);

            return new PasswordResetResult(
                true,
                "A link has been sent to the provided email address. You can use this link to change your password.");
        }
        catch (SmtpException ex)
        {
            _logger.LogError(ex, "Forgot password SMTP failure for {Email}", email);
            return ResetFail("Failed to send email. Please contact administrator.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Forgot password processing failed for {Email}", email);
            return ResetFail("Something went wrong. Please try again later.");
        }
    }

    private async Task<LoginAccount?> GetLoginUserAndLandingPageAsync(
        string loginText,
        string encodedPassword,
        CancellationToken cancellationToken)
    {
        const string sql = @"
SELECT TOP (1)
       a.email,
       a.hotelname,
       a.user_id,
       a.username,
       a.hotel_id,
       a.role,
       a.hotel_role,
       a.expiry_date,
       a.activestatus,
       rp.page_name
FROM dbo.Hms_accounts a
OUTER APPLY
(
    SELECT TOP (1)
           r.page_name
    FROM dbo.RolePagesTB r
    WHERE r.role = a.role
      AND
      (
            a.role IN
            (
                'SuperUser',
                'hotel',
                'council',
                'House Keeper'
            )
            OR r.hotel_id = a.hotel_id
      )
) rp
WHERE
      (a.Email = @Login OR a.username = @Login)
  AND a.[password] = @Password;";

        await using var connection = new SqlConnection(_connectionString);
        await using var cmd = new SqlCommand(sql, connection)
        {
            CommandTimeout = 15
        };

        cmd.Parameters.Add("@Login", SqlDbType.NVarChar, 256).Value = loginText;
        cmd.Parameters.Add("@Password", SqlDbType.NVarChar, 512).Value = encodedPassword;

        await connection.OpenAsync(cancellationToken);
        await using var reader = await cmd.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        return new LoginAccount
        {
            Email = ReadString(reader, "email"),
            HotelName = ReadString(reader, "hotelname"),
            UserId = ReadString(reader, "user_id"),
            UserName = ReadString(reader, "username"),
            HotelId = ReadString(reader, "hotel_id"),
            Role = ReadString(reader, "role"),
            HotelRole = ReadString(reader, "hotel_role"),
            ExpiryDate = ReadString(reader, "expiry_date"),
            ActiveStatus = ReadString(reader, "activestatus"),
            LandingPage = ReadString(reader, "page_name")
        };
    }

    private async Task<ForgotPasswordUser?> GetForgotPasswordUserAsync(
        string email,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_connectionString);
        await using var cmd = new SqlCommand(@"
SELECT TOP (1)
    username,
    email
FROM dbo.Hms_accounts
WHERE Email = @Email;", connection)
        {
            CommandTimeout = 15
        };

        cmd.Parameters.Add("@Email", SqlDbType.NVarChar, 256).Value = email;
        await connection.OpenAsync(cancellationToken);
        await using var reader = await cmd.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        return new ForgotPasswordUser(
            ReadString(reader, "username"),
            ReadString(reader, "email"));
    }

    private string BuildLegacyLoginUrl(string pageName, LoginAccount user)
    {
        var cleanPageName = (pageName ?? string.Empty).Trim();
        if (cleanPageName.EndsWith(".aspx", StringComparison.OrdinalIgnoreCase))
            cleanPageName = cleanPageName[..^5];

        // Do not invent a landing page: preserve the RolePagesTB value used by Web Forms.
        // If the role has no page mapping this intentionally remains the same empty-page legacy URL.
        var query = new Dictionary<string, string?>
        {
            ["UD"] = ToBase64(user.UserId),
            ["UN"] = ToBase64(user.UserName),
            ["cc"] = string.Empty,
            ["vs"] = string.Empty,
            ["RS"] = string.Empty,
            ["hd"] = ToBase64(user.HotelId),
            ["rl"] = ToBase64(user.Role),
            ["hn"] = ToBase64(user.Email),
            ["hr"] = ToBase64(user.HotelRole)
        };

        var url = QueryHelpers.AddQueryString(cleanPageName + ".aspx", query);
        return _urlSigner.AddSignatureToUrl(url);
    }

    private string GetSafeReturnUrl(string? returnUrl, LoginAccount user)
    {
        if (string.IsNullOrWhiteSpace(returnUrl))
            return string.Empty;

        var decodedUrl = WebUtility.UrlDecode(returnUrl)?.Trim() ?? string.Empty;
        if (!IsLocalLegacyUrl(decodedUrl))
            return string.Empty;

        try
        {
            var normalized = decodedUrl.StartsWith('/') ? decodedUrl : "/" + decodedUrl;
            var fakeUri = new Uri("http://localhost" + normalized, UriKind.Absolute);
            var queryParams = QueryHelpers.ParseQuery(fakeUri.Query);

            var queryHd = queryParams.TryGetValue("hd", out var hd) ? hd.ToString() : string.Empty;
            var queryUd = queryParams.TryGetValue("UD", out var ud) ? ud.ToString() : string.Empty;

            if (!string.IsNullOrWhiteSpace(queryHd) &&
                !string.IsNullOrWhiteSpace(queryUd) &&
                string.Equals(queryHd, ToBase64(user.HotelId), StringComparison.Ordinal) &&
                string.Equals(queryUd, ToBase64(user.UserId), StringComparison.Ordinal))
            {
                return _urlSigner.AddSignatureToUrl(decodedUrl.TrimStart('/'));
            }
        }
        catch
        {
            return string.Empty;
        }

        return string.Empty;
    }

    private static bool IsLocalLegacyUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;
        if (url.StartsWith("//", StringComparison.Ordinal) || url.StartsWith("\\\\", StringComparison.Ordinal))
            return false;
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return false;

        return url.StartsWith("/", StringComparison.Ordinal) ||
               url.EndsWith(".aspx", StringComparison.OrdinalIgnoreCase) ||
               url.Contains(".aspx?", StringComparison.OrdinalIgnoreCase);
    }

    private async Task SendPasswordResetEmailAsync(
        string userName,
        string recipient,
        string resetUrl,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        //using var mailMessage = new MailMessage
        //{
        //    From = new MailAddress(_smtp.FromEmail, _smtp.FromName),
        //    Subject = "Reset Password",
        //    IsBodyHtml = true,
        //    DeliveryNotificationOptions = DeliveryNotificationOptions.OnFailure,
        //    Body =
        //        "We received a request to reset your account password. If you made this request, please click the link below to set a new password:<br><br>" +
        //        $"<a href='{WebUtility.HtmlEncode(resetUrl)}'>Reset Password</a><br><br>" +
        //        "For your security, this link is valid for one use only and will expire after 24hours. If you did not request a password reset, please ignore this email or contact our support team immediately.<br><br>" +
        //        "Thank you,<br>" +
        //        "Note: Please do not reply to this email. If you need assistance, visit our support page."
        //};
        //mailMessage.To.Add(new MailAddress(recipient, userName));

        //using var smtpClient = new SmtpClient(_smtp.Host, _smtp.Port)
        //{
        //    EnableSsl = _smtp.EnableSsl,
        //    DeliveryMethod = SmtpDeliveryMethod.Network,
        //    UseDefaultCredentials = false
        //};

        //if (!string.IsNullOrWhiteSpace(_smtp.UserName))
        //    smtpClient.Credentials = new NetworkCredential(_smtp.UserName, _smtp.Password);

        //await smtpClient.SendMailAsync(mailMessage);
    }

    private async Task InsertResetPasswordLogAsync(
        string email,
        string clientIp,
        string formattedDate,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_connectionString);
        await using var cmd = new SqlCommand(@"
INSERT INTO dbo.ResetPasswordRequest
(
    email,
    requeststatus,
    datetime,
    ip
)
VALUES
(
    @email,
    @request,
    @Date,
    @IP
);", connection)
        {
            CommandTimeout = 15
        };

        cmd.Parameters.Add("@email", SqlDbType.NVarChar, 256).Value = email;
        cmd.Parameters.Add("@request", SqlDbType.Int).Value = 1;
        cmd.Parameters.Add("@Date", SqlDbType.NVarChar, 50).Value = formattedDate;
        cmd.Parameters.Add("@IP", SqlDbType.NVarChar, 100).Value = clientIp;

        await connection.OpenAsync(cancellationToken);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    //private bool HasSmtpConfiguration()
    //{
    //    return !string.IsNullOrWhiteSpace(_smtp.Host) &&
    //           _smtp.Port > 0 &&
    //           !string.IsNullOrWhiteSpace(_smtp.FromEmail);
    //}

    private bool IsTemporarilyBlocked(string scope, string keyValue, string ip, int maxAttempts)
    {
        var key = AttemptKey(scope, keyValue, ip);
        return _cache.TryGetValue<int>(key, out var attempts) && attempts >= maxAttempts;
    }

    private void RecordAttempt(string scope, string keyValue, string ip, int minutes)
    {
        var key = AttemptKey(scope, keyValue, ip);
        _cache.TryGetValue<int>(key, out var attempts);
        _cache.Set(key, attempts + 1, TimeSpan.FromMinutes(minutes));
    }

    private void ClearAttempts(string scope, string keyValue, string ip)
    {
        _cache.Remove(AttemptKey(scope, keyValue, ip));
    }

    private static string AttemptKey(string scope, string value, string ip) =>
        $"auth:{scope}:{value.Trim().ToLowerInvariant()}:{ip}";

    private static string CleanInput(string? value, int maxLength)
    {
        var result = value?.Trim() ?? string.Empty;
        return result.Length <= maxLength ? result : result[..maxLength];
    }

    private static string ReadString(SqlDataReader reader, string columnName)
    {
        var ordinal = reader.GetOrdinal(columnName);
        return reader.IsDBNull(ordinal) ? string.Empty : Convert.ToString(reader.GetValue(ordinal))?.Trim() ?? string.Empty;
    }

    private static string ToBase64(string value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value ?? string.Empty));

    private static LoginResult Fail(string message) => new(false, message);
    private static PasswordResetResult ResetFail(string message) => new(false, message);

    private sealed record ForgotPasswordUser(string UserName, string Email);
}
