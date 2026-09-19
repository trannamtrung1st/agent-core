using System.Globalization;
using System.Text.RegularExpressions;
using AgentCore.Application.Agents;
using AgentCore.Application.Events;
using AgentCore.Application.Sessions;
using AgentCore.Application.Speech;
using AgentCore.Contracts.Http;
using AgentCore.Domain.Conversation;

namespace AgentCore.Api.Mapping;

public static partial class HttpMapping
{
    public const int ProtocolVersion = 1;

    public static SessionMode ParseMode(string? mode) =>
        mode switch
        {
            null or "" or "text" => SessionMode.Text,
            "voice" => SessionMode.Voice,
            _ => throw AgentCoreErrors.Validation("mode must be text or voice.")
        };

    // Purpose metadata and completion-authority policy stay private; protocol-v1 status remains the public field.
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
            snapshot.DurableLastEntrySequence,
            activeResponseId?.ToString(),
            ProtocolVersion,
            snapshot.PauseReason,
            LifecycleTransition.ToWire(snapshot.LifecycleStatus),
            ToSpeechLocale(snapshot));

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
            Format(snapshot.UpdatedAt),
            snapshot.PauseReason,
            LifecycleTransition.ToWire(snapshot.LifecycleStatus));

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
            descriptor.VoiceAvailable,
            descriptor.Language);

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
                block.ArtifactId)).ToArray(),
            projected.FinishReason,
            projected.Attachments?.Select(item => new HistoryAttachmentResponse(
                item.AttachmentId.ToString(),
                item.DisplayName,
                item.ContentType)).ToArray());
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

    public static HostSessionViewResponse ToHostView(SessionSnapshot snapshot, Guid? activeResponseId)
    {
        var view = ToView(snapshot, activeResponseId);
        var purpose = snapshot.Purpose ?? SessionPurpose.OngoingDefault;
        var policy = snapshot.CompletionPolicy ?? SessionCompletionPolicy.Default;
        return new HostSessionViewResponse(
            view.SessionId,
            view.AgentId,
            view.AgentVersion,
            view.Mode,
            view.PendingMode,
            view.Status,
            view.CreatedAt,
            view.UpdatedAt,
            view.LastEntrySequence,
            view.ActiveResponseId,
            view.ProtocolVersion,
            view.PauseReason,
            view.LifecycleStatus,
            view.SpeechLocale,
            new HostSessionPurposeResponse(
                ToPurposeKind(purpose.Kind),
                purpose.Description,
                purpose.DeadlineAt is { } deadline ? Format(deadline) : null),
            new HostSessionCompletionPolicyResponse(
                ToAgentCompletion(policy.AgentCompletion),
                policy.UserCompletionAllowed,
                policy.UserCancellationAllowed),
            snapshot.LifecycleSource is { } source ? LifecycleTransition.ToSourceWire(source) : null,
            snapshot.LifecycleReason,
            snapshot.LifecycleChangedAt is { } changed ? Format(changed) : null);
    }

    public static SessionPurpose? ParseHostPurpose(
        HostSessionPurposeRequest? purpose,
        long? maxDurationSeconds,
        out TimeSpan? maxDuration)
    {
        maxDuration = null;
        if (maxDurationSeconds is { } seconds)
        {
            if (seconds <= 0)
            {
                throw AgentCoreErrors.Validation("maxDurationSeconds must be a positive integer.");
            }

            if (seconds > SessionLifecycle.MaxMaxDuration.TotalSeconds)
            {
                throw AgentCoreErrors.Validation(
                    $"maxDurationSeconds must be at most {(long)SessionLifecycle.MaxMaxDuration.TotalSeconds}.");
            }

            maxDuration = TimeSpan.FromSeconds(seconds);
        }

        if (purpose is null && maxDuration is null)
        {
            return null;
        }

        if (purpose?.DeadlineAt is not null && maxDuration is not null)
        {
            throw AgentCoreErrors.Validation("Provide deadlineAt or maxDurationSeconds, not both.");
        }

        DateTimeOffset? deadline = null;
        if (!string.IsNullOrWhiteSpace(purpose?.DeadlineAt))
        {
            var rawDeadline = purpose.DeadlineAt.Trim();
            if (!HasExplicitTimezone(rawDeadline))
            {
                throw AgentCoreErrors.Validation(
                    "deadlineAt must include an explicit timezone (Z or ±HH:mm).");
            }

            if (!DateTimeOffset.TryParse(
                    rawDeadline,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var parsed))
            {
                throw AgentCoreErrors.Validation("deadlineAt must be an ISO-8601 timestamp.");
            }

            deadline = parsed.ToUniversalTime();
        }

        return new SessionPurpose(ParsePurposeKind(purpose?.Kind), purpose?.Description, deadline);
    }

    public static SessionCompletionPolicy? ParseHostPolicy(HostSessionCompletionPolicyRequest? policy)
    {
        if (policy is null)
        {
            return null;
        }

        var defaults = SessionCompletionPolicy.Default;
        return new SessionCompletionPolicy(
            ParseAgentCompletion(policy.AgentCompletion),
            policy.UserCompletionAllowed ?? defaults.UserCompletionAllowed,
            policy.UserCancellationAllowed ?? defaults.UserCancellationAllowed);
    }

    public static SpeechLocaleResponse ToSpeechLocale(SessionSnapshot snapshot)
    {
        var resolved = SpeechLocale.Resolve(snapshot);
        return new SpeechLocaleResponse(
            resolved.Effective,
            SpeechLocale.ToWire(resolved.Source),
            resolved.Override);
    }

    public static string Format(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static SessionPurposeKind ParsePurposeKind(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            null or "" or "ongoing" => SessionPurposeKind.Ongoing,
            "goal" => SessionPurposeKind.Goal,
            _ => throw AgentCoreErrors.Validation("purpose.kind must be ongoing or goal.")
        };

    private static string ToPurposeKind(SessionPurposeKind kind) =>
        kind == SessionPurposeKind.Goal ? "goal" : "ongoing";

    private static AgentCompletionAuthority ParseAgentCompletion(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            null or "" or "disabled" => AgentCompletionAuthority.Disabled,
            "advisory" => AgentCompletionAuthority.Advisory,
            "allowed" => AgentCompletionAuthority.Allowed,
            _ => throw AgentCoreErrors.Validation("agentCompletion must be disabled, advisory, or allowed.")
        };

    private static string ToAgentCompletion(AgentCompletionAuthority authority) =>
        authority switch
        {
            AgentCompletionAuthority.Advisory => "advisory",
            AgentCompletionAuthority.Allowed => "allowed",
            _ => "disabled"
        };

    private static bool HasExplicitTimezone(string raw) =>
        raw.EndsWith("Z", StringComparison.OrdinalIgnoreCase)
        || DeadlineOffsetRegex().IsMatch(raw);

    [GeneratedRegex(@"[+-]\d{2}:\d{2}$", RegexOptions.CultureInvariant)]
    private static partial Regex DeadlineOffsetRegex();
}
