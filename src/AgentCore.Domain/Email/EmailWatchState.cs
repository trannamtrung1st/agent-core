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
    int ConsecutiveFailures = 0, bool Paused = false, Guid? LeaseId = null, DateTimeOffset? LeaseExpiresAtUtc = null, bool RecoveryCheckRequested = false)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public Guid? TenantId { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public long? AuthorizationRevision { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public Guid? LastCommitLeaseId { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public long? LastCommitRuntimeRevision { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public bool? LastCommitHadCursor { get; init; }
}

public static class EmailWatchAuthorization
{
    // Logical account updates fence all in-flight work; OAuth secret refresh alone does not.
    public static string Fingerprint(AgentCore.Domain.Credentials.Credential account) => Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            System.Text.Json.JsonSerializer.Serialize(new { account.EmailAccount, AccountRevision = account.Revision }))));
}
