using AgentCore.Application.Audio;
using AgentCore.Application.Events;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Sessions;
using AgentCore.Contracts.Realtime;
using AgentCore.Domain.Conversation;
using Microsoft.AspNetCore.SignalR;

namespace AgentCore.Api.Realtime;

public sealed partial class TenantSessionLiveDelivery
{
    public Task<CommandAck> SetVoiceModeAsync(string connection, ClientCommand<SetModePayload> command, TenantSessionRuntimeHost host, CancellationToken ct)
        => ExecuteAsync(connection, command, "session.mode.set", _ => null, (_, _, _) => Task.CompletedTask, async (sub, _, token) =>
        {
            if (command.Payload?.Mode == "text")
            {
                var recipient = await access.AuthorizeRecipientAsync(sub.Tenant, sub.Session, sub.Human, token).ConfigureAwait(false);
                if (recipient.Mode == SessionMode.Voice && sub.Voice is null) throw new AccessDeniedException("voice_attachment_in_use");
                if (sub.Voice is { } old) await host.RetireVoiceAsync(old).ConfigureAwait(false);
                return new() { EventId = command.EventId, Accepted = true };
            }
            if (command.Payload?.Mode != "voice") return Reject(command.EventId, "ValidationError", "Choose text or voice.", "Validation");
            if (sub.Voice is { Retiring: false }) return new() { EventId = command.EventId, Accepted = true };
            var lease = await host.PrepareVoiceAsync(sub.Tenant, sub.Session, sub.Human, sub.Attachment, token).ConfigureAwait(false);
            sub.Voice = lease; sub.StopVoice = () => host.RetireVoiceAsync(lease).AsTask();
            try
            {
                if (!_connections.TryGetValue(connection, out var state) || state.Closed || state.Current != sub) throw new AccessDeniedException("StaleAttachment");
                host.StartVoice(lease);
            }
            catch { await host.RetireVoiceAsync(lease).ConfigureAwait(false); throw; }
            return new() { EventId = command.EventId, Accepted = true };
        }, ct);

