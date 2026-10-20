using System.Text.Json;
using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Triggers;
using AgentCore.Domain.Credentials;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Email;
using AgentCore.Domain.Events;
using AgentCore.Domain.Security;
using AgentCore.Domain.Triggers;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore : ITenantEmailWatchStore
{
    private static IQueryable<EmailWatchRecord> OwnedWatches(AgentCoreDbContext db, Guid tenant) => db.EmailWatches.IgnoreQueryFilters().Where(w => w.TenantId == tenant
        && db.Set<TenantCredentialRecord>().Any(c => c.TenantId == tenant && c.CredentialId == w.AccountId && c.DeletedAt == null));
    private static async Task<Credential?> WatchAccount(AgentCoreDbContext db, Guid tenant, Guid account, CancellationToken ct)
    {
        var row = await OwnedCredentials(db, tenant).Include(c => c.EmailAccount).AsNoTracking().SingleOrDefaultAsync(c => c.CredentialId == account.ToString("D"), ct).ConfigureAwait(false);
        return row is null || row.Purpose != nameof(CredentialPurpose.EmailAccount) || row.EmailAccount is null ? null : SqliteCredentialStore.Map(row);
    }
    public async ValueTask<EmailWatchState?> ReadTenantEmailWatchAsync(Guid tenantId, Guid accountId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        if (await WatchAccount(db, tenantId, accountId, ct).ConfigureAwait(false) is null) return null;
        var row = await OwnedWatches(db, tenantId).AsNoTracking().SingleOrDefaultAsync(w => w.AccountId == accountId.ToString("D"), ct).ConfigureAwait(false);
        return row is null ? new(accountId) { TenantId = tenantId } : SqliteEmailWatchStore.Read(row);
    }
    public ValueTask<bool> TryChangeTenantEmailWatchAsync(TenantSecurityState next, long expectedSecurityRevision, Guid accountId,
        TenantEmailWatchChange change, AuthorizationAudit audit, DateTimeOffset now, CancellationToken ct = default)
    {
        now = DateTimeOffset.FromUnixTimeMilliseconds(now.ToUnixTimeMilliseconds());
        TenantEmailWatchRules.Change(next, expectedSecurityRevision, accountId, change, audit);
        return WriteTenantResourceAsync(next, expectedSecurityRevision, audit, async db => {
            var tenant = next.Tenant.TenantId; await RequireHumanSessionAsync(db, tenant, change.Human, ct).ConfigureAwait(false);
            var account = await WatchAccount(db, tenant, accountId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
            var row = await OwnedWatches(db, tenant).SingleOrDefaultAsync(w => w.AccountId == accountId.ToString("D"), ct).ConfigureAwait(false);
            var state = row is null ? new EmailWatchState(accountId) { TenantId = tenant } : SqliteEmailWatchStore.Read(row);
            if (state.TenantId != tenant || account.Revision != change.CredentialRevision || state.ConfigurationRevision != change.WatchRevision) return false;
            var before = (await ReadTenantAsync(db, tenant, ct).ConfigureAwait(false))!;
            await TenantEmailWatchRules.RequireHumanAsync(before, change.Human.PrincipalId, accountId, true, change.CheckNow || change.Enabled, new IamAuthorizer(this), ct).ConfigureAwait(false);
            EmailWatchState updated;
            if (change.CheckNow)
            {
                if (!state.Enabled || !EmailWatchTransitions.Usable(account)) throw new SecurityConflictException("email_monitoring_unavailable");
                updated = state.LeaseExpiresAtUtc > now ? state : state with { NextCheckAtUtc = now, RecoveryCheckRequested = true, RuntimeRevision = checked(state.RuntimeRevision + 1) };
            }
            else updated = EmailWatchTransitions.Configure(state, change.WatchRevision, change.Enabled, change.IntervalSeconds, now);
            if (row is null) { row = new() { AccountId = accountId.ToString("D"), TenantId = tenant }; db.EmailWatches.Add(row); }
            SqliteEmailWatchStore.Write(row, updated); return true;
        }, ct);
    }
    private static async ValueTask<Credential?> ValidWatch(AgentCoreDbContext db, TenantEmailWatchClaim claim, DateTimeOffset now, CancellationToken ct)
    {
        if (claim.TenantId == Guid.Empty || claim.State.TenantId != claim.TenantId
            || !await db.Set<Tenant>().AnyAsync(t => t.TenantId == claim.TenantId && t.Status == IdentityStatus.Active, ct).ConfigureAwait(false)) return null;
        var row = await OwnedWatches(db, claim.TenantId).AsNoTracking().SingleOrDefaultAsync(w => w.AccountId == claim.State.AccountId.ToString("D"), ct).ConfigureAwait(false);
        var account = await WatchAccount(db, claim.TenantId, claim.State.AccountId, ct).ConfigureAwait(false);
        if (row is null || account is null) return null; var state = SqliteEmailWatchStore.Read(row);
        return JsonSerializer.Serialize(state) == JsonSerializer.Serialize(claim.State) && state.TenantId == claim.TenantId && state.AuthorizationRevision == account.Revision && claim.State.AuthorizationRevision == account.Revision && EmailWatchTransitions.Valid(state, new(claim.State, claim.Account), account, now) ? account : null;
    }
    public async ValueTask<IReadOnlyList<TenantEmailWatchClaim>> ClaimDueTenantEmailWatchesAsync(DateTimeOffset now, int limit, CancellationToken ct = default)
    {
        now = DateTimeOffset.FromUnixTimeMilliseconds(now.ToUnixTimeMilliseconds());
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false); await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var rows = await db.EmailWatches.IgnoreQueryFilters().Where(w => w.TenantId != null && w.Enabled && w.NextCheckAtUtc <= now.ToUnixTimeMilliseconds()
            && db.Set<Tenant>().Any(t => t.TenantId == w.TenantId && t.Status == IdentityStatus.Active)
            && db.Set<TenantCredentialRecord>().Any(c => c.TenantId == w.TenantId && c.CredentialId == w.AccountId && c.DeletedAt == null))
            .OrderBy(w => w.NextCheckAtUtc).ThenBy(w => w.AccountId).Take(Math.Clamp(limit, 1, 8)).ToArrayAsync(ct).ConfigureAwait(false);
        var result = new List<TenantEmailWatchClaim>();
        foreach (var row in rows)
        {
            var state = SqliteEmailWatchStore.Read(row); var tenant = row.TenantId!.Value;
            if (state.TenantId != tenant || state.LeaseExpiresAtUtc > now) continue;
            var account = await WatchAccount(db, tenant, state.AccountId, ct).ConfigureAwait(false); if (account is null) continue;
            if (!EmailWatchTransitions.Usable(account) || state.Paused && !state.RecoveryCheckRequested && state.AuthorizationFingerprint == EmailWatchTransitions.Fingerprint(account))
            { SqliteEmailWatchStore.Write(row, state with { NextCheckAtUtc = now.AddMinutes(15), RuntimeRevision = checked(state.RuntimeRevision + 1) }); continue; }
            var claimed = EmailWatchTransitions.Claim(state, account, now); SqliteEmailWatchStore.Write(row, claimed); result.Add(new(tenant, claimed, account));
        }
        try { await db.SaveChangesAsync(ct).ConfigureAwait(false); await tx.CommitAsync(ct).ConfigureAwait(false); return result; }
        catch (DbUpdateConcurrencyException) { return []; }
        catch (SqliteException e) when (e.SqliteErrorCode is 5 or 6) { return []; }
    }
    public async ValueTask<Credential?> RecheckTenantEmailWatchAsync(TenantEmailWatchClaim claim, DateTimeOffset now, CancellationToken ct = default)
    { await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false); return await ValidWatch(db, claim, now, ct).ConfigureAwait(false); }

    public async ValueTask<bool> TryCommitTenantEmailWatchAsync(TenantEmailWatchClaim claim, EmailWatchBatch batch, DateTimeOffset now, CancellationToken ct = default)
    {
        now = DateTimeOffset.FromUnixTimeMilliseconds(now.ToUnixTimeMilliseconds());
        TenantEmailWatchRules.Batch(batch);
        if (claim.State.Cursor is null && batch.MessageRefs.Count != 0) throw new ArgumentException("A mailbox baseline cannot emit historical references.");
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false); await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var account = await ValidWatch(db, claim, now, ct).ConfigureAwait(false); if (account is null) return false;
        var watch = await OwnedWatches(db, claim.TenantId).SingleAsync(w => w.AccountId == account.CredentialId.ToString("D"), ct).ConfigureAwait(false);
        var state = (await ReadTenantAsync(db, claim.TenantId, ct).ConfigureAwait(false))!;
        var candidates = new List<(EmailWatchNotification Notification, IReadOnlyList<EventSubscriptionSnapshot> Subscriptions)>();
        if (!batch.CursorGap && batch.MessageRefs.Count > 0)
        {
            var bindings = await db.AgentCredentialBindings.Where(b => b.TenantId == claim.TenantId && b.CredentialId == account.CredentialId.ToString("D"))
                .OrderBy(b => b.BindingId).ToArrayAsync(ct).ConfigureAwait(false);
            var registrations = (await OwnedAutomations(db, claim.TenantId).Include(a => a.Triggers).ToArrayAsync(ct).ConfigureAwait(false)).Select(TriggerStoreMapping.ToRegistration).ToArray();
            foreach (var row in bindings.GroupBy(b => b.AgentInstanceId).Select(g => g.First()))
            {
                var parent = await OwnedAgents(db, claim.TenantId).AsNoTracking().SingleOrDefaultAsync(a => a.Instance.InstanceId == row.AgentInstanceId, ct).ConfigureAwait(false);
                if (parent is null) continue; var agent = Owned(parent); if (agent.Instance.Lifecycle != AgentInstanceLifecycle.Active) continue;
                try
                {
                    TenantSessionRules.RequireExecutor(state, agent);
                    await TenantEmailWatchRules.RequireExecutorAsync(state, agent.PrincipalId, account.CredentialId, new IamAuthorizer(this), ct).ConfigureAwait(false);
                    var policy = InstanceSettingsResolver.Resolve(await TenantAgentDefinitionBaseline(db, agent, ct).ConfigureAwait(false), agent.Instance.SettingsOverrides);
                    if (!OccurrenceCompatibility.Allows(policy, TriggerSourceKind.CoreEvent)) continue;
                }
                catch (AccessDeniedException) { continue; }
                var binding = MapBinding(row); IReadOnlyList<EventSubscriptionSnapshot>? captured = null;
                foreach (var reference in batch.MessageRefs.Distinct(StringComparer.Ordinal))
                {
                    var fact = TenantEmailWatchRules.Fact(claim.TenantId, agent.PrincipalId, binding, account, claim.State, reference, now);
                    var subscriptions = captured ??= TenantCoreEventProduction.Subscriptions(fact, registrations);
                    if (subscriptions.Count == 0) continue;
                    candidates.Add((new(fact, binding.BindingId, binding.Revision, account.CredentialId, claim.State.Generation, claim.State.AuthorizationFingerprint!, claim.State.LeaseId, claim.State.RuntimeRevision, account.Revision), subscriptions));
                    if (candidates.Sum(c => c.Subscriptions.Count) > 1024) throw new InvalidOperationException("email_watch_fanout_limit");
                }
            }
        }
        if (candidates.Count > 1024) throw new InvalidOperationException("email_watch_fanout_limit");
        var retained = new List<EmailWatchNotification>();
        foreach (var (notification, subscriptions) in candidates)
        {
            if (await db.EmailWatchEvents.IgnoreQueryFilters().AnyAsync(e => e.EventId == notification.Event.EventId.ToString("D"), ct).ConfigureAwait(false)) continue;
            retained.Add(notification);
        }
        try
        {
            // The completed lease is a factual source witness, never an execution
            // grant. Pause release, cursor and both receipt owners remain atomic.
            SqliteEmailWatchStore.Write(watch, EmailWatchTransitions.Success(SqliteEmailWatchStore.Read(watch), batch, retained.Count > 0, now) with {
                LastCommitLeaseId = claim.State.LeaseId, LastCommitRuntimeRevision = claim.State.RuntimeRevision, LastCommitHadCursor = claim.State.Cursor is not null });
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            foreach (var (notification, subscriptions) in candidates.Where(c => retained.Contains(c.Notification)))
                CoreEventPersistence.Stage(db, notification.Event, subscriptions);
            // Source-to-authority FK is deferred until the outer commit, so an
            // email fact can never be retained without its closed hidden proof.
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            foreach (var notification in retained)
                db.EmailWatchEvents.Add(new() { EventId = notification.Event.EventId.ToString("D"), TenantId = claim.TenantId,
                    AccountId = account.CredentialId.ToString("D"), AuthorityJson = JsonSerializer.Serialize(notification) });
            await db.SaveChangesAsync(ct).ConfigureAwait(false); await tx.CommitAsync(ct).ConfigureAwait(false); return true;
        }
        catch (DbUpdateConcurrencyException) { return false; }
        catch (SqliteException e) when (e.SqliteErrorCode is 5 or 6) { return false; }
    }
    public async ValueTask RecordTenantEmailWatchFailureAsync(TenantEmailWatchClaim claim, SafeEmailWatchFailure failure, DateTimeOffset now, CancellationToken ct = default)
    {
        TenantEmailWatchRules.Failure(failure);
        now = DateTimeOffset.FromUnixTimeMilliseconds(now.ToUnixTimeMilliseconds());
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        if (claim.State.TenantId != claim.TenantId) return;
        var row = await OwnedWatches(db, claim.TenantId).SingleOrDefaultAsync(w => w.AccountId == claim.State.AccountId.ToString("D"), ct).ConfigureAwait(false);
        if (row is null) return; var state = SqliteEmailWatchStore.Read(row);
        if (state.TenantId != claim.TenantId || state.LeaseId != claim.State.LeaseId || state.RuntimeRevision != claim.State.RuntimeRevision) return;
        SqliteEmailWatchStore.Write(row, EmailWatchTransitions.Failure(state, failure, now));
        try { await db.SaveChangesAsync(ct).ConfigureAwait(false); } catch (DbUpdateConcurrencyException) { }
    }
    public async ValueTask<bool> TryMaintainTenantEmailWatchAsync(TenantEmailWatchClaim claim, long expectedSecretRevision,
        CredentialOAuthCheck maintenance, DateTimeOffset now, CancellationToken ct = default)
    {
        now = DateTimeOffset.FromUnixTimeMilliseconds(now.ToUnixTimeMilliseconds());
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false); await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var account = await ValidWatch(db, claim, now, ct).ConfigureAwait(false); if (account is null || account.SecretRevision != expectedSecretRevision) return false;
        if (maintenance.RotatedProtectedPayload is null && !maintenance.ReconnectRequired) return true;
        var row = await OAuthCredentialAsync(db, claim.TenantId, account.CredentialId, ct).ConfigureAwait(false); if (row is null || row.SecretRevision != expectedSecretRevision || row.Revision != account.Revision) return false;
        SqliteCredentialStore.StageValue(db, row, TenantCredentialOAuthRules.Checked(account, maintenance, now), claim.TenantId);
        if (maintenance.ReconnectRequired) await SupersedeOAuthAsync(db, claim.TenantId, account.CredentialId, now, ct).ConfigureAwait(false);
        try { await db.SaveChangesAsync(ct).ConfigureAwait(false); await tx.CommitAsync(ct).ConfigureAwait(false); return true; }
        catch (DbUpdateConcurrencyException) { return false; }
    }
    private static async ValueTask<bool> CanDeliverTenantEmail(AgentCoreDbContext db, Guid tenant, Guid eventId, CancellationToken ct, bool retained = false)
    {
        var row = await db.EmailWatchEvents.IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(e => e.TenantId == tenant && e.EventId == eventId.ToString("D"), ct).ConfigureAwait(false);
        if (row is null) return false; var n = JsonSerializer.Deserialize<EmailWatchNotification>(row.AuthorityJson);
        if (n is null || n.Event.Owner.TenantId != tenant || n.Event.EventId != eventId || n.AccountId.ToString("D") != row.AccountId) return false;
        var fact = await db.CoreEvents.IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(e => e.TenantId == tenant && e.EventId == eventId.ToString("D"), ct).ConfigureAwait(false);
        if (fact is null || fact.EmailWatchEventId != row.EventId || JsonSerializer.Serialize(JsonSerializer.Deserialize<CoreEventOccurrence>(fact.PayloadJson, CoreEventPersistence.Json)) != JsonSerializer.Serialize(n.Event)) return false;
        var watch = await OwnedWatches(db, tenant).AsNoTracking().SingleOrDefaultAsync(w => w.AccountId == row.AccountId, ct).ConfigureAwait(false);
        var account = await WatchAccount(db, tenant, n.AccountId, ct).ConfigureAwait(false); if (watch is null || account is null) return false;
        var state = SqliteEmailWatchStore.Read(watch);
        if (state.TenantId != tenant || !retained && (!state.Enabled || state.Paused || state.Generation != n.Generation) || !EmailWatchTransitions.Usable(account)
            || EmailWatchTransitions.Fingerprint(account) != n.AuthorizationFingerprint) return false;
        var reference = JsonSerializer.Deserialize<JsonElement>(n.Event.DataJson).GetProperty("accountRef").GetString();
        return await OwnedCredentialBindings(db, tenant, n.Event.Owner.AgentInstanceId).AnyAsync(b => b.BindingId == n.BindingId.ToString("D")
            && b.Revision == n.BindingRevision && b.CredentialId == n.AccountId.ToString("D") && b.Reference == reference
            && db.AgentInstances.Any(i => i.InstanceId == b.AgentInstanceId && i.Lifecycle == nameof(AgentInstanceLifecycle.Active))
            && db.Set<IamSystemIdentityRow>().Any(i => i.TenantId == tenant && i.AgentInstanceId == b.AgentInstanceId && i.PrincipalId == n.Event.Owner.ExecutorPrincipalId), ct).ConfigureAwait(false);
    }
    public async ValueTask<bool> CanReadRetainedTenantEmailEventAsync(Guid tenantId, Guid eventId, CancellationToken ct = default)
    { await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false); return await CanDeliverTenantEmail(db, tenantId, eventId, ct, retained: true).ConfigureAwait(false); }
    public async ValueTask<bool> CanDeliverTenantEmailEventAsync(Guid tenantId, Guid eventId, CancellationToken ct = default)
    { await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false); return await CanDeliverTenantEmail(db, tenantId, eventId, ct).ConfigureAwait(false); }
}
