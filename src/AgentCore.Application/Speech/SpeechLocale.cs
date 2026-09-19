using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Speech;

public enum SpeechLocaleSource
{
    SessionOverride,
    AgentDefault,
    ProviderFallback
}

public sealed record SpeechLocaleResolution(
    string Effective,
    SpeechLocaleSource Source,
    string? Override);

public static class SpeechLocale
{
    public const string ProviderFallback = "en";
    public const int MaxLength = 35;

    public static SpeechLocaleResolution Resolve(SessionSnapshot snapshot, string? providerFallback = null) =>
        Resolve(snapshot.SpeechLocaleOverride, snapshot.Definition.ConversationPolicy.Language, providerFallback);

    public static SpeechLocaleResolution Resolve(
        string? sessionOverride,
        string? agentDefault,
        string? providerFallback = null)
    {
        if (TryCanonical(sessionOverride, out var overrideTag))
        {
            return new SpeechLocaleResolution(overrideTag, SpeechLocaleSource.SessionOverride, overrideTag);
        }

        if (!ConversationLanguagePolicy.IsAuto(agentDefault)
            && TryCanonical(agentDefault, out var agentTag))
        {
            return new SpeechLocaleResolution(agentTag, SpeechLocaleSource.AgentDefault, null);
        }

        var fallback = TryCanonical(providerFallback, out var providerTag) ? providerTag : ProviderFallback;
        return new SpeechLocaleResolution(fallback, SpeechLocaleSource.ProviderFallback, null);
    }

    public static string? NormalizeOverride(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return Validate(value);
    }

    public static string Validate(string value)
    {
        if (!TryCanonical(value, out var canonical))
        {
            throw AgentCoreErrors.Validation("speech locale must be a BCP-47-like language tag.");
        }

        return canonical;
    }

    public static string ToWire(SpeechLocaleSource source) =>
        source switch
        {
            SpeechLocaleSource.SessionOverride => "sessionOverride",
            SpeechLocaleSource.AgentDefault => "agentDefault",
            SpeechLocaleSource.ProviderFallback => "providerFallback",
            _ => "agentDefault"
        };

    public static bool TryCanonical(string? value, out string canonical)
    {
        canonical = "";
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        if (trimmed.Length is 0 or > MaxLength || trimmed.Contains('_', StringComparison.Ordinal))
        {
            return false;
        }

        var parts = trimmed.Split('-');
        if (parts.Length is 0 or > 8)
        {
            return false;
        }

        for (var index = 0; index < parts.Length; index++)
        {
            var part = parts[index];
            if (part.Length is < 1 or > 8)
            {
                return false;
            }

            for (var i = 0; i < part.Length; i++)
            {
                if (!char.IsAsciiLetterOrDigit(part[i]))
                {
                    return false;
                }
            }

            if (index == 0)
            {
                if (part.Length < 2 || !part.All(char.IsAsciiLetter))
                {
                    return false;
                }

                parts[index] = part.ToLowerInvariant();
                continue;
            }

            parts[index] = part.Length == 2 && part.All(char.IsAsciiLetter)
                ? part.ToUpperInvariant()
                : part.ToLowerInvariant();
        }

        canonical = string.Join('-', parts);
        return true;
    }
}
