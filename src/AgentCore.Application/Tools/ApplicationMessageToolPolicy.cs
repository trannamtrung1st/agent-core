namespace AgentCore.Application.Tools;

internal static class ApplicationMessageToolPolicy
{
    public static bool UnlocksIntermediateMessaging(string toolName) =>
        toolName switch
        {
            ToolCatalog.SkillsLoad or ToolCatalog.AppMessageSend => false,
            ToolCatalog.KnowledgeRetrieve or ToolCatalog.AttachmentsRead => true,
            ToolCatalog.DemoSensitiveAction or ToolCatalog.SandboxRun or ToolCatalog.HttpRequest => true,
            ToolCatalog.ArtifactsCreate or ToolCatalog.ArtifactsCreateFromWorkspace or ToolCatalog.ArtifactsVerify => true,
            _ when toolName.StartsWith("workspace.", StringComparison.Ordinal) => true,
            _ when toolName.StartsWith("web.", StringComparison.Ordinal) => true,
            _ when toolName.StartsWith("email.", StringComparison.Ordinal) => true,
            _ when toolName.StartsWith("browser.", StringComparison.Ordinal) => true,
            _ => false
        };
}
