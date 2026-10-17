using System.Collections.Concurrent;
using AgentCore.Application.Events;
using AgentCore.Application.Observability;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Contracts.Realtime;
using AgentCore.Domain.Conversation;
using Microsoft.AspNetCore.SignalR;

namespace AgentCore.Api.Realtime;

// Recipient transport state only. Mutable conversation state remains in the owning Runtime mailbox.
public sealed partial class TenantSessionLiveDelivery(TenantConversationService access, ITenantRunStore runs,
    IHubContext<SessionHub> hubs, IIdGenerator ids, TimeProvider time, ILogger<TenantSessionLiveDelivery> logger)
{
    private sealed class Connection
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public SemaphoreSlim Commands { get; } = new(1, 1);
        public volatile bool Closed;
        public volatile Subscription? Current;
    }
    private sealed class Subscription(Guid tenant, Guid session, TenantSessionInitiator human, Guid attachment)
    {
        public sealed record Delivered(Guid EntryId, long EntrySequence, int PublishedEnd, int ReceivedEnd);
        public Guid Tenant { get; } = tenant;
        public Guid Session { get; } = session;
        public TenantSessionInitiator Human { get; } = human;
        public Guid Attachment { get; } = attachment;
        public long Sequence;
        public long LastCommand;
        public Dictionary<Guid, CommandRecord> Dedupe { get; } = [];
        public Queue<Guid> DedupeOrder { get; } = [];
        public ConcurrentDictionary<Guid, Guid> KnownRuns { get; } = new();
        public Queue<Guid> ResponseOrder { get; } = [];
        public ConcurrentDictionary<Guid, Delivered> Delivery { get; } = new();
        private Queue<Guid> DeliveryOrder { get; } = [];
        public void ObserveDelivery(Guid response, Guid entry, long sequence, int published)
        {
            if (Delivery.TryAdd(response, new(entry, sequence, published, 0))) DeliveryOrder.Enqueue(response);
            while (DeliveryOrder.Count > 128) Delivery.TryRemove(DeliveryOrder.Dequeue(), out _);
        }
        public void ObserveRun(Guid response, Guid run)
        {
            if (KnownRuns.TryAdd(response, run)) ResponseOrder.Enqueue(response); else KnownRuns[response] = run;
            while (ResponseOrder.Count > 128) KnownRuns.TryRemove(ResponseOrder.Dequeue(), out _);
        }
    }
    private readonly ConcurrentDictionary<string, Connection> _connections = new();
    private readonly ConcurrentDictionary<(Guid Tenant, Guid Session), Source> _sources = new();

    public sealed class Source(TenantSessionLiveDelivery delivery, AgentRun run) : ISessionOutput
    {
        internal SemaphoreSlim Gate { get; } = new(1, 1);
        internal AgentRun Run { get; } = run;
        internal ResponseStartedOutput? Started;
        internal string Prefix = "";
        internal bool Resync;
        internal bool Terminal;
        public ValueTask PublishAsync(SessionOutput output, CancellationToken cancellationToken = default) => delivery.PublishAsync(this, output, cancellationToken);
        public void Activate()
        {
            if (!delivery._sources.TryAdd((Run.Owner.TenantId!.Value, Run.SessionId), this))
                throw new InvalidOperationException("A tenant Session already has an output source.");
        }
        public async ValueTask CloseAsync()
        {
            await Gate.WaitAsync().ConfigureAwait(false);
            try { delivery._sources.TryRemove(new KeyValuePair<(Guid, Guid), Source>((Run.Owner.TenantId!.Value, Run.SessionId), this)); }
            finally { Gate.Release(); }
        }
    }
    public Source CreateSource(AgentRun run) => new(this, run);
    public void Detach(string connection)
    {
        if (_connections.TryRemove(connection, out var state)) state.Closed = true;
    }

    public async Task AttachAsync(string connectionId, Guid tenant, Guid session, TenantSessionInitiator human, CancellationToken ct)
    {
        var connection = _connections.GetOrAdd(connectionId, _ => new());
        while (true)
        {
            _sources.TryGetValue((tenant, session), out var source);
            if (source is not null) await source.Gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await connection.Gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    connection.Current = null; // A switch, including a denied switch, retires the previous tenant subscription.
                    if (connection.Closed) throw new AccessDeniedException("tenant_connection_unavailable");
                    if (_sources.GetValueOrDefault((tenant, session)) != source) continue;
                    var history = await access.ReadAsync(tenant, session, human, ct: ct).ConfigureAwait(false);
                    var run = source is null ? null : await runs.FindTenantRunAsync(tenant, source.Run.AgentRunId, ct).ConfigureAwait(false);
                    var recipient = await access.AuthorizeRecipientAsync(tenant, session, human, ct).ConfigureAwait(false);
                    if (recipient.SecurityRevision != history.SecurityRevision || recipient.SessionRevision != history.SessionRevision)
                        throw new SecurityConflictException("stale_revision");
                    if (recipient.Mode != SessionMode.Text) throw new AccessDeniedException("tenant_voice_transport_unavailable");
                    var subscription = new Subscription(tenant, session, human, ids.NewId());
                    connection.Current = subscription;
                    if (_sources.GetValueOrDefault((tenant, session)) != source) { connection.Current = null; continue; }
                    var entries = history.Items.ToList();
                    var streaming = source is { Started: not null, Terminal: false, Resync: false }
                        && run is { Status: AgentRunStatus.Running, CancellationRequested: false, Claim: { } claim }
                        && claim.Generation == source.Run.Claim!.Generation && claim.LeaseExpiresAtUtc > time.GetUtcNow();
                    if (streaming && source is { Started: { } started })
                    {
                        var entry = entries.FindIndex(e => e.EntryId == started.EntryId);
                        if (entry >= 0 && entries[entry].Status == EntryStatus.Streaming)
                            entries[entry] = entries[entry] with { Text = source.Prefix };
                        else if (entry < 0)
                            entries.Add(new(started.EntryId, started.EntrySequence, null, ConversationRole.Assistant, source.Prefix,
                                source.Run.ResponseId, EntryStatus.Streaming, 0, 0, SessionMode.Text, source.Run.UpdatedAtUtc, []));
                    }
                    var active = streaming ? source!.Run.ResponseId : null;
                    var ready = new SessionReadyProjection(SessionMode.Text, null, recipient.Status, recipient.Agent, null, null,
                        new(false, false, false, false), new(false, false, false, false, false, []), "none",
                        entries.Count == 0 ? 0 : entries.Max(e => e.Sequence), entries.OrderBy(e => e.Sequence).ToArray(), active, "none", "none",
                        recipient.LifecycleStatus, ModelSelection: recipient.Model, AgentRunId: active is null ? null : source!.Run.AgentRunId,
                        OutputActivity: active is null ? "Idle" : "Generating", HasOlderHistory: history.HasOlder, HistoryBeforeSequence: history.NextBefore);
                    var output = new SessionOutput(Context(session), active, new ReadyOutput(ready));
                    var mapped = SessionEventMapper.Map(output, subscription.Attachment, ++subscription.Sequence);
                    mapped.Payload["tenantId"] = tenant.ToString("D"); mapped.Payload["securityRevision"] = recipient.SecurityRevision;
                    mapped.Payload["sessionRevision"] = recipient.SessionRevision;
                    if (connection.Closed) throw new AccessDeniedException("tenant_connection_unavailable");
                    await hubs.Clients.Client(connectionId).SendAsync("SessionEvent", mapped, ct).ConfigureAwait(false);
                    foreach (var entry in entries.Where(e => e.Role == ConversationRole.Assistant && e.DeliveryMode == SessionMode.Text
                        && e.ResponseId is not null && e.Status is EntryStatus.Streaming or EntryStatus.Completed))
                        subscription.ObserveDelivery(entry.ResponseId!.Value, entry.EntryId, entry.Sequence, entry.Text.Length);
                    if (active is { } response) subscription.ObserveRun(response, source!.Run.AgentRunId);
                    return;
                }
                catch { connection.Current = null; throw; }
                finally { connection.Gate.Release(); }
            }
            finally { source?.Gate.Release(); }
        }
    }

    private async ValueTask PublishAsync(Source source, SessionOutput output, CancellationToken ct)
    {
        if (output.Context.SessionId != source.Run.SessionId || output.ResponseId is { } response && response != source.Run.ResponseId) return;
        var safe = Safe(output.Payload);
        if (safe is null) return;
        await source.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_sources.GetValueOrDefault((source.Run.Owner.TenantId!.Value, source.Run.SessionId)) != source) return;
            var current = await runs.FindTenantRunAsync(source.Run.Owner.TenantId.Value, source.Run.AgentRunId, ct).ConfigureAwait(false);
            if (current is null || current.Owner != source.Run.Owner || current.ResponseId != source.Run.ResponseId) return;
            var durableWait = safe is DurableApprovalPendingOutput pending && current.Status == AgentRunStatus.WaitingForApproval
                && current.ApprovalRequestId == pending.RequestId && current.AgentRunId == pending.RunId;
            if (!current.IsTerminal && !durableWait && (current.Status != AgentRunStatus.Running || current.Claim?.Generation != source.Run.Claim!.Generation
                || current.CancellationRequested || current.Claim.LeaseExpiresAtUtc <= time.GetUtcNow())) return;
            if (current.IsTerminal && safe is not (ResponseCompletedOutput or ResponseInterruptedOutput or StateChangedOutput)) return;
            if (current.Status == AgentRunStatus.Cancelled && safe is not (ResponseCompletedOutput { InterruptReason: not null } or ResponseInterruptedOutput or StateChangedOutput)) return;
            switch (safe)
            {
                case ResponseStartedOutput started: source.Started = started; source.Prefix = ""; source.Resync = false; source.Terminal = false; break;
                case TextDeltaOutput delta when delta.TextStart == source.Prefix.Length: source.Prefix += delta.Text; break;
                case TextDeltaOutput: source.Resync = true; break;
                case ResponseCompletedOutput or ResponseInterruptedOutput or DurableApprovalPendingOutput: source.Terminal = true; break;
            }
            if (source.Resync && safe is TextDeltaOutput or TextCompletedOutput or BlockUpsertOutput) return;
            var visible = output with { Payload = safe };
            await Task.WhenAll(_connections.ToArray().Where(pair => pair.Value.Current is { } sub
                && sub.Tenant == source.Run.Owner.TenantId && sub.Session == source.Run.SessionId)
                .Select(pair => DeliverAsync(source, visible, pair.Key, pair.Value, ct))).ConfigureAwait(false);
        }
        finally { source.Gate.Release(); }
    }
    private async Task DeliverAsync(Source source, SessionOutput output, string connectionId, Connection state, CancellationToken ct)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5), time);
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        var entered = false;
        try
        {
            await state.Gate.WaitAsync(bounded.Token).ConfigureAwait(false); entered = true;
            var subscription = state.Current;
            if (state.Closed || subscription is null || subscription.Tenant != source.Run.Owner.TenantId || subscription.Session != source.Run.SessionId) return;
            var recipient = await access.AuthorizeRecipientAsync(subscription.Tenant, subscription.Session, subscription.Human, bounded.Token).ConfigureAwait(false);
            if (recipient.Mode != SessionMode.Text) { state.Current = null; return; }
            if (state.Closed || state.Current != subscription) return;
            await hubs.Clients.Client(connectionId).SendAsync("SessionEvent", SessionEventMapper.Map(output, subscription.Attachment, ++subscription.Sequence), bounded.Token).ConfigureAwait(false);
            if (output.Payload is ResponseStartedOutput && output.ResponseId is { } response) subscription.ObserveRun(response, source.Run.AgentRunId);
            if (output.ResponseId is { } delivered)
            {
                if (output.Payload is ResponseStartedOutput started) subscription.ObserveDelivery(delivered, started.EntryId, started.EntrySequence, 0);
                if (output.Payload is TextDeltaOutput delta && subscription.Delivery.TryGetValue(delivered, out var bound) && bound.PublishedEnd == delta.TextStart)
                    subscription.Delivery.AddOrUpdate(delivered, bound with { PublishedEnd = delta.TextStart + delta.Text.Length },
                        (_, old) => old with { PublishedEnd = Math.Max(old.PublishedEnd, delta.TextStart + delta.Text.Length) });
            }
        }
        catch (Exception ex) when (ex is AccessDeniedException or SecurityConflictException or OperationCanceledException)
        { if (entered) state.Current = null; }
        catch (Exception ex)
        {
            if (entered) state.Current = null;
            DiagnosticLog.Warning(logger, ex, ids.NewId(), "Tenant recipient delivery unavailable.");
        }
        finally { if (entered) state.Gate.Release(); }
    }
    private static OutputPayload? Safe(OutputPayload payload) => payload switch
    {
        ResponseStartedOutput or TextDeltaOutput or TextCompletedOutput or ResponseInterruptedOutput or StateChangedOutput or DurableApprovalPendingOutput => payload,
        BlockUpsertOutput { Kind: "markdown", AttachmentId: null, ArtifactId: null } => payload,
        ResponseCompletedOutput completed => completed with { MemoryReceipts = null, EffectReceipts = null },
        _ => null // Rich resource, approval, provider and voice payloads require their own recipient-aware conversion.
    };
    private EventContext Context(Guid session) => new(ids.NewId(), session, ids.NewId(), time.GetUtcNow(), ids.NewId(), null);
}
