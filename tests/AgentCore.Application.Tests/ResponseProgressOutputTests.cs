using AgentCore.Application.Events;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Tests;

public sealed class ResponseProgressOutputTests
{
    [Fact]
    public void Progress_output_does_not_duplicate_response_ownership()
    {
        var properties = typeof(ResponseProgressOutput).GetProperties();
        Assert.DoesNotContain(
            properties,
            property => string.Equals(property.Name, "ResponseId", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(typeof(SessionOutput).GetProperties(), property => property.Name == "ResponseId");
        Assert.IsAssignableFrom<OutputPayload>(
            new ResponseProgressOutput(ResponseProgressKind.Preparing, ResponseProgressState.Started));
    }

    [Fact]
    public void Conversation_history_types_do_not_carry_progress()
    {
        Assert.DoesNotContain(
            typeof(ConversationEntry).GetProperties(),
            property => property.Name.Contains("Progress", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            typeof(PublicHistoryEntry).GetProperties(),
            property => property.Name.Contains("Progress", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            typeof(SessionReadyProjection).GetProperties(),
            property => property.Name.Contains("Progress", StringComparison.OrdinalIgnoreCase));
    }
}
