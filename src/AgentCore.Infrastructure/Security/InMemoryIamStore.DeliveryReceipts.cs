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
        TenantDeliveryReceiptRules.RequireBlocksAsync(proof, _tenants[proof.Owner.TenantId], new IamAuthorizer(this), async (id, token) =>
            artifactBacking is not null && await artifactBacking.FindTenantAsync(proof.Owner.TenantId, proof.Owner.SessionId, id, token).ConfigureAwait(false) is not null, CancellationToken.None).GetAwaiter().GetResult();
    }
    public async ValueTask SaveTenantRuntimeReceiptAsync(TenantRuntimeFrame frame, SessionSnapshot snapshot, long expectedRevision,
        TenantDeliveryReceipt proof, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await using var operation = proof.Heard is { } heard ? await heard.Lease.EnterAsync(true, ct).ConfigureAwait(false) : null;
        TenantDeliveryReceiptRules.RequireVoiceOperation(proof, operation);
        lock (_gate) lock (sessionBacking.AdmissionGate)
        {
            RequireReceiptHuman(proof); RequireRuntimeOwner(frame.Owner);
            var saved = sessionBacking.ReadTenantSession(proof.Owner.TenantId, frame.SessionId) ?? throw new AccessDeniedException("session_unavailable");
            var entry = snapshot.Entries.SingleOrDefault(e => e.EntryId == proof.EntryId) ?? throw new AccessDeniedException("receipt_unavailable");
            if (frame.SessionId != proof.Owner.SessionId || frame.Owner.AgentInstanceId != proof.Owner.AgentInstanceId
                || frame.Owner.TenantId != proof.Owner.TenantId || frame.Owner.ExecutorPrincipalId != proof.Owner.ExecutorPrincipalId
                || TenantDeliveryReceiptRules.Apply(proof, saved.Owner, entry) != entry)
                throw new AccessDeniedException("receipt_unavailable");
            SaveTenantRuntimeState(frame, snapshot, expectedRevision, (clock ?? TimeProvider.System).GetUtcNow());
            return;
        }
    }
    public async ValueTask<long> SaveTenantQuiescentReceiptAsync(TenantDeliveryReceipt proof, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await using var operation = proof.Heard is { } heard ? await heard.Lease.EnterAsync(true, ct).ConfigureAwait(false) : null;
        TenantDeliveryReceiptRules.RequireVoiceOperation(proof, operation);
        lock (_gate) lock (sessionBacking.AdmissionGate)
        {
            RequireReceiptHuman(proof);
            return sessionBacking.SaveTenantQuiescentReceipt(proof);
        }
    }
}
