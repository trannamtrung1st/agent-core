using System.Text.Json;
using AgentCore.Domain.Events;
using AgentCore.Domain.Triggers;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Credentials;
using AgentCore.Domain.Email;

namespace AgentCore.Infrastructure.Persistence;

internal static class EmailWatchTransitions
{
    internal static IReadOnlyList<EventSubscriptionSnapshot> Subscriptions(IEnumerable<Automation> automations, TriggerOwner owner) =>
        automations.Where(a => a.Owner == owner && a.Status == AutomationStatus.Active)
            .SelectMany(a => a.Triggers.Where(t => t.Enabled && t.Configuration is CoreEventTrigger { CoreEventKey: "email.message.received" })
                .Select(t => new EventSubscriptionSnapshot(a.AutomationId, a.Owner, t.Revision,
                    ((CoreEventTrigger)t.Configuration).FilterExpression, ((CoreEventTrigger)t.Configuration).Dispatch) { TriggerId = t.TriggerId })).ToArray();

    // Admission fences exactly the retained evidence, including its membership in the durable bucket.
    internal static IReadOnlyList<Guid>? AdmissionSources(CoreEventBucket bucket, string evidence)
    {
        if (!bucket.Sources.Any(s => s.Key == "email.message.received")) return [];
        try
        {
            using var document = JsonDocument.Parse(evidence);
            var context = document.RootElement.GetProperty("triggerContext");
            if (context.GetProperty("bucketId").GetGuid() != bucket.BucketId) return null;
            var retained = context.GetProperty("sources").EnumerateArray().ToArray();
            if (retained.Length == 0 || context.GetProperty("count").GetInt32() != retained.Length) return null;
            var ids = new HashSet<Guid>(); var emailIds = new List<Guid>();
            foreach (var entry in retained)
            {
                var id = entry.GetProperty("eventId").GetGuid();
                var source = bucket.Sources.SingleOrDefault(s => s.EventId == id);
                if (source is null || !ids.Add(id) || !JsonElement.DeepEquals(entry.GetProperty("data"), JsonSerializer.Deserialize<JsonElement>(source.DataJson))) return null;
                if (source.Key == "email.message.received") emailIds.Add(id);
            }
            return emailIds;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException) { return null; }
    }

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
            Cursor = changed ? null : s.Cursor, AuthorizationFingerprint = changed ? null : s.AuthorizationFingerprint, AuthorizationRevision = changed ? null : s.AuthorizationRevision,
            LeaseId = null, LeaseExpiresAtUtc = null, RecoveryCheckRequested = false, Paused = changed ? false : s.Paused, LastErrorCode = changed ? null : s.LastErrorCode, LastDiagnosticId = changed ? null : s.LastDiagnosticId,
            LastGapAtUtc = enabled && changed && s.Generation > 0 ? now : s.LastGapAtUtc,
            NextCheckAtUtc = enabled ? changed ? now : now.AddSeconds(interval) : null };
    }
    internal static EmailWatchState Claim(EmailWatchState s, Credential c, DateTimeOffset now)
    {
        var fingerprint = Fingerprint(c); var changed = s.AuthorizationFingerprint is not null && s.AuthorizationFingerprint != fingerprint;
        return s with { RuntimeRevision = s.RuntimeRevision + 1, LeaseId = Guid.NewGuid(), LeaseExpiresAtUtc = now.AddSeconds(90),
            Generation = s.Generation + (changed ? 1 : 0), Cursor = changed ? null : s.Cursor, AuthorizationFingerprint = fingerprint, AuthorizationRevision = s.TenantId is null ? null : c.Revision,
            LastGapAtUtc = changed ? now : s.LastGapAtUtc };
    }
    internal static bool Valid(EmailWatchState s, EmailWatchClaim claim, Credential? c, DateTimeOffset now) =>
        s.Enabled && s.LeaseId == claim.State.LeaseId && s.RuntimeRevision == claim.State.RuntimeRevision
        && s.Generation == claim.State.Generation && s.LeaseExpiresAtUtc > now && c is not null && Usable(c)
        && Fingerprint(c) == claim.State.AuthorizationFingerprint;
    internal static EmailWatchState Success(EmailWatchState s, EmailWatchBatch batch, bool emitted, DateTimeOffset now) => s with
    { Cursor = batch.NextCursor, LastCheckedAtUtc = now, LastSuccessAtUtc = now, LastEventAtUtc = emitted ? now : s.LastEventAtUtc,
        LastGapAtUtc = batch.CursorGap ? now : s.LastGapAtUtc, Generation = s.Generation + (batch.CursorGap ? 1 : 0),
        NextCheckAtUtc = batch.HasMore ? now.AddSeconds(15) : now.AddSeconds(s.IntervalSeconds), ConsecutiveFailures = 0,
        LastErrorCode = null, LastDiagnosticId = null, Paused = false, RecoveryCheckRequested = false, LeaseId = null, LeaseExpiresAtUtc = null, RuntimeRevision = s.RuntimeRevision + 1 };
    internal static EmailWatchState Failure(EmailWatchState s, SafeEmailWatchFailure f, DateTimeOffset now) => s with
    { LastCheckedAtUtc = now, LastErrorCode = f.Code, LastDiagnosticId = f.DiagnosticId, Paused = s.Paused || f.Permanent, RecoveryCheckRequested = false,
        ConsecutiveFailures = Math.Min(20, s.ConsecutiveFailures + 1), NextCheckAtUtc = now.AddSeconds(new[] { 60, 120, 300, 900 }[Math.Min(3, s.ConsecutiveFailures)]),
        LeaseId = null, LeaseExpiresAtUtc = null, RuntimeRevision = s.RuntimeRevision + 1 };
}
