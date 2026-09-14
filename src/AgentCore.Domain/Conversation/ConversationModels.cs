using AgentCore.Domain.Definitions;

namespace AgentCore.Domain.Conversation;

public enum ConversationRole { User, Assistant }

public enum EntryStatus { Streaming, Completed, Interrupted, Failed }

public enum SessionMode { Text, Voice }

public enum SessionStatus { Created, Attached, Paused, Ending, Ended }

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
    DateTimeOffset CreatedAt);

public sealed record UserProfile(
    Guid ProfileId,
    long Revision,
    IReadOnlyDictionary<string, string> Preferences,
    DateTimeOffset UpdatedAt);

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
    DateTimeOffset UpdatedAt);
