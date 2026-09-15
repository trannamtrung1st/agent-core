using System.Collections.Concurrent;
using System.Globalization;
using AgentCore.Api.Mapping;
using AgentCore.Application.Events;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Contracts.Realtime;
using AgentCore.Domain.Conversation;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace AgentCore.Api.Realtime;

public sealed class AgentCoreOptions
{
    public string Profile { get; set; } = "Synthetic";
    public string AgentDirectory { get; set; } = "agents";
    public int MaxActiveSessions { get; set; } = 10;
    public int MaxEntriesPerSession { get; set; } = 1000;
    public int PendingVoiceTimeoutMs { get; set; } = 30_000;
}

public sealed partial class SessionHost : ISessionOutput, ISessionAudioOutput, IEnvironmentEventIngress
{
    private readonly SessionManager _sessions;
    private readonly SessionRuntimeFactory _factory;
    private readonly IHubContext<SessionHub> _hubs;
    private readonly TimeProvider _time;
    private readonly AgentCoreOptions _options;
    private readonly ConcurrentDictionary<Guid, Live> _live = new();
    private readonly ConcurrentDictionary<string, Guid> _connections = new();
    private readonly object _gate = new();
    private bool _admitting = true;

    internal Func<Task>? AfterAdmitHold { get; set; }

    public SessionHost(
        SessionManager sessions,
        SessionRuntimeFactory factory,
        IHubContext<SessionHub> hubs,
        TimeProvider time,
        IOptions<AgentCoreOptions> options)
    {
        _sessions = sessions;
        _factory = factory;
        _hubs = hubs;
        _time = time;
        _options = options.Value;
    }

    public Guid? ActiveResponseId(Guid sessionId) =>
        _live.TryGetValue(sessionId, out var live) ? live.Runtime.ActiveResponseId : null;

    public SessionSnapshot? LiveSnapshot(Guid sessionId) =>
        _live.TryGetValue(sessionId, out var live) ? live.Runtime.Snapshot : null;

    public bool Admitting => _admitting;

