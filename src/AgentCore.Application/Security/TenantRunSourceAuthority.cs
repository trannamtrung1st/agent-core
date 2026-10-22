using AgentCore.Application.Execution;
using AgentCore.Domain.Conversation;
namespace AgentCore.Application.Security;

// The same retained private-source checks are required before model reentry,
// completion inspection and report delivery. Their existing owners perform each check.
public sealed class TenantRunSourceAuthority(TenantRunProfileService profiles, TenantMemoryService memories,
    TenantContinuityReadService continuity, TenantAutomationReadService automations, TenantManagementReadService management)
{
    public ValueTask<AgentCore.Application.Ports.TenantSkillProjection> SkillContextAsync(AgentRun run, CancellationToken ct) => management.SkillContextAsync(run, ct);
    public ValueTask<(AgentCore.Application.Ports.TenantManagementReadObservation Observation, OperationAuthorizationDecision Decision)> SkillLoadAsync(AgentRun run, IReadOnlyList<string> ids, CancellationToken ct) => management.SkillLoadAsync(run, ids, ct);
    public static bool HasSources(AgentRun run, bool checkProfile = true) => checkProfile && AgentRunToolCallCheckpoint.ReadExecutionState(run.Checkpoint).TenantProfileSource is not null
        || TenantManagementReadReferences.Read(run.Checkpoint).Count > 0 || TenantMemoryOperationReferences.Read(run.Checkpoint).Count > 0 || TenantAutomationReadReferences.Read(run.Checkpoint).Count > 0
        || TenantExperienceReferences.Sources(run.Checkpoint).Count > 0 || TenantExperienceReferences.Facts(run.Checkpoint).Count > 0 || TenantHistoricalReferences.Read(run.Checkpoint).Count > 0;
    public async ValueTask RequireAsync(AgentRun run, CancellationToken ct, bool checkProfile = true)
    {
        if (checkProfile && AgentRunToolCallCheckpoint.ReadExecutionState(run.Checkpoint).TenantProfileSource is { } profile)
            await profiles.RequireAsync(run, profile, ct).ConfigureAwait(false);
        if (TenantMemoryOperationReferences.Read(run.Checkpoint) is { Count: > 0 } learned)
            await memories.RequireAsync(run, learned, ct).ConfigureAwait(false);
        if (TenantManagementReadReferences.Read(run.Checkpoint).Count > 0) await management.RequireAsync(run, ct).ConfigureAwait(false);
        if (TenantAutomationReadReferences.Read(run.Checkpoint).Count > 0) await automations.RequireAsync(run, ct).ConfigureAwait(false);
        if (TenantExperienceReferences.Sources(run.Checkpoint).Count > 0 || TenantExperienceReferences.Facts(run.Checkpoint).Count > 0)
            await continuity.RequireExperienceAsync(run, ct).ConfigureAwait(false);
        if (TenantHistoricalReferences.Read(run.Checkpoint) is { Count: > 0 } historical)
            await continuity.RequireAsync(run, historical, null, ct).ConfigureAwait(false);
    }
}
