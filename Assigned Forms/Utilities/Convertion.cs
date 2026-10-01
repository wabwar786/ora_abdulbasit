using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Web;

namespace hotelsoftware.Utilities
{
    public class Convertion
    {
        public static string B64UrlEncode(string plain)
        {
            plain = plain ?? "";
            var bytes = Encoding.UTF8.GetBytes(plain);
            return Convert.ToBase64String(bytes)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        }

        public static string B64UrlDecode(string b64url)
        {
            if (string.IsNullOrWhiteSpace(b64url)) return "";

            string s = b64url.Replace('-', '+').Replace('_', '/');
            switch (s.Length % 4)
            {
                case 2: s += "=="; break;
                case 3: s += "="; break;
            }

            var bytes = Convert.FromBase64String(s);
            return Encoding.UTF8.GetString(bytes);
        }
    }
}