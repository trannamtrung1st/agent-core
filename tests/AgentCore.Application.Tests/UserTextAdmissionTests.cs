using AgentCore.Application.Events;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Tests;

public sealed class UserTextAdmissionTests
{
    [Fact]
    public void ValidatePendingBatch_rejects_when_message_count_exceeds_limit()
    {
        var entries = new List<ConversationEntry>();
        for (var index = 0; index < UserTextAdmission.MaxPendingBatchMessages; index++)
        {
            entries.Add(UserEntry(index, "x"));
        }

        var ex = Assert.Throws<AgentCoreException>(() => UserTextAdmission.ValidatePendingBatch(entries, "y"));
        Assert.Equal("ValidationError", ex.Code);
    }

    [Fact]
    public void ValidatePendingBatch_rejects_when_text_units_exceed_limit()
    {
        var chunk = new string('a', UserTextAdmission.MaxPendingBatchTextUnits / 3 + 1);
        var entries = new[]
        {
            UserEntry(1, chunk),
            UserEntry(2, chunk)
        };

        var ex = Assert.Throws<AgentCoreException>(() => UserTextAdmission.ValidatePendingBatch(entries, chunk));
        Assert.Equal("ValidationError", ex.Code);
    }

    [Fact]
    public void Fingerprint_changes_when_behavior_changes()
    {
        var interrupt = UserTextAdmission.Fingerprint(null, "Hello", [], UserTextBehavior.Interrupt);
        var queue = UserTextAdmission.Fingerprint(null, "Hello", [], UserTextBehavior.Queue);
        Assert.NotEqual(interrupt, queue);
    }

    [Fact]
    public void FingerprintFromStoredEntry_assumes_interrupt_for_legacy_rows_without_fingerprint()
    {
        var entry = UserEntry(1, "queued legacy");
        var fingerprint = UserTextAdmission.FingerprintFromStoredEntry(entry);
        var expected = UserTextAdmission.Fingerprint(null, entry.Text, [], UserTextBehavior.Interrupt);
        Assert.Equal(expected, fingerprint);
    }

    private static ConversationEntry UserEntry(int sequence, string text) =>
        new(
            Guid.NewGuid(),
            sequence,
            Guid.NewGuid(),
            ConversationRole.User,
            text,
            null,
            EntryStatus.Completed,
            SessionMode.Text,
            text.Length,
            text.Length,
            DateTimeOffset.UtcNow);
}
