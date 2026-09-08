using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;

namespace Orapmshms.Services;

/// <summary>
/// Keeps the legacy qsig hook isolated from the MVC login code.
/// If Security:QueryStringSigningKey is empty, the URL is returned unchanged,
/// which is useful while only the Login page has been migrated.
/// </summary>
public sealed class LegacyUrlSigner(IConfiguration configuration) : ILegacyUrlSigner
{
    public string AddSignatureToUrl(string relativeUrl)
    {
        if (string.IsNullOrWhiteSpace(relativeUrl))
            return relativeUrl;

        var keyText = configuration["Security:QueryStringSigningKey"]?.Trim();
        if (string.IsNullOrWhiteSpace(keyText))
            return relativeUrl;

        byte[] key;
        try
        {
            key = Convert.FromBase64String(keyText);
        }
        catch (FormatException)
        {
            key = Encoding.UTF8.GetBytes(keyText);
        }

        using var hmac = new HMACSHA256(key);
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(relativeUrl));
        var signature = WebEncoders.Base64UrlEncode(hash);
        var separator = relativeUrl.Contains('?') ? "&" : "?";
        return relativeUrl + separator + "qsig=" + Uri.EscapeDataString(signature);
    }
}
