using AgentCore.Domain.Conversation;

namespace AgentCore.Domain.Tests;

public sealed class TrailingUserSuffixTests
{
    [Fact]
    public void Empty_history_has_no_pending_batch()
    {
        Assert.Empty(TrailingUserSuffix.Of([]));
        Assert.False(TrailingUserSuffix.HasPending([]));
    }

    [Fact]
    public void Trailing_users_after_terminal_assistant_are_the_batch()
    {
        var now = new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero);
        var entries = new[]
        {
            User(1, "U1", now),
            Assistant(2, EntryStatus.Completed, now),
            User(3, "U2", now),
            User(4, "U3", now)
        };
        var batch = TrailingUserSuffix.Of(entries);
        Assert.Equal(["U2", "U3"], batch.Select(entry => entry.Text).ToArray());
        Assert.True(TrailingUserSuffix.HasPending(entries));
    }

    [Fact]
    public void Assistant_after_users_clears_the_pending_batch()
    {
        var now = new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero);
        var entries = new[]
        {
            User(1, "U1", now),
            Assistant(2, EntryStatus.Completed, now),
            User(3, "U2", now),
            User(4, "U3", now),
            Assistant(5, EntryStatus.Streaming, now)
        };
        Assert.Empty(TrailingUserSuffix.Of(entries));
        Assert.False(TrailingUserSuffix.HasPending(entries));
    }

    [Fact]
    public void Interrupted_or_failed_assistant_at_the_end_is_not_a_user_suffix()
    {
        var now = new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero);
        Assert.Empty(TrailingUserSuffix.Of(
        [
            User(1, "U2", now),
            User(2, "U3", now),
            Assistant(3, EntryStatus.Interrupted, now)
        ]));
        Assert.Empty(TrailingUserSuffix.Of(
        [
            User(1, "U2", now),
            Assistant(2, EntryStatus.Failed, now)
        ]));
    }

    [Fact]
    public void Batch_follows_durable_sequence_not_list_reversal_only()
    {
        var now = new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero);
        var batch = TrailingUserSuffix.Of(
        [
            User(10, "first", now),
            User(11, "second", now)
        ]);
        Assert.Equal([10L, 11L], batch.Select(entry => entry.Sequence).ToArray());
    }

    private static ConversationEntry User(long sequence, string text, DateTimeOffset now) =>
        new(
            Guid.Parse($"019944af-0000-7000-8000-{sequence:D12}"),
            sequence,
            Guid.Parse($"019944af-0000-7000-8000-{sequence:D12}"),
            ConversationRole.User,
            text,
            null,
            EntryStatus.Completed,
            SessionMode.Text,
            text.Length,
            text.Length,
            now);

    private static ConversationEntry Assistant(long sequence, EntryStatus status, DateTimeOffset now) =>
        new(
            Guid.Parse($"019944af-0000-7000-8000-{sequence:D12}"),
            sequence,
            null,
            ConversationRole.Assistant,
            "R",
            Guid.Parse($"019944af-0000-7000-8000-{sequence + 100:D12}"),
            status,
            SessionMode.Text,
            1,
            1,
            now);
}
