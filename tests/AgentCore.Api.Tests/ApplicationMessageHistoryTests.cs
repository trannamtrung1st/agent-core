using AgentCore.Api.Mapping;
using AgentCore.Domain.Conversation;

namespace AgentCore.Api.Tests;

public sealed class ApplicationMessageHistoryTests
{
    [Fact]
    public void History_role_is_application_message()
    {
        var text = "Still checking the order";
        var responseId = Guid.Parse("019944af-00ee-7000-8000-0000000000f2");
        var entry = new ConversationEntry(
            Guid.Parse("019944af-00ee-7000-8000-0000000000f1"),
            2,
            null,
            ConversationRole.ApplicationMessage,
            text,
            responseId,
            EntryStatus.Completed,
            SessionMode.Text,
            0,
            text.Length,
            DateTimeOffset.Parse("2026-09-30T12:00:00Z"),
            ApplicationMessageEffectKey: "v1:effect");
        var item = HttpMapping.ToHistoryItem(entry);
        Assert.Equal("applicationMessage", item.Role);
        Assert.Equal(text, item.Text);
        Assert.Equal("user", HttpMapping.ToHistoryRole(ConversationRole.User));
        Assert.Equal("assistant", HttpMapping.ToHistoryRole(ConversationRole.Assistant));
    }
}
