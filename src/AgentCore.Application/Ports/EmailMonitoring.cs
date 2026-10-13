using AgentCore.Domain.Credentials;
using AgentCore.Domain.Email;
using AgentCore.Domain.Events;

namespace AgentCore.Application.Ports;

public sealed record EmailWatchClaim(EmailWatchState State, Credential Account);
public sealed record EmailWatchBatch(IReadOnlyList<string> MessageRefs, EmailWatchCursor NextCursor,
    bool HasMore = false, bool CursorGap = false);
public sealed record EmailWatchNotification(CoreEventOccurrence Event, Guid BindingId, long BindingRevision,
    Guid AccountId, long Generation, string AuthorizationFingerprint);
public sealed record SafeEmailWatchFailure(string Code, Guid? DiagnosticId, bool Permanent);
public interface IEmailChangeFeed
{
    bool IsSynthetic => false;
    ValueTask<EmailWatchCursor> BaselineAsync(Credential account, CancellationToken ct);
    ValueTask<EmailWatchBatch> PollAsync(Credential account, EmailWatchCursor cursor, CancellationToken ct);
}
public interface IEmailWatchStore
{
    ValueTask<EmailWatchState> GetAsync(Guid accountId, CancellationToken ct = default);
    ValueTask<EmailWatchState> ConfigureAsync(Guid accountId, long expectedRevision, bool enabled, int intervalSeconds, DateTimeOffset now, CancellationToken ct = default);
    ValueTask CheckNowAsync(Guid accountId, long expectedRevision, DateTimeOffset now, CancellationToken ct = default);
    ValueTask<IReadOnlyList<EmailWatchClaim>> ClaimDueAsync(DateTimeOffset now, int limit, CancellationToken ct = default);
    // Cursor and all notifications commit together; stale claims return false without effects.
    ValueTask<bool> CommitAsync(EmailWatchClaim claim, EmailWatchBatch batch, IReadOnlyList<EmailWatchNotification> notifications, DateTimeOffset now, CancellationToken ct = default);
    ValueTask RecordFailureAsync(EmailWatchClaim claim, SafeEmailWatchFailure failure, DateTimeOffset now, CancellationToken ct = default);
    ValueTask<bool> CanDeliverAsync(Guid eventId, CancellationToken ct = default);
}
