using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Security;

public static class TenantInputReplayRules
{
    public static void Match(AgentRun run, ConversationEntry entry, string text, IReadOnlyList<Guid> attachments, TenantInputInterruption? interruption, SessionMode mode)
    {
        if (entry.DeliveryMode != mode || entry.Text != text || !(entry.Attachments ?? []).Select(a => a.AttachmentId).SequenceEqual(attachments)
            || run.Admission.Activation.EvidenceJson != TenantInputInterruptionRules.Evidence(interruption))
            throw new SecurityConflictException("message_id_conflict");
    }
}
