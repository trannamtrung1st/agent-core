using System.Threading.Channels;
using AgentCore.Application.Audio;
using AgentCore.Application.Events;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Sessions;
using AgentCore.Application.Speech;
using AgentCore.Domain.Conversation;

namespace AgentCore.Api.Realtime;

public sealed partial class TenantSessionRuntimeHost
{
    // This resource owns input continuity and supervised handoff only. It owns no conversation state.
    private sealed class VoiceInput(TenantVoiceLease lease, bool ownsCapacity)
    {
        public TenantVoiceLease Lease { get; } = lease;
        public bool OwnsCapacity = ownsCapacity;
        public bool Activated;
        public CancellationTokenSource Lifetime { get; } = new();
        public SemaphoreSlim Commands { get; } = new(1, 1);
        public Channel<VoiceWork> Work { get; } = Channel.CreateBounded<VoiceWork>(new BoundedChannelOptions(128) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        public volatile SpeechRecognitionIngress? Ingress;
        public Task Activation { get; set; } = Task.CompletedTask;
        public Task Pump { get; set; } = Task.CompletedTask;
        public Task? Retirement;
        public Guid? Utterance;
        public int PartialRevision = -1;
        public Guid? ForwardedRun;
        public bool FinalReceived;
        public CancellationTokenSource? UtteranceTimeout;
        public HashSet<Guid> Closed { get; } = [];
        public Queue<Guid> ClosedOrder { get; } = [];
        public void CloseUtterance(Guid id)
        {
            if (Closed.Add(id)) ClosedOrder.Enqueue(id);
            while (ClosedOrder.Count > 128) Closed.Remove(ClosedOrder.Dequeue());
            if (Utterance == id) { Utterance = null; UtteranceTimeout?.Cancel(); }
        }
    }
    private abstract record VoiceWork;
    private sealed record VoiceEvidence(Guid Stream, SpeechRecognitionEvent Evidence, double? Score) : VoiceWork;
    private sealed record VoiceSignal(TenantVoiceRuntimeSignal Signal) : VoiceWork;
    private sealed record VoiceFault(Guid Stream, string Code = "SpeechRecognitionFailed") : VoiceWork;
    private readonly Dictionary<Guid, VoiceInput> _voices = [];

