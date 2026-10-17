using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Security;

public static class TenantDeliveryReceiptRules
{
    public static ConversationEntry Apply(TenantDeliveryReceipt proof, TenantSessionRoot owner, ConversationEntry entry)
    {
        if (proof.Owner != owner || proof.EntryId == Guid.Empty || proof.ResponseId == Guid.Empty || proof.EntrySequence < 1
            || entry.EntryId != proof.EntryId || entry.Sequence != proof.EntrySequence || entry.ResponseId != proof.ResponseId
            || entry.Role != ConversationRole.Assistant || entry.DeliveryMode != SessionMode.Text
            || entry.Status is EntryStatus.Interrupted or EntryStatus.Failed)
            throw new AccessDeniedException("receipt_unavailable");
        var length = (entry.Envelope?.DisplayText ?? entry.Text).Length;
        if (proof.ReceivedEnd < 0 || proof.PublishedEnd < proof.ReceivedEnd || proof.ReceivedEnd > length)
            throw new AccessDeniedException("receipt_out_of_bounds");
        return entry with { ReceivedTextEndExclusive = Math.Max(entry.ReceivedTextEndExclusive, proof.ReceivedEnd) };
    }
}