    public Task<CommandAck> MuteVoiceAsync(string connection, ClientCommand<MutePayload> command, TenantSessionRuntimeHost host, CancellationToken ct)
        => ExecuteAsync(connection, command, "session.mute", _ => null, (_, _, _) => Task.CompletedTask, async (sub, _, token) =>
        {
            if (command.Payload is null || sub.Voice is not { Active: true } lease) throw new AccessDeniedException("voice_lease_unavailable");
            await host.MuteVoiceAsync(lease, command.Payload.Muted, token).ConfigureAwait(false);
            return new() { EventId = command.EventId, Accepted = true };
        }, ct);
    public Task<CommandAck> VoiceBoundaryAsync(string connection, IRealtimeCommand command, TenantSessionRuntimeHost host, bool started, CancellationToken ct)
        => ExecuteAsync(connection, command, started ? "user.speech.started" : "user.speech.ended", _ => null, (_, _, _) => Task.CompletedTask, (sub, _, token) =>
        {
            var start = command.Payload as SpeechStartedPayload; var end = command.Payload as SpeechEndedPayload;
            var streamText = started ? start?.StreamId : end?.StreamId;
            var utteranceText = started ? start?.UtteranceId : end?.UtteranceId;
            var offset = started ? start?.SampleOffset : end?.SampleOffset;
            var score = started ? start?.ActivityScore : end?.ActivityScore;
            var duration = started ? 0 : end?.DurationMs;
            var admitted = sub.Voice is { InputActive: true } lease && Guid.TryParse(streamText, out var stream)
                && Guid.TryParse(utteranceText, out var utterance) && offset is >= 0 && duration is >= 0
                && host.AdmitVoiceBoundary(lease, stream, utterance, started ? SpeechBoundary.Started : SpeechBoundary.Ended, score, offset.Value, duration.Value);
            return Task.FromResult(admitted ? new CommandAck { EventId = command.EventId, Accepted = true }
                : Reject(command.EventId, "InvalidSpeech", "Speech belongs to a retired or unavailable input stream.", "Validation"));
        }, ct);
    public Task<CommandAck> VoiceEvidenceAsync(string connection, ClientCommand<ClientSpeechEvidencePayload> command, TenantSessionRuntimeHost host, CancellationToken ct)
        => ExecuteAsync(connection, command, "client.speech.evidence", _ => null, (_, _, _) => Task.CompletedTask, (sub, _, token) =>
        {
            var body = command.Payload;
            if (body is null || sub.Voice is not { InputActive: true } lease || !Guid.TryParse(body.UtteranceId, out var utterance)
                || !Guid.TryParse(body.StreamId, out var stream) || stream != lease.StreamId
                || utterance == Guid.Empty || body.Revision is < 0 || body.Text is { Length: > 8000 }
                || !Finite(body.Confidence) || !Finite(body.ActivityScore) || body.DurationMs is { } ms && (!double.IsFinite(ms) || ms < 0))
                return Task.FromResult(Reject(command.EventId, "InvalidSpeech", "Speech evidence is invalid.", "Validation"));
            SpeechRecognitionEvent? evidence = body.Kind switch {
                "started" => new SpeechStarted(utterance),
                "partial" when body.Text is not null && body.Revision is { } revision => new SpeechPartial(utterance, revision, body.Text, body.Confidence),
                "final" when body.Text is not null => new SpeechFinal(utterance, body.Text, body.Confidence),
                "ended" => new SpeechEnded(utterance),
                "failed" => new RecognitionFailed(utterance, new(ProviderErrorCode.Unknown, "Client transcript failed.")), _ => null };
            var admitted = evidence is not null && host.AdmitVoiceEvidence(lease, evidence, body.ActivityScore);
            return Task.FromResult(admitted ? new CommandAck { EventId = command.EventId, Accepted = true }
                : Reject(command.EventId, "InvalidSpeech", "The selected input transport cannot accept this evidence.", "Validation"));
        }, ct);
    private static bool Finite(double? n) => n is null || double.IsFinite(n.Value) && n is >= 0 and <= 1;

    public async Task<bool> VoiceAudioAsync(string connection, InputAudioDto dto, TenantSessionRuntimeHost host, CancellationToken ct)
    {
        if (!_connections.TryGetValue(connection, out var state) || state.Closed || state.Current is not { } sub
            || sub.Voice is not { InputActive: true } lease || dto.ProtocolVersion != 1
            || !Guid.TryParse(dto.SessionId, out var session) || session != sub.Session
            || !Guid.TryParse(dto.AttachmentId, out var attachment) || attachment != sub.Attachment
            || !Guid.TryParse(dto.StreamId, out var stream) || stream != lease.StreamId) return false;
        try
        {
            await RequireVoiceRecipientAsync(lease, ct).ConfigureAwait(false);
            if (state.Current != sub || state.Closed) return false;
            host.AdmitVoiceAudio(lease, stream, new(dto.FrameSequence, dto.SampleOffset, dto.Data));
        }
        catch (AccessDeniedException) { RetireVoice(sub); }
        catch (SecurityConflictException) { RetireVoice(sub); }
        return false;
    }
    public Task<CommandAck> VoicePlaybackAsync(string connection, ClientCommand<PlaybackPayload> command, TenantSessionRuntimeHost host, string kind, CancellationToken ct)
        => ExecuteAsync(connection, command, "playback." + kind, _ => null, (_, _, _) => Task.CompletedTask, async (sub, _, token) =>
        {
            if (command.Payload is not { } body || sub.Voice is not { Active: true } lease
                || !Guid.TryParse(command.ResponseId, out var response) || !sub.Delivery.TryGetValue(response, out var delivered))
                return Reject(command.EventId, "InvalidPlayback", "Playback must belong to speech delivered on this attachment.", "Validation");
            var recipient = await RequireVoiceRecipientAsync(lease, token).ConfigureAwait(false);
            var accepted = await host.ReceiveVoicePlaybackAsync(new(lease, recipient.SecurityRevision, delivered.EntryId, delivered.EntrySequence,
                response, kind, body.ConsumedSamples, body.TextEndExclusive, delivered.Samples, speech?.EffectivePlan.OutputTransport == SpeechTransport.ClientSpeech ? delivered.SpeechEnd : delivered.ProjectionEnd), token).ConfigureAwait(false);
            return accepted ? new() { EventId = command.EventId, Accepted = true } : Reject(command.EventId, "InvalidPlayback", "Playback progress is unavailable.");
        }, ct);

