using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed partial class TenantManagementReadService
{
    public async ValueTask<TenantSkillProjection> SkillContextAsync(AgentRun run, CancellationToken ct)
    {
        var visible = new List<EffectiveSkill>(); var sources = new List<TenantManagementReadReference>();
        var retained = TenantManagementReadReferences.Read(run.Checkpoint);
        foreach (var skill in run.PinnedSkillCatalog)
        {
            if (ManagementBuiltInSkills.IsBuiltIn(skill)) { visible.Add(skill); continue; }
            var arguments = TenantSkillLoadContract.Arguments([skill.Key]);
            var prior = retained.FirstOrDefault(s => s.ToolName == ToolCatalog.SkillsLoad && TenantSkillLoadContract.Parse(s.ArgumentsJson).Contains(skill.Key));
            try
            {
                if (prior is not null)
                    await store.RequireTenantManagementReadSourcesAsync(new(run.Owner.TenantId!.Value, run.AgentRunId), [prior], run.Claim?.Generation, ct).ConfigureAwait(false);
                else
                {
                    var observed = await PrepareAsync(new(run.Owner.TenantId!.Value, run.AgentRunId), run.Claim?.Generation, ToolCatalog.SkillsLoad, arguments, ct).ConfigureAwait(false);
                    if ((await DecisionAsync(observed, ct).ConfigureAwait(false)).Decision != OperationDecisionKind.Allowed) continue;
                    prior = Reference(observed.Value, run.Owner.TenantId.Value);
                    await store.RequireTenantManagementReadSourcesAsync(new(run.Owner.TenantId.Value, run.AgentRunId), [prior], run.Claim?.Generation, ct).ConfigureAwait(false);
                }
                visible.Add(skill); sources.Add(prior);
            }
            // Already consumed sources remain mandatory. New unavailable metadata is withheld.
            catch (Exception error) when (prior is null && error is AccessDeniedException or SecurityConflictException) { }
        }
        return new(visible, sources);
    }
    public async ValueTask<(TenantManagementReadObservation Observation, OperationAuthorizationDecision Decision)> SkillLoadAsync(AgentRun run, IReadOnlyList<string> ids, CancellationToken ct)
    {
        var observed = await PrepareAsync(new(run.Owner.TenantId!.Value, run.AgentRunId), run.Claim!.Generation,
            ToolCatalog.SkillsLoad, TenantSkillLoadContract.Arguments(ids), ct).ConfigureAwait(false);
        var decision = await DecisionAsync(observed, ct).ConfigureAwait(false);
        if (decision.Decision == OperationDecisionKind.Allowed)
            await store.RequireTenantManagementReadSourcesAsync(new(run.Owner.TenantId.Value, run.AgentRunId),
                [Reference(observed.Value, run.Owner.TenantId.Value)], run.Claim.Generation, ct).ConfigureAwait(false);
        return (observed, decision);
    }
}
