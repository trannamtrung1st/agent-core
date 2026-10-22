using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Ports;

public sealed record TenantSkillProjection(IReadOnlyList<AgentCore.Domain.Definitions.EffectiveSkill> Catalog,
    IReadOnlyList<AgentCore.Domain.Security.TenantManagementReadReference> Sources);

// Current presentation facts only. No role/permission snapshot is persisted or accepted as effect authority.
public interface ITenantRunContext
{
    ValueTask<TenantSkillProjection> CurrentSkillContextAsync(AgentRun run, CancellationToken ct = default)
        => throw new AccessDeniedException("skill_context_unavailable");
    ValueTask<(TenantManagementReadObservation Observation, OperationAuthorizationDecision Decision)> PrepareSkillLoadAsync(AgentRun run, IReadOnlyList<string> ids, CancellationToken ct = default)
        => throw new AccessDeniedException("skill_context_unavailable");
    // Projection may refresh an owned Profile witness before the ordinary source/effect fence.
    ValueTask<AgentCore.Domain.Definitions.AgentDefinition?> CurrentProjectionDefinitionAsync(AgentRun run, CancellationToken ct = default);
    ValueTask<AgentCore.Application.Tools.HarnessChatContext?> CurrentHarnessContextAsync(AgentRun run, CancellationToken ct = default)
        => ValueTask.FromResult<AgentCore.Application.Tools.HarnessChatContext?>(null);
    ValueTask<AgentRunContinuitySettings?> CurrentContinuitySettingsAsync(AgentRun run, CancellationToken ct = default) => ValueTask.FromResult<AgentRunContinuitySettings?>(null);
    ValueTask<TenantRequesterContext> CurrentRequesterAsync(AgentRun run, CancellationToken ct = default);
}
