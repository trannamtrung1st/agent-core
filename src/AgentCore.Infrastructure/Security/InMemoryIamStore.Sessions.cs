using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore
{
    public ValueTask<TenantSession?> TryChangeTenantSessionAsync(TenantSecurityState next, long expectedSecurityRevision,
        TenantSessionChange change, TenantSessionInitiator initiator, DateTimeOffset at, AuthorizationAudit audit, CancellationToken ct = default)
    {
        TenantSessionRules.ValidateModelChange(next.Tenant.TenantId, change);
        ct.ThrowIfCancellationRequested(); next = IamStateRules.Freeze(next);
        if (next.Tenant.Revision != checked(expectedSecurityRevision + 1) || audit.TenantId != next.Tenant.TenantId || audit.ActorPrincipalId != initiator.PrincipalId
            || audit.Target != change.SessionId.ToString("D") || audit.Action != TenantSessionMutations.AuditAction(change.Action)) throw new ArgumentException("Invalid Session mutation transaction.");
        lock (_gate) lock (sessionBacking.AdmissionGate) lock (instances.CredentialGate)
        {
            if (!_tenants.TryGetValue(next.Tenant.TenantId, out var before) || before.Tenant.Revision != expectedSecurityRevision) return ValueTask.FromResult<TenantSession?>(null);
            IamStateRules.ValidateAgentContinuity(before.SystemIdentities, next); ValidateAgentActor(before, initiator.PrincipalId); EnsureReferences(next, audit, ct: ct);
            RequireHumanSession(next.Tenant.TenantId, initiator);
            if (change.Action == TenantSessionAction.Delete && next.Assignments.Any(a => a.Scope == TenantSessionRules.Scope(next.Tenant.TenantId, change.SessionId)
                || TenantWorkspaceRules.BelongsToSession(a.Scope, change.SessionId)
                || TenantRunRules.TryId(a.Scope, out var runId) && sessionBacking.TenantRunIds(next.Tenant.TenantId, change.SessionId).Contains(runId)
                || TenantAttachmentRules.TryId(a.Scope, out var attachment) && attachmentBacking?.RetainedTenant(next.Tenant.TenantId, change.SessionId, attachment) is not null
                || TenantArtifactRules.TryId(a.Scope, out var artifact) && artifactBacking?.FindTenantAsync(next.Tenant.TenantId, change.SessionId, artifact, ct).Result is not null))
                throw new ArgumentException("Deleting a Session must retire its direct, workspace, Run, Artifact and attachment grants.");
            if (change.Action == TenantSessionAction.Resume)
            {
                var root = sessionBacking.ReadTenantSession(next.Tenant.TenantId, change.SessionId)?.Owner;
                var parent = root is null ? null : FindTenantAgentAsync(root.TenantId, root.AgentInstanceId, ct).Result;
                if (parent is null) return ValueTask.FromResult<TenantSession?>(null);
                if (parent.Instance.Lifecycle != AgentCore.Domain.Definitions.AgentInstanceLifecycle.Active) throw new AccessDeniedException("agent_archived");
                TenantSessionRules.RequireExecutor(before, parent);
            }
            var prior = sessionBacking.ReadTenantSession(next.Tenant.TenantId, change.SessionId);
            ct.ThrowIfCancellationRequested(); var saved = sessionBacking.ChangeTenantSession(next.Tenant.TenantId, change, at);
            if (saved is null) return ValueTask.FromResult<TenantSession?>(null);
            if (change.Action == TenantSessionAction.Delete) TombstoneSessionMemory(next.Tenant.TenantId, change.SessionId, at);
            AppendTenantCoreSession(prior!, saved);
            _tenants[next.Tenant.TenantId] = next; _audit.Add(audit); return ValueTask.FromResult<TenantSession?>(saved);
        }
    }
    public ValueTask<TenantSession?> FindTenantSessionAsync(Guid tenantId, Guid sessionId, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); lock (_gate) return ValueTask.FromResult(sessionBacking.ReadTenantSession(tenantId, sessionId)); }
    public ValueTask<TenantSessionPage> ListTenantSessionsAsync(Guid tenantId, IReadOnlyList<Guid>? allowedIds, int page, int pageSize, CancellationToken ct = default, bool includeArchived = true, SessionSurface? surface = null, Guid? instanceId = null, bool recentFirst = false)
    {
        ct.ThrowIfCancellationRequested();
        if (tenantId == Guid.Empty || page < 0 || page > 100_000 || pageSize is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(page));
        if (surface is not null and not SessionSurface.ChatList and not SessionSurface.BackgroundWork) throw new ArgumentOutOfRangeException(nameof(surface));
        if (instanceId == Guid.Empty) throw new ArgumentException("Agent Instance is required.");
        lock (_gate) return ValueTask.FromResult(sessionBacking.ListTenantSessions(tenantId, allowedIds, page, pageSize, includeArchived, surface, instanceId, recentFirst));
    }
    public ValueTask<bool> TryCreateTenantSessionAsync(TenantSecurityState next, long expectedSecurityRevision, TenantSessionRoot owner,
        SessionSnapshot snapshot, TenantSessionInitiator initiator, long expectedInstanceRevision, AuthorizationAudit audit, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); next = IamStateRules.Freeze(next);
        if (owner.TenantId != next.Tenant.TenantId || audit.TenantId != owner.TenantId || next.Tenant.Revision != checked(expectedSecurityRevision + 1)) throw new ArgumentException("Invalid tenant Session transaction.");
        lock (_gate) lock (sessionBacking.AdmissionGate) lock (instances.CredentialGate)
        {
            if (!_tenants.TryGetValue(owner.TenantId, out var before) || before.Tenant.Revision != expectedSecurityRevision) return ValueTask.FromResult(false);
            IamStateRules.ValidateAgentContinuity(before.SystemIdentities, next); ValidateAgentActor(before, audit.ActorPrincipalId); EnsureReferences(next, audit, ct: ct);
            RequireHumanSession(owner.TenantId, initiator);
            var agent = FindTenantAgentAsync(owner.TenantId, owner.AgentInstanceId, ct).Result;
            if (agent is null || agent.Instance.Revision != expectedInstanceRevision) return ValueTask.FromResult(false);
            TenantSessionRules.RequireExecutor(before, agent);
            if (snapshot.RetentionKind == SessionRetentionKind.Saved && snapshot.RetainAfterTerminalDays != next.Tenant.SavedChatRetentionDays)
                throw new ArgumentException("Saved Chat retention must pin the current tenant policy.");
            TenantSessionRules.ValidateCreation(owner, snapshot, agent, initiator, audit, FindPrivateSpaceAsync(owner.TenantId, owner.AgentInstanceId, ct).Result ?? throw new AccessDeniedException("private_space_unavailable"));
            ct.ThrowIfCancellationRequested(); if (!sessionBacking.CreateTenantSession(owner, snapshot)) return ValueTask.FromResult(false);
            _tenants[owner.TenantId] = next; _audit.Add(audit); return ValueTask.FromResult(true);
        }
    }
}
