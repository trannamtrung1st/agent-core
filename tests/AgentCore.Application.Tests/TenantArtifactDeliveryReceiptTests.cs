using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Tests;

public sealed class TenantArtifactDeliveryReceiptTests
{
    private static readonly Guid FileId = Guid.NewGuid();
    private static ConversationEntry Entry() => new(Guid.NewGuid(), 1, null, ConversationRole.Assistant, "Published.", Guid.NewGuid(), EntryStatus.Completed,
        SessionMode.Text, 0, 0, DateTimeOffset.UtcNow, Envelope: new("Published.", null, [new("b1", ResponseBlockKind.ArtifactReference, FileId.ToString("D"), FileId.ToString("D"), null, FileId.ToString("D"), false)]));
    private static readonly TenantSessionRoot Owner = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow);
    private static TenantDeliveryReceipt Proof(ConversationEntry entry) => new(Owner, new(Guid.NewGuid(), Guid.NewGuid(), Owner.CreatedByPrincipalId), 1,
        entry.EntryId, entry.Sequence, entry.ResponseId!.Value, entry.Text.Length, entry.Text.Length, [new("b1", ResponseBlockKind.ArtifactReference, FileId)]);

    [Fact]
    public void A_late_pre_envelope_writer_cannot_erase_acknowledged_block_credit_when_text_length_is_unchanged()
    {
        var prior = Entry(); var delivered = TenantDeliveryReceiptRules.Apply(Proof(prior), Owner, prior);
        var late = prior with { Envelope = prior.Envelope! with { Blocks = [] } };
        var carried = TenantDeliveryReceiptRules.Carry(late, delivered);
        Assert.True(Assert.Single(carried.Envelope!.Blocks).DisplayDelivered);
        Assert.Equal(delivered.ReceivedTextEndExclusive, carried.ReceivedTextEndExclusive);
    }
    [Fact]
    public void Block_credit_requires_exact_saved_kind_identity_and_unique_published_block()
    {
        var entry = Entry(); var proof = Proof(entry);
        foreach (var blocks in new IReadOnlyList<TenantDeliveredBlock>[] {
            [new("b1", ResponseBlockKind.ArtifactReference, Guid.NewGuid())], [new("other", ResponseBlockKind.ArtifactReference, FileId)],
            [new("b1", ResponseBlockKind.Unknown)], [proof.Blocks![0], proof.Blocks[0]] })
            Assert.Throws<AccessDeniedException>(() => TenantDeliveryReceiptRules.Apply(proof with { Blocks = blocks }, Owner, entry));
        var saved = TenantDeliveryReceiptRules.Apply(proof, Owner, entry);
        Assert.Equal(saved, TenantDeliveryReceiptRules.Apply(proof, Owner, saved));
        Assert.Equal(0, saved.HeardTextEndExclusive);
    }
}
