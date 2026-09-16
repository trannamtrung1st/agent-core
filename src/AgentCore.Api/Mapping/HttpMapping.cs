using System.Globalization;
using AgentCore.Application.Agents;
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

    public static SessionCatalogItemResponse ToCatalogItem(SessionSnapshot snapshot) =>
        new(
            snapshot.SessionId.ToString(),
            snapshot.Title,
            snapshot.Definition.Id,
            snapshot.Definition.Version,
            ToStatus(snapshot.Status),
            snapshot.ArchivedAt is not null,
            snapshot.Status == SessionStatus.Ended,
            snapshot.WorkspaceOwned,
            snapshot.RuntimeEpoch,
            snapshot.Revision,
            Format(snapshot.CreatedAt),
            Format(snapshot.UpdatedAt));

    public static AttachmentResponse ToAttachment(AgentCore.Application.Ports.AttachmentRecord record) =>
        new(
            record.AttachmentId.ToString(),
            record.SessionId.ToString(),
            record.DisplayName,
            record.ContentType,
            record.ByteSize,
            record.Sha256Hex,
            record.State == AttachmentState.Bound ? "bound" : "pending",
            record.Readable,
            record.EntryId?.ToString(),
            Format(record.CreatedAt),
            record.ExpiresAt is { } expires ? Format(expires) : null);

    public static AgentDescriptorResponse ToAgent(PublicAgentDescriptor descriptor) =>
        new(
            descriptor.Id,
            descriptor.Version,
            descriptor.Name,
            descriptor.Role,
            descriptor.Description,
            descriptor.VoiceAvailable);

    public static KnowledgeDocumentResponse ToKnowledge(KnowledgeDocument document) =>
        new(
            document.Identity,
            document.Title,
            document.Citation,
            document.Content,
            document.SourceVersion,
            Format(document.RetrievedAt));

    public static WorkspaceNodeResponse ToWorkspaceNode(AgentCore.Application.Ports.WorkspaceNode node) =>
        new(node.LogicalPath, node.Directory, node.ByteSize, node.Writable);

    public static ArtifactResponse ToArtifact(AgentCore.Application.Ports.ArtifactRecord record) =>
        new(
            record.ArtifactId.ToString(),
            record.SessionId.ToString(),
            record.DisplayName,
            record.ContentType,
            record.ByteSize,
            record.Sha256Hex,
            record.SourceAttachmentId?.ToString(),
            record.WorkspaceLogicalPath,
            Format(record.CreatedAt));

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
            Format(projected.CreatedAt),
            projected.Blocks.Select(block => new HistoryBlockResponse(
                block.BlockId,
                block.Kind,
                block.Text,
                block.FallbackText,
                block.AttachmentId,
                block.ArtifactId)).ToArray());
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
