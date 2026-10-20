using AgentCore.Domain.Events;

namespace AgentCore.Infrastructure.Persistence;

public sealed partial class InMemoryExternalEventStore
{
    internal Lock TenantGate => _gate;
    internal WebhookEvent? TenantSource(Guid tenant, Guid source) => _sources.GetValueOrDefault(source) is { } row && row.TenantId == tenant ? row : null;
    internal bool OwnsTenantSource(Guid tenant, Guid source) { lock (_gate) return TenantSource(tenant, source) is not null; }
    internal IReadOnlyList<WebhookEvent> TenantSources(Guid tenant) => _sources.Values.Where(s => s.TenantId == tenant).ToArray();
    internal bool SourceIdExists(Guid source) => _sources.ContainsKey(source);
    internal bool TenantKeyExists(Guid tenant, string key, Guid except) => _sources.Values.Any(s => s.TenantId == tenant && s.EventKey == key && s.ResourceId != except);
    internal void PutTenantSource(WebhookEvent source) => _sources[source.ResourceId] = source;
    internal IReadOnlyList<ExternalEvent> TenantReceipts(Guid tenant, Guid source) => _events.Values.Where(r => r.TenantId == tenant && r.ResourceId == source).ToArray();
    internal ExternalEvent? TenantReceipt(Guid tenant, Guid source, string sourceEventId) => _events.GetValueOrDefault((source, sourceEventId)) is { } row && row.TenantId == tenant ? row : null;
    internal bool EventIdExists(Guid id) => _events.Values.Any(e => e.EventId == id);
    internal ExternalEvent? TenantReceiptById(Guid tenant, Guid id) => _events.Values.SingleOrDefault(r => r.TenantId == tenant && r.EventId == id);
    internal IReadOnlyList<ExternalEventDelivery> TenantDeliveries() => _deliveries.Values.Where(d => d.Snapshot?.Owner.TenantId is not null).ToArray();
    internal ExternalEventDelivery? TenantDelivery(Guid tenant, Guid id, Guid trigger) => _deliveries.GetValueOrDefault((id, trigger)) is { } row && row.Snapshot?.Owner.TenantId == tenant ? row : null;
    internal void PutTenantDelivery(ExternalEventDelivery delivery) => _deliveries[(delivery.EventId, delivery.TriggerId)] = delivery;
    internal void PutTenantReceipt(ExternalEvent receipt) => _events.Add((receipt.ResourceId, receipt.SourceEventId), receipt);
}
