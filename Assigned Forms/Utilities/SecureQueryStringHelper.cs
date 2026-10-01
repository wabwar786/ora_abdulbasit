using System;
using System.Collections.Specialized;
using System.Configuration;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web;
using System.Web.UI;

namespace hotelsoftware.Utilities
{
    public static class SecureQueryStringHelper
    {
        private static string SigningKey
        {
            get { return ConfigurationManager.AppSettings["QueryStringSigningKey"]; }
        }

        public static string EncodeBase64(string value)
        {
            if (value == null) value = "";
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
        }

        public static string DecodeBase64Safe(string value)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(value))
                    return "";

                return Encoding.UTF8.GetString(Convert.FromBase64String(value));
            }
            catch
            {
                return "";
            }
        }

        public static string AddSignatureToUrl(string url, int expiryMinutes = 480)
        {
            if (string.IsNullOrWhiteSpace(url))
                return url;

            if (string.IsNullOrWhiteSpace(SigningKey))
                throw new Exception("QueryStringSigningKey is missing in web.config.");

            string pagePath;
            NameValueCollection query;

            SplitUrl(url, out pagePath, out query);

            query.Remove("qsig");
            query.Remove("exp");

            long exp = DateTimeOffset.UtcNow.AddMinutes(expiryMinutes).ToUnixTimeSeconds();
            query["exp"] = exp.ToString();

            string canonical = BuildCanonicalString(pagePath, query);
            string signature = CreateHmac(canonical);

            query["qsig"] = signature;

            return pagePath + "?" + query.ToString();
        }

        public static bool IsValidSignature(HttpRequest request, out string reason)
        {
            reason = "";

            if (request == null)
            {
                reason = "Request missing.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(SigningKey))
            {
                reason = "Signing key missing.";
                return false;
            }

            string providedSignature = request.QueryString["qsig"];
            string expRaw = request.QueryString["exp"];

            if (string.IsNullOrWhiteSpace(providedSignature))
            {
                reason = "Signature missing.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(expRaw))
            {
                reason = "Expiry missing.";
                return false;
            }

            long exp;
            if (!long.TryParse(expRaw, out exp))
            {
                reason = "Invalid expiry.";
                return false;
            }

            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            if (now > exp)
            {
                reason = "Signature expired.";
                return false;
            }

            NameValueCollection query = HttpUtility.ParseQueryString(request.Url.Query.TrimStart('?'));
            query.Remove("qsig");

            string pagePath = request.Url.AbsolutePath;
            string canonical = BuildCanonicalString(pagePath, query);
            string expectedSignature = CreateHmac(canonical);

            if (!SlowEquals(providedSignature, expectedSignature))
            {
                reason = "Invalid signature.";
                return false;
            }

            return true;
        }

        public static bool RequireValidSignedInternalRequest(Page page, string pageNameForLog)
        {
            string reason;

            if (!IsValidSignature(page.Request, out reason))
            {
                Log_helper.Log(
                    "Security",
                    "Invalid Query Signature",
                    Convert.ToString(page.Session["hotel"]),
                    Convert.ToString(page.Session["UserId"]),
                    pageNameForLog + " | " + reason + " | " + page.Request.RawUrl
                );

                page.Response.Redirect("~/loginHMS.aspx", false);
                if (HttpContext.Current != null && HttpContext.Current.ApplicationInstance != null)
                {
                    HttpContext.Current.ApplicationInstance.CompleteRequest();
                }
                return false;
            }

            if (!IsSessionMatchingQuery(page))
            {
                Log_helper.Log(
                    "Security",
                    "Query Session Mismatch",
                    Convert.ToString(page.Session["hotel"]),
                    Convert.ToString(page.Session["UserId"]),
                    pageNameForLog + " | " + page.Request.RawUrl
                );

                page.Response.Redirect("~/loginHMS.aspx", false);
                if (HttpContext.Current != null && HttpContext.Current.ApplicationInstance != null)
                {
                    HttpContext.Current.ApplicationInstance.CompleteRequest();
                }
                return false;
            }

            return true;
        }

        public static bool IsSessionMatchingQuery(Page page)
        {
            string sessionHotelId = Convert.ToString(page.Session["hotel"]);
            string sessionUserId = Convert.ToString(page.Session["UserId"]);

            string queryHotelId = DecodeBase64Safe(page.Request.QueryString["hd"]);
            string queryUserId = DecodeBase64Safe(page.Request.QueryString["UD"]);

            if (string.IsNullOrWhiteSpace(sessionHotelId) || string.IsNullOrWhiteSpace(sessionUserId))
                return false;

            if (!string.IsNullOrWhiteSpace(queryHotelId) &&
                !string.Equals(sessionHotelId, queryHotelId, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(queryUserId) &&
                !string.Equals(sessionUserId, queryUserId, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return true;
        }

        private static void SplitUrl(string url, out string pagePath, out NameValueCollection query)
        {
            string[] parts = url.Split(new[] { '?' }, 2);

            pagePath = parts[0];

            string queryText = parts.Length > 1 ? parts[1] : "";
            query = HttpUtility.ParseQueryString(queryText);
        }

        private static string BuildCanonicalString(string pagePath, NameValueCollection query)
        {
            var keys = query.AllKeys
                .Where(k => !string.IsNullOrWhiteSpace(k))
                .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
                .ToList();

            StringBuilder sb = new StringBuilder();

            sb.Append(NormalizePagePath(pagePath));

            foreach (string key in keys)
            {
                sb.Append("|");
                sb.Append(key.Trim().ToLowerInvariant());
                sb.Append("=");
                sb.Append(query[key] ?? "");
            }

            return sb.ToString();
        }
        private static string NormalizePagePath(string pagePath)
        {
            if (string.IsNullOrWhiteSpace(pagePath))
                return "";

            pagePath = pagePath.Trim().Replace("\\", "/");

            if (pagePath.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                pagePath.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                Uri uri;
                if (Uri.TryCreate(pagePath, UriKind.Absolute, out uri))
                {
                    pagePath = uri.AbsolutePath;
                }
            }

            int queryIndex = pagePath.IndexOf("?", StringComparison.Ordinal);
            if (queryIndex >= 0)
            {
                pagePath = pagePath.Substring(0, queryIndex);
            }

            if (pagePath.StartsWith("~/", StringComparison.Ordinal))
            {
                pagePath = pagePath.Substring(2);
            }

            string appPath = "";

            try
            {
                if (HttpContext.Current != null &&
                    HttpContext.Current.Request != null &&
                    HttpContext.Current.Request.ApplicationPath != null)
                {
                    appPath = HttpContext.Current.Request.ApplicationPath.Trim();
                }
            }
            catch
            {
                appPath = "";
            }

            if (!string.IsNullOrWhiteSpace(appPath) && appPath != "/")
            {
                appPath = appPath.TrimEnd('/');

                if (pagePath.StartsWith(appPath + "/", StringComparison.OrdinalIgnoreCase))
                {
                    pagePath = pagePath.Substring(appPath.Length);
                }
            }

            pagePath = pagePath.TrimStart('/');

            return pagePath.ToLowerInvariant();
        }

        private static string CreateHmac(string text)
        {
            using (HMACSHA256 hmac = new HMACSHA256(Encoding.UTF8.GetBytes(SigningKey)))
            {
                byte[] hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(text ?? ""));

                return Convert.ToBase64String(hash)
                    .TrimEnd('=')
                    .Replace('+', '-')
                    .Replace('/', '_');
            }
        }

        private static bool SlowEquals(string a, string b)
        {
            if (a == null || b == null)
                return false;

            int diff = a.Length ^ b.Length;

            for (int i = 0; i < a.Length && i < b.Length; i++)
            {
                diff |= a[i] ^ b[i];
            }

            return diff == 0;
        }
    }
}