using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web;

namespace hotelsoftware.Utilities
{
    public static class EmailCrypto
    {
        // Additional entropy ties encryption to your application name (change if you want)
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ORA_PMS_EMAIL_SETTINGS_V1");
        // Encrypt plaintext -> Base64
        public static string EncryptToBase64(string plain)
        {
            if (string.IsNullOrEmpty(plain)) return "";
            var bytes = Encoding.UTF8.GetBytes(plain);
            var protectedBytes = ProtectedData.Protect(bytes, Entropy, DataProtectionScope.LocalMachine);
            return Convert.ToBase64String(protectedBytes);
        }
        // Decrypt Base64 -> plaintext
        public static string DecryptFromBase64(string base64)
        {
            if (string.IsNullOrEmpty(base64)) return "";
            var protectedBytes = Convert.FromBase64String(base64);
            var bytes = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.LocalMachine);
            return Encoding.UTF8.GetString(bytes);
        }
    }
}