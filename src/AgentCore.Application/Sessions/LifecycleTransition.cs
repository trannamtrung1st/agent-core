using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Sessions;

public static class LifecycleTransition
{
    public static SessionSnapshot Apply(
        SessionSnapshot snapshot,
        SessionLifecycleStatus target,
        LifecycleTransitionSource source,
        DateTimeOffset now,
        string? reason = null)
    {
        if (!IsTerminal(snapshot) && SessionLifecycle.DeadlineElapsed(snapshot.Purpose, now))
        {
            snapshot = ApplyCore(snapshot, SessionLifecycleStatus.Expired, LifecycleTransitionSource.System, now, "deadline");
            if (target != SessionLifecycleStatus.Expired)
            {
                throw AgentCoreErrors.Validation("Session has expired.");
            }

            return snapshot;
        }

        if (IsTerminal(snapshot))
        {
            if (snapshot.LifecycleStatus == target)
            {
                return snapshot;
            }

            throw AgentCoreErrors.Validation($"Session is already {ToWire(snapshot.LifecycleStatus)}.");
        }

        if (snapshot.LifecycleStatus == target)
        {
            return snapshot;
        }

        Authorize(source, target, snapshot.CompletionPolicy ?? SessionCompletionPolicy.Default);
        if (!SessionLifecycle.Allows(snapshot.LifecycleStatus, target))
        {
            throw AgentCoreErrors.Validation("Lifecycle transition is not allowed.");
        }

        return ApplyCore(snapshot, target, source, now, reason);
    }

    public static bool IsTerminal(SessionSnapshot snapshot) =>
        SessionLifecycle.IsTerminal(snapshot.LifecycleStatus) || snapshot.Status is SessionStatus.Ended or SessionStatus.Ending;

    public static string ToWire(SessionLifecycleStatus status) =>
        status switch
        {
            SessionLifecycleStatus.Active => "active",
            SessionLifecycleStatus.Paused => "paused",
            SessionLifecycleStatus.Completed => "completed",
            SessionLifecycleStatus.Expired => "expired",
            SessionLifecycleStatus.Cancelled => "cancelled",
            SessionLifecycleStatus.Ended => "ended",
            _ => status.ToString().ToLowerInvariant()
        };

    public static SessionLifecycleStatus Parse(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "active" => SessionLifecycleStatus.Active,
            "paused" => SessionLifecycleStatus.Paused,
            "completed" => SessionLifecycleStatus.Completed,
            "expired" => SessionLifecycleStatus.Expired,
            "cancelled" or "canceled" => SessionLifecycleStatus.Cancelled,
            "ended" => SessionLifecycleStatus.Ended,
            _ => throw AgentCoreErrors.Validation("target must be a lifecycleStatus value.")
        };

    public static LifecycleTransitionSource ParseSource(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            null or "" or "user" => LifecycleTransitionSource.User,
            "host" => LifecycleTransitionSource.Host,
            "system" => LifecycleTransitionSource.System,
            "agent" => LifecycleTransitionSource.Agent,
            "legacy" => LifecycleTransitionSource.Legacy,
            _ => throw AgentCoreErrors.Validation("source must be host, system, user, agent, or legacy.")
        };

    /// <summary>
    /// First-party/user lifecycle callers are always User. Request JSON cannot self-promote.
    /// </summary>
    public static LifecycleTransitionSource UserFacingSource(string? requested)
    {
        _ = requested;
        return LifecycleTransitionSource.User;
    }

    public static string ToSourceWire(LifecycleTransitionSource source) =>
        source switch
        {
            LifecycleTransitionSource.Host => "host",
            LifecycleTransitionSource.System => "system",
            LifecycleTransitionSource.User => "user",
            LifecycleTransitionSource.Agent => "agent",
            LifecycleTransitionSource.Legacy => "legacy",
            _ => source.ToString().ToLowerInvariant()
        };

    private static void Authorize(
        LifecycleTransitionSource source,
        SessionLifecycleStatus target,
        SessionCompletionPolicy policy)
    {
        if (source is LifecycleTransitionSource.Host
            or LifecycleTransitionSource.System
            or LifecycleTransitionSource.Legacy)
        {
            return;
        }

        if (source == LifecycleTransitionSource.User)
        {
            if (target == SessionLifecycleStatus.Completed && !policy.UserCompletionAllowed)
            {
                throw AgentCoreErrors.Forbidden("User completion is not allowed for this session.");
            }

            if (target == SessionLifecycleStatus.Cancelled && !policy.UserCancellationAllowed)
            {
                throw AgentCoreErrors.Forbidden("User cancellation is not allowed for this session.");
            }

            if (target is SessionLifecycleStatus.Expired or SessionLifecycleStatus.Ended)
            {
                throw AgentCoreErrors.Forbidden("Users cannot expire or end a session through lifecycle transition.");
            }

            return;
        }

        if (source == LifecycleTransitionSource.Agent)
        {
            if (target != SessionLifecycleStatus.Completed
                || policy.AgentCompletion != AgentCompletionAuthority.Allowed)
            {
                throw AgentCoreErrors.Forbidden("Agent completion is not allowed for this session.");
            }
        }
    }

    private static SessionSnapshot ApplyCore(
        SessionSnapshot snapshot,
        SessionLifecycleStatus target,
        LifecycleTransitionSource source,
        DateTimeOffset now,
        string? reason)
    {
        return target switch
        {
            SessionLifecycleStatus.Paused => snapshot with
            {
                Status = SessionStatus.Paused,
                LifecycleStatus = SessionLifecycleStatus.Paused,
                PendingMode = null,
                RuntimeEpoch = snapshot.RuntimeEpoch + 1,
                PauseReason = string.IsNullOrWhiteSpace(reason) ? "manual" : reason,
                LifecycleReason = reason,
                LifecycleSource = source,
                LifecycleChangedAt = now,
                UpdatedAt = now
            },
            SessionLifecycleStatus.Active => snapshot with
            {
                Status = snapshot.Status == SessionStatus.Attached ? SessionStatus.Attached : SessionStatus.Created,
                LifecycleStatus = SessionLifecycleStatus.Active,
                PendingMode = null,
                PauseReason = null,
                RuntimeEpoch = snapshot.Status == SessionStatus.Paused
                    ? snapshot.RuntimeEpoch + 1
                    : snapshot.RuntimeEpoch,
                LastUserActivityAt = snapshot.Status == SessionStatus.Paused ? now : snapshot.LastUserActivityAt,
                LifecycleReason = reason,
                LifecycleSource = source,
                LifecycleChangedAt = now,
                UpdatedAt = now
            },
            _ => snapshot with
            {
                Status = SessionStatus.Ended,
                LifecycleStatus = target,
                PendingMode = null,
                PauseReason = null,
                RuntimeEpoch = snapshot.RuntimeEpoch + 1,
                LifecycleReason = reason,
                LifecycleSource = source,
                LifecycleChangedAt = now,
                UpdatedAt = now
            }
        };
    }
}
