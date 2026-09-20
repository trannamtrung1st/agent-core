using System.Text.RegularExpressions;

namespace AgentCore.Domain.Conversation;

internal static partial class UserProfileFieldPatterns
{
    internal static bool IsLanguageTag(string value) => LanguageTag().IsMatch(value);

    internal static bool IsLocaleTag(string value) => LocaleTag().IsMatch(value);

    internal static bool IsTimeZoneId(string value) => TimeZoneId().IsMatch(value);

    [GeneratedRegex("^[A-Za-z]{2,3}(-[A-Za-z0-9]{2,8})*$")]
    private static partial Regex LanguageTag();

    [GeneratedRegex("^[A-Za-z]{2,3}(-[A-Za-z0-9]{2,8})*$")]
    private static partial Regex LocaleTag();

    [GeneratedRegex("^(UTC|[A-Za-z]+(?:\\/[A-Za-z0-9_+\\-]+)+)$")]
    private static partial Regex TimeZoneId();
}
