using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Security;

public static class TenantInputReplayRules
{
    public static bool SameParts(IReadOnlyList<UserMessagePart>? left, IReadOnlyList<UserMessagePart>? right) =>
        System.Text.Json.JsonSerializer.Serialize(UserMessageContent.WithoutLabels(left), UserMessageContent.Json) == System.Text.Json.JsonSerializer.Serialize(UserMessageContent.WithoutLabels(right), UserMessageContent.Json);
    public static void Match(AgentRun run, ConversationEntry entry, string text, IReadOnlyList<Guid> attachments, TenantInputInterruption? interruption, SessionMode mode, IReadOnlyList<UserMessagePart>? parts = null)
    {
        if (!SameParts(entry.Parts, parts) || entry.DeliveryMode != mode || entry.Text != text || !(entry.Attachments ?? []).Select(a => a.AttachmentId).SequenceEqual(attachments)
            || run.Admission.Activation.EvidenceJson != TenantInputInterruptionRules.Evidence(interruption))
            throw new SecurityConflictException("message_id_conflict");
    }
}
