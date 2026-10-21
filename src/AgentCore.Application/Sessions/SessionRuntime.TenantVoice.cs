using AgentCore.Application.Events;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Sessions;

public sealed partial class SessionRuntime
{
    private readonly TenantVoiceRuntimeBinding? _tenantVoiceBinding;
    private Guid _tenantSpeechStream;
    private Guid? _tenantInterruptedUtterance;
    private bool HasTenantSpeechInput => _tenantVoiceBinding is { Lease.InputActive: true }
        && _tenantSpeechStream == _tenantVoiceBinding.Lease.StreamId;
    private sealed record TenantSpeechReceived(EventContext Context, TenantVoiceLease Lease, Guid StreamId,
        SpeechRecognitionEvent Evidence, double? Score, TaskCompletionSource<bool> Completed) : SessionInput(Context);
    private sealed record TenantVoiceInputChanged(EventContext Context, TenantVoiceLease Lease,
        TaskCompletionSource<bool> Completed) : SessionInput(Context);
    private sealed record TenantVoiceRetired(EventContext Context, TenantSessionRoot Owner, TenantVoiceLease? Lease,
        TaskCompletionSource<bool> Completed) : SessionInput(Context);

    public async Task<bool> SubmitTenantSpeechAsync(TenantVoiceLease lease, Guid streamId, SpeechRecognitionEvent evidence, double? score, CancellationToken ct = default)
    {
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); BeginWork();
        if (!Enqueue(new TenantSpeechReceived(NewContext(), lease, streamId, evidence, score, completed), urgent: false)) return false;
        return await completed.Task.WaitAsync(ct).ConfigureAwait(false);
    }
    public async Task<bool> UpdateTenantVoiceInputAsync(TenantVoiceLease lease, CancellationToken ct = default)
    {
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); BeginWork();
        if (!Enqueue(new TenantVoiceInputChanged(NewContext(), lease, completed), urgent: true)) return false;
        return await completed.Task.WaitAsync(ct).ConfigureAwait(false);
    }
    public async Task<bool> RetireTenantVoiceAsync(TenantSessionRoot owner, TenantVoiceLease? lease, CancellationToken ct = default)
    {
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); BeginWork();
        if (!Enqueue(new TenantVoiceRetired(NewContext(), owner, lease, completed), urgent: true)) return false;
        return await completed.Task.WaitAsync(ct).ConfigureAwait(false);
    }
    private sealed record TenantPlaybackReceived(EventContext Context, TenantPlaybackInput Playback,
        TaskCompletionSource<bool> Completed) : SessionInput(Context);
    public async Task<bool> SubmitTenantPlaybackAsync(TenantPlaybackInput input, CancellationToken ct = default)
    {
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); BeginWork();
        if (!Enqueue(new TenantPlaybackReceived(NewContext(), input, completed), urgent: false)) return false;
        return await completed.Task.WaitAsync(ct).ConfigureAwait(false);
    }
    private Task HandleTenantPlaybackAsync(TenantPlaybackReceived input, CancellationToken ct)
    {
        try
        {
            var p = input.Playback;
            if (!OwnsTenantVoice(p.Lease) || !p.Lease.Active || _tenantTerminalCommitted || _tenantApprovalPending
                || _tenantApprovalSuspended || _tenantWaitSuspended || _store is not ITenantRuntimeReceiptWriter)
                throw new AccessDeniedException("voice_lease_unavailable");
            if (_activeResponseId != p.ResponseId || _responseLifecycle != ResponseLifecycle.Live)
            { input.Completed.TrySetResult(true); return Task.CompletedTask; }
            if (_activeEntryId != p.EntryId || p.Kind is not ("started" or "progress" or "completed" or "stopped")
                || p.ConsumedSamples < 0 || p.ConsumedSamples > p.DeliveredSamples || p.TextEndExclusive < 0
                || p.TextEndExclusive > p.DeliveredSpeechEnd || p.TextEndExclusive < _ackedPlaybackText)
                throw new AccessDeniedException("receipt_out_of_bounds");
            int heard;
            if (UsesClientSpeech)
            {
                if (p.ConsumedSamples != 0 || p.TextEndExclusive > _speechTextEndExclusive
                    || p.Kind == "started" && p.TextEndExclusive != 0
                    || p.Kind == "completed" && (!_speechOutputCompleted || p.TextEndExclusive != _speechTextEndExclusive))
                    throw new AccessDeniedException("receipt_out_of_bounds");
                heard = p.TextEndExclusive;
            }
            else
            {
                if (p.ConsumedSamples < _ackedSamples || p.ConsumedSamples > _sentSamples
                    || p.Kind == "started" && p.ConsumedSamples != 0 || p.TextEndExclusive > SpeechCoordinateLength())
                    throw new AccessDeniedException("receipt_out_of_bounds");
                heard = _spokenUntil.Credit(p.ConsumedSamples);
            }
            UpdateStreamingAssistant();
            var entry = _snapshot.Entries.SingleOrDefault(e => e.EntryId == p.EntryId && e.Sequence == p.EntrySequence)
                ?? throw new AccessDeniedException("receipt_unavailable");
            var proof = new TenantDeliveryReceipt(p.Lease.Owner, p.Lease.Human, p.SecurityRevision, p.EntryId, p.EntrySequence,
                p.ResponseId, 0, 0, Heard: new(p.Lease, heard));
            var updated = TenantDeliveryReceiptRules.Apply(proof, p.Lease.Owner, entry);
            var proposed = _snapshot with { Entries = _snapshot.Entries.Select(e => e.EntryId == updated.EntryId ? updated : e).ToArray() };
            // Release TTS backpressure and playback completion only after the current-human write commits.
            RequestPersist(proposed, ended: input.Completed, tenantReceipt: proof, then: token =>
                HandlePlaybackAsync(new(input.Context, p.ResponseId, p.Kind, p.ConsumedSamples, p.TextEndExclusive, input.Completed), token));
        }
        catch (Exception ex) { input.Completed.TrySetException(ex); }
        return Task.CompletedTask;
    }
    private bool OwnsTenantVoice(TenantVoiceLease lease) => ReferenceEquals(_tenantVoiceBinding?.Lease, lease)
        && _runtimeOwner == AgentRunOwner.ForTenant(lease.Owner.TenantId, lease.Owner.AgentInstanceId, lease.Owner.ExecutorPrincipalId)
        && SessionId == lease.Owner.SessionId;
    private async Task HandleTenantSpeechAsync(TenantSpeechReceived input, CancellationToken ct)
    {
        try
        {
            if (!OwnsTenantVoice(input.Lease) || !input.Lease.InputActive || input.StreamId != input.Lease.StreamId
                || _tenantTerminalCommitted || _tenantApprovalPending || _tenantApprovalSuspended || _tenantWaitSuspended || _responseTerminal)
            { input.Completed.TrySetResult(false); return; }
            _tenantSpeechStream = input.StreamId;
            await HandleSpeechAsync(new(input.Context, input.Evidence, input.Score, _speechEpoch), ct).ConfigureAwait(false);
            input.Completed.TrySetResult(true);
        }
        catch (Exception ex) { input.Completed.TrySetException(ex); }
    }
    private void HandleTenantVoiceInputChanged(TenantVoiceInputChanged input)
    {
        if (!OwnsTenantVoice(input.Lease) || !input.Lease.Active) { input.Completed.TrySetResult(false); return; }
        AbandonLiveSpeech(rotateEpoch: true); _muted = !input.Lease.InputActive;
        _tenantSpeechStream = input.Lease.StreamId; SetStreamId(input.Lease.StreamId);
        _input = _muted ? InputActivity.Idle : InputActivity.Listening;
        input.Completed.TrySetResult(true);
    }
    private async Task HandleTenantVoiceRetiredAsync(TenantVoiceRetired input, CancellationToken ct)
    {
        try
        {
            var owner = input.Owner;
            if (_runtimeOwner != AgentRunOwner.ForTenant(owner.TenantId, owner.AgentInstanceId, owner.ExecutorPrincipalId) || SessionId != owner.SessionId
                || input.Lease is not null && !OwnsTenantVoice(input.Lease) || input.Lease is null && _tenantVoiceBinding is not null)
                throw new AccessDeniedException("voice_lease_unavailable");
            if (_tenantTerminalCommitted || _tenantApprovalPending || _tenantApprovalSuspended || _tenantWaitSuspended)
            { input.Completed.TrySetResult(false); return; }
            // Retiring transport is not a human cancellation of already accepted work.
            await StopTransportDeliveryAsync().ConfigureAwait(false);
            MarkVoicePlaybackSettledForHeadlessDetach(); _headlessTransportDetached = true;
            _snapshot = _snapshot with { Mode = SessionMode.Text, PendingMode = null, UpdatedAt = _time.GetUtcNow() };
            _input = InputActivity.Idle;
            if (_modelDone && !_responseTerminal && _activeResponseId is { } response)
            {
                await CompleteAsync(input.Context, response, failed: false, ct).ConfigureAwait(false);
                input.Completed.TrySetResult(true);
            }
            else RequestPersist(_snapshot, ended: input.Completed);
        }
        catch (Exception ex) { input.Completed.TrySetException(ex); }
    }
    private void SignalTenantVoice(TenantVoiceRuntimeSignal signal)
    {
        if (!ReferenceEquals(_tenantVoiceBinding?.Lease, signal.Lease) || !signal.Lease.Active) return;
        // The receiver only queues supervised work; it must not await reentry into this mailbox.
        try { _tenantVoiceBinding.Signal(signal); }
        catch (Exception ex) { LogHandledRuntimeFailure(ex, "speech.tenant.handoff", "voice_handoff_unavailable"); }
    }
    private void ProposeTenantVoiceInterruption()
    {
        if (!HasTenantSpeechInput || _activeUtteranceId is not { } utterance || _tenantInterruptedUtterance == utterance
            || _boundAgentRun is not { Status: AgentRunStatus.Running, Claim: { } claim } run) return;
        _tenantInterruptedUtterance = utterance;
        SignalTenantVoice(new TenantVoiceInterrupt(_tenantVoiceBinding!.Lease, _tenantSpeechStream, run.AgentRunId, claim.Generation));
    }
    private void ProposeTenantVoiceTurn(string text)
    {
        if (!HasTenantSpeechInput || _activeUtteranceId is not { } utterance) return;
        _committedUtteranceId = utterance; InvalidateMaxUtteranceTimer();
        SignalTenantVoice(new TenantVoiceTurn(_tenantVoiceBinding!.Lease, _tenantSpeechStream, utterance, text));
    }
}
