using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Credentials;
using AgentCore.Domain.Email;

namespace AgentCore.Infrastructure.Persistence;

internal static class EmailWatchTransitions
{
    internal static Credential Require(Credential? c) => c is { Purpose: CredentialPurpose.EmailAccount, EmailAccount: not null }
        ? c : throw AgentCoreErrors.NotFound("Email account was not found.");
    internal static bool Usable(Credential c) => c.Status == CredentialStatus.Active && c.EmailAccount?.IsUsable == true;
    // OAuth secret rotation is independent; Custom password/profile changes fence the cursor.
    internal static string Fingerprint(Credential c) => EmailWatchAuthorization.Fingerprint(c);
    internal static void Revision(EmailWatchState s, long expected)
    { if (expected < 0 || s.ConfigurationRevision != expected) throw AgentCoreErrors.Conflict("Monitoring revision is stale. Reload and review your changes."); }
    internal static EmailWatchState Configure(EmailWatchState s, long expected, bool enabled, int interval, DateTimeOffset now)
    {
        Revision(s, expected);
        if (interval is not (60 or 300 or 900)) throw AgentCoreErrors.Validation("Monitoring interval must be 60, 300 or 900 seconds.");
        var changed = s.Enabled != enabled;
        return s with { Enabled = enabled, IntervalSeconds = interval, ConfigurationRevision = s.ConfigurationRevision + 1,
            RuntimeRevision = s.RuntimeRevision + 1, Generation = s.Generation + (changed ? 1 : 0),
            Cursor = changed ? null : s.Cursor, AuthorizationFingerprint = changed ? null : s.AuthorizationFingerprint,
            LeaseId = null, LeaseExpiresAtUtc = null, Paused = false, LastErrorCode = null, LastDiagnosticId = null,
            NextCheckAtUtc = enabled ? changed ? now : now.AddSeconds(interval) : null };
    }
    internal static EmailWatchState Claim(EmailWatchState s, Credential c, DateTimeOffset now)
    {
        var fingerprint = Fingerprint(c); var changed = s.AuthorizationFingerprint is not null && s.AuthorizationFingerprint != fingerprint;
        return s with { RuntimeRevision = s.RuntimeRevision + 1, LeaseId = Guid.NewGuid(), LeaseExpiresAtUtc = now.AddSeconds(90),
            Generation = s.Generation + (changed ? 1 : 0), Cursor = changed ? null : s.Cursor, AuthorizationFingerprint = fingerprint,
            LastGapAtUtc = changed ? now : s.LastGapAtUtc, Paused = false };
    }
    internal static bool Valid(EmailWatchState s, EmailWatchClaim claim, Credential? c, DateTimeOffset now) =>
        s.Enabled && s.LeaseId == claim.State.LeaseId && s.RuntimeRevision == claim.State.RuntimeRevision
        && s.Generation == claim.State.Generation && s.LeaseExpiresAtUtc > now && c is not null && Usable(c)
        && Fingerprint(c) == claim.State.AuthorizationFingerprint;
    internal static EmailWatchState Success(EmailWatchState s, EmailWatchBatch batch, bool emitted, DateTimeOffset now) => s with
    { Cursor = batch.NextCursor, LastCheckedAtUtc = now, LastSuccessAtUtc = now, LastEventAtUtc = emitted ? now : s.LastEventAtUtc,
        LastGapAtUtc = batch.CursorGap ? now : s.LastGapAtUtc, Generation = s.Generation + (batch.CursorGap ? 1 : 0),
        NextCheckAtUtc = batch.HasMore ? now.AddSeconds(15) : now.AddSeconds(s.IntervalSeconds), ConsecutiveFailures = 0,
        LastErrorCode = null, LastDiagnosticId = null, Paused = false, LeaseId = null, LeaseExpiresAtUtc = null, RuntimeRevision = s.RuntimeRevision + 1 };
    internal static EmailWatchState Failure(EmailWatchState s, SafeEmailWatchFailure f, DateTimeOffset now) => s with
    { LastCheckedAtUtc = now, LastErrorCode = f.Code, LastDiagnosticId = f.DiagnosticId, Paused = f.Permanent,
        ConsecutiveFailures = Math.Min(20, s.ConsecutiveFailures + 1), NextCheckAtUtc = now.AddSeconds(new[] { 60, 120, 300, 900 }[Math.Min(3, s.ConsecutiveFailures)]),
        LeaseId = null, LeaseExpiresAtUtc = null, RuntimeRevision = s.RuntimeRevision + 1 };
}
