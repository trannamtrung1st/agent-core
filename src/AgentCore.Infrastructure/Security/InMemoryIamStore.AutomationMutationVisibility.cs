using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Security;
using AgentCore.Domain.Triggers;
namespace AgentCore.Infrastructure.Security;
public sealed partial class InMemoryIamStore
{
    private bool AutomationMutationApprovalVisible(TenantApprovalRequest request, TenantAutomationApprovalVisibility? visibility)
    {
        if (visibility is null || request.Operation.AutomationMutation is not { } value) return false;
        if (visibility.Agents is not null && !visibility.Agents.Contains(request.Operation.InstanceId)) return false;
        if (value.Current is { } current && (visibility.ReadAutomations is not null && !visibility.ReadAutomations.Contains(current.AutomationId)
            || automationBacking.ReadTenantAutomation(request.TenantId, current.AutomationId)?.Owner != current.Owner)) return false;
        if (value.ToolName is AgentCore.Application.Tools.ToolCatalog.AutomationDelete or AgentCore.Application.Tools.ToolCatalog.AutomationDisable) return true;
        if (value.Invocation is not null && (TenantAutomationConversationRules.Existing(value.Proposed) ? !visibility.CanCreateRuns : !visibility.CanCreate)
            || value.Invocation is not null && visibility.RunAutomations is not null && !visibility.RunAutomations.Contains(value.Proposed.AutomationId)) return false;
        foreach (var child in value.Invocation is null ? value.Proposed.Triggers : [])
            if (child.Configuration is EventTrigger webhook && visibility.WebhookSources is not null && !visibility.WebhookSources.Contains(webhook.EventId)
                || child.Configuration is CoreEventTrigger && visibility.CoreEvents is not null) return false;
        foreach (var id in new[] { value.Proposed.ExecutionTarget.SessionId, value.Proposed.CompletionDelivery.SessionId }.OfType<Guid>())
        {
            var owner = sessionBacking.ReadTenantSession(request.TenantId, id)?.Owner;
            if (owner?.AgentInstanceId != request.Operation.InstanceId || owner.ExecutorPrincipalId != request.ExecutorPrincipalId
                || visibility.ConversationSessions is not null && !visibility.ConversationSessions.Contains(id)
                || visibility.ChatAgents is not null && !visibility.ChatAgents.Contains(request.Operation.InstanceId)) return false;
        }
        return true;
    }
}
