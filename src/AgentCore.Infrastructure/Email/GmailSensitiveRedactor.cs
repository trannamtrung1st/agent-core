using System.Text.RegularExpressions;

namespace AgentCore.Infrastructure.Email;

public static partial class GmailSensitiveRedactor
{
    [GeneratedRegex(@"ya29\.[0-9A-Za-z\-_]+", RegexOptions.CultureInvariant)]
    private static partial Regex AccessTokenPattern();

    [GeneratedRegex(@"""access_token""\s*:\s*""[^""]+""", RegexOptions.CultureInvariant)]
    private static partial Regex JsonAccessTokenPattern();

    [GeneratedRegex(@"""refresh_token""\s*:\s*""[^""]+""", RegexOptions.CultureInvariant)]
    private static partial Regex JsonRefreshTokenPattern();

    [GeneratedRegex(@"Bearer\s+[0-9A-Za-z\-._~+/]+=*", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex BearerPattern();

    public static string Redact(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value ?? string.Empty;
        }

        var redacted = JsonRefreshTokenPattern().Replace(value, """"refresh_token":"[redacted]"""");
        redacted = JsonAccessTokenPattern().Replace(redacted, """"access_token":"[redacted]"""");
        redacted = BearerPattern().Replace(redacted, "Bearer [redacted]");
        redacted = AccessTokenPattern().Replace(redacted, "[redacted]");
        return redacted;
    }
}
