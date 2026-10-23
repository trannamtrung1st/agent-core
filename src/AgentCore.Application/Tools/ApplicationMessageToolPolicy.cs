using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Tools;

internal static class ApplicationMessageToolPolicy
{
    public static bool AllowsTenantRun(AgentRun run) => run.Owner.TenantId is not null
        && AllowsConversation(run.Admission.ExecutionContext, run.Admission.OutputContract, run.Admission.TaskIntent);
    public static bool AllowsConversation(TrustedExecutionContext? execution, AgentRunOutputContract output, AgentRunTaskIntent? intent) =>
        output == AgentRunOutputContract.ConversationResponse && intent?.Source != AgentRunTaskSource.Automation
        && execution is { OriginalInitiatorPrincipalId: not null,
            Origin: ExecutionOrigin.UserMessage or ExecutionOrigin.ApprovalContinuation };

    public static bool UnlocksIntermediateMessaging(string toolName) =>
        toolName switch
        {
            ToolCatalog.SkillsLoad or ToolCatalog.AppMessageSend => false,
            ToolCatalog.KnowledgeRetrieve or ToolCatalog.AttachmentsRead => true,
            ToolCatalog.SandboxExec or ToolCatalog.HttpRequest => true,
            ToolCatalog.ArtifactsCreate or ToolCatalog.ArtifactsCreateFromWorkspace or ToolCatalog.ArtifactsVerify => true,
            _ when toolName.StartsWith("workspace.", StringComparison.Ordinal) => true,
            _ when toolName.StartsWith("web.", StringComparison.Ordinal) => true,
            _ when toolName.StartsWith("email.", StringComparison.Ordinal) => true,
            _ when toolName.StartsWith("browser.", StringComparison.Ordinal) => true,
            _ => false
        };
}
