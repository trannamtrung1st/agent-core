using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Agents;

/// <summary>
/// Delivered assistant text for prompt, compaction, and memory admission.
/// Interrupt reasons stay metadata and are not semantic text.
/// </summary>
public static class AssistantSemanticProjection
{
    public static string Text(ConversationEntry entry)
    {
        if (entry.DeliveryMode == SessionMode.Voice)
        {
            var spoken = entry.Envelope?.SpeechText ?? entry.Text;
            var end = Math.Clamp(entry.HeardTextEndExclusive, 0, spoken.Length);
            return spoken[..end];
        }

        var received = Math.Clamp(entry.ReceivedTextEndExclusive, 0, entry.Text.Length);
        return entry.Text[..received];
    }
}
