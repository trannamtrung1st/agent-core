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

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore : ITenantEmailWatchStore
{
    private InMemoryEmailWatchStore? watchBacking = emailWatches;
    private InMemoryEmailWatchStore Watches => watchBacking ??= new(instances, CredentialBacking(), CredentialBacking(), sessionBacking.CoreEvents, automationBacking);
    private TenantCredential WatchAccount(Guid tenant, Guid id) => CredentialBacking().ReadTenantCredential(tenant, id) is { Value.Purpose: CredentialPurpose.EmailAccount, Value.EmailAccount: not null } account
        ? account : throw new AccessDeniedException("resource_unavailable");
    private EmailWatchState WatchState(Guid tenant, Guid id)
    {
        var state = Watches.TenantStates.GetValueOrDefault(id) ?? new EmailWatchState(id) { TenantId = tenant };
        return state.TenantId == tenant ? state : throw new AccessDeniedException("resource_unavailable");
    }
    public ValueTask<EmailWatchState?> ReadTenantEmailWatchAsync(Guid tenantId, Guid accountId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate) lock (instances.CredentialGate)
        {
            if (CredentialBacking().ReadTenantCredential(tenantId, accountId) is not { Value.Purpose: CredentialPurpose.EmailAccount, Value.EmailAccount: not null }) return ValueTask.FromResult<EmailWatchState?>(null);
            return ValueTask.FromResult<EmailWatchState?>(WatchState(tenantId, accountId));
        }
    }
    public ValueTask<bool> TryChangeTenantEmailWatchAsync(TenantSecurityState next, long expectedSecurityRevision,
        Guid accountId, TenantEmailWatchChange change, AuthorizationAudit audit, DateTimeOffset now, CancellationToken ct = default)
    {
        now = DateTimeOffset.FromUnixTimeMilliseconds(now.ToUnixTimeMilliseconds());
        next = IamStateRules.Freeze(next); TenantEmailWatchRules.Change(next, expectedSecurityRevision, accountId, change, audit); ct.ThrowIfCancellationRequested();
        lock (_gate) lock (instances.CredentialGate)
        {
            var tenant = next.Tenant.TenantId;
            if (!_tenants.TryGetValue(tenant, out var before) || before.Tenant.Revision != expectedSecurityRevision) return ValueTask.FromResult(false);
            RequireHumanSession(tenant, change.Human); var account = WatchAccount(tenant, accountId); var state = WatchState(tenant, accountId);
            if (account.Value.Revision != change.CredentialRevision || state.ConfigurationRevision != change.WatchRevision) return ValueTask.FromResult(false);
            TenantEmailWatchRules.RequireHumanAsync(before, change.Human.PrincipalId, accountId, true, change.CheckNow || change.Enabled, new IamAuthorizer(this), ct).AsTask().GetAwaiter().GetResult();
            EmailWatchState updated;
            if (change.CheckNow)
            {
                if (!state.Enabled || !EmailWatchTransitions.Usable(account.Value)) throw new SecurityConflictException("email_monitoring_unavailable");
                updated = state.LeaseExpiresAtUtc > now ? state : state with { NextCheckAtUtc = now, RecoveryCheckRequested = true, RuntimeRevision = checked(state.RuntimeRevision + 1) };
            }
            else updated = EmailWatchTransitions.Configure(state, change.WatchRevision, change.Enabled, change.IntervalSeconds, now);
            IamStateRules.ValidateAgentContinuity(before.SystemIdentities, next); EnsureReferences(next, audit, ct: ct);
            ct.ThrowIfCancellationRequested(); Watches.TenantStates[accountId] = updated; _tenants[tenant] = next; _audit.Add(audit); return ValueTask.FromResult(true);
        }
    }
    private bool ValidWatch(TenantEmailWatchClaim claim, DateTimeOffset now, out Credential? account)
    {
        account = null;
        if (claim.TenantId == Guid.Empty || claim.State.TenantId != claim.TenantId || !_tenants.TryGetValue(claim.TenantId, out var tenant) || tenant.Tenant.Status != IdentityStatus.Active) return false;
        var current = CredentialBacking().ReadTenantCredential(claim.TenantId, claim.State.AccountId)?.Value;
        if (!Watches.TenantStates.TryGetValue(claim.State.AccountId, out var state) || JsonSerializer.Serialize(state) != JsonSerializer.Serialize(claim.State) || state.TenantId != claim.TenantId
            || state.AuthorizationRevision != current?.Revision || claim.State.AuthorizationRevision != current?.Revision
            || !EmailWatchTransitions.Valid(state, new(claim.State, claim.Account), current, now)) return false;
        account = current; return true;
    }
    public ValueTask<IReadOnlyList<TenantEmailWatchClaim>> ClaimDueTenantEmailWatchesAsync(DateTimeOffset now, int limit, CancellationToken ct = default)
    {
        now = DateTimeOffset.FromUnixTimeMilliseconds(now.ToUnixTimeMilliseconds());
        ct.ThrowIfCancellationRequested(); lock (_gate) lock (instances.CredentialGate)
        {
            var result = new List<TenantEmailWatchClaim>();
            foreach (var s in Watches.TenantStates.Values.Where(s => s.TenantId is { } owner && _tenants.TryGetValue(owner, out var tenantState) && tenantState.Tenant.Status == IdentityStatus.Active && CredentialBacking().ReadTenantCredential(owner, s.AccountId) is { Value.Purpose: CredentialPurpose.EmailAccount, Value.EmailAccount: not null } && s.Enabled && s.NextCheckAtUtc <= now && (s.LeaseId is null || s.LeaseExpiresAtUtc <= now))
                .OrderBy(s => s.NextCheckAtUtc).ThenBy(s => s.AccountId).Take(Math.Clamp(limit, 1, 8)).ToArray())
            {
                var tenant = s.TenantId!.Value; var c = CredentialBacking().ReadTenantCredential(tenant, s.AccountId)?.Value;
                if (c is null || !_tenants.TryGetValue(tenant, out var security) || security.Tenant.Status != IdentityStatus.Active) continue;
                if (!EmailWatchTransitions.Usable(c) || s.Paused && !s.RecoveryCheckRequested && s.AuthorizationFingerprint == EmailWatchTransitions.Fingerprint(c))
                { Watches.TenantStates[s.AccountId] = s with { NextCheckAtUtc = now.AddMinutes(15), RuntimeRevision = checked(s.RuntimeRevision + 1) }; continue; }
                var claimed = EmailWatchTransitions.Claim(s, c, now); Watches.TenantStates[s.AccountId] = claimed; result.Add(new(tenant, claimed, c));
            }
            return ValueTask.FromResult<IReadOnlyList<TenantEmailWatchClaim>>(result);
        }
    }
    public ValueTask<Credential?> RecheckTenantEmailWatchAsync(TenantEmailWatchClaim claim, DateTimeOffset now, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); lock (_gate) lock (instances.CredentialGate) return ValueTask.FromResult(ValidWatch(claim, now, out var account) ? account : null); }

    public ValueTask<bool> TryCommitTenantEmailWatchAsync(TenantEmailWatchClaim claim, EmailWatchBatch batch, DateTimeOffset now, CancellationToken ct = default)
    {
        now = DateTimeOffset.FromUnixTimeMilliseconds(now.ToUnixTimeMilliseconds());
        TenantEmailWatchRules.Batch(batch);
        if (claim.State.Cursor is null && batch.MessageRefs.Count != 0) throw new ArgumentException("A mailbox baseline cannot emit historical references."); ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult(WithTenantEvents(() => {
            if (!ValidWatch(claim, now, out var account)) return false;
            var state = _tenants[claim.TenantId]; var candidates = new List<(EmailWatchNotification Notification, IReadOnlyList<EventSubscriptionSnapshot> Subscriptions)>();
            if (!batch.CursorGap && batch.MessageRefs.Count > 0)
            foreach (var identity in state.SystemIdentities.Where(i => i.Kind == SystemIdentityKind.Agent && i.AgentInstanceId is not null && i.Status == IdentityStatus.Active))
            {
                var snapshot = ReadTenantCredentialBindingsAsync(claim.TenantId, identity.AgentInstanceId!.Value, ct).Result;
                if (snapshot is null || snapshot.Agent.Instance.Lifecycle != AgentInstanceLifecycle.Active) continue;
                var binding = snapshot.Bindings.Where(b => b.Binding.CredentialId == claim.State.AccountId).OrderBy(b => b.Binding.BindingId).FirstOrDefault()?.Binding;
                if (binding is null) continue;
                try
                {
                    TenantSessionRules.RequireExecutor(state, snapshot.Agent);
                    TenantEmailWatchRules.RequireExecutorAsync(state, identity.PrincipalId, account!.CredentialId, new IamAuthorizer(this), ct).AsTask().GetAwaiter().GetResult();
                    var policy = InstanceSettingsResolver.Resolve(TenantAgentDefinitionBaseline(snapshot.Agent), snapshot.Agent.Instance.SettingsOverrides);
                    if (!OccurrenceCompatibility.Allows(policy, TriggerSourceKind.CoreEvent)) continue;
                }
                catch (AccessDeniedException) { continue; }
                IReadOnlyList<EventSubscriptionSnapshot>? captured = null;
                foreach (var reference in batch.MessageRefs.Distinct(StringComparer.Ordinal))
                {
                    var fact = TenantEmailWatchRules.Fact(claim.TenantId, identity.PrincipalId, binding, account!, claim.State, reference, now);
                    var subscriptions = captured ??= TenantCoreEventProduction.Subscriptions(fact, automationBacking.TenantAutomations(claim.TenantId));
                    if (subscriptions.Count == 0) continue;
                    var n = new EmailWatchNotification(fact, binding.BindingId, binding.Revision, account!.CredentialId, claim.State.Generation, claim.State.AuthorizationFingerprint!, claim.State.LeaseId, claim.State.RuntimeRevision, account.Revision);
                    candidates.Add((n, subscriptions));
                    if (candidates.Sum(c => c.Subscriptions.Count) > 1024) throw new InvalidOperationException("email_watch_fanout_limit");
                }
            }
            if (candidates.Count > 1024) throw new InvalidOperationException("email_watch_fanout_limit");
            // Validate the complete source batch before the first factual write.
            foreach (var (notification, subscriptions) in candidates)
                if (!Watches.TenantNotifications.ContainsKey(notification.Event.EventId)) sessionBacking.CoreEvents.ValidateTenantAppend(notification.Event, subscriptions);
            var emitted = false;
            foreach (var (notification, subscriptions) in candidates)
            {
                if (Watches.TenantNotifications.ContainsKey(notification.Event.EventId)) continue;
                sessionBacking.CoreEvents.AppendTenant(notification.Event, subscriptions);
                Watches.TenantNotifications.Add(notification.Event.EventId, notification); emitted = true;
            }
            Watches.TenantStates[claim.State.AccountId] = EmailWatchTransitions.Success(WatchState(claim.TenantId, claim.State.AccountId), batch, emitted, now) with {
                LastCommitLeaseId = claim.State.LeaseId, LastCommitRuntimeRevision = claim.State.RuntimeRevision, LastCommitHadCursor = claim.State.Cursor is not null }; return true;
        }));
    }
    public ValueTask RecordTenantEmailWatchFailureAsync(TenantEmailWatchClaim claim, SafeEmailWatchFailure failure, DateTimeOffset now, CancellationToken ct = default)
    {
        TenantEmailWatchRules.Failure(failure);
        now = DateTimeOffset.FromUnixTimeMilliseconds(now.ToUnixTimeMilliseconds());
        ct.ThrowIfCancellationRequested(); lock (_gate) lock (instances.CredentialGate)
            if (claim.State.TenantId == claim.TenantId && Watches.TenantStates.GetValueOrDefault(claim.State.AccountId) is { } state
                && state.TenantId == claim.TenantId && state.LeaseId == claim.State.LeaseId && state.RuntimeRevision == claim.State.RuntimeRevision)
                Watches.TenantStates[state.AccountId] = EmailWatchTransitions.Failure(state, failure, now);
        return ValueTask.CompletedTask;
    }
    public ValueTask<bool> TryMaintainTenantEmailWatchAsync(TenantEmailWatchClaim claim, long expectedSecretRevision,
        CredentialOAuthCheck maintenance, DateTimeOffset now, CancellationToken ct = default)
    {
        now = DateTimeOffset.FromUnixTimeMilliseconds(now.ToUnixTimeMilliseconds());
        ct.ThrowIfCancellationRequested(); lock (_gate) lock (instances.CredentialGate)
        {
            if (!ValidWatch(claim, now, out var c) || c!.SecretRevision != expectedSecretRevision) return ValueTask.FromResult(false);
            if (maintenance.RotatedProtectedPayload is null && !maintenance.ReconnectRequired) return ValueTask.FromResult(true);
            var changed = CredentialBacking().ApplyTenantOAuthCheck(claim.TenantId, c.CredentialId, c.Revision, c.SecretRevision, maintenance, now);
            if (changed && maintenance.ReconnectRequired) SupersedeOAuth(claim.TenantId, c.CredentialId);
            return ValueTask.FromResult(changed);
        }
    }
    private bool CanDeliverTenantEmail(Guid tenant, Guid eventId, bool retained = false)
    {
        if (!Watches.TenantNotifications.TryGetValue(eventId, out var n) || n.Event.Owner.TenantId != tenant
            || Watches.TenantStates.GetValueOrDefault(n.AccountId) is not { } s || s.TenantId != tenant || !retained && (!s.Enabled || s.Paused || s.Generation != n.Generation)) return false;
        var receipt = sessionBacking.CoreEvents.TenantReceipt(tenant, eventId);
        if (receipt is null || receipt.Event.Key != "email.message.received" || JsonSerializer.Serialize(receipt.Event) != JsonSerializer.Serialize(n.Event)) return false;
        var c = CredentialBacking().ReadTenantCredential(tenant, n.AccountId)?.Value;
        if (c is null || !EmailWatchTransitions.Usable(c) || EmailWatchTransitions.Fingerprint(c) != n.AuthorizationFingerprint) return false;
        var snapshot = ReadTenantCredentialBindingsAsync(tenant, n.Event.Owner.AgentInstanceId).Result;
        return snapshot?.Agent.Instance.Lifecycle == AgentInstanceLifecycle.Active && snapshot.Agent.PrincipalId == n.Event.Owner.ExecutorPrincipalId
            && snapshot.Bindings.Any(b => b.Binding.BindingId == n.BindingId && b.Binding.Revision == n.BindingRevision && b.Binding.CredentialId == n.AccountId
                && JsonSerializer.Deserialize<JsonElement>(n.Event.DataJson).GetProperty("accountRef").GetString() == b.Binding.Reference);
    }
    public ValueTask<bool> CanReadRetainedTenantEmailEventAsync(Guid tenantId, Guid eventId, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); return ValueTask.FromResult(WithTenantEvents(() => CanDeliverTenantEmail(tenantId, eventId, retained: true))); }
    public ValueTask<bool> CanDeliverTenantEmailEventAsync(Guid tenantId, Guid eventId, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); return ValueTask.FromResult(WithTenantEvents(() => CanDeliverTenantEmail(tenantId, eventId))); }
}
