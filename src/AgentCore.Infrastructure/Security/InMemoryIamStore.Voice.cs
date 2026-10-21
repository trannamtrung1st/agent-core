using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Sessions;
using AgentCore.Application.Speech;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore : ITenantVoiceModeStore
{
    public ValueTask<TenantSession> ActivateTenantVoiceAsync(TenantVoiceModeChange request, Guid auditId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); var lease = request.Lease;
        lock (_gate) lock (sessionBacking.AdmissionGate) lock (instances.CredentialGate)
        {
            RequireHumanSession(lease.Owner.TenantId, lease.Human);
            var saved = sessionBacking.ReadTenantSession(lease.Owner.TenantId, lease.Owner.SessionId) ?? throw new AccessDeniedException("resource_unavailable");
            var agent = FindTenantAgentAsync(lease.Owner.TenantId, lease.Owner.AgentInstanceId, ct).Result ?? throw new AccessDeniedException("resource_unavailable");
            var state = _tenants[lease.Owner.TenantId];
            TenantVoiceRules.RequireAsync(saved, agent, state, lease.Human, new IamAuthorizer(this), ct).GetAwaiter().GetResult();
            TenantVoiceRules.Shape(request, saved, agent, state);
            if (auditId == Guid.Empty || _audit.Any(a => a.AuditId == auditId)) throw new ArgumentException("Use a fresh voice audit identity.");
            var at = (clock ?? TimeProvider.System).GetUtcNow();
            var changed = sessionBacking.ChangeTenantVoiceMode(lease.Owner, saved.Snapshot.Revision, SessionMode.Voice, at);
            _audit.Add(new(auditId, lease.Owner.TenantId, lease.Human.PrincipalId, "sessions.voice.activate", lease.Owner.SessionId.ToString("D"), "allowed", "voice_attachment_activated", at));
            return ValueTask.FromResult(changed);
        }
    }
    public ValueTask RetireTenantVoiceAsync(TenantVoiceLease lease, long expectedRevision, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!lease.Retiring) throw new AccessDeniedException("voice_lease_unavailable");
        lock (_gate) lock (sessionBacking.AdmissionGate)
        {
            var saved = sessionBacking.ReadTenantSession(lease.Owner.TenantId, lease.Owner.SessionId);
            if (saved is null) return ValueTask.CompletedTask;
            if (saved.Owner != lease.Owner) throw new AccessDeniedException("voice_lease_unavailable");
            if (saved.Snapshot.Mode != SessionMode.Text || saved.Snapshot.PendingMode is not null)
                sessionBacking.ChangeTenantVoiceMode(lease.Owner, expectedRevision, SessionMode.Text, (clock ?? TimeProvider.System).GetUtcNow());
            return ValueTask.CompletedTask;
        }
    }
}
