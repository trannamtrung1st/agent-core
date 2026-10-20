using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Credentials;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Email;

namespace AgentCore.Infrastructure.Persistence;

public sealed class InMemoryEmailWatchStore : IEmailWatchStore
{
    private readonly InMemoryAgentInstanceStore instances;
    private readonly ICredentialStore credentials;
    private readonly IAgentCredentialBindingStore bindings;
    private readonly InMemoryCoreEventStore events;
    private readonly Dictionary<Guid, EmailWatchState> states = [];
    public InMemoryEmailWatchStore(InMemoryAgentInstanceStore instances, ICredentialStore credentials,
        IAgentCredentialBindingStore bindings, InMemoryCoreEventStore events, ITriggerStore? triggers = null)
    {
        this.instances = instances; this.credentials = credentials; this.bindings = bindings; this.events = events; this.triggers = triggers;
        if (triggers is InMemoryTriggerStore memory)
        {
            memory.EmailAdmissionGate = instances.CredentialGate;
            memory.EmailNeedsFence = occurrence => occurrence.SourceKind == AgentCore.Domain.Triggers.TriggerSourceKind.CoreEvent
                && occurrence.SourceEventId is Guid id && events.EmailSources(id) is { Count: > 0 };
            memory.EmailCanAdmit = occurrence => occurrence.SourceKind != AgentCore.Domain.Triggers.TriggerSourceKind.CoreEvent
                || occurrence.SourceEventId is not Guid id || events.EmailSources(id, occurrence.EvidenceJson) is { } sources && sources.All(source => CanDeliverAsync(source).Result);
        }
    }
    private readonly ITriggerStore? triggers;
    private readonly Dictionary<Guid, EmailWatchNotification> notifications = [];
    // Both facets share the incumbent state and receipt owners under CredentialGate.
    internal Dictionary<Guid, EmailWatchState> TenantStates => states;
    internal Dictionary<Guid, EmailWatchNotification> TenantNotifications => notifications;
    private Credential Account(Guid id) => EmailWatchTransitions.Require(credentials.GetAsync(id).GetAwaiter().GetResult());
    public ValueTask<EmailWatchState> GetAsync(Guid id, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); lock (instances.CredentialGate) { Account(id); return ValueTask.FromResult(states.GetValueOrDefault(id) ?? new(id)); } }
    public ValueTask<EmailWatchState> ConfigureAsync(Guid id, long expected, bool enabled, int interval, DateTimeOffset now, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); lock (instances.CredentialGate) { Account(id); return ValueTask.FromResult(states[id] = EmailWatchTransitions.Configure(states.GetValueOrDefault(id) ?? new(id), expected, enabled, interval, now)); } }
    public ValueTask CheckNowAsync(Guid id, long expected, DateTimeOffset now, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (instances.CredentialGate)
        { var c = Account(id); var s = states.GetValueOrDefault(id) ?? new(id); EmailWatchTransitions.Revision(s, expected);
            if (!s.Enabled || !EmailWatchTransitions.Usable(c)) throw AgentCoreErrors.Conflict("Enable monitoring on an available account before checking.");
            if (s.LeaseExpiresAtUtc <= now || s.LeaseId is null) states[id] = s with { NextCheckAtUtc = now, RecoveryCheckRequested = true, RuntimeRevision = s.RuntimeRevision + 1 };
        } return ValueTask.CompletedTask;
    }
    public ValueTask<IReadOnlyList<EmailWatchClaim>> ClaimDueAsync(DateTimeOffset now, int limit, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (instances.CredentialGate)
        {
            var result = new List<EmailWatchClaim>();
            foreach (var s in states.Values.Where(s => s.TenantId is null && s.Enabled && s.NextCheckAtUtc <= now && (s.LeaseId == null || s.LeaseExpiresAtUtc <= now)).OrderBy(s => s.NextCheckAtUtc).ThenBy(s => s.AccountId).Take(Math.Clamp(limit, 1, 8)).ToArray())
            {
                if (s.LeaseExpiresAtUtc > now) continue;
                var c = credentials.GetAsync(s.AccountId).Result;
                if (c is null) { states.Remove(s.AccountId); continue; }
                if (!EmailWatchTransitions.Usable(c) || s.Paused && !s.RecoveryCheckRequested && s.AuthorizationFingerprint == EmailWatchTransitions.Fingerprint(c))
                { states[s.AccountId] = s with { NextCheckAtUtc = now.AddMinutes(15), RuntimeRevision = s.RuntimeRevision + 1 }; continue; }
                var claimed = states[s.AccountId] = EmailWatchTransitions.Claim(s, c, now); result.Add(new(claimed, c));
            }
            return ValueTask.FromResult<IReadOnlyList<EmailWatchClaim>>(result);
        }
    }
    private bool BindingValid(EmailWatchNotification n) => bindings.ListBindingsAsync(n.Event.Owner.AgentInstanceId).Result.Any(b =>
        b.BindingId == n.BindingId && b.CredentialId == n.AccountId && b.Revision == n.BindingRevision)
        && instances.FindAsync(n.Event.Owner.AgentInstanceId).Result?.Lifecycle == AgentInstanceLifecycle.Active
        && triggers is not null && triggers.ListAsync(n.Event.Owner, AgentCore.Domain.Triggers.AutomationStatus.Active).Result.Any(a => a.Triggers.Any(t => t.Enabled && t.Configuration is AgentCore.Domain.Triggers.CoreEventTrigger { CoreEventKey: "email.message.received" }));
    public ValueTask<bool> CommitAsync(EmailWatchClaim claim, EmailWatchBatch batch, IReadOnlyList<EmailWatchNotification> proposed, DateTimeOffset now, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (instances.CredentialGate)
        {
            if (claim.State.TenantId is not null || !states.TryGetValue(claim.State.AccountId, out var s) || s.TenantId is not null || !EmailWatchTransitions.Valid(s, claim, credentials.GetAsync(s.AccountId).Result, now)) return ValueTask.FromResult(false);
            var emitted = false;
            foreach (var n in proposed)
            {
                if (batch.CursorGap || n.AccountId != s.AccountId || n.Generation != s.Generation || n.AuthorizationFingerprint != s.AuthorizationFingerprint || !BindingValid(n)) continue;
                var subscriptions = EmailWatchTransitions.Subscriptions(triggers!.ListAsync(n.Event.Owner, null).Result, n.Event.Owner);
                if (subscriptions.Count == 0 || !notifications.TryAdd(n.Event.EventId, n)) continue;
                events.Append(n.Event, subscriptions); emitted = true;
            }
            states[s.AccountId] = EmailWatchTransitions.Success(s, batch, emitted, now); return ValueTask.FromResult(true);
        }
    }
    public ValueTask RecordFailureAsync(EmailWatchClaim claim, SafeEmailWatchFailure failure, DateTimeOffset now, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); lock (instances.CredentialGate) { if (claim.State.TenantId is null && states.TryGetValue(claim.State.AccountId, out var s) && s.TenantId is null && s.LeaseId == claim.State.LeaseId && s.RuntimeRevision == claim.State.RuntimeRevision) states[s.AccountId] = EmailWatchTransitions.Failure(s, failure, now); } return ValueTask.CompletedTask; }
    public ValueTask<bool> CanDeliverAsync(Guid eventId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (instances.CredentialGate)
        {
            if (!notifications.TryGetValue(eventId, out var n) || n.Event.Owner.TenantId is not null || !states.TryGetValue(n.AccountId, out var s) || s.TenantId is not null) return ValueTask.FromResult(false);
            var c = credentials.GetAsync(n.AccountId).Result;
            return ValueTask.FromResult(s.Enabled && !s.Paused && s.Generation == n.Generation && c is not null && EmailWatchTransitions.Usable(c) && EmailWatchTransitions.Fingerprint(c) == n.AuthorizationFingerprint && BindingValid(n));
        }
    }
}
