namespace AgentCore.Domain.Definitions;

public static class ConversationLanguagePolicy
{
    public const string Auto = "auto";
    public const int MaxTagLength = 35;

    public static bool IsAuto(string? language) =>
        string.Equals(language?.Trim(), Auto, StringComparison.OrdinalIgnoreCase);

    public static string Validate(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            throw new ArgumentException("language is required.");
        }

        if (IsAuto(language))
        {
            return Auto;
        }

        if (TryCanonicalFixedTag(language, out var canonical))
        {
            return canonical;
        }

        throw new ArgumentException("language must be 'auto' or a BCP-47-like language tag.");
    }

    public static string PromptInstruction(string language) =>
        IsAuto(language)
            ? "Respond in the same language as the user's current turn unless they explicitly request another language."
            : $"Respond in {language.Trim()}.";

    internal static bool TryCanonicalFixedTag(string? value, out string canonical)
    {
        canonical = "";
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        if (trimmed.Length is 0 or > MaxTagLength || trimmed.Contains('_', StringComparison.Ordinal))
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
