using System.Globalization;
using AgentCore.Application.Events;
using AgentCore.Application.Sessions;
using AgentCore.Contracts.Http;
using AgentCore.Domain.Conversation;

namespace AgentCore.Api.Mapping;

public static class HttpMapping
{
    public const int ProtocolVersion = 1;

    public static SessionMode ParseMode(string? mode) =>
        mode switch
        {
            null or "" or "text" => SessionMode.Text,
            "voice" => SessionMode.Voice,
            _ => throw AgentCoreErrors.Validation("mode must be text or voice.")
        };

    public static SessionViewResponse ToView(SessionSnapshot snapshot, Guid? activeResponseId) =>
        new(
            snapshot.SessionId.ToString(),
            snapshot.Definition.Id,
            snapshot.Definition.Version,
            ToMode(snapshot.Mode),
            snapshot.PendingMode is { } pending ? ToMode(pending) : null,
            ToStatus(snapshot.Status),
            Format(snapshot.CreatedAt),
            Format(snapshot.UpdatedAt),
            snapshot.Entries.Count == 0 ? 0 : snapshot.Entries[^1].Sequence,
            activeResponseId?.ToString(),
            ProtocolVersion);

    public static AgentDescriptorResponse ToAgent(PublicAgentDescriptor descriptor) =>
        new(
            descriptor.Id,
            descriptor.Version,
            descriptor.Name,
            descriptor.Role,
            descriptor.Description,
            descriptor.VoiceAvailable);

    public static HistoryItemResponse ToHistoryItem(ConversationEntry entry)
    {
        var projected = PublicHistory.FromEntry(entry);
        return new HistoryItemResponse(
            projected.EntryId.ToString(),
            projected.Sequence,
            projected.SourceEventId?.ToString(),
            projected.Role == ConversationRole.User ? "user" : "assistant",
            projected.Text,
            projected.ResponseId?.ToString(),
            ToEntryStatus(projected.Status),
            ToMode(projected.DeliveryMode),
            projected.HeardTextEndExclusive,
            projected.ReceivedTextEndExclusive,
            Format(projected.CreatedAt));
    }

    public static string ToMode(SessionMode mode) => mode == SessionMode.Voice ? "voice" : "text";

    public static string ToStatus(SessionStatus status) => status switch
    {
        SessionStatus.Created => "created",
        SessionStatus.Attached => "attached",
        SessionStatus.Paused => "paused",
        SessionStatus.Ending => "ending",
        SessionStatus.Ended => "ended",
        _ => status.ToString().ToLowerInvariant()
    };

    public static string ToEntryStatus(EntryStatus status) => status switch
    {
        EntryStatus.Streaming => "streaming",
        EntryStatus.Completed => "completed",
        EntryStatus.Interrupted => "interrupted",
        EntryStatus.Failed => "failed",
        _ => status.ToString().ToLowerInvariant()
    };

    public static string Format(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}
