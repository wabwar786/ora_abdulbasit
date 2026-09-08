namespace Orapmshms.Services;

public interface ILegacyUrlSigner
{
    string AddSignatureToUrl(string relativeUrl);
}
