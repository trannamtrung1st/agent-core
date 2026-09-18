namespace AgentCore.Infrastructure.Providers;

internal static class SpeechLocaleCompatibility
{
    public static string PrimarySubtag(string locale)
    {
        var trimmed = locale.Trim();
        var dash = trimmed.IndexOf('-', StringComparison.Ordinal);
        return (dash > 0 ? trimmed[..dash] : trimmed).ToLowerInvariant();
    }

    public static bool Matches(string requested, string configured)
    {
        if (string.Equals(requested.Trim(), configured.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var requestPrimary = PrimarySubtag(requested);
        var configuredPrimary = PrimarySubtag(configured);
        return requestPrimary.Length > 0
            && string.Equals(requestPrimary, configuredPrimary, StringComparison.OrdinalIgnoreCase);
    }

    public static bool AnyMatch(string requested, IEnumerable<string> configured) =>
        configured.Any(item => Matches(requested, item));
}
