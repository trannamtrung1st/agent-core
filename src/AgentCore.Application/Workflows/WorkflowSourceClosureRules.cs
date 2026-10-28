using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Domain.Triggers;
using AgentCore.Domain.Workflows;

namespace AgentCore.Application.Workflows;

/// <summary>Technical source lifecycle closure. It carries no human decision, permission grant or claimed Run.</summary>
public static class WorkflowSourceClosureRules
{
    public static async ValueTask<string?> ReasonAsync(WorkflowExecution execution, TenantSecurityState state,
        IIamStore security, ITenantSessionStore sessions, ITenantRunStore runs, DateTimeOffset now, CancellationToken ct)
    {
        if (state.Tenant.Status != IdentityStatus.Active) return "workflow_tenant_disabled";
        if (execution.Source.SessionId is { } id)
        {
            var source = await sessions.FindTenantSessionAsync(execution.TenantId, id, ct);
            if (source is null || SessionRetention.IsDue(source.Snapshot, now)) return "workflow_source_session_ended";
        }
        if (execution.Source.Kind == WorkflowInvocationKind.HumanManual
            && (execution.Source.AccountId is not { } account || await security.ReadAccountAsync(account, ct) is not { Status: IdentityStatus.Active }
                || !state.Users.Any(u => u.PrincipalId == execution.Source.PrincipalId && u.Status == IdentityStatus.Active)))
            return "workflow_source_identity_ended";
        if (execution.Source.Kind == WorkflowInvocationKind.AutomationOccurrence && security is ITenantAutomationStore automations)
        {
            var automation = await automations.FindTenantAutomationAsync(execution.TenantId, execution.Source.AutomationId!.Value, ct);
            if (automation is null) return "workflow_source_automation_ended";
        }
        if (execution.Source.Kind == WorkflowInvocationKind.ClaimedAgent && execution.Source.RunId is { } run
            && await runs.FindTenantRunAsync(execution.TenantId, run, ct) is null) return "workflow_source_session_ended";
        return null;
    }
}
