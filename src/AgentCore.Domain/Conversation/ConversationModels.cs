using AgentCore.Domain.Definitions;

namespace AgentCore.Domain.Conversation;

public enum ConversationRole { User, Assistant }

public enum EntryStatus { Streaming, Completed, Interrupted, Failed }

public enum SessionMode { Text, Voice }

public enum SessionStatus { Created, Attached, Paused, Ending, Ended }

public sealed record ConversationAttachmentRef(
    Guid AttachmentId,
    string DisplayName,
    string ContentType);

public sealed record ConversationEntry(
    Guid EntryId,
    long Sequence,
    Guid? SourceEventId,
    ConversationRole Role,
    string Text,
    Guid? ResponseId,
    EntryStatus Status,
    SessionMode DeliveryMode,
    int HeardTextEndExclusive,
    int ReceivedTextEndExclusive,
    DateTimeOffset CreatedAt,
    ResponseEnvelope? Envelope = null,
    IReadOnlyList<ConversationAttachmentRef>? Attachments = null);

public sealed record UserProfile(
    Guid ProfileId,
    long Revision,
    IReadOnlyDictionary<string, string> Preferences,
    DateTimeOffset UpdatedAt);

public static class LocalUserProfile
{
    public static readonly Guid Id = Guid.Parse("019944af-0000-7000-8000-0000000000aa");

    public static readonly IReadOnlyList<string> AllowedKeys = ["language", "preferredName"];

    public static void Validate(IReadOnlyDictionary<string, string> preferences)
    {
        if (preferences.Count > 16)
        {
            throw new ArgumentOutOfRangeException(nameof(preferences), "Profile may contain at most 16 entries.");
        }

        var total = 0;
        foreach (var pair in preferences)
        {
            if (!AllowedKeys.Any(key => string.Equals(key, pair.Key, StringComparison.Ordinal)))
            {
                throw new ArgumentOutOfRangeException(nameof(preferences), $"Profile key '{pair.Key}' is not allowlisted.");
            }

            total += pair.Key.Length + pair.Value.Length;
        }

        if (total > 2000)
        {
            throw new ArgumentOutOfRangeException(nameof(preferences), "Profile preferences exceed 2000 characters.");
        }
    }
}

public static class SessionTitles
{
    public const string Default = "New chat";
    public const int MaxLength = 200;

    public static string FromUserText(string text)
    {
        var collapsed = string.Join(
            ' ',
            text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (collapsed.Length == 0)
        {
            return Default;
        }

        return collapsed.Length <= MaxLength ? collapsed : collapsed[..MaxLength].TrimEnd();
    }

    public static string FromAttachments(IReadOnlyList<string> displayNames, bool imageOnly)
    {
        if (displayNames.Count == 0)
        {
            return Default;
        }

        var first = AttachmentClassification.SanitizeDisplayName(displayNames[0]);
        if (imageOnly && displayNames.Count > 1)
        {
            return "Image conversation";
        }

        if (displayNames.Count == 1)
        {
            return first;
        }

        var suffix = $"Files: {first} +{displayNames.Count - 1}";
        return suffix.Length <= MaxLength ? suffix : suffix[..MaxLength].TrimEnd();
    }
}

public sealed record SessionSnapshot(
    int SchemaVersion,
    Guid SessionId,
    long Revision,
    AgentDefinition Definition,
    SessionMode Mode,
    SessionMode? PendingMode,
    SessionStatus Status,
    IReadOnlyList<ConversationEntry> Entries,
    string Summary,
    long SummarizedThroughEntrySequence,
    string? PendingTopic,
    Guid? ProfileId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? LastUserActivityAt = null,
    string? PauseReason = null,
    string Title = SessionTitles.Default,
    long RuntimeEpoch = 0,
    bool WorkspaceOwned = true,
    DateTimeOffset? ArchivedAt = null,
    DateTimeOffset? DurablyDeletedAt = null);
