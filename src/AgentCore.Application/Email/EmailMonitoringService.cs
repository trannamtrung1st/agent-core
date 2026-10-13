using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentCore.Application.Observability;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Triggers;
using AgentCore.Domain.Credentials;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Email;
using AgentCore.Domain.Events;
using AgentCore.Domain.Triggers;
using Microsoft.Extensions.Logging;

namespace AgentCore.Application.Email;

public sealed class EmailMonitoringOptions { public bool Enabled { get; set; } = true; }
public sealed record EmailMonitoringView(Guid AccountId, bool Enabled, int IntervalSeconds, long ConfigurationRevision,
    string Status, bool HostAvailable, bool AccountAvailable, int SubscribedInstances,
    DateTimeOffset? LastCheckedAtUtc, DateTimeOffset? LastSuccessAtUtc, DateTimeOffset? LastEventAtUtc,
    DateTimeOffset? NextCheckAtUtc, DateTimeOffset? LastGapAtUtc, string? ErrorCode, Guid? DiagnosticId, bool CheckPending);

public sealed class EmailMonitoringService(IEmailWatchStore watches, IEmailChangeFeed feed, ICredentialStore credentials,
    IAgentCredentialBindingStore bindings, IAgentInstanceStore instances, ITriggerStore triggers, ITriggerAdmissionGuard guard,
    IEmailProviderFactory providers, EmailMonitoringOptions options, TimeProvider time,
    ILogger<EmailMonitoringService> logger, IDiagnosticIdSource diagnostics)
{
    public bool HostAvailable => options.Enabled && providers.IsEnabled;
    private async ValueTask<Credential> Account(Guid id, CancellationToken ct) => await credentials.GetAsync(id, ct) is { Purpose: CredentialPurpose.EmailAccount, EmailAccount: not null } c
        ? c : throw AgentCoreErrors.NotFound("Email account was not found.");
    private bool Available(Credential c) => c.Status == CredentialStatus.Active && providers.CanUse(c.EmailAccount!);
    private async ValueTask<IReadOnlyList<AgentCredentialBinding>> Recipients(Guid accountId, CancellationToken ct)
    {
        var result = new List<AgentCredentialBinding>();
        foreach (var group in (await bindings.ListBindingsAsync(null, ct)).Where(b => b.CredentialId == accountId).GroupBy(b => b.AgentInstanceId))
        {
            if ((await instances.FindAsync(group.Key, ct))?.Lifecycle != AgentInstanceLifecycle.Active) continue;
            var owner = new TriggerOwner(group.Key, LocalUserProfile.Id);
            if ((await guard.EvaluateAsync(owner, TriggerSourceKind.CoreEvent, ct)).Kind != TriggerAdmissionDecisionKind.Allow) continue;
            if ((await triggers.ListAsync(owner, AutomationStatus.Active, ct)).Any(a => a.Triggers.Any(t => t.Enabled && t.Configuration is CoreEventTrigger { CoreEventKey: "email.message.received" })))
                result.Add(group.OrderBy(b => b.BindingId).First());
        }
        return result;
    }
    public async ValueTask<EmailMonitoringView> GetAsync(Guid id, CancellationToken ct = default)
    {
        var c = await Account(id, ct); var s = await watches.GetAsync(id, ct); var now = time.GetUtcNow();
        var authChanged = s.AuthorizationFingerprint is not null && s.AuthorizationFingerprint != EmailWatchAuthorization.Fingerprint(c);
        var pending = s.LeaseExpiresAtUtc > now || s.Enabled && (!s.Paused || authChanged) && s.NextCheckAtUtc <= now;
        var status = !s.Enabled ? "Off" : !HostAvailable ? "HostDisabled" : !Available(c) || s.Paused && !authChanged ? "Paused"
            : authChanged ? "Initializing" : s.LeaseExpiresAtUtc > now ? s.Cursor is null ? "Initializing" : "Checking" : s.LastErrorCode is not null ? "Retrying"
            : s.Cursor is null || authChanged ? "Initializing" : s.LastGapAtUtc is not null ? "Gap" : "Monitoring";
        return new(id, s.Enabled, s.IntervalSeconds, s.ConfigurationRevision, status, HostAvailable, Available(c), (await Recipients(id, ct)).Count,
            s.LastCheckedAtUtc, s.LastSuccessAtUtc, s.LastEventAtUtc, s.NextCheckAtUtc, s.LastGapAtUtc, s.LastErrorCode, s.LastDiagnosticId, pending && HostAvailable && Available(c));
    }
    public async ValueTask<EmailMonitoringView> ConfigureAsync(Guid id, long expected, bool enabled, int interval, CancellationToken ct = default)
    { await watches.ConfigureAsync(id, expected, enabled, interval, time.GetUtcNow(), ct); return await GetAsync(id, ct); }
    public async ValueTask<EmailMonitoringView> CheckAsync(Guid id, long expected, CancellationToken ct = default)
    { if (!HostAvailable || !Available(await Account(id, ct))) throw AgentCoreErrors.Conflict("Monitoring is unavailable. Review host settings and account access.");
        await watches.CheckNowAsync(id, expected, time.GetUtcNow(), ct); return await GetAsync(id, ct); }
    public async ValueTask<int> RunOnceAsync(CancellationToken ct = default)
    {
        if (!HostAvailable) return 0;
        var claims = await watches.ClaimDueAsync(time.GetUtcNow(), 3, ct);
        await Parallel.ForEachAsync(claims, new ParallelOptions { MaxDegreeOfParallelism = 3, CancellationToken = ct }, async (claim, token) =>
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60), time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
            var operation = claim.State.Cursor is null ? "watch.baseline" : "watch.poll";
            var started = time.GetTimestamp(); var outcome = "success";
            using var activity = RuntimeTelemetry.Activity.StartActivity("email." + operation);
            try
            {
                if (!HostAvailable || !Available(await Account(claim.State.AccountId, linked.Token))) { outcome = "denied"; return; }
                var batch = claim.State.Cursor is null ? new EmailWatchBatch([], await feed.BaselineAsync(claim.Account, linked.Token))
                    : await feed.PollAsync(claim.Account, claim.State.Cursor, linked.Token);
                if (batch.MessageRefs.Count > 25 || batch.MessageRefs.Any(r => r.Length is < 1 or > 512 || r.Any(char.IsControl))) throw new InvalidDataException("Invalid change-feed references.");
                var notifications = new List<EmailWatchNotification>();
                if (!batch.CursorGap && batch.MessageRefs.Count > 0)
                foreach (var binding in await Recipients(claim.State.AccountId, linked.Token))
                foreach (var reference in batch.MessageRefs.Distinct(StringComparer.Ordinal))
                {
                    var key = $"email.message.received:{claim.State.AccountId:N}:{claim.State.Generation}:{reference}:{binding.AgentInstanceId:N}";
                    var id = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(key)).AsSpan(0, 16));
                    var e = new CoreEventOccurrence(id, key, new(binding.AgentInstanceId, LocalUserProfile.Id), "email.message.received", time.GetUtcNow(),
                        JsonSerializer.Serialize(new { accountId = claim.State.AccountId, accountRef = binding.Reference, messageRef = reference, provider = claim.Account.EmailAccount!.Preset.ToString() }));
                    notifications.Add(new(e, binding.BindingId, binding.Revision, claim.State.AccountId, claim.State.Generation, claim.State.AuthorizationFingerprint!));
                }
                if (!HostAvailable) { outcome = "denied"; return; }
                var commitStarted = time.GetTimestamp();
                var committed = await watches.CommitAsync(claim, batch, notifications, time.GetUtcNow(), linked.Token);
                outcome = committed ? "success" : "denied";
                RuntimeTelemetry.RecordEmailOperation(Provider(claim.Account), batch.CursorGap ? "watch.gap" : "watch.commit", outcome, feed.IsSynthetic, time.GetElapsedTime(commitStarted).TotalMilliseconds);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { outcome = "cancelled"; }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                var permanent = ex is AgentCoreException { StatusCode: 403 or 404 } or MailAuthorizationUnavailableException or HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden };
                // Provider exceptions can contain transport challenges; log a normalized cause only.
                var code = permanent ? "email_watch_access_required" : "email_watch_temporary_failure";
                var id = HandledFailureLog.Warning(logger, new InvalidOperationException(code), "email." + operation, code,
                    new DiagnosticContext(ProviderAlias: Provider(claim.Account), CredentialId: claim.State.AccountId), diagnostics: diagnostics);
                await watches.RecordFailureAsync(claim, new(code, id, permanent), time.GetUtcNow(), token); outcome = permanent ? "reconnect_required" : "temporary_failure";
            }
            finally { RuntimeTelemetry.RecordEmailOperation(Provider(claim.Account), operation, outcome, feed.IsSynthetic, time.GetElapsedTime(started).TotalMilliseconds); }
        });
        return claims.Count;
    }
    private static string Provider(Credential c) => c.EmailAccount!.Preset == EmailProviderPreset.Gmail ? "gmail" : "imap-smtp";
}
// Infrastructure classifies permanent TLS/auth/endpoint rejection without exposing vendor objects.
public sealed class MailAuthorizationUnavailableException : Exception;
