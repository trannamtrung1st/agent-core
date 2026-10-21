using AgentCore.Application.Agents;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore : ITenantRunProfileStore
{
    private (TenantUser User, TenantAgent Agent, AgentDefinition Current, long SecurityRevision)? RunProfileReader(AgentRun run, CancellationToken ct)
    {
        var tenant = run.Owner.TenantId ?? throw new AccessDeniedException("profile_context_unavailable");
        var state = _tenants.GetValueOrDefault(tenant) ?? throw new AccessDeniedException("profile_context_unavailable");
        var root = TenantCoreRoot(run); var user = TenantRunProfileRules.User(run, root, state);
        if (user is null || _accounts.GetValueOrDefault(user.AccountId)?.Status != IdentityStatus.Active) return null;
        var session = sessionBacking.ReadTenantSession(tenant, run.SessionId); var parent = sessionBacking.ReadTenantSession(tenant, root.SessionId);
        var agent = FindTenantAgentAsync(tenant, run.AgentInstanceId, ct).Result;
        if (session is null || parent is null || agent is null || !TenantRunProfileRules.AllowedAsync(run, root, state, session, parent, agent,
            new IamAuthorizer(this), ct).AsTask().GetAwaiter().GetResult()) return null;
        return (user, agent, InstanceSettingsResolver.Resolve(TenantAgentDefinitionBaseline(agent), agent.Instance.SettingsOverrides), state.Tenant.Revision);
    }
    public ValueTask<TenantRunProfileObservation?> ReadTenantRunProfileAsync(TenantRuntimeFrame frame, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate) lock (sessionBacking.AdmissionGate) lock (instances.CredentialGate) lock (definitions.TenantGate)
        {
            var run = sessionBacking.ReadTenantRun(frame.Owner.TenantId ?? Guid.Empty, frame.RunId) ?? throw new AccessDeniedException("profile_context_unavailable");
            var session = sessionBacking.ReadTenantSession(frame.Owner.TenantId!.Value, frame.SessionId) ?? throw new AccessDeniedException("profile_context_unavailable");
            TenantRuntimeRules.Require(frame, session.Owner, run, (clock ?? TimeProvider.System).GetUtcNow());
            var reader = RunProfileReader(run, ct);
            if (reader is null || !TenantRunProfileRules.Eligible(run, reader.Value.Current)) return ValueTask.FromResult<TenantRunProfileObservation?>(null);
            var profile = userProfiles.GetValueOrDefault((reader.Value.User.TenantId, reader.Value.User.PrincipalId));
            return ValueTask.FromResult<TenantRunProfileObservation?>(profile is null ? null : new(profile.Freeze(), TenantRunProfileRules.Source(profile), reader.Value.SecurityRevision, reader.Value.Agent.Instance.Revision));
        }
    }
    public ValueTask RequireTenantProfileProjectionAsync(TenantRuntimeFrame frame, TenantRunProfileObservation observation, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate) lock (sessionBacking.AdmissionGate) lock (instances.CredentialGate) lock (definitions.TenantGate)
        {
            var run = sessionBacking.ReadTenantRun(frame.Owner.TenantId ?? Guid.Empty, frame.RunId) ?? throw new AccessDeniedException("profile_context_unavailable");
            var session = sessionBacking.ReadTenantSession(frame.Owner.TenantId!.Value, frame.SessionId) ?? throw new AccessDeniedException("profile_context_unavailable");
            TenantRuntimeRules.Require(frame, session.Owner, run, (clock ?? TimeProvider.System).GetUtcNow());
            var reader = RunProfileReader(run, ct);
            if (reader is null || !TenantRunProfileRules.Eligible(run, reader.Value.Current) || reader.Value.SecurityRevision != observation.SecurityRevision
                || reader.Value.Agent.Instance.Revision != observation.InstanceRevision || TenantRunProfileRules.Source(observation.Profile) != observation.Source)
                throw new AccessDeniedException("profile_context_changed");
            RequireProfileSource(run, observation.Source, ct); return ValueTask.CompletedTask;
        }
    }
    public ValueTask RequireTenantProfileSourceAsync(TenantRunReference reference, TenantProfileSource source, Guid? generation, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate) lock (sessionBacking.AdmissionGate) lock (instances.CredentialGate) lock (definitions.TenantGate)
        {
            var run = sessionBacking.ReadRetainedTenantOperationRun(reference.TenantId, reference.RunId) ?? throw new AccessDeniedException("profile_context_unavailable");
            if (generation is { } expected && (run.Status != AgentRunStatus.Running || run.CancellationRequested || run.Claim?.Generation != expected
                || run.Claim.LeaseExpiresAtUtc <= (clock ?? TimeProvider.System).GetUtcNow()) || generation is null && !run.IsTerminal)
                throw new AccessDeniedException("profile_context_unavailable");
            RequireProfileSource(run, source, ct); return ValueTask.CompletedTask;
        }
    }
    private void RequireProfileSource(AgentRun run, TenantProfileSource source, CancellationToken ct)
    {
        TenantRunProfileRules.Validate(source);
        var reader = RunProfileReader(run, ct);
        if (reader is null || run.Admission.Configuration is null || run.Admission.Configuration.Definition.StateIsolationPolicy?.ProfileEvidence == false
            || source.TenantId != run.Owner.TenantId || source.PrincipalId != reader.Value.User.PrincipalId || source.AccountId != reader.Value.User.AccountId)
            throw new AccessDeniedException("profile_read_unavailable");
        var profile = userProfiles.GetValueOrDefault((source.TenantId, source.PrincipalId));
        if (profile is null || TenantRunProfileRules.Source(profile) != source) throw new AccessDeniedException("profile_source_changed");
    }
    private void RequireStoredProfileSource(AgentRun run, CancellationToken ct)
    {
        if (AgentRunToolCallCheckpoint.ReadExecutionState(run.Checkpoint).TenantProfileSource is { } source) RequireProfileSource(run, source, ct);
    }
}