    public async ValueTask PublishAsync(Guid sessionId, EnvironmentEvent input, CancellationToken cancellationToken = default)
    {
        if (!_live.TryGetValue(sessionId, out var live))
        {
            return;
        }

        await live.Runtime.SubmitEnvironmentAsync(input, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CommandAck> AttachAsync(
        string connectionId,
        ClientCommand<AttachPayload> command,
        CancellationToken cancellationToken)
    {
        var ack = Validate(command, attach: true, expectedType: "session.attach");
        if (!ack.Accepted)
        {
            return ack;
        }

        if (!string.IsNullOrEmpty(command.AttachmentId))
        {
            return Reject(command.EventId, "Protocol", "ProtocolError", "Attach must omit attachmentId.", true, null);
        }

        if (!_admitting)
        {
            return Reject(command.EventId, "Session", "ServiceUnavailable", "The host is shutting down.", false, 1000);
        }

        var sessionId = Guid.Parse(command.SessionId);
        var fingerprint = Fingerprint(command);
        try
        {
            var snapshot = await _sessions.GetAsync(sessionId, cancellationToken).ConfigureAwait(false);
            if (snapshot.Status == SessionStatus.Ended)
            {
                return Reject(command.EventId, "Session", "NotFound", "Session has ended.", true, null);
            }

            while (true)
            {
                Live live;
                var created = false;
                var replay = false;
                CommandAck? replayAck = null;
                lock (_gate)
                {
                    if (_connections.TryGetValue(connectionId, out var owned) && owned != sessionId)
                    {
                        return Reject(
                            command.EventId,
                            "Session",
                            "SessionInUse",
                            "Connection is already attached to another session.",
                            false,
                            null);
                    }

                    if (_live.TryGetValue(sessionId, out var existing))
                    {
                        live = existing;
                    }
                    else
                    {
                        if (_live.Count >= Math.Max(1, _options.MaxActiveSessions))
                        {
                            return Reject(
                                command.EventId,
                                "Session",
                                "SessionCapacityExceeded",
                                "Maximum active sessions reached.",
                                false,
                                5000);
                        }

                        live = new Live(_factory.Create(snapshot, this));
                        live.ConnectionId = connectionId;
                        live.LastAttachEventId = command.EventId;
                        live.LastAttachFingerprint = fingerprint;
                        _live[sessionId] = live;
                        _connections[connectionId] = sessionId;
                        created = true;
                    }
                }

                if (!created)
                {
                    await live.Admission.WaitAsync(cancellationToken).ConfigureAwait(false);
                    var detached = false;
                    try
                    {
                        if (!_live.TryGetValue(sessionId, out var current) || !ReferenceEquals(current, live))
                        {
                            detached = true;
                        }
                        else if (live.ConnectionId == connectionId
                            && live.LastAttachEventId == command.EventId)
                        {
                            if (live.LastAttachFingerprint != fingerprint)
                            {
                                return Reject(
                                    command.EventId,
                                    "Protocol",
                                    "ProtocolError",
                                    "Repeated eventId with a different payload.",
                                    true,
                                    null);
                            }

                            replay = true;
                            replayAck = live.LastAttachAck ?? Accept(command.EventId);
                        }
                        else if (live.ConnectionId is not null)
                        {
                            return Reject(command.EventId, "Session", "SessionInUse", "Session is attached to another connection.", false, null);
                        }
                        else
                        {
                            live.ConnectionId = connectionId;
                            live.LastAttachEventId = command.EventId;
                            live.LastAttachFingerprint = fingerprint;
                            live.AttachmentId = Guid.NewGuid();
                            live.ResetControl();
                            lock (_gate)
                            {
                                _connections[connectionId] = sessionId;
                            }
                        }
                    }
                    finally
                    {
                        live.Admission.Release();
                    }

                    if (detached)
                    {
                        continue;
                    }
                }

                if (replay)
                {
                    return replayAck!;
                }

                await live.Runtime.AttachAsync(cancellationToken).ConfigureAwait(false);
                await live.Runtime.WaitUntilMailboxDrainedAsync(cancellationToken).ConfigureAwait(false);
                var accepted = Accept(command.EventId);
                await live.Admission.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    if (_live.TryGetValue(sessionId, out var still) && ReferenceEquals(still, live))
                    {
                        live.LastAttachAck = accepted;
                    }
                }
                finally
                {
                    live.Admission.Release();
                }

                return accepted;
            }
        }
        catch (AgentCoreException ex)
        {
            return Reject(command.EventId, "Session", ex.Code, ex.Message, ex.Fatal, ex.RetryAfterMs);
        }
    }

    public async Task DetachAsync(string connectionId)
    {
        if (!_connections.TryRemove(connectionId, out var sessionId))
        {
            return;
        }

        Live? live = null;
        lock (_gate)
        {
            if (_live.TryGetValue(sessionId, out var found) && found.ConnectionId == connectionId)
            {
                live = found;
                _live.TryRemove(sessionId, out _);
            }
        }

        if (live is null)
        {
            return;
        }

        await live.Runtime.DetachAsync().ConfigureAwait(false);
        await live.Runtime.WaitUntilMailboxDrainedAsync().ConfigureAwait(false);
        await live.Runtime.DisposeAsync().ConfigureAwait(false);
    }

    public async Task TerminateAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        if (_live.TryGetValue(sessionId, out var live) && live.ConnectionId is { } connectionId)
        {
            var ended = await live.Runtime.RequestEndAsync(cancellationToken).ConfigureAwait(false);
            await live.Runtime.WaitUntilMailboxDrainedAsync(cancellationToken).ConfigureAwait(false);
            if (!ended)
            {
                throw AgentCoreErrors.Persistence("Failed to persist session end.");
            }

            await DetachAsync(connectionId).ConfigureAwait(false);
        }

        await _sessions.EndAsync(sessionId, cancellationToken).ConfigureAwait(false);
    }

