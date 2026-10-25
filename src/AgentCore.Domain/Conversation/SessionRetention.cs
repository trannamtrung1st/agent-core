namespace AgentCore.Domain.Conversation;

public enum SessionRetentionKind { Saved, Temporary }

/// <summary>Immutable Chat policy. Logical access ends at the deadline, independently of cleanup.</summary>
public static class SessionRetention
{
    public static void Validate(SessionSnapshot session)
    {
        if (!Enum.IsDefined(session.RetentionKind)
            || session.RetainAfterTerminalDays is < 1 or > 3650
            || session.Origin.Kind != SessionOriginKind.UserChat && (session.RetentionKind != SessionRetentionKind.Saved
                || session.RetainAfterTerminalDays is not null || session.RetentionDueAtUtc is not null)
            || session.RetentionKind == SessionRetentionKind.Saved && session.RetentionDueAtUtc !=
                (SessionLifecycle.IsTerminal(session.LifecycleStatus) && session.RetainAfterTerminalDays is { } days
                    ? (session.LifecycleChangedAt ?? throw new ArgumentException("Terminal retention needs an authoritative timestamp.")).AddDays(days) : null)
            || session.RetentionKind == SessionRetentionKind.Temporary
                && (session.Origin.Kind != SessionOriginKind.UserChat || session.RetainAfterTerminalDays is not null
                    || session.RetentionDueAtUtc != session.CreatedAt.AddHours(24)))
            throw new ArgumentException("Invalid immutable Session retention policy.");
    }

    public static bool IsDue(SessionSnapshot session, DateTimeOffset now) =>
        session.RetentionDueAtUtc is { } due && due <= now;

    public static DateTimeOffset? TerminalDue(SessionSnapshot session, DateTimeOffset terminalAt) =>
        session.RetentionKind == SessionRetentionKind.Temporary ? session.RetentionDueAtUtc
            : session.RetainAfterTerminalDays is { } days ? terminalAt.AddDays(days) : null;
}
