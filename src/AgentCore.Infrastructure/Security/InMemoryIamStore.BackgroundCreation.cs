using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore
{
    private TenantSession RequireBackgroundCreationOperation(TenantApprovalRequest request, AgentRun run, TenantAgent agent)
    {
        var state = _tenants[request.TenantId];
        var session = sessionBacking.ReadTenantSession(request.TenantId, request.SessionId) ?? throw new AccessDeniedException("resource_unavailable");
        var current = InstanceSettingsResolver.Resolve(TenantAgentDefinitionBaseline(agent), agent.Instance.SettingsOverrides);
        TenantWorkspaceEffectRules.Session(run, agent, session);
        TenantBackgroundCreationRules.Require(request, run, agent, current);
        RequireTenantExecutionSourcesAsync(run, state, new IamAuthorizer(this), this, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        TenantBackgroundCreationRules.ExecutorAsync(state, request, new IamAuthorizer(this), CancellationToken.None).AsTask().GetAwaiter().GetResult();
        return session;
    }
    private void StageTenantBackgroundGraph(TenantSessionRoot root, SessionSnapshot snapshot, AgentRun child)
    {
        var all = sessionBacking.AgentRuns;
        AgentRunStoreMapping.ValidateBackgroundBudget(child, all.Runs.Values);
        var key = AgentRunStoreMapping.BackgroundSourceKey(snapshot, child)!;
        if (sessionBacking.HasTenantBackgroundIdentityConflict(snapshot) || all.Runs.ContainsKey(child.AgentRunId) || all.Activations.ContainsKey(child.ActivationId)
            || all.ByAdmission.ContainsKey((child.SessionId, child.Admission.Activation.DedupeKey)) || all.ByBackgroundSource.ContainsKey((child.Owner, key))
            || sessionBacking.FindEntry(child.SessionId, snapshot.Entries[0].EntryId) is not null) throw new SecurityConflictException("identity_conflict");
        var activation = AgentRunStoreMapping.ToActivationRecord(snapshot, child);
        if (!sessionBacking.CreateTenantSession(root, snapshot)) throw new SecurityConflictException("identity_conflict");
        all.Runs.Add(child.AgentRunId, child); all.Activations.Add(child.ActivationId, activation);
        all.ByAdmission.Add((child.SessionId, child.Admission.Activation.DedupeKey), child.AgentRunId);
        all.BySourceEntry.Add((child.SessionId, snapshot.Entries[0].EntryId), child.ActivationId);
        all.ByBackgroundSource.Add((child.Owner, key), child.AgentRunId);
    }
}
