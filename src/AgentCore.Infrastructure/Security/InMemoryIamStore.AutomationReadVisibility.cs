using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Security;
using AgentCore.Domain.Triggers;
namespace AgentCore.Infrastructure.Security;
public sealed partial class InMemoryIamStore
{
    private bool AutomationReadApprovalVisible(TenantApprovalRequest request, TenantAutomationApprovalVisibility? visibility)
    {
        if (visibility is null || request.Operation.AutomationRead is not { } read) return false;
        if (visibility.Agents is not null && !visibility.Agents.Contains(request.Operation.InstanceId)) return false;
        foreach (var source in TenantAutomationReadRules.Sources(read))
        {
            if (visibility.ReadAutomations is not null && !visibility.ReadAutomations.Contains(source.AutomationId)) return false;
            var current = automationBacking.ReadTenantAutomation(request.TenantId, source.AutomationId);
            if (current?.Owner != TriggerOwner.ForTenant(request.TenantId, request.Operation.InstanceId, request.ExecutorPrincipalId)) return false;
        }
        foreach (var source in TenantAutomationReadRules.Events(read))
        {
            if (source.Kind == TriggerSourceKind.CoreEvent ? visibility.CoreEvents is not null && !visibility.CoreEvents.Contains(source.EventId)
                : visibility.WebhookSources is not null && !visibility.WebhookSources.Contains(source.ResourceId!.Value)) return false;
            if (source.EmailAccountId is { } account && (visibility.EmailAccounts is not null && !visibility.EmailAccounts.Contains(account)
                || CredentialBacking().ReadTenantCredential(request.TenantId, account) is null)) return false;
        }
        return true;
    }
}
