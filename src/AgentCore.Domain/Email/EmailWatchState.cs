namespace AgentCore.Domain.Email;

// References only. Provider transports and protected authorization stay in Infrastructure.
public enum EmailWatchCursorKind { GmailHistory, ImapUid, Synthetic }
public sealed record EmailWatchCursor(EmailWatchCursorKind Kind, string? HistoryId = null,
    uint UidValidity = 0, uint LastUid = 0, string? PageToken = null, int Offset = 0);
public sealed record EmailWatchState(Guid AccountId, bool Enabled = false, int IntervalSeconds = 300,
    long ConfigurationRevision = 0, long Generation = 0, long RuntimeRevision = 0,
    EmailWatchCursor? Cursor = null, string? AuthorizationFingerprint = null,
    DateTimeOffset? LastCheckedAtUtc = null, DateTimeOffset? LastSuccessAtUtc = null,
    DateTimeOffset? LastEventAtUtc = null, DateTimeOffset? NextCheckAtUtc = null,
    DateTimeOffset? LastGapAtUtc = null, string? LastErrorCode = null, Guid? LastDiagnosticId = null,
    int ConsecutiveFailures = 0, bool Paused = false, Guid? LeaseId = null, DateTimeOffset? LeaseExpiresAtUtc = null);
