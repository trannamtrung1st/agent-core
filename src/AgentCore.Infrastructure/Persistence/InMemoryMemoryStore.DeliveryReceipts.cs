using AgentCore.Application.Ports;
using AgentCore.Application.Security;

namespace AgentCore.Infrastructure.Persistence;

public sealed partial class InMemoryMemoryStore
{
    internal long SaveTenantQuiescentReceipt(TenantDeliveryReceipt proof)
    {
        lock (_gate)
        {
            var saved = ReadTenantSession(proof.Owner.TenantId, proof.Owner.SessionId) ?? throw new AccessDeniedException("session_unavailable");
            var entry = FindEntry(proof.Owner.SessionId, proof.EntryId) ?? throw new AccessDeniedException("receipt_unavailable");
            var updated = TenantDeliveryReceiptRules.Apply(proof, saved.Owner, entry);
            if (updated == entry) return saved.Snapshot.Revision;
            var snapshot = saved.Snapshot with { Revision = checked(saved.Snapshot.Revision + 1), Entries = [updated] };
            Store(snapshot); return snapshot.Revision;
        }
    }
}
