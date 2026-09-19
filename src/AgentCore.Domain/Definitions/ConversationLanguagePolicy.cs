namespace AgentCore.Domain.Definitions;

public static class ConversationLanguagePolicy
{
    public const string Auto = "auto";

    public static bool IsAuto(string? language) =>
        string.Equals(language, Auto, StringComparison.OrdinalIgnoreCase);

    public static string PromptInstruction(string language) =>
        IsAuto(language)
            ? "Respond in the same language as the user's current turn unless they explicitly request another language."
            : $"Respond in {language.Trim()}.";
}
