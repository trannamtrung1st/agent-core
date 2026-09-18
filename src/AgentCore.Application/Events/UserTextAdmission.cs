using System.Text.Json;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Events;

public static class UserTextAdmission
{
    public const int MaxPendingBatchMessages = 20;
    public const int MaxPendingBatchTextUnits = 24000;

    public static string Fingerprint(
        string? responseId,
        string text,
        IReadOnlyList<Guid> attachmentIds,
        UserTextBehavior behavior)
    {
        var payload = new WirePayload
        {
            Text = text,
            AttachmentIds = attachmentIds.Count == 0
                ? null
                : attachmentIds.Select(id => id.ToString("D")).ToArray(),
            Behavior = UserTextBehaviors.WireName(behavior)
        };
        var json = JsonSerializer.Serialize(payload);
        return $"user.text|{responseId}|{json}";
    }

    public static string FingerprintFromStoredEntry(ConversationEntry entry)
    {
        var attachmentIds = entry.Attachments?.Select(item => item.AttachmentId).ToArray() ?? [];
        return Fingerprint(null, entry.Text, attachmentIds, UserTextBehavior.Interrupt);
    }

    public static void ValidatePendingBatch(IReadOnlyList<ConversationEntry> entries, string additionalText)
    {
        var suffix = TrailingUserSuffix.Of(entries);
        var count = suffix.Count + 1;
        if (count > MaxPendingBatchMessages)
        {
            throw AgentCoreErrors.Validation(
                $"Queued user messages exceed the limit of {MaxPendingBatchMessages}.");
        }

        var characters = suffix.Sum(entry => entry.Text.Length) + additionalText.Length;
        if (characters > MaxPendingBatchTextUnits)
        {
            throw AgentCoreErrors.Validation(
                $"Queued user text exceeds the limit of {MaxPendingBatchTextUnits} UTF-16 code units.");
        }
    }

    private sealed class WirePayload
    {
        public string Text { get; set; } = "";
        public string[]? AttachmentIds { get; set; }
        public string? Behavior { get; set; }
    }
}
