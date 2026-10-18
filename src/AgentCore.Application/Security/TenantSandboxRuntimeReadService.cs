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

public sealed partial class TenantSandboxRuntimeReadService(IIamStore security, ITenantSessionStore sessions, ITenantSandboxRuntimeStore storage,
    IdentityService identity, IamAuthorizer authorizer, TenantOperationAuthorizer? operations = null,
    TenantRunConfigurationResolver? runConfiguration = null, ITenantSandboxProfileStore? profileStore = null,
    ISandboxImageAuthority? images = null, ISandboxReadiness? readiness = null, TimeProvider? time = null,
    ITenantApprovalStore? approvalStore = null, ITenantApprovedOperationStore? effectStore = null, ISandboxVerificationHost? host = null)
{
    public async ValueTask<TenantSandboxRuntimeView> ReadAsync(Guid tenant, Guid session, HumanSessionReference human, Guid? executionId = null, CancellationToken ct = default) =>
        (await ReadHuman(tenant, session, human, executionId, ct).ConfigureAwait(false)).View;

    private sealed record HumanObservation(TenantSecurityState Security, TenantSession Session, TenantSandboxRuntimeSnapshot Snapshot, TenantSandboxRuntimeView View);
    private async ValueTask<HumanObservation> ReadHuman(Guid tenant, Guid session, HumanSessionReference human, Guid? executionId, CancellationToken ct)
    {
        var state = await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, human, ct).ConfigureAwait(false);
        var saved = await sessions.FindTenantSessionAsync(tenant, session, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        await RequireRead(state, saved.Owner, human.PrincipalId, [], ct).ConfigureAwait(false);
        var snapshot = await storage.ReadTenantSandboxRuntimeAsync(tenant, session, executionId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        if (snapshot.Owner != saved.Owner || snapshot.SessionRevision != saved.Snapshot.Revision) throw new SecurityConflictException("stale_revision");
        if (executionId is not null && snapshot.Execution is null) throw new AccessDeniedException("resource_unavailable");
        var profileIds = new[] { snapshot.Lease?.Profile.Id, snapshot.Execution?.ProfileId }.OfType<string>().Distinct(StringComparer.Ordinal).ToArray();
        await RequireRead(state, saved.Owner, human.PrincipalId, profileIds, ct).ConfigureAwait(false);
        var currentSession = await sessions.FindTenantSessionAsync(tenant, session, ct).ConfigureAwait(false);
        var current = await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, human, ct).ConfigureAwait(false);
        await RequireRead(current, saved.Owner, human.PrincipalId, profileIds, ct).ConfigureAwait(false);
        var target = snapshot.Lease?.SelectionWitness is not null ? snapshot.Lease.TransitionProfileId : null;
        var readableSelection = target is not null && (await authorizer.CheckTenantAsync(current, human.PrincipalId, PermissionCatalog.Get("sandbox.read"),
            TenantSandboxProfileRules.Scope(tenant, target), ct).ConfigureAwait(false)).Allowed;
        var final = await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, human, ct).ConfigureAwait(false);
        if (currentSession?.Owner != saved.Owner || currentSession.Snapshot.Revision != saved.Snapshot.Revision || current.Tenant.Revision != state.Tenant.Revision
            || final.Tenant.Revision != state.Tenant.Revision) throw new SecurityConflictException("stale_revision");
        return new(state, saved, snapshot, Project(snapshot, state.Tenant.Revision, readableSelection));
    }
    private static TenantSandboxRuntimeView Project(TenantSandboxRuntimeSnapshot snapshot, long securityRevision, bool readableSelection)
    {
        var l = snapshot.Lease; var e = snapshot.Execution;
        var target = l?.SelectionWitness is not null ? l.TransitionProfileId : null;
        return new(snapshot.Owner.SessionId, l is null ? null : new(l.Generation, l.Revision, l.State.ToString(), l.Profile.Id, l.Profile.Revision, l.Profile.Name,
            l.Profile.ImageReference, l.CreatedAt, l.ExpiresAt, l.ActiveExecutionId, l.LastExecutionId, l.State is SandboxLeaseState.Terminating or SandboxLeaseState.NeedsReconciliation || e is { ProcessStop: not null, TerminationConfirmed: false },
            l.SafeMessage, l.Initialization,
            SelectedProfileId: target is null ? l.CleanupReason == "reset" ? null : l.SelectedProfileId ?? l.Profile.Id : readableSelection ? target : null,
            Selection: readableSelection ? new(target!, l.TransitionProfileRevision!.Value, l.SelectionWitness!.InstanceRevision, l.TransitionPhase!) : null,
            SelectionRestricted: target is not null && !readableSelection), e is null ? null : new(e.Id, e.Revision, e.AgentRunId, e.Generation, e.State.ToString(), e.ProfileId, e.ProfileRevision,
            e.CommandText, e.Background, e.StartedAt, e.FinishedAt, e.ExitCode, e.Stdout, e.Stderr, e.Truncated, e.DurationMs, e.FilesystemEffectsMayHaveOccurred,
            e.TerminationConfirmed, e.RemoteTerminationConfirmed, e.DiagnosticId, e.ProcessStop is not null, e.ProcessStop?.DispatchStartedAt is not null), snapshot.SessionRevision, securityRevision);
    }
    private static IReadOnlyList<(string Permission, AccessScope Scope)> ReadTargets(TenantSessionRoot owner, IEnumerable<string> profiles) =>
        TenantSandboxReadOperationRules.Targets(owner, profiles);
    private async ValueTask RequireRead(TenantSecurityState state, TenantSessionRoot owner, Guid principal, IEnumerable<string> profiles, CancellationToken ct)
    {
        foreach (var target in ReadTargets(owner, profiles))
        {
            var decision = await authorizer.CheckTenantAsync(state, principal, PermissionCatalog.Get(target.Permission), target.Scope, ct).ConfigureAwait(false);
            if (!decision.Allowed) throw new AccessDeniedException(decision.ReasonCode);
        }
    }
}
