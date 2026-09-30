namespace AgentCore.Domain.Definitions;

public static class SkillCapabilities
{
    public const string ChatRespond = "chat.respond";
}

public sealed record SkillSpec(
    string Id,
    string Name,
    string Description,
    string Procedure,
    IReadOnlyList<string> ActivationKeywords,
    IReadOnlyList<string> RequiredCapabilities,
    IReadOnlyList<string> ResourcePaths);