    public async ValueTask<TenantConversationRecipient> RequireVoiceRecipientAsync(TenantVoiceLease lease, CancellationToken ct)
    {
        var pair = _connections.FirstOrDefault(p => p.Value.Current is { } s && ReferenceEquals(s.Voice, lease));
        if (pair.Value is not { Closed: false, Current: { } sub } || sub.Attachment != lease.AttachmentId || sub.Human != lease.Human
            || sub.Tenant != lease.Owner.TenantId || sub.Session != lease.Owner.SessionId || lease.Retiring)
            throw new AccessDeniedException("voice_lease_unavailable");
        var recipient = await access.AuthorizeRecipientAsync(sub.Tenant, sub.Session, sub.Human, ct).ConfigureAwait(false);
        if (recipient.Owner != lease.Owner || pair.Value.Closed || pair.Value.Current != sub || lease.Retiring)
            throw new AccessDeniedException("voice_lease_unavailable");
        return recipient;
    }
    public async Task PublishVoiceStateAsync(TenantVoiceLease lease, bool pending, CancellationToken ct)
    {
        // Transport state is projected per recipient. Only the owner receives the input stream token.
        foreach (var pair in _connections.ToArray().Where(p => p.Value.Current is { } s && s.Tenant == lease.Owner.TenantId && s.Session == lease.Owner.SessionId))
        {
            var state = pair.Value; await state.Gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (state.Closed || state.Current is not { } sub) continue;
                var recipient = await access.AuthorizeRecipientAsync(sub.Tenant, sub.Session, sub.Human, ct).ConfigureAwait(false);
                if (recipient.Owner != lease.Owner) continue;
                var own = ReferenceEquals(sub.Voice, lease) && !lease.Retiring;
                var payload = new StateChangedOutput(recipient.Status, recipient.Mode, pending ? SessionMode.Voice : null,
                    own && lease.InputActive ? "Listening" : "Idle",
                    _sources.GetValueOrDefault((sub.Tenant, sub.Session))?.State?.OutputState ?? "Idle", own && lease.Active && !lease.InputActive,
                    own && lease.Active ? lease.StreamId : null, LifecycleStatus: recipient.LifecycleStatus);
                await hubs.Clients.Client(pair.Key).SendAsync("SessionEvent", SessionEventMapper.Map(new(Context(sub.Session), null, payload), sub.Attachment, ++sub.Sequence), ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is AccessDeniedException or SecurityConflictException) { if (state.Current is { } failed) RetireVoice(failed); state.Current = null; }
            finally { state.Gate.Release(); }
        }
    }
    public async Task PublishVoiceAsync(TenantVoiceLease lease, OutputPayload payload, CancellationToken ct)
    {
        await RequireVoiceRecipientAsync(lease, ct).ConfigureAwait(false);
        foreach (var pair in _connections.ToArray().Where(p => p.Value.Current is { } s && ReferenceEquals(s.Voice, lease)))
        {
            var state = pair.Value; await state.Gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (state.Closed || state.Current is not { } sub || !ReferenceEquals(sub.Voice, lease) || (!lease.Active && payload is not ErrorOutput) || lease.Retiring) continue;
                await access.AuthorizeRecipientAsync(sub.Tenant, sub.Session, sub.Human, ct).ConfigureAwait(false);
                if (state.Closed || state.Current != sub || (!lease.Active && payload is not ErrorOutput) || lease.Retiring) continue;
                await hubs.Clients.Client(pair.Key).SendAsync("SessionEvent", SessionEventMapper.Map(new(Context(sub.Session), null, payload), sub.Attachment, ++sub.Sequence), ct).ConfigureAwait(false);
            }
            finally { state.Gate.Release(); }
        }
    }
    private async Task RetireVoiceIfAccessLostAsync(Subscription? sub, CancellationToken ct)
    {
        if (sub?.Voice is null) return;
        try { await access.AuthorizeRecipientAsync(sub.Tenant, sub.Session, sub.Human, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is AccessDeniedException or SecurityConflictException) { RetireVoice(sub); }
    }
    private void RetireVoice(Subscription subscription)
    {
        if (subscription.StopVoice is { } stop) _ = ObserveVoiceCloseAsync(stop());
    }
    private async Task ObserveVoiceCloseAsync(Task cleanup)
    {
        try { await cleanup.ConfigureAwait(false); }
        catch (Exception ex) { AgentCore.Application.Observability.DiagnosticLog.Warning(logger, ex, ids.NewId(), "Tenant attachment voice cleanup failed."); }
    }
    public async Task DetachAsync(string connection)
    {
        if (_connections.TryRemove(connection, out var state))
        {
            state.Closed = true;
            if (state.Current?.StopVoice is { } close) await close().ConfigureAwait(false);
        }
    }
    private async ValueTask PublishAudioAsync(Source source, ResponseAudio audio, CancellationToken ct)
    {
        if (audio.SessionId != source.Run.SessionId || audio.ResponseId != source.Run.ResponseId) return;
        await source.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var current = await runs.FindTenantRunAsync(source.Run.Owner.TenantId!.Value, source.Run.AgentRunId, ct).ConfigureAwait(false);
            if (_sources.GetValueOrDefault((source.Run.Owner.TenantId.Value, source.Run.SessionId)) != source
                || current is not { Status: AgentRunStatus.Running, CancellationRequested: false, Claim: { } claim }
                || claim.Generation != source.Run.Claim!.Generation || claim.LeaseExpiresAtUtc <= time.GetUtcNow()) return;
            foreach (var pair in _connections.ToArray().Where(p => p.Value.Current is { } s && s.Tenant == source.Run.Owner.TenantId && s.Session == source.Run.SessionId && ReferenceEquals(s.Voice, source.Voice) && s.Voice?.Active == true))
            {
                var state = pair.Value; using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5), time);
                using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
                await state.Gate.WaitAsync(bounded.Token).ConfigureAwait(false);
                try
                {
                    if (state.Closed || state.Current is not { Voice: { Active: true } lease } sub) continue;
                    var recipient = await access.AuthorizeRecipientAsync(sub.Tenant, sub.Session, sub.Human, bounded.Token).ConfigureAwait(false);
                    if (recipient.Owner != lease.Owner || state.Closed || state.Current != sub || !lease.Active || recipient.Mode != SessionMode.Voice) continue;
                    await hubs.Clients.Client(pair.Key).SendAsync("AudioOutput", new OutputAudioDto { ProtocolVersion = 1, SessionId = sub.Session.ToString("D"),
                        AttachmentId = sub.Attachment.ToString("D"), ResponseId = audio.ResponseId.ToString("D"), FrameSequence = audio.FrameSequence,
                        SampleOffset = audio.SampleOffset, IsFinal = audio.IsFinal, Data = audio.Data.ToArray() }, bounded.Token).ConfigureAwait(false);
                    if (sub.Delivery.TryGetValue(audio.ResponseId, out var prior)) sub.Delivery[audio.ResponseId] = prior with {
                        Samples = Math.Max(prior.Samples, audio.SampleOffset + PcmCodec.SampleCount(audio.Data)) };
                }
                catch (Exception ex) when (ex is AccessDeniedException or SecurityConflictException or OperationCanceledException)
                { if (state.Current is { } failed) RetireVoice(failed); state.Current = null; }
                finally { state.Gate.Release(); }
            }
        }
        finally { source.Gate.Release(); }
    }
}
