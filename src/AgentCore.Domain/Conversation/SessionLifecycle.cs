namespace AgentCore.Domain.Conversation;

public enum SessionLifecycleStatus
{
    Active,
    Paused,
    Completed,
    Expired,
    Cancelled,
    Ended
}

public enum SessionPurposeKind
{
    Ongoing,
    Goal
}

public enum AgentCompletionAuthority
{
    Disabled,
    Advisory,
    Allowed
}

public enum LifecycleTransitionSource
{
    Host,
    System,
    User,
    Agent,
    Legacy
}

public sealed record SessionPurpose(
    SessionPurposeKind Kind,
    string? Description = null,
    DateTimeOffset? DeadlineAt = null,
    IReadOnlyDictionary<string, string>? Metadata = null)
{
    public static SessionPurpose OngoingDefault { get; } = new(SessionPurposeKind.Ongoing);
}

public sealed record SessionCompletionPolicy(
    AgentCompletionAuthority AgentCompletion,
    bool UserCompletionAllowed,
    bool UserCancellationAllowed)
{
    public static SessionCompletionPolicy Default { get; } = new(
        AgentCompletionAuthority.Disabled,
        UserCompletionAllowed: true,
        UserCancellationAllowed: true);
}

public static class SessionLifecycle
{
    public const int MaxPurposeDescriptionLength = 2000;
    public const int MaxMetadataEntries = 16;
    public const int MaxMetadataCharacters = 4000;

    public static SessionLifecycleStatus FromProtocolStatus(SessionStatus status) =>
        status switch
        {
            SessionStatus.Paused => SessionLifecycleStatus.Paused,
            SessionStatus.Ended => SessionLifecycleStatus.Ended,
            _ => SessionLifecycleStatus.Active
        };

    public static SessionLifecycleStatus Align(SessionStatus status, SessionLifecycleStatus current) =>
        current is SessionLifecycleStatus.Completed
            or SessionLifecycleStatus.Expired
            or SessionLifecycleStatus.Cancelled
            ? current
            : FromProtocolStatus(status);

    public static SessionPurpose ResolvePurpose(
        SessionPurpose? purpose,
        DateTimeOffset now,
        TimeSpan? maxDuration = null)
    {
        var resolved = purpose ?? SessionPurpose.OngoingDefault;
        if (maxDuration is { } duration)
        {
            if (duration <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(maxDuration), "maxDuration must be positive.");
            }

            if (resolved.DeadlineAt is null)
            {
                resolved = resolved with { DeadlineAt = now.Add(duration) };
            }
        }

        Validate(resolved);
        return resolved;
    }

    public static void Validate(SessionPurpose purpose)
    {
        if (purpose.Description is { Length: > MaxPurposeDescriptionLength })
        {
            throw new ArgumentOutOfRangeException(
                nameof(purpose),
                $"Purpose description may contain at most {MaxPurposeDescriptionLength} characters.");
        }

        var metadata = purpose.Metadata;
        if (metadata is null || metadata.Count == 0)
        {
            return;
        }

        if (metadata.Count > MaxMetadataEntries)
        {
            throw new ArgumentOutOfRangeException(
                nameof(purpose),
                $"Purpose metadata may contain at most {MaxMetadataEntries} entries.");
        }

        var total = 0;
        foreach (var pair in metadata)
        {
            total += pair.Key.Length + pair.Value.Length;
        }

        if (total > MaxMetadataCharacters)
        {
            throw new ArgumentOutOfRangeException(
                nameof(purpose),
                $"Purpose metadata exceed {MaxMetadataCharacters} characters.");
        }
    }
}
