using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed partial class TenantApprovalService
{
    public async ValueTask<TenantApprovalPreparationResult> PrepareSandboxAsync(TenantRunReference reference, string profileId, SandboxCommandRequest command,
        long expectedRunRevision, Guid generation, CancellationToken ct = default)
    {
        try { return await PrepareSandboxCoreAsync(reference, profileId, command, expectedRunRevision, generation, ct).ConfigureAwait(false); }
        catch (Exception e) when (e is AccessDeniedException or SecurityConflictException)
        {
            await operations.RecordFailureAsync(reference, "sandbox.execute", e is AccessDeniedException denied ? denied.ReasonCode : ((SecurityConflictException)e).ReasonCode,
                ExecutionAuthorizationPurpose.ApprovalPreparation, ct).ConfigureAwait(false);
            throw;
        }
    }
    private async ValueTask<TenantApprovalPreparationResult> PrepareSandboxCoreAsync(TenantRunReference reference, string profileId, SandboxCommandRequest command,
        long expectedRunRevision, Guid generation, CancellationToken ct)
    {
        var (run, state) = await operations.ResolveForDecisionAsync(reference, "sandbox.execute", ExecutionAuthorizationPurpose.ApprovalPreparation, ct).ConfigureAwait(false);
        if (run.Status != AgentRunStatus.Running || run.Claim?.Generation != generation || run.Revision != expectedRunRevision || run.Claim.LeaseExpiresAtUtc <= time.GetUtcNow())
            throw new AccessDeniedException("tenant_runtime_unavailable");
        var target = await (sandboxTargets ?? throw new AccessDeniedException("tenant_effect_unavailable")).ResolveAsync(run, profileId, ct).ConfigureAwait(false);
        var pinned = TenantSandboxOperationRules.Profile(run, profileId);
        var current = InstanceSettingsResolver.Resolve(target.Baseline, target.Agent.Instance.SettingsOverrides);
        var home = StateIsolationPolicy.Restrict(run.Admission.Configuration!.Definition.StateIsolationPolicy ?? StateIsolationPolicy.Default,
            current.StateIsolationPolicy ?? StateIsolationPolicy.Default).HomeAccess;
        var parameters = TenantSandboxOperationRules.Parameters(run, pinned, target.Lease, command, home);
        TenantSandboxOperationRules.Require(parameters, run, target.Agent, target.Session, target.Baseline, target.Profile, target.Lease, time.GetUtcNow());
        var operation = TenantApprovalRules.PrepareSandbox(ids.NewId(), run.AgentInstanceId, parameters, target.Agent.Instance.Revision, target.Agent.Instance.PersonaRevision);
        return await PersistAsync(reference, run, state, target.Agent, operation, TenantSandboxOperationRules.Snapshot(target.Agent, pinned, target.Lease),
            false, expectedRunRevision, generation, ct).ConfigureAwait(false);
    }
}
