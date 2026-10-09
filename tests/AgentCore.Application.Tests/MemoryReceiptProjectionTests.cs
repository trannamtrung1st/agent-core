using AgentCore.Application.Events;
using AgentCore.Application.Memory;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Memory;

namespace AgentCore.Application.Tests;

public sealed class MemoryReceiptProjectionTests
{
    [Theory]
    [InlineData(MemoryProposalSource.AgentInferred, MemoryProposalOperation.Upsert, MemoryAdmissionStatus.Stored, MemoryReceipt.Silent, null)]
    [InlineData(MemoryProposalSource.AgentInferred, MemoryProposalOperation.Upsert, MemoryAdmissionStatus.Rejected, MemoryReceipt.Silent, null)]
    [InlineData(MemoryProposalSource.UserExplicit, MemoryProposalOperation.Upsert, MemoryAdmissionStatus.Stored, MemoryReceipt.Indicator, "Remembered")]
    [InlineData(MemoryProposalSource.UserExplicit, MemoryProposalOperation.Upsert, MemoryAdmissionStatus.Updated, MemoryReceipt.Indicator, "Remembered")]
    [InlineData(MemoryProposalSource.UserExplicit, MemoryProposalOperation.Upsert, MemoryAdmissionStatus.AlreadyStored, MemoryReceipt.Indicator, "Remembered")]
    [InlineData(MemoryProposalSource.UserExplicit, MemoryProposalOperation.Upsert, MemoryAdmissionStatus.Rejected, MemoryReceipt.Explicit, "Not saved: project codename.")]
    [InlineData(MemoryProposalSource.UserExplicit, MemoryProposalOperation.Upsert, MemoryAdmissionStatus.Unavailable, MemoryReceipt.Explicit, "Memory unavailable: project codename.")]
    [InlineData(MemoryProposalSource.UserExplicit, MemoryProposalOperation.Delete, MemoryAdmissionStatus.Deleted, MemoryReceipt.Indicator, "Forgotten")]
    [InlineData(MemoryProposalSource.UserExplicit, MemoryProposalOperation.Delete, MemoryAdmissionStatus.Rejected, MemoryReceipt.Explicit, "Deletion rejected: project codename.")]
    [InlineData(MemoryProposalSource.UserExplicit, MemoryProposalOperation.Delete, MemoryAdmissionStatus.Unavailable, MemoryReceipt.Explicit, "Memory unavailable: project codename.")]
    [InlineData(MemoryProposalSource.UserExplicit, MemoryProposalOperation.Resolve, MemoryAdmissionStatus.Resolved, MemoryReceipt.Indicator, "Closed")]
    [InlineData(MemoryProposalSource.UserExplicit, MemoryProposalOperation.Resolve, MemoryAdmissionStatus.AlreadyResolved, MemoryReceipt.Indicator, "Closed")]
    [InlineData(MemoryProposalSource.UserExplicit, MemoryProposalOperation.Resolve, MemoryAdmissionStatus.NotFound, MemoryReceipt.Explicit, "No matching open loop found: project codename.")]
    [InlineData(MemoryProposalSource.UserExplicit, MemoryProposalOperation.Delete, MemoryAdmissionStatus.NotFound, MemoryReceipt.Explicit, "No matching memory found: project codename.")]
    [InlineData(MemoryProposalSource.UserExplicit, MemoryProposalOperation.Delete, MemoryAdmissionStatus.ApprovalRequired, MemoryReceipt.Explicit, "Not forgotten: project codename. Forgetting requires approval.")]
    [InlineData(MemoryProposalSource.AgentInferred, MemoryProposalOperation.Delete, MemoryAdmissionStatus.ApprovalRequired, MemoryReceipt.Explicit, "Not forgotten: project codename. Forgetting requires approval.")]
    [InlineData(MemoryProposalSource.UserExplicit, MemoryProposalOperation.Delete, MemoryAdmissionStatus.PartiallyDeleted, MemoryReceipt.Explicit, "Partially forgotten: project codename. Some scopes could not be deleted.")]
    public void Visibility_follows_the_user_request_not_the_reply(
        MemoryProposalSource source,
        MemoryProposalOperation operation,
        MemoryAdmissionStatus status,
        string presentation,
        string? label)
    {
        var receipt = MemoryReceiptProjection.FromResult(new MemoryAdmissionResult(
            status,
            new MemoryProposal(operation, MemoryKind.Fact, "project codename", "Atlas", null, source)));
        Assert.Equal(presentation, receipt.Presentation);
        var visible = PublicMemoryReceipt.From(receipt);
        if (label is null)
        {
            Assert.Null(visible);
            return;
        }

        Assert.Equal(label, visible!.Label);
        Assert.DoesNotContain("Atlas", visible.Label, StringComparison.Ordinal);
    }

    [Fact]
    public void Public_history_keeps_the_reply_and_projects_only_visible_receipts()
    {
        var envelope = new ResponseEnvelope(
            "Got it.",
            "Got it.",
            [],
            ResponseSpeechMode.Custom,
            [
                new MemoryReceipt("stored", "upsert", "agentInferred", "User identity", ["identityUser"], MemoryReceipt.Silent),
                new MemoryReceipt("rejected", "upsert", "userExplicit", "project codename", null, MemoryReceipt.Explicit)
            ]);
        var entry = new ConversationEntry(
            Guid.NewGuid(),
            2,
            null,
            ConversationRole.Assistant,
            envelope.DisplayText,
            Guid.NewGuid(),
            EntryStatus.Completed,
            SessionMode.Text,
            0,
            envelope.DisplayText.Length,
            DateTimeOffset.UnixEpoch,
            envelope);
        var projected = PublicHistory.FromEntry(entry);
        Assert.Equal("Got it.", projected.Text);
        Assert.Null(projected.SpeechText);
        var receipt = Assert.Single(projected.MemoryReceipts!);
        Assert.Equal("Not saved: project codename.", receipt.Label);
        Assert.Equal(MemoryReceipt.Explicit, receipt.Presentation);
    }
}
