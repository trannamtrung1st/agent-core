using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed record TenantSandboxSelectionObservation(string ProfileId, long ProfileRevision, long InstanceRevision, string StoredPhase);
public sealed record TenantSandboxLeaseObservation(Guid Generation, long Revision, string StoredState, string ProfileId, long ProfileRevision,
    string ProfileName, string ImageReference, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, Guid? ActiveExecutionId, Guid? LastExecutionId,
    bool CleanupPending, string? Message, SandboxInitializationEvidence? Initialization, string StorageEnforcement = "sampled", string? SelectedProfileId = null, TenantSandboxSelectionObservation? Selection = null, bool SelectionRestricted = false);
public sealed record TenantSandboxExecutionObservation(Guid ExecutionId, long Revision, Guid AgentRunId, Guid Generation, string State,
    string? ProfileId, long? ProfileRevision, string CommandText, bool Background, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt,
    int? ExitCode, string Stdout, string Stderr, bool Truncated, long? DurationMs, bool FilesystemEffectsMayHaveOccurred, bool TerminationConfirmed,
    bool RemoteTerminationConfirmed, string? DiagnosticId, bool StopRequested = false, bool StopDispatchStarted = false);
public sealed record TenantSandboxRuntimeView(Guid SessionId, TenantSandboxLeaseObservation? Environment, TenantSandboxExecutionObservation? Execution,
    long SessionRevision, long SecurityRevision, bool LiveAuthorityConfirmed = false);

public sealed class TenantSandboxRuntimeReadService(IIamStore security, ITenantSessionStore sessions, ITenantSandboxRuntimeStore storage,
    IdentityService identity, IamAuthorizer authorizer)
{
    public async ValueTask<TenantSandboxRuntimeView> ReadAsync(Guid tenant, Guid session, HumanSessionReference human, Guid? executionId = null, CancellationToken ct = default)
    {
        var state = await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, human, ct).ConfigureAwait(false);
        var saved = await sessions.FindTenantSessionAsync(tenant, session, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        await RequireRead(state, saved.Owner, human, [], ct).ConfigureAwait(false);
        var snapshot = await storage.ReadTenantSandboxRuntimeAsync(tenant, session, executionId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        if (snapshot.Owner != saved.Owner || snapshot.SessionRevision != saved.Snapshot.Revision) throw new SecurityConflictException("stale_revision");
        if (executionId is not null && snapshot.Execution is null) throw new AccessDeniedException("resource_unavailable");
        var profileIds = new[] { snapshot.Lease?.Profile.Id, snapshot.Execution?.ProfileId }.OfType<string>().Distinct(StringComparer.Ordinal).ToArray();
        await RequireRead(state, saved.Owner, human, profileIds, ct).ConfigureAwait(false);
        var currentSession = await sessions.FindTenantSessionAsync(tenant, session, ct).ConfigureAwait(false);
        var current = await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, human, ct).ConfigureAwait(false);
        await RequireRead(current, saved.Owner, human, profileIds, ct).ConfigureAwait(false);
        var target = snapshot.Lease?.SelectionWitness is not null ? snapshot.Lease.TransitionProfileId : null;
        var readableSelection = target is not null && (await authorizer.CheckTenantAsync(current, human.PrincipalId, PermissionCatalog.Get("sandbox.read"),
            TenantSandboxProfileRules.Scope(tenant, target), ct).ConfigureAwait(false)).Allowed;
        var final = await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, human, ct).ConfigureAwait(false);
        if (currentSession?.Owner != saved.Owner || currentSession.Snapshot.Revision != saved.Snapshot.Revision || current.Tenant.Revision != state.Tenant.Revision
            || final.Tenant.Revision != state.Tenant.Revision) throw new SecurityConflictException("stale_revision");
        var l = snapshot.Lease; var e = snapshot.Execution;
        return new(session, l is null ? null : new(l.Generation, l.Revision, l.State.ToString(), l.Profile.Id, l.Profile.Revision, l.Profile.Name,
            l.Profile.ImageReference, l.CreatedAt, l.ExpiresAt, l.ActiveExecutionId, l.LastExecutionId, l.State is SandboxLeaseState.Terminating or SandboxLeaseState.NeedsReconciliation || e is { ProcessStop: not null, TerminationConfirmed: false },
            l.SafeMessage, l.Initialization,
            SelectedProfileId: target is null ? l.CleanupReason == "reset" ? null : l.SelectedProfileId ?? l.Profile.Id : readableSelection ? target : null,
            Selection: readableSelection ? new(target!, l.TransitionProfileRevision!.Value, l.SelectionWitness!.InstanceRevision, l.TransitionPhase!) : null,
            SelectionRestricted: target is not null && !readableSelection), e is null ? null : new(e.Id, e.Revision, e.AgentRunId, e.Generation, e.State.ToString(), e.ProfileId, e.ProfileRevision,
            e.CommandText, e.Background, e.StartedAt, e.FinishedAt, e.ExitCode, e.Stdout, e.Stderr, e.Truncated, e.DurationMs, e.FilesystemEffectsMayHaveOccurred,
            e.TerminationConfirmed, e.RemoteTerminationConfirmed, e.DiagnosticId, e.ProcessStop is not null, e.ProcessStop?.DispatchStartedAt is not null), snapshot.SessionRevision, state.Tenant.Revision);
    }
    private async ValueTask RequireRead(TenantSecurityState state, TenantSessionRoot owner, HumanSessionReference human, IEnumerable<string> profiles, CancellationToken ct)
    {
        var targets = new List<(string Permission, AccessScope Scope)> { ("sessions.read", TenantSessionRules.Scope(owner.TenantId, owner.SessionId)),
            ("sandbox.read", AccessScope.ForResource(owner.TenantId, "agent", owner.AgentInstanceId.ToString("D"))) };
        targets.AddRange(profiles.Select(id => ("sandbox.read", TenantSandboxProfileRules.Scope(owner.TenantId, id))));
        foreach (var target in targets)
        {
            var decision = await authorizer.CheckTenantAsync(state, human.PrincipalId, PermissionCatalog.Get(target.Permission), target.Scope, ct).ConfigureAwait(false);
            if (!decision.Allowed) throw new AccessDeniedException(decision.ReasonCode);
        }
    }
}
