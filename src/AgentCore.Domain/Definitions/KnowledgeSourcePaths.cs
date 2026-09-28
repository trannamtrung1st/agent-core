namespace AgentCore.Domain.Definitions;

public static class KnowledgeSourcePaths
{
    public static string ResolveBackingPath(KnowledgeSourceRef source) =>
        ResolveBackingPath(source.Identity, source.ResourcePath);

    public static string ResolveBackingPath(string identity, string? resourcePath)
    {
        if (!string.IsNullOrWhiteSpace(resourcePath))
        {
            return resourcePath.Trim();
        }

        return $"knowledge/{identity}";
    }
}
