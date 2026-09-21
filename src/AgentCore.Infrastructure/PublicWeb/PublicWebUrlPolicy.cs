namespace AgentCore.Infrastructure.PublicWeb;

internal static class PublicWebUrlPolicy
{
    public static bool TryValidate(Uri uri, out string? errorCode, out string? errorMessage)
    {
        errorCode = null;
        errorMessage = null;
        if (!uri.IsAbsoluteUri)
        {
            return Reject("invalid_url", "URL must be absolute.", out errorCode, out errorMessage);
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            return Reject("unsupported_scheme", "Only http and https URLs are permitted.", out errorCode, out errorMessage);
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            return Reject("credentials_in_url", "Embedded credentials in URLs are not permitted.", out errorCode, out errorMessage);
        }

        if (!PublicAddressPolicy.IsAllowedHostName(uri.Host))
        {
            return Reject("forbidden_host", "Host is not permitted for public web fetch.", out errorCode, out errorMessage);
        }

        return true;
    }

    private static bool Reject(string code, string message, out string? errorCode, out string? errorMessage)
    {
        errorCode = code;
        errorMessage = message;
        return false;
    }
}
