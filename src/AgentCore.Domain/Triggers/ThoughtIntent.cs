namespace AgentCore.Domain.Triggers;

public static class ThoughtIntent
{
    public const int MaxRegistrationsPerInstance = 8;
    public const int MinIntervalSeconds = 3600;
    public const int MaxPromptCharacters = 2000;
    public static string Require(string value)
    {
        var text = value?.Trim();
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxPromptCharacters
            || text.Any(c => char.IsControl(c) && c is not '\n' and not '\r' and not '\t'))
            throw new ArgumentException("Thinking prompt must be 1..2000 characters.");
        return text;
    }
}
