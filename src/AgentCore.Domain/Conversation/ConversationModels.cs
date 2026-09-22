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
    IReadOnlyList<ConversationAttachmentRef>? Attachments = null,
    string? SourceAdmissionFingerprint = null,
    string? FinishReason = null,
    string? InterruptReason = null,
    ModelGenerationProvenance? ModelProvenance = null);

public sealed record UserProfile(
    Guid ProfileId,
    long Revision,
    IReadOnlyDictionary<string, UserProfileValue> Preferences,
    DateTimeOffset UpdatedAt);

public static class LocalUserProfile
{
    public static readonly Guid Id = Guid.Parse("019944af-0000-7000-8000-0000000000aa");

    public static readonly IReadOnlyList<string> AllowedKeys = ["language", "preferredName", "locale", "timeZone"];

    public const int MaxPreferredNameLength = 128;
    public const int MaxLanguageLength = 32;
    public const int MaxLocaleLength = 35;
    public const int MaxTimeZoneLength = 64;

    /// <summary>
    /// Historical local-demo seed. It is not a user-supplied name: there is no profile CRUD UI.
    /// </summary>
    public const string InventedPreferredNameSeed = "friend";

    public static UserProfileValue ApplicationProfileValue(string value, DateTimeOffset updatedAt) =>
        new(value, UserProfileValueSource.ApplicationProfile, updatedAt);

    public static IReadOnlyDictionary<string, UserProfileValue> CreateDefaultSeed(DateTimeOffset updatedAt) =>
        new Dictionary<string, UserProfileValue>(StringComparer.Ordinal)
        {
            ["language"] = ApplicationProfileValue("en", updatedAt)
        };

    public static IReadOnlyDictionary<string, string> ForPrompt(IReadOnlyDictionary<string, UserProfileValue>? preferences)
    {
        if (preferences is null || preferences.Count == 0)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        var trusted = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in preferences)
        {
            if (string.IsNullOrWhiteSpace(pair.Value.Value))
            {
                continue;
            }

            if (IsInventedPreferredName(pair.Key, pair.Value.Value))
            {
                continue;
            }

            trusted[pair.Key] = pair.Value.Value;
        }

        return trusted;
    }

    public static bool HasPreferredName(IReadOnlyDictionary<string, string> trustedPreferences) =>
        trustedPreferences.TryGetValue("preferredName", out var name) && !string.IsNullOrWhiteSpace(name);

    public static bool InventedPreferredNameNeedsRemoval(IReadOnlyDictionary<string, UserProfileValue> preferences) =>
        preferences.TryGetValue("preferredName", out var value)
        && IsInventedPreferredName("preferredName", value.Value);

    public static IReadOnlyDictionary<string, UserProfileValue> WithoutInventedPreferredName(
        IReadOnlyDictionary<string, UserProfileValue> preferences)
    {
        if (!InventedPreferredNameNeedsRemoval(preferences))
        {
            return preferences;
        }

        var cleaned = new Dictionary<string, UserProfileValue>(StringComparer.Ordinal);
        foreach (var pair in preferences)
        {
            if (string.Equals(pair.Key, "preferredName", StringComparison.Ordinal))
            {
                continue;
            }

            cleaned[pair.Key] = pair.Value;
        }

        return cleaned;
    }

    public static void Validate(IReadOnlyDictionary<string, UserProfileValue> preferences)
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

            ValidateField(pair.Key, pair.Value.Value);
            total += pair.Key.Length + pair.Value.Value.Length;
        }

        if (total > 2000)
        {
            throw new ArgumentOutOfRangeException(nameof(preferences), "Profile preferences exceed 2000 characters.");
        }
    }

    public static void ValidateField(string key, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentOutOfRangeException(nameof(value), $"Profile field '{key}' cannot be empty.");
        }

        switch (key)
        {
            case "preferredName":
                var trimmedName = value.Trim();
                if (trimmedName.Length > MaxPreferredNameLength)
                {
                    throw new ArgumentOutOfRangeException(nameof(value), "preferredName exceeds the maximum length.");
                }

                if (string.Equals(trimmedName, InventedPreferredNameSeed, StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentOutOfRangeException(nameof(value), "preferredName cannot be the historical seed value.");
                }

                break;
            case "language":
                if (value.Length > MaxLanguageLength || !UserProfileFieldPatterns.IsLanguageTag(value))
                {
                    throw new ArgumentOutOfRangeException(nameof(value), "language is not a valid bounded language tag.");
                }

                break;
            case "locale":
                if (value.Length > MaxLocaleLength || !UserProfileFieldPatterns.IsLocaleTag(value))
                {
                    throw new ArgumentOutOfRangeException(nameof(value), "locale is not a valid bounded locale tag.");
                }

                break;
            case "timeZone":
                if (value.Length > MaxTimeZoneLength || !UserProfileFieldPatterns.IsTimeZoneId(value))
                {
                    throw new ArgumentOutOfRangeException(nameof(value), "timeZone is not a valid bounded timezone identifier.");
                }

                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(key), $"Profile key '{key}' is not allowlisted.");
        }
    }

    private static bool IsInventedPreferredName(string key, string value) =>
        string.Equals(key, "preferredName", StringComparison.Ordinal)
        && string.Equals(value.Trim(), InventedPreferredNameSeed, StringComparison.OrdinalIgnoreCase);
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

public static class SummaryFormats
{
    public const int Legacy = 0;
    public const int Semantic = 1;
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
    DateTimeOffset? DurablyDeletedAt = null,
    long LastEntrySequence = 0,
    SessionLifecycleStatus LifecycleStatus = SessionLifecycleStatus.Active,
    SessionPurpose? Purpose = null,
    SessionCompletionPolicy? CompletionPolicy = null,
    string? LifecycleReason = null,
    LifecycleTransitionSource? LifecycleSource = null,
    DateTimeOffset? LifecycleChangedAt = null,
    string? SpeechLocaleOverride = null,
    SessionModelSelection? ModelSelection = null,
    int SummaryFormatVersion = SummaryFormats.Legacy,
    DateTimeOffset? SummaryGeneratedAt = null,
    ModelGenerationProvenance? SummaryModel = null,
    Guid? AgentInstanceId = null,
    AgentIdentity? PinnedPersona = null)
{
    public long DurableLastEntrySequence
    {
        get
        {
            var fromEntries = 0L;
            for (var index = 0; index < Entries.Count; index++)
            {
                var sequence = Entries[index].Sequence;
                if (sequence > fromEntries)
                {
                    fromEntries = sequence;
                }
            }

            return LastEntrySequence >= fromEntries ? LastEntrySequence : fromEntries;
        }
    }
}
