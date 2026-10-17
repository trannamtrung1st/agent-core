using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore : ITenantDeliveryReceiptStore
{
    private void RequireReceiptHuman(TenantDeliveryReceipt proof)
    {
        RequireHumanSession(proof.Owner.TenantId, proof.Human);
        if (_tenants[proof.Owner.TenantId].Tenant.Revision != proof.SecurityRevision) throw new SecurityConflictException("stale_revision");
    }
    public ValueTask SaveTenantRuntimeReceiptAsync(TenantRuntimeFrame frame, SessionSnapshot snapshot, long expectedRevision,
        TenantDeliveryReceipt proof, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate) lock (sessionBacking.AdmissionGate)
        {
            RequireReceiptHuman(proof); RequireRuntimeOwner(frame.Owner);
            var saved = sessionBacking.ReadTenantSession(proof.Owner.TenantId, frame.SessionId) ?? throw new AccessDeniedException("session_unavailable");
            var entry = snapshot.Entries.SingleOrDefault(e => e.EntryId == proof.EntryId) ?? throw new AccessDeniedException("receipt_unavailable");
            if (frame.SessionId != proof.Owner.SessionId || frame.Owner.AgentInstanceId != proof.Owner.AgentInstanceId
                || frame.Owner.TenantId != proof.Owner.TenantId || frame.Owner.ExecutorPrincipalId != proof.Owner.ExecutorPrincipalId
                || TenantDeliveryReceiptRules.Apply(proof, saved.Owner, entry) != entry)
                throw new AccessDeniedException("receipt_unavailable");
            sessionBacking.SaveTenantRuntime(frame, snapshot, expectedRevision, (clock ?? TimeProvider.System).GetUtcNow());
            return ValueTask.CompletedTask;
        }
    }
    public ValueTask<long> SaveTenantQuiescentReceiptAsync(TenantDeliveryReceipt proof, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate) lock (sessionBacking.AdmissionGate)
        {
            RequireReceiptHuman(proof);
            return ValueTask.FromResult(sessionBacking.SaveTenantQuiescentReceipt(proof));
        }
    }
}
