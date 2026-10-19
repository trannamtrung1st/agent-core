using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore : ITenantSessionForegroundStore
{
    private TenantSession RequireForeground(TenantSessionForeground request, bool live, CancellationToken ct)
    {
        RequireHumanSession(request.Owner.TenantId, request.Human);
        var saved = sessionBacking.ReadTenantSession(request.Owner.TenantId, request.Owner.SessionId) ?? throw new AccessDeniedException("resource_unavailable");
        var agent = FindTenantAgentAsync(request.Owner.TenantId, request.Owner.AgentInstanceId, ct).Result ?? throw new AccessDeniedException("resource_unavailable");
        TenantSessionForegroundRules.RequireAsync(request, saved, agent, _tenants[request.Owner.TenantId], new IamAuthorizer(this), live, ct).AsTask().GetAwaiter().GetResult();
        return saved;
    }
    private void AuditForeground(TenantSessionForeground request, Guid auditId, DateTimeOffset at)
    {
        var state = _tenants[request.Owner.TenantId];
        if (auditId == Guid.Empty || _audit.Any(a => a.AuditId == auditId)) throw new ArgumentException("Unique audit identity is required.");
        _tenants[request.Owner.TenantId] = state with { Tenant = state.Tenant with { Revision = checked(state.Tenant.Revision + 1) } };
        _audit.Add(new(auditId, request.Owner.TenantId, request.Human.PrincipalId, "sessions.continue-in-chat", request.Owner.SessionId.ToString("D"), "allowed", "chat_surface_added", at));
    }
    public ValueTask SaveTenantRuntimeForegroundAsync(TenantRuntimeFrame frame, SessionSnapshot snapshot, long expectedRevision,
        TenantSessionForeground request, Guid auditId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate) lock (sessionBacking.AdmissionGate) lock (instances.CredentialGate)
        {
            var saved = RequireForeground(request, live: true, ct);
            if (frame.SessionId != saved.Owner.SessionId || frame.Owner != AgentRunOwner.ForTenant(saved.Owner.TenantId, saved.Owner.AgentInstanceId, saved.Owner.ExecutorPrincipalId))
                throw new AccessDeniedException("tenant_context_mismatch");
            if (auditId == Guid.Empty || _audit.Any(a => a.AuditId == auditId)) throw new ArgumentException("Unique audit identity is required.");
            _ = checked(_tenants[request.Owner.TenantId].Tenant.Revision + 1);
            var at = (clock ?? TimeProvider.System).GetUtcNow();
            sessionBacking.SaveTenantRuntime(frame, snapshot, expectedRevision, at, foreground: true);
            if (!saved.Snapshot.Surfaces.HasFlag(SessionSurface.ChatList)) AuditForeground(request, auditId, at);
            return ValueTask.CompletedTask;
        }
    }
    public ValueTask ContinueTenantQuiescentAsync(TenantSessionForeground request, Guid auditId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate) lock (sessionBacking.AdmissionGate) lock (instances.CredentialGate)
        {
            var saved = RequireForeground(request, live: false, ct);
            if (auditId == Guid.Empty || _audit.Any(a => a.AuditId == auditId)) throw new ArgumentException("Unique audit identity is required.");
            _ = checked(_tenants[request.Owner.TenantId].Tenant.Revision + 1);
            var at = (clock ?? TimeProvider.System).GetUtcNow();
            sessionBacking.ContinueTenantQuiescent(request, at);
            if (!saved.Snapshot.Surfaces.HasFlag(SessionSurface.ChatList)) AuditForeground(request, auditId, at);
            return ValueTask.CompletedTask;
        }
    }
}
