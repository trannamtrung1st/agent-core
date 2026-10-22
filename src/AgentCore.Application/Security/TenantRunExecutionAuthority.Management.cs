using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Security;

public sealed partial class TenantRunExecutionAuthority
{
    public async ValueTask<AgentCore.Application.Ports.TenantSkillProjection> CurrentSkillContextAsync(AgentRun run, CancellationToken ct = default)
    {
        _ = await ResolveCurrentAsync(run, ct).ConfigureAwait(false);
        return await (sourceAuthority ?? throw new AccessDeniedException("skill_context_unavailable")).SkillContextAsync(run, ct).ConfigureAwait(false);
    }
    public async ValueTask<(AgentCore.Application.Ports.TenantManagementReadObservation Observation, OperationAuthorizationDecision Decision)> PrepareSkillLoadAsync(AgentRun run, IReadOnlyList<string> ids, CancellationToken ct = default)
    {
        _ = await ResolveCurrentAsync(run, ct).ConfigureAwait(false);
        return await (sourceAuthority ?? throw new AccessDeniedException("skill_context_unavailable")).SkillLoadAsync(run, ids, ct).ConfigureAwait(false);
    }
    // Eligibility metadata only. This does not read child content, authorize a
    // management effect or replace its current IAM/consent/transaction fences.
    public async ValueTask<HarnessChatContext?> CurrentHarnessContextAsync(AgentRun run, CancellationToken ct = default)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var before = await ResolveCurrentAsync(run, ct).ConfigureAwait(false);
            if (before.Run.Admission.Configuration?.Definition.SelfManagement is not { } pin || !ManagementTools.Enabled(pin)) return null;
            var current = await configurations.CurrentPolicyAsync(before.State.Tenant.TenantId, run.AgentInstanceId, ct).ConfigureAwait(false);
            var after = await ResolveCurrentAsync(run, ct).ConfigureAwait(false);
            if (current.Agent.PrincipalId != run.Owner.ExecutorPrincipalId || current.Agent.Instance.DefinitionId != run.DefinitionId
                || current.Agent.Instance.Lifecycle != AgentInstanceLifecycle.Active)
                throw new AccessDeniedException("management_unavailable");
            if (before.State.Tenant.Revision != after.State.Tenant.Revision || before.Run.Revision != after.Run.Revision) continue;
            var policy = ManagementPolicyResolver.Restrict(pin, current.Definition.SelfManagement);
            return ManagementTools.Enabled(policy)
                ? new(policy, current.Agent.Instance.HarnessManagement?.PolicyRevision ?? 1, current.Agent.Instance.ActiveVersion) : null;
        }
        throw new SecurityConflictException("management_context_changed");
    }
}
