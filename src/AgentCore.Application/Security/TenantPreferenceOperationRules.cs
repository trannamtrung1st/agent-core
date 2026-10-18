using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public static class TenantPreferenceOperationRules
{
    public static AgentInstance Apply(AgentRun run, TenantAgent target, TenantPreferenceCommit commit, DateTimeOffset at)
    {
        TenantLocalEffectRules.RequireDirectRun(run, commit.Generation, at);
        if (run.AgentRunId != commit.RunId || run.Revision != commit.ExpectedRunRevision || target.TenantId != run.Owner.TenantId
            || target.Instance.InstanceId != run.AgentInstanceId || target.PrincipalId != run.Owner.ExecutorPrincipalId
            || commit.OperationId == Guid.Empty || string.IsNullOrWhiteSpace(commit.SourceCallId) || commit.SourceCallId.Length > 200
            || commit.ExpectedInstanceRevision != target.Instance.Revision) throw new SecurityConflictException("operation_mismatch");
        var patch = TenantPreferenceToolContract.Parse(commit.ArgumentsJson);
        if (patch.ParametersHash != commit.ParametersHash || commit.ActionHash != TenantPreferenceToolContract.ActionHash(run.AgentInstanceId, target.Instance.Revision, patch.ParametersHash))
            throw new SecurityConflictException("operation_mismatch");
        var pinned = run.Admission.Configuration!.Definition;
        if (!TenantPreferenceToolContract.Allows(pinned) || !ManagementPolicyResolver.Resolve(pinned, target.Instance).AutoApplies(ManagementAutoApply.ConversationPreferences)
            || !ManagementPolicyResolver.Resolve(pinned, target.Instance).Allows(HarnessManagementScope.Settings)) throw new AccessDeniedException("management_unavailable");
        var settings = InstanceSettingsResolver.Patch(target.Instance.SettingsOverrides ?? new(), "conversationPolicy", patch.Set, patch.Clear);
        return TenantAgentConfigurationRules.Apply(target.Instance,
            new(target.Instance.InstanceId, target.Instance.Revision, SetSettingsOverrides: true, SettingsOverrides: settings, AutomationPolicyChanges: []), at);
    }
    public static void Transaction(TenantSecurityState next, long expected, AgentRun run, AuthorizationAudit audit)
    {
        if (next.Tenant.TenantId != run.Owner.TenantId || next.Tenant.Revision != checked(expected + 1) || audit.TenantId != next.Tenant.TenantId
            || audit.ActorPrincipalId != run.Owner.ExecutorPrincipalId || audit.Action != "operations.preferences.apply" || audit.Target != run.AgentInstanceId.ToString("D")
            || audit.Decision != "allowed" || audit.ReasonCode != "operation_completed") throw new ArgumentException("Invalid direct preference transaction.");
    }
}