    // Called under _gate. One Session slot follows its Runtime and optional idle input resource.
    private bool TryReserveVoiceOrRunCapacity(Guid session, out bool owns)
    {
        owns = _voices.GetValueOrDefault(session)?.OwnsCapacity != true;
        return !owns || capacity.TryReserveTenantRuntime();
    }
    public async ValueTask ResetOrphanVoiceAsync(Guid tenant, Guid session, TenantSessionInitiator human, CancellationToken ct)
    {
        var saved = await inputSessions.FindTenantSessionAsync(tenant, session, ct).ConfigureAwait(false);
        if (saved is null || saved.Snapshot.Mode == SessionMode.Text && saved.Snapshot.PendingMode is null) return;
        while (true)
        {
            Live? live;
            lock (_gate)
            {
                if (_voices.ContainsKey(session)) return; // observers never take another attachment's microphone
                live = _live.GetValueOrDefault(session);
                if (live is null)
                {
                    if (!_quiescentWrites.Add(session)) throw new SecurityConflictException("session_in_use");
                    _sessionWriteEpoch = checked(_sessionWriteEpoch + 1);
                }
            }
            if (live is not null)
            {
                if (await live.Runtime.RetireTenantVoiceAsync(saved.Owner, null, ct).ConfigureAwait(false)) return;
                await live.Stopped.Task.WaitAsync(ct).ConfigureAwait(false); continue;
            }
            try
            {
                var lease = await (voiceService ?? throw new AccessDeniedException("tenant_voice_transport_unavailable"))
                    .PrepareRetirementAsync(tenant, session, human, ids.NewId(), ct).ConfigureAwait(false);
                await lease.DisposeAsync().ConfigureAwait(false);
                saved = await inputSessions.FindTenantSessionAsync(tenant, session, ct).ConfigureAwait(false);
                if (saved is not null) await ((ITenantVoiceModeStore)inputSecurity).RetireTenantVoiceAsync(lease, saved.Snapshot.Revision, ct).ConfigureAwait(false);
                return;
            }
            finally { lock (_gate) { _quiescentWrites.Remove(session); _sessionWriteEpoch = checked(_sessionWriteEpoch + 1); } }
        }
    }
    public async ValueTask<TenantVoiceLease> PrepareVoiceAsync(Guid tenant, Guid session, TenantSessionInitiator human, Guid attachment, CancellationToken ct)
    {
        if (voiceService is null || voiceAvailability is null || voiceCommands is null) throw new AccessDeniedException("tenant_voice_transport_unavailable");
        var lease = await voiceService.PrepareAsync(tenant, session, human, attachment, ct).ConfigureAwait(false);
        lock (_gate)
        {
            if (_stopping || _voices.ContainsKey(session)) throw new AccessDeniedException("voice_attachment_in_use");
            var owns = !_live.ContainsKey(session);
            if (owns && !capacity.TryReserveTenantRuntime()) throw new AccessDeniedException("tenant_runtime_unavailable");
            _voices.Add(session, new(lease, owns));
        }
        return lease;
    }
    public void StartVoice(TenantVoiceLease lease)
    {
        VoiceInput voice;
        lock (_gate)
        {
            voice = _voices.GetValueOrDefault(lease.Owner.SessionId) ?? throw new AccessDeniedException("voice_lease_unavailable");
            if (!ReferenceEquals(voice.Lease, lease) || voice.Retirement is not null) throw new AccessDeniedException("voice_lease_unavailable");
            // Task bodies are scheduled outside the registry lock; no provider callback can enter it synchronously.
            voice.Pump = Task.Run(() => PumpVoiceAsync(voice));
            voice.Activation = Task.Run(() => ActivateVoiceAsync(voice));
        }
    }
    private async Task ActivateVoiceAsync(VoiceInput voice)
    {
        var lease = voice.Lease;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds((voicePolicy ?? new()).PendingVoiceTimeoutMs), time);
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, voice.Lifetime.Token);
        try
        {
            await delivery.PublishVoiceStateAsync(lease, pending: true, bounded.Token).ConfigureAwait(false);
            // Provider setup must never hold the Session's writer reservation.
            await OpenVoiceInputAsync(voice, lease.StreamId, bounded.Token).ConfigureAwait(false);
            while (true)
            {
                Live? live; bool reserved;
                lock (_gate)
                {
                    if (_stopping || _voices.GetValueOrDefault(lease.Owner.SessionId) != voice || lease.Retiring) return;
                    live = _live.GetValueOrDefault(lease.Owner.SessionId);
                    reserved = live is null && _quiescentWrites.Add(lease.Owner.SessionId);
                    if (reserved) _sessionWriteEpoch = checked(_sessionWriteEpoch + 1);
                }
                if (live is not null) { await live.Stopped.Task.WaitAsync(bounded.Token).ConfigureAwait(false); continue; }
                if (!reserved) { await Task.Delay(TimeSpan.FromMilliseconds(10), time, bounded.Token).ConfigureAwait(false); continue; }
                var activated = false;
                try
                {
                    await voiceService!.ActivateQuiescentAsync(lease, bounded.Token).ConfigureAwait(false);
                    voice.Ingress?.Start(); voice.Activated = true; activated = true;
                }
                catch (SecurityConflictException ex) when (ex.ReasonCode == "session_in_use") { }
                finally { lock (_gate) { _quiescentWrites.Remove(lease.Owner.SessionId); _sessionWriteEpoch = checked(_sessionWriteEpoch + 1); } }
                if (!activated) { await Task.Delay(TimeSpan.FromMilliseconds(10), time, bounded.Token).ConfigureAwait(false); continue; }
                await delivery.PublishVoiceStateAsync(lease, pending: false, bounded.Token).ConfigureAwait(false);
                return;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            await ReportVoiceUnavailableAsync(voice, timeout.IsCancellationRequested ? "VoiceSetupTimeout" : "VoiceUnavailable").ConfigureAwait(false);
            // A denied/failed/expired setup never leaves a resumable microphone behind.
            _ = ObserveVoiceRetirementAsync(RetireVoiceAsync(lease).AsTask());
        }
    }
    private async Task ReportVoiceUnavailableAsync(VoiceInput voice, string code)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5), time);
        try { await delivery.PublishVoiceAsync(voice.Lease, new ErrorOutput("Speech", code,
            "Voice became unavailable. Continue in text or start voice again.", false, null), deadline.Token).ConfigureAwait(false); }
        catch (Exception) { } // Best-effort diagnostics must never prevent resource retirement.
    }
    private async Task OpenVoiceInputAsync(VoiceInput voice, Guid stream, CancellationToken ct)
    {
        if (voiceAvailability!.EffectivePlan.InputTransport == SpeechTransport.ClientTranscript) return;
        var saved = await inputSessions.FindTenantSessionAsync(voice.Lease.Owner.TenantId, voice.Lease.Owner.SessionId, ct).ConfigureAwait(false)
            ?? throw new AccessDeniedException("resource_unavailable");
        var provider = await factory.OpenTenantRecognitionAsync(new(CanonicalAudio.Format, SpeechLocale.Resolve(saved.Snapshot).Effective), ct).ConfigureAwait(false);
        voice.Ingress = new(provider, stream, (e, score) => QueueVoice(voice, new VoiceEvidence(stream, e, score)),
            (code, _) => QueueVoice(voice, new VoiceFault(stream, code)), voice.Lifetime.Token);
    }
    private void QueueVoice(VoiceInput voice, VoiceWork work)
    {
        if (voice.Lease.Retiring || voice.Lifetime.IsCancellationRequested) return;
        if (!voice.Work.Writer.TryWrite(work)) _ = ObserveVoiceRetirementAsync(RetireVoiceAsync(voice.Lease).AsTask());
    }
    private async Task PumpVoiceAsync(VoiceInput voice)
    {
        try
        {
            await foreach (var work in voice.Work.Reader.ReadAllAsync(voice.Lifetime.Token).ConfigureAwait(false))
            {
                if (!voice.Lease.Active) continue;
                try
                {
                    if (work is VoiceEvidence evidence) await RouteVoiceEvidenceAsync(voice, evidence, voice.Lifetime.Token).ConfigureAwait(false);
                    else if (work is VoiceSignal { Signal: TenantVoiceTurn turn } && turn.StreamId == voice.Lease.StreamId)
                        await AdmitVoiceTurnAsync(voice, turn.StreamId, turn.UtteranceId, turn.Text, voice.Lifetime.Token).ConfigureAwait(false);
                    else if (work is VoiceSignal { Signal: TenantVoiceInterrupt interrupt } && interrupt.StreamId == voice.Lease.StreamId)
                        await InterruptVoiceRunAsync(voice, interrupt, voice.Lifetime.Token).ConfigureAwait(false);
                    else if (work is VoiceFault fault && fault.Stream == voice.Lease.StreamId && voice.Lease.InputActive)
                    {
                        if (fault.Code is "AudioDiscontinuity" or "MaxUtterance") await RestartVoiceInputAsync(voice, fault.Code, voice.Lifetime.Token).ConfigureAwait(false);
                        else { await ReportVoiceUnavailableAsync(voice, "VoiceUnavailable").ConfigureAwait(false); _ = ObserveVoiceRetirementAsync(RetireVoiceAsync(voice.Lease).AsTask()); return; }
                    }
                    else if (work is VoiceSignal { Signal: TenantVoiceUnavailable })
                    { await ReportVoiceUnavailableAsync(voice, "VoiceUnavailable").ConfigureAwait(false); _ = ObserveVoiceRetirementAsync(RetireVoiceAsync(voice.Lease).AsTask()); return; }
                }
                catch (SecurityConflictException) { } // exact stale owning operation is denied; no stale replay
                catch (AccessDeniedException)
                { _ = ObserveVoiceRetirementAsync(RetireVoiceAsync(voice.Lease).AsTask()); return; }
            }
        }
        catch (OperationCanceledException) when (voice.Lifetime.IsCancellationRequested) { }
        catch (Exception)
        { _ = ObserveVoiceRetirementAsync(RetireVoiceAsync(voice.Lease).AsTask()); }
    }
    private async Task RouteVoiceEvidenceAsync(VoiceInput voice, VoiceEvidence input, CancellationToken ct)
    {
        var lease = voice.Lease; var evidence = input.Evidence;
        if (!lease.InputActive || lease.StreamId != input.Stream || voice.Closed.Contains(evidence.UtteranceId)) return;
        await delivery.RequireVoiceRecipientAsync(lease, ct).ConfigureAwait(false);
        if (evidence.UtteranceId == Guid.Empty || input.Score is { } score && (!double.IsFinite(score) || score is < 0 or > 1)) return;
        if (evidence is SpeechStarted)
        {
            if (voice.Utterance is { } previous && previous != evidence.UtteranceId && !voice.FinalReceived) voice.CloseUtterance(previous);
            if (voice.Utterance == evidence.UtteranceId) return;
            voice.Utterance = evidence.UtteranceId; voice.PartialRevision = -1; voice.ForwardedRun = null; voice.FinalReceived = false;
            voice.UtteranceTimeout?.Cancel();
            voice.UtteranceTimeout?.Dispose(); voice.UtteranceTimeout = CancellationTokenSource.CreateLinkedTokenSource(voice.Lifetime.Token);
            _ = LimitUtteranceAsync(voice, input.Stream, evidence.UtteranceId, voice.UtteranceTimeout.Token);
        }
        else if (evidence is not RecognitionFailed && voice.Utterance != evidence.UtteranceId) return;
        if (evidence is SpeechPartial partial)
        {
            if (partial.Revision < 0 || partial.Revision <= voice.PartialRevision || partial.Text.Length > 8000 || !Confidence(partial.Confidence)) return;
            voice.PartialRevision = partial.Revision;
        }
        if (evidence is SpeechFinal final && (final.Text.Length > 8000 || !Confidence(final.Confidence))) return;
        if (evidence is SpeechFinal) { voice.FinalReceived = true; voice.UtteranceTimeout?.Cancel(); }
        if (evidence is RecognitionFailed) { QueueVoice(voice, new VoiceFault(input.Stream)); return; }
        Live? live; lock (_gate) live = _live.GetValueOrDefault(lease.Owner.SessionId);
        if (live is not null)
        {
            if (voice.ForwardedRun != live.Run.AgentRunId && evidence is not SpeechStarted)
                await live.Runtime.SubmitTenantSpeechAsync(lease, input.Stream, new SpeechStarted(evidence.UtteranceId), input.Score, ct).ConfigureAwait(false);
            voice.ForwardedRun = live.Run.AgentRunId;
            if (await live.Runtime.SubmitTenantSpeechAsync(lease, input.Stream, evidence, input.Score, ct).ConfigureAwait(false)) return;
        }
        if (evidence is SpeechPartial text) await delivery.PublishVoiceAsync(lease, new TranscriptPartialOutput(text.UtteranceId, text.Revision, text.Text), ct).ConfigureAwait(false);
        if (evidence is SpeechFinal turn) await AdmitVoiceTurnAsync(voice, input.Stream, turn.UtteranceId, turn.Text, ct).ConfigureAwait(false);
    }
    private static bool Confidence(double? value) => value is null || double.IsFinite(value.Value) && value is >= 0 and <= 1;
    private async Task LimitUtteranceAsync(VoiceInput voice, Guid stream, Guid utterance, CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds((voicePolicy ?? new()).MaxUtteranceSeconds), time, ct).ConfigureAwait(false);
            // Retire the overlong stream; no synthetic transcript or human cancellation is invented.
            QueueVoice(voice, new VoiceFault(stream, "MaxUtterance"));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }
    private async Task AdmitVoiceTurnAsync(VoiceInput voice, Guid stream, Guid utterance, string text, CancellationToken ct)
    {
        if (!voice.Lease.InputActive || stream != voice.Lease.StreamId || voice.Closed.Contains(utterance)) return;
        text = text.Trim();
        if (string.IsNullOrWhiteSpace(text)) { voice.CloseUtterance(utterance); await delivery.PublishVoiceAsync(voice.Lease, new TranscriptDiscardedOutput(utterance), ct).ConfigureAwait(false); return; }
        await delivery.RequireVoiceRecipientAsync(voice.Lease, ct).ConfigureAwait(false);
        var receipt = await voiceCommands!().SubmitSpeechAsync(voice.Lease, stream, utterance, text, ct).ConfigureAwait(false);
        voice.CloseUtterance(utterance);
        await delivery.PublishVoiceAsync(voice.Lease, new TranscriptFinalOutput(utterance, text, receipt.Run.EntryId, receipt.EntrySequence), ct).ConfigureAwait(false);
    }
    private async Task InterruptVoiceRunAsync(VoiceInput voice, TenantVoiceInterrupt interrupt, CancellationToken ct)
    {
        if (!voice.Lease.InputActive) return;
        try
        {
            await using var operation = await voice.Lease.EnterAsync(true, ct).ConfigureAwait(false);
            if (!operation.IsHeldInputBy(voice.Lease) || operation.StreamId != interrupt.StreamId) return;
            var recipient = await delivery.RequireVoiceRecipientAsync(voice.Lease, ct).ConfigureAwait(false);
            var current = await runs.FindTenantRunAsync(voice.Lease.Owner.TenantId, interrupt.RunId, ct).ConfigureAwait(false);
            if (current is not { Status: AgentRunStatus.Running, Claim: { } claim } || claim.Generation != interrupt.Generation) return;
            await voiceCommands!().CancelAsync(voice.Lease.Owner.TenantId, current.AgentRunId, voice.Lease.Human,
                recipient.SecurityRevision, current.Revision, ct).ConfigureAwait(false);
        }
        catch (AccessDeniedException) { await delivery.PublishVoiceAsync(voice.Lease, new PlaybackGainOutput(1, 20, null), ct).ConfigureAwait(false); }
        catch (SecurityConflictException) { }
    }
    private async Task RestartVoiceInputAsync(VoiceInput voice, string code, CancellationToken ct)
    {
        await voice.Commands.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var lease = voice.Lease;
            if (!lease.InputActive) return;
            await delivery.RequireVoiceRecipientAsync(lease, ct).ConfigureAwait(false);
            await voiceService!.PauseInputAsync(lease, ct).ConfigureAwait(false);
            voice.UtteranceTimeout?.Cancel();
            if (voice.Utterance is { } utterance) voice.CloseUtterance(utterance);
            var prior = voice.Ingress; voice.Ingress = null;
            if (prior is not null) await prior.DisposeAsync().ConfigureAwait(false);
            var stream = await voiceService.ResumeInputAsync(lease, ct).ConfigureAwait(false);
            await OpenVoiceInputAsync(voice, stream, ct).ConfigureAwait(false); voice.Ingress?.Start();
            Live? live; lock (_gate) live = _live.GetValueOrDefault(lease.Owner.SessionId);
            if (live is not null) await live.Runtime.UpdateTenantVoiceInputAsync(lease, ct).ConfigureAwait(false);
            await delivery.PublishVoiceAsync(lease, new ErrorOutput("Transport", code, "Speech input restarted. Please say that again.", false, null), ct).ConfigureAwait(false);
            await delivery.PublishVoiceStateAsync(lease, false, ct).ConfigureAwait(false);
        }
        finally { voice.Commands.Release(); }
    }
    public async ValueTask MuteVoiceAsync(TenantVoiceLease lease, bool muted, CancellationToken ct)
    {
        VoiceInput voice; lock (_gate) voice = _voices.GetValueOrDefault(lease.Owner.SessionId) ?? throw new AccessDeniedException("voice_lease_unavailable");
        if (!ReferenceEquals(voice.Lease, lease)) throw new AccessDeniedException("voice_lease_unavailable");
        await voice.Commands.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await delivery.RequireVoiceRecipientAsync(lease, ct).ConfigureAwait(false);
            if (muted == !lease.InputActive) return;
            await voiceService!.PauseInputAsync(lease, ct).ConfigureAwait(false);
            voice.UtteranceTimeout?.Cancel();
            var old = voice.Ingress; voice.Ingress = null;
            if (old is not null) await old.DisposeAsync().ConfigureAwait(false);
            if (!muted)
            {
                var stream = await voiceService.ResumeInputAsync(lease, ct).ConfigureAwait(false);
                await OpenVoiceInputAsync(voice, stream, ct).ConfigureAwait(false); voice.Ingress?.Start();
            }
            Live? live; lock (_gate) live = _live.GetValueOrDefault(lease.Owner.SessionId);
            if (live is not null) await live.Runtime.UpdateTenantVoiceInputAsync(lease, ct).ConfigureAwait(false);
            await delivery.PublishVoiceStateAsync(lease, false, ct).ConfigureAwait(false);
        }
        catch { _ = ObserveVoiceRetirementAsync(RetireVoiceAsync(lease).AsTask()); throw; }
        finally { voice.Commands.Release(); }
    }
    public bool AdmitVoiceAudio(TenantVoiceLease lease, Guid stream, AudioFrame frame)
    {
        VoiceInput? voice; lock (_gate) voice = _voices.GetValueOrDefault(lease.Owner.SessionId);
        return lease.InputActive && lease.StreamId == stream && ReferenceEquals(voice?.Lease, lease)
            && voice?.Ingress?.TryAdmitAudio(frame, stream) == true;
    }
    public bool AdmitVoiceBoundary(TenantVoiceLease lease, Guid stream, Guid utterance, SpeechBoundary boundary, double? score, long offset, double duration)
    {
        VoiceInput? voice; lock (_gate) voice = _voices.GetValueOrDefault(lease.Owner.SessionId);
        return lease.InputActive && lease.StreamId == stream && ReferenceEquals(voice?.Lease, lease)
            && voice?.Ingress?.TryAdmitBoundary(utterance, boundary, score, offset, duration, stream) == true;
    }
    public bool AdmitVoiceEvidence(TenantVoiceLease lease, SpeechRecognitionEvent evidence, double? score)
    {
        lock (_gate)
        {
            if (voiceAvailability?.EffectivePlan.InputTransport != SpeechTransport.ClientTranscript || !lease.InputActive
                || _voices.GetValueOrDefault(lease.Owner.SessionId) is not { } voice || !ReferenceEquals(voice.Lease, lease)) return false;
            QueueVoice(voice, new VoiceEvidence(lease.StreamId, evidence, score)); return !lease.Retiring;
        }
    }
    public async ValueTask<bool> ReceiveVoicePlaybackAsync(TenantPlaybackInput input, CancellationToken ct)
    {
        await delivery.RequireVoiceRecipientAsync(input.Lease, ct).ConfigureAwait(false);
        Live? live; lock (_gate) live = _live.GetValueOrDefault(input.Lease.Owner.SessionId);
        if (live is null) return true; // no replay and no retroactive hearing after the owning Run retired
        return await live.Runtime.SubmitTenantPlaybackAsync(input, ct).ConfigureAwait(false);
    }
    public ValueTask RetireVoiceAsync(TenantVoiceLease lease)
    {
        lock (_gate)
        {
            if (_voices.GetValueOrDefault(lease.Owner.SessionId) is not { } voice || !ReferenceEquals(voice.Lease, lease)) return ValueTask.CompletedTask;
            // Dispose starts by closing new ingress synchronously. Cleanup waits outside this lock.
            if (voice.Retirement is null)
            {
                var retired = lease.DisposeAsync();
                voice.Retirement = Task.Run(() => RetireVoiceCoreAsync(voice, retired));
            }
            return new(voice.Retirement);
        }
    }
    private async Task RetireVoiceCoreAsync(VoiceInput voice, ValueTask retired)
    {
        try
        {
            await retired.ConfigureAwait(false); await voice.Lifetime.CancelAsync().ConfigureAwait(false); voice.Work.Writer.TryComplete();
            await voice.Activation.ConfigureAwait(false);
            await voice.Commands.WaitAsync().ConfigureAwait(false);
            try { if (voice.Ingress is { } ingress) { voice.Ingress = null; await ingress.DisposeAsync().ConfigureAwait(false); } }
            finally { voice.Commands.Release(); }
            while (voice.Activated)
            {
                Live? live; bool reserved;
                lock (_gate)
                {
                    live = _live.GetValueOrDefault(voice.Lease.Owner.SessionId);
                    reserved = live is null && _quiescentWrites.Add(voice.Lease.Owner.SessionId);
                    if (reserved) _sessionWriteEpoch = checked(_sessionWriteEpoch + 1);
                }
                if (live is not null)
                {
                    if (await live.Runtime.RetireTenantVoiceAsync(voice.Lease.Owner, ReferenceEquals(live.Output.Voice, voice.Lease) ? voice.Lease : null).ConfigureAwait(false)) break;
                    await live.Stopped.Task.ConfigureAwait(false); continue;
                }
                if (!reserved) { await Task.Delay(TimeSpan.FromMilliseconds(10), time).ConfigureAwait(false); continue; }
                try
                {
                    var saved = await inputSessions.FindTenantSessionAsync(voice.Lease.Owner.TenantId, voice.Lease.Owner.SessionId).ConfigureAwait(false);
                    if (saved is not null) await ((ITenantVoiceModeStore)inputSecurity).RetireTenantVoiceAsync(voice.Lease, saved.Snapshot.Revision).ConfigureAwait(false);
                    break;
                }
                finally { lock (_gate) { _quiescentWrites.Remove(voice.Lease.Owner.SessionId); _sessionWriteEpoch = checked(_sessionWriteEpoch + 1); } }
            }
            await delivery.PublishVoiceStateAsync(voice.Lease, false, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            var release = false;
            lock (_gate)
            {
                if (_voices.GetValueOrDefault(voice.Lease.Owner.SessionId) == voice) _voices.Remove(voice.Lease.Owner.SessionId);
                if (voice.OwnsCapacity && _live.GetValueOrDefault(voice.Lease.Owner.SessionId) is { } live)
                { live.OwnsCapacity = true; voice.OwnsCapacity = false; }
                release = voice.OwnsCapacity; voice.OwnsCapacity = false;
            }
            if (release) capacity.ReleaseTenantRuntime(); voice.UtteranceTimeout?.Cancel(); voice.UtteranceTimeout?.Dispose();
            await voice.Pump.ConfigureAwait(false); voice.Lifetime.Dispose();
        }
    }
    private async Task ObserveVoiceRetirementAsync(Task pending)
    {
        try { await pending.ConfigureAwait(false); }
        catch (Exception ex) { AgentCore.Application.Observability.DiagnosticLog.Warning(logger, ex, ids.NewId(), "Tenant voice retirement failed."); }
    }
}
