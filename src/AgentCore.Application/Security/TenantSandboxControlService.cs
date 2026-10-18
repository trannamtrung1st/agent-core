using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed record TenantSandboxResetReceipt(Guid SessionId, Guid Generation, long LeaseRevision, long SessionRevision,
    long SecurityRevision, bool RetirementConfirmed);

// Explicit human controls grant cleanup of an exact existing target, never another command or initialization.
public sealed partial class TenantSandboxControlService(IIamStore security, ITenantSessionStore sessions, ITenantSandboxRuntimeStore storage,
    IdentityService identity, IamAuthorizer authorizer, IIdGenerator ids, TimeProvider time)
{
    public async ValueTask<TenantSandboxResetReceipt> RequestResetAsync(Guid tenant, HumanSessionReference human, TenantSandboxReset reset,
        long expectedSecurityRevision, CancellationToken ct = default)
    {
        var state = await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, human, ct).ConfigureAwait(false);
        var session = await sessions.FindTenantSessionAsync(tenant, reset.SessionId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        await TenantSandboxControlRules.RequirePermissionsAsync(state, session.Owner, null, human, authorizer, ct).ConfigureAwait(false);
        var saved = await storage.ReadTenantSandboxRuntimeAsync(tenant, reset.SessionId, ct: ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        var lease = saved.Lease ?? throw new AccessDeniedException("resource_unavailable");
        await TenantSandboxControlRules.RequirePermissionsAsync(state, session.Owner, lease.Profile.Id, human, authorizer, ct).ConfigureAwait(false);
        // Authority precedes stale witnesses and repeats in the owning transaction, including the original login.
        var current = await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, human, ct).ConfigureAwait(false);
        if (state.Tenant.Revision != expectedSecurityRevision || current.Tenant.Revision != expectedSecurityRevision) throw new SecurityConflictException("stale_revision");
        TenantSandboxControlRules.Require(reset, session, lease);
        if (saved.Owner != session.Owner || saved.SessionRevision != session.Snapshot.Revision) throw new SecurityConflictException("stale_revision");
        var next = state with { Tenant = state.Tenant with { Revision = checked(expectedSecurityRevision + 1) } };
        var audit = new AuthorizationAudit(ids.NewId(), tenant, human.PrincipalId, "sandbox.reset.request", reset.SessionId.ToString("D"), "allowed", "human_reset", time.GetUtcNow());
        var requested = await storage.TryRequestTenantSandboxResetAsync(next, expectedSecurityRevision, reset, human, audit, ct).ConfigureAwait(false)
            ?? throw new SecurityConflictException("stale_revision");
        return new(reset.SessionId, requested.Generation, requested.Revision, reset.ExpectedSessionRevision, next.Tenant.Revision, requested.State == SandboxLeaseState.Terminated);
    }
}

public static partial class TenantSandboxControlRules
{
    public static async ValueTask RequirePermissionsAsync(TenantSecurityState state, TenantSessionRoot owner, string? profile,
        HumanSessionReference human, IamAuthorizer authorizer, CancellationToken ct)
    {
        var targets = new List<(string Key, AccessScope Scope)> {
            ("sessions.read", TenantSessionRules.Scope(owner.TenantId, owner.SessionId)),
            ("sessions.manage", TenantSessionRules.Scope(owner.TenantId, owner.SessionId)),
            ("sandbox.read", AccessScope.ForResource(owner.TenantId, "agent", owner.AgentInstanceId.ToString("D"))),
            ("sandbox.manage", AccessScope.ForResource(owner.TenantId, "agent", owner.AgentInstanceId.ToString("D"))) };
        if (profile is not null) targets.Add(("sandbox.read", TenantSandboxProfileRules.Scope(owner.TenantId, profile)));
        foreach (var target in targets)
            if (!(await authorizer.CheckTenantAsync(state, human.PrincipalId, PermissionCatalog.Get(target.Key), target.Scope, ct).ConfigureAwait(false)).Allowed)
                throw new AccessDeniedException("permission_denied");
    }
    public static void Require(TenantSandboxReset reset, TenantSession session, SandboxLease lease)
    {
        if (reset.SessionId == Guid.Empty || reset.ExpectedGeneration == Guid.Empty || reset.ExpectedExecutionId == Guid.Empty
            || reset.ExpectedSessionRevision < 1 || reset.ExpectedLeaseRevision < 1) throw new ArgumentException("Use exact current Sandbox reset witnesses.");
        if (session.Owner.SessionId != reset.SessionId || session.Owner.TenantId != lease.TenantId || session.Owner.ExecutorPrincipalId != lease.ExecutorPrincipalId
            || session.Owner.AgentInstanceId != lease.AgentInstanceId || lease.SessionId != reset.SessionId || session.Snapshot.ProfileId is not null
            || session.Snapshot.DurablyDeletedAt is not null) throw new AccessDeniedException("resource_unavailable");
        if (session.Snapshot.Revision != reset.ExpectedSessionRevision || lease.Generation != reset.ExpectedGeneration
            || lease.Revision != reset.ExpectedLeaseRevision || (lease.ActiveExecutionId ?? lease.LastExecutionId) != reset.ExpectedExecutionId)
            throw new SecurityConflictException("sandbox_operation_witness_changed");
    }
    public static void Audit(Guid tenant, TenantSandboxReset reset, HumanSessionReference human, AuthorizationAudit audit)
    {
        if (audit.TenantId != tenant || audit.ActorPrincipalId != human.PrincipalId || audit.Action != "sandbox.reset.request"
            || audit.Target != reset.SessionId.ToString("D") || audit.Decision != "allowed" || audit.ReasonCode != "human_reset")
            throw new ArgumentException("Invalid exact human Sandbox reset audit.");
    }
    public static SandboxLease Apply(SandboxLease lease) => lease with { Revision = checked(lease.Revision + 1),
        State = lease.State == SandboxLeaseState.Terminated ? SandboxLeaseState.Terminated : SandboxLeaseState.Terminating,
        CleanupReason = "reset", SelectedProfileId = null, TransitionProfileId = null, TransitionProfileRevision = null, TransitionPhase = null,
        SafeMessage = lease.State == SandboxLeaseState.Terminated ? "Reset recorded. Canonical files are retained; another command requires a fresh exact Review."
            : "Reset requested. Retirement is pending; writes may continue until confirmed. Canonical files are retained." };
}
