using AgentCore.Application.Email;
using AgentCore.Application.Observability;
using AgentCore.Application.Ports;
using AgentCore.Domain.Credentials;
using AgentCore.Domain.Email;
using AgentCore.Domain.Security;
using Microsoft.Extensions.Logging;

namespace AgentCore.Application.Security;

public sealed record TenantEmailMonitoringView(Guid AccountId, bool Enabled, int IntervalSeconds, long ConfigurationRevision,
    long CredentialRevision, long SecurityRevision, string Status, bool HostAvailable, bool AccountAvailable, bool CanManage, bool CanEnable, bool CanCheck,
    DateTimeOffset? LastCheckedAtUtc, DateTimeOffset? LastSuccessAtUtc, DateTimeOffset? LastEventAtUtc, DateTimeOffset? NextCheckAtUtc,
    DateTimeOffset? LastGapAtUtc, string? ErrorCode, Guid? DiagnosticId, bool CheckPending);

public sealed class TenantEmailMonitoringService(IIamStore security, ITenantCredentialStore credentials, ITenantEmailWatchStore watches,
    ITenantEmailChangeFeed feed, IdentityService identity, IamAuthorizer iam, EmailMonitoringOptions options, IIdGenerator ids, TimeProvider time,
    ILogger<TenantEmailMonitoringService> logger, IDiagnosticIdSource diagnostics)
{
    private readonly SemaphoreSlim cycle = new(1, 1);
    public async ValueTask<TenantEmailMonitoringView> GetAsync(Guid tenant, Guid accountId, HumanSessionReference human, CancellationToken ct = default)
    {
        var state = await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, human, ct).ConfigureAwait(false);
        var account = await Account(tenant, accountId, ct).ConfigureAwait(false);
        await TenantEmailWatchRules.RequireHumanAsync(state, human.PrincipalId, accountId, false, false, iam, ct).ConfigureAwait(false);
        var watch = await watches.ReadTenantEmailWatchAsync(tenant, accountId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        var now = time.GetUtcNow(); var available = feed.WatchAvailability(account.Value).Available; var host = options.Enabled && feed.HostAvailable;
        var changed = watch.AuthorizationFingerprint is not null && watch.AuthorizationFingerprint != EmailWatchAuthorization.Fingerprint(account.Value);
        var pending = watch.LeaseExpiresAtUtc > now || watch.Enabled && (!watch.Paused || watch.RecoveryCheckRequested || changed) && watch.NextCheckAtUtc <= now;
        var status = !watch.Enabled ? "Off" : !host ? "HostDisabled" : !available || watch.Paused && !changed ? "Paused"
            : changed ? "Initializing" : watch.LeaseExpiresAtUtc > now ? watch.Cursor is null ? "Initializing" : "Checking"
            : watch.LastErrorCode is not null ? "Retrying" : watch.Cursor is null ? "Initializing" : watch.LastGapAtUtc is not null ? "Gap" : "Monitoring";
        var scope = TenantCredentialRules.Scope(tenant, accountId);
        var manage = (await iam.CheckOwnedTenantAsync(state, human.PrincipalId, PermissionCatalog.Get("credentials.manage"), scope, ct).ConfigureAwait(false)).Allowed;
        var use = (await iam.CheckOwnedTenantAsync(state, human.PrincipalId, PermissionCatalog.Get("credentials.use"), scope, ct).ConfigureAwait(false)).Allowed;
        var final = await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, human, ct).ConfigureAwait(false);
        if (final.Tenant.Revision != state.Tenant.Revision) throw new SecurityConflictException("stale_revision");
        return new(accountId, watch.Enabled, watch.IntervalSeconds, watch.ConfigurationRevision, account.Value.Revision, state.Tenant.Revision,
            status, host, available, manage, manage && use, manage && use && host && available && watch.Enabled,
            watch.LastCheckedAtUtc, watch.LastSuccessAtUtc, watch.LastEventAtUtc, watch.NextCheckAtUtc, watch.LastGapAtUtc,
            watch.LastErrorCode, watch.LastDiagnosticId, pending && host && available);
    }
    public async ValueTask<TenantEmailMonitoringView> ChangeAsync(Guid tenant, Guid accountId, HumanSessionReference human,
        long securityRevision, long credentialRevision, long watchRevision, bool checkNow, bool enabled = false, int intervalSeconds = 300, CancellationToken ct = default)
    {
        var state = await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, human, ct).ConfigureAwait(false);
        var account = await Account(tenant, accountId, ct).ConfigureAwait(false);
        await TenantEmailWatchRules.RequireHumanAsync(state, human.PrincipalId, accountId, true, checkNow || enabled, iam, ct).ConfigureAwait(false);
        if (state.Tenant.Revision != securityRevision || account.Value.Revision != credentialRevision) throw new SecurityConflictException("stale_revision");
        if (checkNow && (!options.Enabled || !feed.HostAvailable || !feed.WatchAvailability(account.Value).Available)) throw new SecurityConflictException("email_monitoring_unavailable");
        var now = time.GetUtcNow(); var next = state with { Tenant = state.Tenant with { Revision = checked(state.Tenant.Revision + 1) } };
        var change = new TenantEmailWatchChange(human, credentialRevision, watchRevision, checkNow, enabled, intervalSeconds);
        var audit = new AuthorizationAudit(ids.NewId(), tenant, human.PrincipalId, checkNow ? "email.monitoring.check" : "email.monitoring.configure", accountId.ToString("D"), "allowed", "email_monitoring_changed", now);
        if (!await watches.TryChangeTenantEmailWatchAsync(next, securityRevision, accountId, change, audit, now, ct).ConfigureAwait(false)) throw new SecurityConflictException("stale_revision");
        return await GetAsync(tenant, accountId, human, ct).ConfigureAwait(false);
    }
    private async ValueTask<TenantCredential> Account(Guid tenant, Guid account, CancellationToken ct) =>
        await credentials.FindTenantCredentialAsync(tenant, account, ct).ConfigureAwait(false) is { Value.Purpose: CredentialPurpose.EmailAccount, Value.EmailAccount: not null } result
            ? result : throw new AccessDeniedException("resource_unavailable");

    public async ValueTask<int> RunOnceAsync(CancellationToken ct = default)
    {
        if (!options.Enabled || !feed.HostAvailable) return 0;
        await cycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var claims = await watches.ClaimDueTenantEmailWatchesAsync(time.GetUtcNow(), 3, ct).ConfigureAwait(false);
            await Parallel.ForEachAsync(claims, new ParallelOptions { MaxDegreeOfParallelism = 3, CancellationToken = ct }, async (claim, token) => {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60), time);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
                var current = claim.Account; var started = time.GetTimestamp(); var outcome = "denied";
                async ValueTask<Credential> Current(CancellationToken check)
                {
                    if (!options.Enabled || !feed.HostAvailable) throw new AccessDeniedException("email_watch_host_unavailable");
                    current = await watches.RecheckTenantEmailWatchAsync(claim, time.GetUtcNow(), check).ConfigureAwait(false) ?? throw new AccessDeniedException("email_watch_source_unavailable");
                    if (!feed.WatchAvailability(current).Available) throw new AccessDeniedException("email_watch_source_unavailable");
                    return current;
                }
                async ValueTask Maintain(CredentialOAuthCheck value, CancellationToken check)
                {
                    if (!await watches.TryMaintainTenantEmailWatchAsync(claim, current.SecretRevision, value, time.GetUtcNow(), check).ConfigureAwait(false))
                        throw new AccessDeniedException("email_watch_source_changed");
                }
                try
                {
                    await Current(linked.Token).ConfigureAwait(false);
                    var batch = await feed.ReadTenantChangesAsync(claim.TenantId, current, claim.State.Cursor, Current, Maintain, linked.Token).ConfigureAwait(false);
                    TenantEmailWatchRules.Batch(batch); await Current(linked.Token).ConfigureAwait(false);
                    outcome = await watches.TryCommitTenantEmailWatchAsync(claim, batch, time.GetUtcNow(), linked.Token).ConfigureAwait(false) ? "success" : "denied";
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { outcome = "cancelled"; }
                catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
                {
                    var hostUnavailable = error is AccessDeniedException { ReasonCode: "email_watch_host_unavailable" };
                    var permanent = !hostUnavailable && (error is AccessDeniedException or MailAuthorizationUnavailableException or AgentCore.Application.Sessions.AgentCoreException { StatusCode: 403 or 404 }
                        or HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden });
                    var code = error is InvalidOperationException { Message: "email_watch_fanout_limit" } ? "email_watch_fanout_limit"
                        : hostUnavailable ? "email_watch_host_unavailable" : permanent ? "email_watch_access_required" : "email_watch_temporary_failure";
                    var diagnostic = HandledFailureLog.Warning(logger, new InvalidOperationException(code), "email.watch.tenant", code,
                        new DiagnosticContext(ProviderAlias: "email-watch", CredentialId: claim.Account.CredentialId), diagnostics: diagnostics);
                    await watches.RecordTenantEmailWatchFailureAsync(claim, new(code, diagnostic, permanent), time.GetUtcNow(), token).ConfigureAwait(false);
                    outcome = permanent ? "reconnect_required" : "temporary_failure";
                }
                finally { RuntimeTelemetry.RecordEmailOperation(claim.Account.EmailAccount!.Preset == EmailProviderPreset.Gmail ? "gmail" : "imap-smtp", claim.State.Cursor is null ? "watch.baseline" : "watch.poll", outcome, feed.IsSynthetic, time.GetElapsedTime(started).TotalMilliseconds); }
            }).ConfigureAwait(false);
            return claims.Count;
        }
        finally { cycle.Release(); }
    }
}
