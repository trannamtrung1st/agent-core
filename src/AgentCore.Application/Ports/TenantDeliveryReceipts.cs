using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Ports;

// Trusted transport evidence, constructed only after current recipient authorization. Never a wire DTO.
public sealed record TenantDeliveryReceipt(TenantSessionRoot Owner, TenantSessionInitiator Human, long SecurityRevision,
    Guid EntryId, long EntrySequence, Guid ResponseId, int PublishedEnd, int ReceivedEnd);

public interface ITenantDeliveryReceiptStore
{
    ValueTask SaveTenantRuntimeReceiptAsync(TenantRuntimeFrame frame, SessionSnapshot snapshot, long expectedRevision,
        TenantDeliveryReceipt receipt, CancellationToken ct = default);
    // The host reserves this Session against Runtime admission before invoking this partial update.
    ValueTask<long> SaveTenantQuiescentReceiptAsync(TenantDeliveryReceipt receipt, CancellationToken ct = default);
}

public interface ITenantDeliveryReceiptControl
{
    ValueTask ReceiveAsync(TenantDeliveryReceipt receipt, CancellationToken ct = default);
}

public interface ITenantRuntimeReceiptWriter
{
    ValueTask SaveReceiptAsync(SessionSnapshot snapshot, long expectedRevision, TenantDeliveryReceipt receipt, CancellationToken ct);
}
