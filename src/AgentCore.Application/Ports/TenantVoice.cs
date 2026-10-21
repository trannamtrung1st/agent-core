using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Ports;

// Core-held transport identity. It is never a wire/model argument or a persisted credential.
// Only the authenticated voice use case can mint a lease. Retirement immediately shuts
// ingress and waits for already admitted owning transactions to linearize before cleanup.
public sealed class TenantVoiceLease : IAsyncDisposable
{
    private readonly SemaphoreSlim _operations = new(1, 1);
    private int _active, _retireRequested, _retired;
    private sealed record Input(Guid StreamId, bool Enabled);
    private Input _input;
    internal TenantVoiceLease(TenantSessionRoot owner, TenantSessionInitiator human, Guid attachmentId, Guid streamId)
    { Owner = owner; Human = human; AttachmentId = attachmentId; _input = new(streamId, false); }
    public TenantSessionRoot Owner { get; }
    public TenantSessionInitiator Human { get; }
    public Guid AttachmentId { get; }
    public Guid StreamId => Volatile.Read(ref _input).StreamId;
    public bool InputActive => Active && Volatile.Read(ref _input).Enabled;
    public bool Active => Volatile.Read(ref _active) != 0 && Volatile.Read(ref _retireRequested) == 0;
    public bool Retiring => Volatile.Read(ref _retireRequested) != 0;
    public async ValueTask<Operation> EnterAsync(bool requireActive, CancellationToken ct = default)
    {
        if (Retiring) throw new Security.AccessDeniedException("voice_lease_unavailable");
        await _operations.WaitAsync(ct).ConfigureAwait(false);
        if (Retiring || requireActive && Volatile.Read(ref _active) == 0)
        { _operations.Release(); throw new Security.AccessDeniedException("voice_lease_unavailable"); }
        return new(this, requireActive);
    }
    internal void Activate(Operation operation)
    {
        if (!operation.IsHeldBy(this)) throw new Security.AccessDeniedException("voice_lease_unavailable");
        Volatile.Write(ref _active, 1);
        Volatile.Write(ref _input, new(StreamId, true));
    }
    internal void PauseInput(Operation operation)
    { if (!operation.IsHeldActiveBy(this)) throw new Security.AccessDeniedException("voice_lease_unavailable"); Volatile.Write(ref _input, new(StreamId, false)); }
    internal void ResumeInput(Operation operation, Guid streamId)
    {
        if (!operation.IsHeldActiveBy(this) || Retiring || streamId == Guid.Empty || streamId == StreamId) throw new Security.AccessDeniedException("voice_lease_unavailable");
        Volatile.Write(ref _input, new(streamId, true));
    }
    public sealed class Operation : IAsyncDisposable
    {
        private readonly TenantVoiceLease _lease;
        private readonly bool _requiresActive;
        private int _released;
        internal Operation(TenantVoiceLease lease, bool requiresActive) { _lease = lease; _requiresActive = requiresActive; StreamId = lease.StreamId; }
        public Guid StreamId { get; }
        public bool IsHeldBy(TenantVoiceLease lease) => ReferenceEquals(_lease, lease) && Volatile.Read(ref _released) == 0
            && Volatile.Read(ref lease._retired) == 0 && (!_requiresActive || Volatile.Read(ref lease._active) != 0);
        public bool IsHeldActiveBy(TenantVoiceLease lease) => _requiresActive && IsHeldBy(lease);
        public bool IsHeldInputBy(TenantVoiceLease lease) => IsHeldActiveBy(lease) && lease._input.Enabled && StreamId == lease.StreamId;
        public ValueTask DisposeAsync()
        { if (Interlocked.Exchange(ref _released, 1) == 0) _lease._operations.Release(); return ValueTask.CompletedTask; }
    }
    public async ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _retireRequested, 1);
        await _operations.WaitAsync().ConfigureAwait(false);
        try { Volatile.Write(ref _active, 0); Volatile.Write(ref _retired, 1); }
        finally { _operations.Release(); }
    }
}

public sealed record TenantVoiceModeChange(TenantVoiceLease Lease, TenantVoiceLease.Operation Operation,
    long SecurityRevision, long ObservedSessionRevision, long InstanceRevision);
public sealed record TenantSpeechAdmission(TenantVoiceLease Lease, Guid StreamId, Guid UtteranceId);
public interface ITenantVoiceModeStore
{
    // Positive activation happens only under the host's quiescent Session reservation.
    ValueTask<TenantSession> ActivateTenantVoiceAsync(TenantVoiceModeChange request, Guid auditId, CancellationToken ct = default);
    // A retired actual lease can only downgrade its own Session to Text; no new authority/content.
    ValueTask RetireTenantVoiceAsync(TenantVoiceLease lease, long expectedRevision, CancellationToken ct = default);
}

public sealed record TenantVoiceRuntimeBinding(TenantVoiceLease Lease, Action<TenantVoiceRuntimeSignal> Signal);
public abstract record TenantVoiceRuntimeSignal(TenantVoiceLease Lease);
public sealed record TenantVoiceTurn(TenantVoiceLease Lease, Guid StreamId, Guid UtteranceId, string Text) : TenantVoiceRuntimeSignal(Lease);
public sealed record TenantVoiceInterrupt(TenantVoiceLease Lease, Guid StreamId, Guid RunId, Guid Generation) : TenantVoiceRuntimeSignal(Lease);
public sealed record TenantVoiceUnavailable(TenantVoiceLease Lease) : TenantVoiceRuntimeSignal(Lease);

// Exact attachment delivery bounds are supplied by the authenticated transport, never the browser.
public sealed record TenantPlaybackInput(TenantVoiceLease Lease, long SecurityRevision, Guid EntryId, long EntrySequence,
    Guid ResponseId, string Kind, long ConsumedSamples, int TextEndExclusive, long DeliveredSamples, int DeliveredSpeechEnd);