    public async Task DrainAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _admitting = false;
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(5));
        foreach (var live in _live.Values.ToArray())
        {
            try
            {
                if (live.Runtime.ActiveResponseId is not null)
                {
                    await live.Runtime.CancelActiveResponseAsync(budget.Token).ConfigureAwait(false);
                }

                await live.Runtime.DetachAsync().ConfigureAwait(false);
                await live.Runtime.WaitUntilMailboxDrainedAsync(budget.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (AgentCoreException)
            {
            }
        }
    }

    public async Task<CommandAck> SendTextAsync(
        string connectionId,
        ClientCommand<UserTextPayload> command,
        CancellationToken cancellationToken)
    {
        var envelope = Validate(command, attach: false, expectedType: "user.text");
        if (!envelope.Accepted)
        {
            return envelope;
        }

        var text = command.Payload?.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            return Reject(command.EventId, "Validation", "ValidationError", "text is required.", false, null);
        }

        if (text.Length > 8000)
        {
            return Reject(command.EventId, "Validation", "ValidationError", "Text exceeds 8000 UTF-16 code units.", false, null);
        }

        return await AdmitControlAsync(connectionId, command, async live =>
        {
            if (!Guid.TryParse(command.EventId, out var sourceEventId))
            {
                return Reject(command.EventId, "Protocol", "ProtocolError", "IDs must be UUIDs.", true, null);
            }

            try
            {
                if (!await live.Runtime.SubmitUserTextAsync(text, sourceEventId, cancellationToken).ConfigureAwait(false))
                {
                    return Reject(
                        command.EventId,
                        "Transport",
                        "Backpressure",
                        "The session mailbox is full. Stop or retry after the current work drains.",
                        false,
                        1000);
                }

                return Accept(command.EventId);
            }
            catch (AgentCoreException ex)
            {
                return Reject(command.EventId, "Validation", ex.Code, ex.Message, ex.Fatal, ex.RetryAfterMs);
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CommandAck> SetModeAsync(
        string connectionId,
        ClientCommand<SetModePayload> command,
        CancellationToken cancellationToken)
    {
        var envelope = Validate(command, attach: false, expectedType: "session.mode.set");
        if (!envelope.Accepted)
        {
            return envelope;
        }

        var raw = command.Payload?.Mode;
        if (raw is not ("text" or "voice"))
        {
            return Reject(command.EventId, "Validation", "ValidationError", "mode must be text or voice.", false, null);
        }

        return await AdmitControlAsync(connectionId, command, async live =>
        {
            var mode = raw == "voice" ? SessionMode.Voice : SessionMode.Text;
            await live.Runtime.SetModeAsync(mode, cancellationToken).ConfigureAwait(false);
            return Accept(command.EventId);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CommandAck> EndAsync(
        string connectionId,
        ClientCommand<EndPayload> command,
        CancellationToken cancellationToken)
    {
        var envelope = Validate(command, attach: false, expectedType: "session.end");
        if (!envelope.Accepted)
        {
            return envelope;
        }

        if (string.IsNullOrWhiteSpace(command.Payload?.Reason)
            || !string.Equals(command.Payload.Reason, "userEnded", StringComparison.Ordinal))
        {
            return Reject(command.EventId, "Validation", "ValidationError", "reason must be userEnded.", false, null);
        }

        if (!Guid.TryParse(command.SessionId, out var sessionId))
        {
            return Reject(command.EventId, "Protocol", "ProtocolError", "sessionId is invalid.", true, null);
        }

        return await AdmitControlAsync(connectionId, command, async live =>
        {
            _ = live;
            try
            {
                await TerminateAsync(sessionId, cancellationToken).ConfigureAwait(false);
                return Accept(command.EventId);
            }
            catch (AgentCoreException ex) when (ex.Code == "SessionPersistenceUnavailable")
            {
                return Reject(command.EventId, "Session", ex.Code, ex.Message, false, ex.RetryAfterMs ?? 1000);
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    public Task<CommandAck> AdmitSpeechStartedAsync(string connectionId, ClientCommand<SpeechStartedPayload> command)
    {
        var envelope = Validate(command, attach: false, expectedType: "user.speech.started");
        if (!envelope.Accepted)
        {
            return Task.FromResult(envelope);
        }

        var payload = command.Payload;
        if (payload is null
            || !Guid.TryParse(payload.UtteranceId, out var utteranceId)
            || !Guid.TryParse(payload.StreamId, out _)
            || payload.SampleOffset < 0
            || payload.ActivityScore is < 0 or > 1)
        {
            return Task.FromResult(Reject(command.EventId, "Validation", "ValidationError", "Speech started payload is invalid.", false, null));
        }

        return AdmitSpeechAsync(connectionId, command, utteranceId, payload.StreamId, payload.SampleOffset, payload.ActivityScore, SpeechBoundary.Started, durationMs: 0);
    }

    public Task<CommandAck> AdmitSpeechEndedAsync(string connectionId, ClientCommand<SpeechEndedPayload> command)
    {
        var envelope = Validate(command, attach: false, expectedType: "user.speech.ended");
        if (!envelope.Accepted)
        {
            return Task.FromResult(envelope);
        }

        var payload = command.Payload;
        if (payload is null
            || !Guid.TryParse(payload.UtteranceId, out var utteranceId)
            || !Guid.TryParse(payload.StreamId, out _)
            || payload.SampleOffset < 0
            || payload.DurationMs < 0
            || payload.ActivityScore is < 0 or > 1)
        {
            return Task.FromResult(Reject(command.EventId, "Validation", "ValidationError", "Speech ended payload is invalid.", false, null));
        }

        return AdmitSpeechAsync(connectionId, command, utteranceId, payload.StreamId, payload.SampleOffset, payload.ActivityScore, SpeechBoundary.Ended, payload.DurationMs);
    }

    public async Task<bool> AdmitAudioAsync(string connectionId, InputAudioDto dto)
    {
        if (dto.ProtocolVersion != 1)
        {
            return true;
        }

        if (!_connections.TryGetValue(connectionId, out var sessionId) || !_live.TryGetValue(sessionId, out var live))
        {
            return false;
        }

        if (!Guid.TryParse(dto.SessionId, out var audioSession) || audioSession != sessionId)
        {
            await PublishAudioProtocolErrorAsync(sessionId, "Audio sessionId does not match the attached session.").ConfigureAwait(false);
            return true;
        }

        if (live.Runtime.Snapshot.Mode != SessionMode.Voice
            || !string.Equals(dto.AttachmentId, live.AttachmentId.ToString(), StringComparison.OrdinalIgnoreCase)
            || live.Runtime.StreamId is not { } streamId
            || !string.Equals(dto.StreamId, streamId.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            await PublishAudioProtocolErrorAsync(sessionId, "Do not send PCM until Mode is voice.").ConfigureAwait(false);
            return true;
        }

        if (dto.Data is null
            || dto.Data.Length == 0
            || dto.Data.Length % 2 != 0
            || dto.Data.Length > CanonicalAudio.MaxFrameBytes)
        {
            await PublishAudioProtocolErrorAsync(sessionId, "PCM frame must be 2..1920 even bytes.").ConfigureAwait(false);
            return true;
        }

        var frame = new AudioFrame(dto.FrameSequence, dto.SampleOffset, dto.Data);
        _ = live.Runtime.TryAdmitAudio(frame);
        return false;
    }

    public async Task<CommandAck> AcceptPlaybackAsync(
        string connectionId,
        ClientCommand<PlaybackPayload> command,
        string expectedType)
    {
        var envelope = Validate(command, attach: false, expectedType);
        if (!envelope.Accepted)
        {
            return envelope;
        }

        if (!Guid.TryParse(command.ResponseId, out var responseId))
        {
            return Reject(command.EventId, "Validation", "ValidationError", "responseId is required.", false, null);
        }

        var payload = command.Payload;
        if (payload is null || payload.ConsumedSamples < 0 || payload.TextEndExclusive < 0)
        {
            return Reject(command.EventId, "Validation", "ValidationError", "Playback payload is invalid.", false, null);
        }

        if (string.Equals(expectedType, "playback.started", StringComparison.Ordinal) && payload.ConsumedSamples != 0)
        {
            return Reject(command.EventId, "Validation", "ValidationError", "playback.started requires consumedSamples=0.", false, null);
        }

        return await AdmitControlAsync(connectionId, command, async live =>
        {
            var kind = expectedType switch
            {
                "playback.started" => "started",
                "playback.progress" => "progress",
                "playback.completed" => "completed",
                "playback.stopped" => "stopped",
                _ => "progress"
            };
            var admitted = await live.Runtime.SubmitPlaybackAsync(responseId, kind, payload.ConsumedSamples, payload.TextEndExclusive)
                .ConfigureAwait(false);
            if (admitted is null)
            {
                return Reject(command.EventId, "Transport", "Backpressure", "The session mailbox is full.", false, 1000);
            }

            if (!admitted.Value)
            {
                return Reject(
                    command.EventId,
                    "Validation",
                    "ValidationError",
                    "Playback offset is not monotonic, exceeds sent samples, or playback.started is not at 0.",
                    false,
                    null);
            }

            return Accept(command.EventId);
        }).ConfigureAwait(false);
    }

    public async Task<CommandAck> AcceptReceiptAsync(string connectionId, ClientCommand<ResponseReceiptPayload> command)
    {
        var envelope = Validate(command, attach: false, expectedType: "response.received");
        if (!envelope.Accepted)
        {
            return envelope;
        }

        if (!Guid.TryParse(command.ResponseId, out var responseId))
        {
            return Reject(command.EventId, "Validation", "ValidationError", "responseId is required.", false, null);
        }

        var payload = command.Payload;
        if (payload is null || payload.TextEndExclusive < 0)
        {
            return Reject(command.EventId, "Validation", "ValidationError", "textEndExclusive is required.", false, null);
        }

        return await AdmitControlAsync(connectionId, command, async live =>
        {
            var admitted = await live.Runtime.SubmitReceiptAsync(responseId, payload.TextEndExclusive).ConfigureAwait(false);
            if (admitted is null)
            {
                return Reject(command.EventId, "Transport", "Backpressure", "The session mailbox is full.", false, 1000);
            }

            if (!admitted.Value)
            {
                return Reject(command.EventId, "Validation", "ValidationError", "Receipt offset is not monotonic or exceeds emitted text.", false, null);
            }

            return Accept(command.EventId);
        }).ConfigureAwait(false);
    }

    public async Task<CommandAck> AcceptMuteAsync(string connectionId, ClientCommand<MutePayload> command)
    {
        var envelope = Validate(command, attach: false, expectedType: "session.mute");
        if (!envelope.Accepted)
        {
            return envelope;
        }

        if (command.Payload is null)
        {
            return Reject(command.EventId, "Validation", "ValidationError", "muted is required.", false, null);
        }

        return await AdmitControlAsync(connectionId, command, async live =>
        {
            await live.Runtime.SetMutedAsync(command.Payload.Muted).ConfigureAwait(false);
            return Accept(command.EventId);
        }).ConfigureAwait(false);
    }

    public ValueTask PublishAsync(ResponseAudio audio, CancellationToken cancellationToken = default)
    {
        if (!_live.TryGetValue(audio.SessionId, out var live) || live.ConnectionId is null)
        {
            return ValueTask.CompletedTask;
        }

        var dto = new OutputAudioDto
        {
            ProtocolVersion = 1,
            SessionId = audio.SessionId.ToString(),
            AttachmentId = live.AttachmentId.ToString(),
            ResponseId = audio.ResponseId.ToString(),
            FrameSequence = audio.FrameSequence,
            SampleOffset = audio.SampleOffset,
            IsFinal = audio.IsFinal,
            Data = audio.Data.ToArray()
        };
        return new ValueTask(_hubs.Clients.Client(live.ConnectionId).SendAsync("AudioOutput", dto, cancellationToken));
    }

    public ValueTask PublishAsync(SessionOutput output, CancellationToken cancellationToken = default)
    {
        if (!_live.TryGetValue(output.Context.SessionId, out var live) || live.ConnectionId is null)
        {
            return ValueTask.CompletedTask;
        }

        if (output.Payload is AudioFrameOutput audio)
        {
            return PublishAsync(
                new ResponseAudio(
                    output.Context.SessionId,
                    output.ResponseId ?? Guid.Empty,
                    audio.FrameSequence,
                    audio.SampleOffset,
                    audio.IsFinal,
                    audio.Data),
                cancellationToken);
        }

        var evt = SessionEventMapper.Map(output, live.AttachmentId, live.NextSequence());
        return new ValueTask(_hubs.Clients.Client(live.ConnectionId).SendAsync("SessionEvent", evt, cancellationToken));
    }

    private Task<CommandAck> AdmitSpeechAsync(
        string connectionId,
        IRealtimeCommand command,
        Guid utteranceId,
        string streamId,
        long sampleOffset,
        double? activityScore,
        SpeechBoundary boundary,
        double durationMs)
    {
        return AdmitControlAsync(connectionId, command, live =>
        {
            if (live.Runtime.Snapshot.Mode != SessionMode.Voice)
            {
                return Task.FromResult(Reject(command.EventId, "Protocol", "ProtocolError", "Speech is not admitted until voice mode is applied.", true, null));
            }

            if (live.Runtime.StreamId is { } expected
                && !string.Equals(streamId, expected.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(Reject(command.EventId, "Transport", "AudioDiscontinuity", "Speech streamId does not match.", false, null));
            }

            if (!live.Runtime.TryAdmitBoundary(utteranceId, boundary, activityScore, sampleOffset, durationMs))
            {
                return Task.FromResult(Reject(command.EventId, "Transport", "AudioDiscontinuity", "Speech boundary was dropped.", false, null));
            }

            return Task.FromResult(Accept(command.EventId));
        });
    }

    private async Task PublishAudioProtocolErrorAsync(Guid sessionId, string message)
    {
        await PublishAsync(
                new SessionOutput(
                    new EventContext(
                        Guid.NewGuid(),
                        sessionId,
                        Guid.Empty,
                        _time.GetUtcNow(),
                        Guid.NewGuid(),
                        null),
                    null,
                    new ErrorOutput("Protocol", "ProtocolError", message, true, null)),
                CancellationToken.None)
            .ConfigureAwait(false);
    }

    private static CommandAck Validate(IRealtimeCommand command, bool attach, string expectedType)
    {
        if (command.CorrelationId is not null || command.CausationId is not null)
        {
            return Reject(command.EventId, "Protocol", "ProtocolError", "Client must not set correlationId or causationId.", true, null);
        }

        if (command.ProtocolVersion != 1)
        {
            var mismatch = Reject(command.EventId, "Protocol", "ProtocolVersionMismatch", "Unsupported protocol version.", true, null);
            mismatch.Error!.Extensions = new Dictionary<string, object?> { ["supportedVersions"] = new[] { 1 } };
            return mismatch;
        }

        if (!string.Equals(command.Type, expectedType, StringComparison.Ordinal))
        {
            return Reject(command.EventId, "Protocol", "ProtocolError", "Command type does not match the hub method.", true, null);
        }

        if (!Guid.TryParse(command.SessionId, out _) || !Guid.TryParse(command.EventId, out _))
        {
            return Reject(command.EventId, "Protocol", "ProtocolError", "IDs must be UUIDs.", true, null);
        }

        if (attach && command.Sequence != 0)
        {
            return Reject(command.EventId, "Protocol", "ProtocolError", "Attach sequence must be 0.", true, null);
        }

        if (!attach && command.Sequence < 1)
        {
            return Reject(command.EventId, "Protocol", "StaleCommand", "Control sequence must increase from 1.", false, null);
        }

        return Accept(command.EventId);
    }

    private async Task<CommandAck> AdmitControlAsync(
        string connectionId,
        IRealtimeCommand command,
        Func<Live, Task<CommandAck>> action,
        CancellationToken cancellationToken = default)
    {
        var admission = await TryAdmitAsync(connectionId, command, cancellationToken).ConfigureAwait(false);
        if (!admission.Ok)
        {
            return admission.Reject!;
        }

        var live = admission.Live;
        if (AfterAdmitHold is not null)
        {
            await AfterAdmitHold().ConfigureAwait(false);
        }

        try
        {
            var ack = await action(live).ConfigureAwait(false);
            await FinishAdmitAsync(live, command, ack, cancellationToken).ConfigureAwait(false);
            return ack;
        }
        catch (Exception)
        {
            await FinishAdmitAsync(
                    live,
                    command,
                    Reject(command.EventId, "Session", "Unavailable", "Command failed.", false, 1000),
                    cancellationToken)
                .ConfigureAwait(false);
            throw;
        }
    }

    private async Task<(bool Ok, Live Live, CommandAck? Reject)> TryAdmitAsync(
        string connectionId,
        IRealtimeCommand command,
        CancellationToken cancellationToken)
    {
        if (!_connections.TryGetValue(connectionId, out var sessionId)
            || !_live.TryGetValue(sessionId, out var found)
            || !string.Equals(command.SessionId, sessionId.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            return (false, null!, Reject(command.EventId, "Session", "NotFound", "Session is not attached.", false, null));
        }

        Task<CommandAck>? inflight = null;
        await found.Admission.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_connections.TryGetValue(connectionId, out var attached)
                || attached != sessionId
                || !_live.TryGetValue(sessionId, out var current)
                || !ReferenceEquals(current, found))
            {
                return (false, found, Reject(command.EventId, "Session", "NotFound", "Session is not attached.", false, null));
            }

            if (string.IsNullOrWhiteSpace(command.AttachmentId)
                || !string.Equals(command.AttachmentId, found.AttachmentId.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                return (false, found, Reject(command.EventId, "Session", "StaleCommand", "Attachment lease is invalid.", false, null));
            }

            var fingerprint = Fingerprint(command);
            if (found.Dedupe.TryGetValue(command.EventId, out var prior))
            {
                if (!string.Equals(prior.Fingerprint, fingerprint, StringComparison.Ordinal))
                {
                    return (false, found, Reject(
                        command.EventId,
                        "Protocol",
                        "ProtocolError",
                        "Repeated eventId with a different payload.",
                        true,
                        null));
                }

                return (false, found, prior.Ack);
            }

            if (found.InFlight.TryGetValue(command.EventId, out var pending))
            {
                if (!string.Equals(pending.Fingerprint, fingerprint, StringComparison.Ordinal))
                {
                    return (false, found, Reject(
                        command.EventId,
                        "Protocol",
                        "ProtocolError",
                        "Repeated eventId with a different payload.",
                        true,
                        null));
                }

                inflight = pending.Ack.Task;
            }
            else if (command.Sequence <= found.LastSequence)
            {
                return (false, found, Reject(command.EventId, "Session", "StaleCommand", "Backwards command sequence.", false, null));
            }
            else
            {
                found.LastSequence = command.Sequence;
                found.LastEventId = command.EventId;
                found.InFlight[command.EventId] = new InFlightAdmit(
                    fingerprint,
                    new TaskCompletionSource<CommandAck>(TaskCreationOptions.RunContinuationsAsynchronously));
                return (true, found, null);
            }
        }
        finally
        {
            found.Admission.Release();
        }

        if (inflight is not null)
        {
            return (false, found, await inflight.WaitAsync(cancellationToken).ConfigureAwait(false));
        }

        return (false, found, Reject(command.EventId, "Session", "NotFound", "Session is not attached.", false, null));
    }

    private static async Task FinishAdmitAsync(
        Live live,
        IRealtimeCommand command,
        CommandAck ack,
        CancellationToken cancellationToken)
    {
        await live.Admission.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (live.InFlight.Remove(command.EventId, out var pending))
            {
                live.Remember(command.EventId, pending.Fingerprint, ack);
                pending.Ack.TrySetResult(ack);
            }
            else
            {
                live.Remember(command.EventId, Fingerprint(command), ack);
            }
        }
        finally
        {
            live.Admission.Release();
        }
    }

    private static string Fingerprint(IRealtimeCommand command)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(command.Payload);
        return $"{command.Type}|{command.ResponseId}|{json}";
    }

    private static CommandAck Accept(string eventId) => new() { EventId = eventId, Accepted = true };

    private static CommandAck Reject(string eventId, string category, string code, string message, bool fatal, int? retry) =>
        new()
        {
            EventId = eventId,
            Accepted = false,
            Error = new CommandError
            {
                Category = category,
                Code = code,
                Message = message,
                Fatal = fatal,
                RetryAfterMs = retry
            }
        };

    private sealed class Live(SessionRuntime runtime)
    {
        public SessionRuntime Runtime { get; } = runtime;
        public string? ConnectionId { get; set; }
        public Guid AttachmentId { get; set; } = Guid.NewGuid();
        public string? LastAttachEventId { get; set; }
        public string? LastAttachFingerprint { get; set; }
        public CommandAck? LastAttachAck { get; set; }
        public long LastSequence { get; set; }
        public string? LastEventId { get; set; }
        public SemaphoreSlim Admission { get; } = new(1, 1);
        public Dictionary<string, InFlightAdmit> InFlight { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, DedupeRecord> Dedupe { get; } = new(StringComparer.OrdinalIgnoreCase);
        private readonly Queue<string> _dedupeOrder = new();
        private long _serverSequence;

        public long NextSequence() => Interlocked.Increment(ref _serverSequence);

        public void Remember(string eventId, string fingerprint, CommandAck ack)
        {
            Dedupe[eventId] = new DedupeRecord(fingerprint, ack);
            _dedupeOrder.Enqueue(eventId);
            while (_dedupeOrder.Count > 1024)
            {
                var oldest = _dedupeOrder.Dequeue();
                if (oldest != eventId)
                {
                    Dedupe.Remove(oldest);
                }
            }
        }

        public void ResetControl()
        {
            LastSequence = 0;
            LastEventId = null;
            foreach (var pending in InFlight.Values)
            {
                pending.Ack.TrySetResult(Reject("", "Session", "StaleCommand", "Attachment was replaced.", false, null));
            }

            InFlight.Clear();
            Dedupe.Clear();
            _dedupeOrder.Clear();
            _serverSequence = 0;
        }
    }

    public readonly record struct DedupeRecord(string Fingerprint, CommandAck Ack);

    public readonly record struct InFlightAdmit(string Fingerprint, TaskCompletionSource<CommandAck> Ack);
}

public static class SessionEventMapper
{
    public static ServerEvent Map(SessionOutput output, Guid attachmentId, long sequence)
    {
        var (type, payload) = output.Payload switch
        {
            ReadyOutput ready => ("session.ready", Ready(ready.Ready)),
            ResponseStartedOutput started => ("agent.response.started", new Dictionary<string, object?>
            {
                ["entryId"] = started.EntryId.ToString(),
                ["entrySequence"] = started.EntrySequence,
                ["trigger"] = ToTrigger(started.Trigger)
            }),
            TextDeltaOutput delta => ("agent.text.delta", new Dictionary<string, object?>
            {
                ["text"] = delta.Text,
                ["textStart"] = delta.TextStart
            }),
            TextCompletedOutput completed => ("agent.text.completed", new Dictionary<string, object?>
            {
                ["textLength"] = completed.TextLength
            }),
            ResponseCompletedOutput terminal when terminal.InterruptReason is { } reason =>
                ("agent.response.interrupted", new Dictionary<string, object?>
                {
                    ["reason"] = reason,
                    ["heardTextEndExclusive"] = terminal.HeardTextEndExclusive
                }),
            ResponseCompletedOutput terminal => ("agent.response.completed", new Dictionary<string, object?>
            {
                ["status"] = terminal.Failed ? "failed" : "completed",
                ["heardTextEndExclusive"] = terminal.HeardTextEndExclusive
            }),
            PlaybackStopOutput stop => ("playback.stop", new Dictionary<string, object?>
            {
                ["reason"] = stop.Reason
            }),
            PlaybackGainOutput gain => ("playback.gain", new Dictionary<string, object?>
            {
                ["gain"] = gain.Gain,
                ["rampMs"] = gain.RampMs,
                ["candidateId"] = gain.CandidateId?.ToString()
            }),
            StateChangedOutput state => ("session.state.changed", new Dictionary<string, object?>
            {
                ["status"] = HttpMapping.ToStatus(state.Status),
                ["mode"] = HttpMapping.ToMode(state.Mode),
                ["pendingMode"] = state.PendingMode is { } pending ? HttpMapping.ToMode(pending) : null,
                ["inputState"] = ToInput(state.InputState),
                ["outputState"] = ToOutput(state.OutputState),
                ["muted"] = state.Muted,
                ["streamId"] = state.StreamId?.ToString()
            }),
            TranscriptPartialOutput partial => ("transcript.partial", new Dictionary<string, object?>
            {
                ["utteranceId"] = partial.UtteranceId.ToString(),
                ["revision"] = partial.Revision,
                ["text"] = partial.Text
            }),
            TranscriptFinalOutput final => ("transcript.final", new Dictionary<string, object?>
            {
                ["utteranceId"] = final.UtteranceId.ToString(),
                ["text"] = final.Text,
                ["entryId"] = final.EntryId?.ToString(),
                ["entrySequence"] = final.EntrySequence
            }),
            ErrorOutput error => ("error", new Dictionary<string, object?>
            {
                ["category"] = error.Category,
                ["code"] = error.Code,
                ["message"] = error.SafeMessage,
                ["fatal"] = error.Fatal,
                ["retryAfterMs"] = error.RetryAfter is { } retry ? (int)retry.TotalMilliseconds : null
            }),
            _ => ("error", new Dictionary<string, object?>
            {
                ["category"] = "Session",
                ["code"] = "Unknown",
                ["message"] = "Unsupported event.",
                ["fatal"] = false,
                ["retryAfterMs"] = null
            })
        };

        return new ServerEvent
        {
            ProtocolVersion = 1,
            SessionId = output.Context.SessionId.ToString(),
            AttachmentId = attachmentId.ToString(),
            EventId = output.Context.EventId.ToString(),
            Sequence = sequence,
            Timestamp = output.Context.Timestamp.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
            CorrelationId = output.Context.CorrelationId.ToString(),
            CausationId = output.Context.CausationId?.ToString(),
            ResponseId = output.ResponseId?.ToString(),
            Type = type,
            Payload = payload
        };
    }

    private static Dictionary<string, object?> Ready(SessionReadyProjection ready)
    {
        var history = ready.History.Select(entry => (object)new Dictionary<string, object?>
        {
            ["entryId"] = entry.EntryId.ToString(),
            ["sequence"] = entry.Sequence,
            ["sourceEventId"] = entry.SourceEventId?.ToString(),
            ["role"] = entry.Role == ConversationRole.User ? "user" : "assistant",
            ["text"] = entry.Text,
            ["responseId"] = entry.ResponseId?.ToString(),
            ["status"] = HttpMapping.ToEntryStatus(entry.Status),
            ["deliveryMode"] = HttpMapping.ToMode(entry.DeliveryMode),
            ["heardTextEndExclusive"] = entry.HeardTextEndExclusive,
            ["receivedTextEndExclusive"] = entry.ReceivedTextEndExclusive,
            ["createdAt"] = entry.CreatedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)
        }).ToArray();

        return new Dictionary<string, object?>
        {
            ["mode"] = HttpMapping.ToMode(ready.Mode),
            ["pendingMode"] = ready.PendingMode is { } pending ? HttpMapping.ToMode(pending) : null,
            ["status"] = HttpMapping.ToStatus(ready.Status),
            ["agent"] = new Dictionary<string, object?>
            {
                ["id"] = ready.Agent.Id,
                ["version"] = ready.Agent.Version,
                ["name"] = ready.Agent.Name,
                ["role"] = ready.Agent.Role,
                ["description"] = ready.Agent.Description,
                ["voiceAvailable"] = ready.Agent.VoiceAvailable
            },
            ["streamId"] = ready.StreamId?.ToString(),
            ["audioFormat"] = ready.AudioFormat is { } format
                ? new Dictionary<string, object?>
                {
                    ["encoding"] = format.Encoding,
                    ["sampleRateHz"] = format.SampleRateHz,
                    ["channels"] = format.Channels,
                    ["frameDurationMs"] = 20
                }
                : null,
            ["capabilities"] = new Dictionary<string, object?>
            {
                ["stt"] = new Dictionary<string, object?>
                {
                    ["streamingAudio"] = ready.Recognition.StreamingAudio,
                    ["partialTranscripts"] = ready.Recognition.PartialTranscripts,
                    ["speechBoundaryEvents"] = ready.Recognition.SpeechBoundaryEvents,
                    ["cancellation"] = ready.Recognition.Cancellation
                },
                ["tts"] = new Dictionary<string, object?>
                {
                    ["streamingAudio"] = ready.Synthesis.StreamingAudio,
                    ["timingMarks"] = ready.Synthesis.TimingMarks,
                    ["cancellation"] = ready.Synthesis.Cancellation,
                    ["voiceSelection"] = ready.Synthesis.VoiceSelection,
                    ["speakingRate"] = ready.Synthesis.SpeakingRate,
                    ["supportedFormats"] = Array.Empty<object>()
                },
                ["bargeInPolicy"] = ready.BargeInPolicy
            },
            ["lastEntrySequence"] = ready.LastEntrySequence,
            ["history"] = history,
            ["activeResponseId"] = ready.ActiveResponseId?.ToString()
        };
    }

    private static string ToTrigger(string trigger) => trigger switch
    {
        "UserTurn" => "userTurn",
        "LongSilence" => "longSilence",
        "EnvironmentUpdate" => "environmentUpdate",
        "UnfinishedInteraction" => "unfinishedInteraction",
        _ => "userTurn"
    };

    private static string ToInput(string value) => value switch
    {
        nameof(InputActivity.Listening) => "listening",
        nameof(InputActivity.UserSpeaking) => "userSpeaking",
        nameof(InputActivity.Finalizing) => "finalizing",
        _ => "idle"
    };

    private static string ToOutput(string value) => value switch
    {
        nameof(OutputActivity.WaitingForAgent) => "waitingForAgent",
        nameof(OutputActivity.AgentGenerating) => "agentGenerating",
        nameof(OutputActivity.AgentSpeaking) => "agentSpeaking",
        nameof(OutputActivity.Interrupted) => "interrupted",
        _ => "idle"
    };
}
