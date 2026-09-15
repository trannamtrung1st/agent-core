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

public sealed class SessionHost : ISessionOutput, ISessionAudioOutput, IEnvironmentEventIngress
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

    public async Task<CommandAck> AttachAsync(string connectionId, ClientCommand command, CancellationToken cancellationToken)
    {
        var ack = Validate(command, attach: true);
        if (!ack.Accepted)
        {
            return ack;
        }

        if (!_admitting)
        {
            return Reject(command.EventId, "Session", "ServiceUnavailable", "The host is shutting down.", false, 1000);
        }

        var sessionId = Guid.Parse(command.SessionId);
        try
        {
            var snapshot = await _sessions.GetAsync(sessionId, cancellationToken).ConfigureAwait(false);
            if (snapshot.Status == SessionStatus.Ended)
            {
                return Reject(command.EventId, "Session", "NotFound", "Session has ended.", true, null);
            }

            Live live;
            var replay = false;
            lock (_gate)
            {
                if (_live.TryGetValue(sessionId, out var existing))
                {
                    if (existing.ConnectionId == connectionId && existing.LastAttachEventId == command.EventId)
                    {
                        live = existing;
                        replay = true;
                    }
                    else if (existing.ConnectionId is not null)
                    {
                        return Reject(command.EventId, "Session", "SessionInUse", "Session is attached to another connection.", false, null);
                    }
                    else
                    {
                        live = existing;
                        live.ConnectionId = connectionId;
                        live.LastAttachEventId = command.EventId;
                        live.AttachmentId = Guid.NewGuid();
                        live.ResetControl();
                    }
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
                    _live[sessionId] = live;
                }

                _connections[connectionId] = sessionId;
            }

            await live.Runtime.AttachAsync(cancellationToken).ConfigureAwait(false);
            await live.Runtime.WaitUntilMailboxDrainedAsync(cancellationToken).ConfigureAwait(false);
            _ = replay;
            return Accept(command.EventId);
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
            await live.Runtime.RequestEndAsync(cancellationToken).ConfigureAwait(false);
            await live.Runtime.WaitUntilMailboxDrainedAsync(cancellationToken).ConfigureAwait(false);
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

    public async Task<CommandAck> SendTextAsync(string connectionId, ClientCommand command, CancellationToken cancellationToken)
    {
        var ack = Validate(command, attach: false);
        if (!ack.Accepted)
        {
            return ack;
        }

        if (!TryLive(connectionId, command, out var live, out var reject))
        {
            return reject!;
        }

        var text = command.Payload.TryGetValue("text", out var value) ? value?.ToString() : null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return Reject(command.EventId, "Validation", "ValidationError", "text is required.", false, null);
        }

        if (text.Length > 8000)
        {
            return Reject(command.EventId, "Validation", "ValidationError", "Text exceeds 8000 UTF-16 code units.", false, null);
        }

        try
        {
            await live.Runtime.SubmitUserTextAsync(text, cancellationToken).ConfigureAwait(false);
            return Accept(command.EventId);
        }
        catch (AgentCoreException ex)
        {
            return Reject(command.EventId, "Validation", ex.Code, ex.Message, ex.Fatal, ex.RetryAfterMs);
        }
    }

    public async Task<CommandAck> SetModeAsync(string connectionId, ClientCommand command, CancellationToken cancellationToken)
    {
        var ack = Validate(command, attach: false);
        if (!ack.Accepted)
        {
            return ack;
        }

        if (!TryLive(connectionId, command, out var live, out var reject))
        {
            return reject!;
        }

        var raw = command.Payload.TryGetValue("mode", out var value) ? value?.ToString() : null;
        if (raw is not ("text" or "voice"))
        {
            return Reject(command.EventId, "Validation", "ValidationError", "mode must be text or voice.", false, null);
        }

        var mode = raw == "voice" ? SessionMode.Voice : SessionMode.Text;
        await live.Runtime.SetModeAsync(mode, cancellationToken).ConfigureAwait(false);
        return Accept(command.EventId);
    }

    public async Task<CommandAck> EndAsync(string connectionId, ClientCommand command, CancellationToken cancellationToken)
    {
        var ack = Validate(command, attach: false);
        if (!ack.Accepted)
        {
            return ack;
        }

        if (!Guid.TryParse(command.SessionId, out var sessionId))
        {
            return Reject(command.EventId, "Protocol", "ProtocolError", "sessionId is invalid.", true, null);
        }

        if (!TryLive(connectionId, command, out _, out var reject))
        {
            return reject!;
        }

        await TerminateAsync(sessionId, cancellationToken).ConfigureAwait(false);
        return Accept(command.EventId);
    }

    public Task<CommandAck> AdmitSpeechAsync(string connectionId, ClientCommand command, SpeechBoundary boundary)
    {
        var ack = Validate(command, attach: false);
        if (!ack.Accepted)
        {
            return Task.FromResult(ack);
        }

        if (!TryLive(connectionId, command, out var live, out var reject))
        {
            return Task.FromResult(reject!);
        }

        if (live.Runtime.Snapshot.Mode != SessionMode.Voice)
        {
            return Task.FromResult(Reject(command.EventId, "Protocol", "ProtocolError", "Speech is not admitted until voice mode is applied.", true, null));
        }

        if (!Guid.TryParse(AsString(command.Payload, "utteranceId"), out var utteranceId))
        {
            return Task.FromResult(Reject(command.EventId, "Validation", "ValidationError", "utteranceId is required.", false, null));
        }

        var streamId = AsString(command.Payload, "streamId");
        if (live.Runtime.StreamId is { } expected
            && !string.Equals(streamId, expected.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(Reject(command.EventId, "Transport", "AudioDiscontinuity", "Speech streamId does not match.", false, null));
        }

        var score = AsDouble(command.Payload, "activityScore");
        if (!live.Runtime.TryAdmitBoundary(utteranceId, boundary, score))
        {
            return Task.FromResult(Reject(command.EventId, "Transport", "AudioDiscontinuity", "Speech boundary was dropped.", false, null));
        }

        return Task.FromResult(Accept(command.EventId));
    }

    public async Task AdmitAudioAsync(string connectionId, InputAudioDto dto)
    {
        if (!_connections.TryGetValue(connectionId, out var sessionId) || !_live.TryGetValue(sessionId, out var live))
        {
            return;
        }

        if (live.Runtime.Snapshot.Mode != SessionMode.Voice
            || !string.Equals(dto.AttachmentId, live.AttachmentId.ToString(), StringComparison.OrdinalIgnoreCase)
            || live.Runtime.StreamId is not { } streamId
            || !string.Equals(dto.StreamId, streamId.ToString(), StringComparison.OrdinalIgnoreCase)
            || dto.ProtocolVersion != 1)
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
                        new ErrorOutput("Protocol", "ProtocolError", "Do not send PCM until Mode is voice.", true, null)),
                    CancellationToken.None)
                .ConfigureAwait(false);
            return;
        }

        var frame = new AudioFrame(dto.FrameSequence, dto.SampleOffset, dto.Data);
        _ = live.Runtime.TryAdmitAudio(frame);
        await Task.CompletedTask.ConfigureAwait(false);
    }

    public async Task<CommandAck> AcceptControlAsync(string connectionId, ClientCommand command)
    {
        var ack = Validate(command, attach: false);
        if (!ack.Accepted)
        {
            return ack;
        }

        if (!TryLive(connectionId, command, out var live, out var reject))
        {
            return reject!;
        }

        if (string.Equals(command.Type, "session.mute", StringComparison.Ordinal))
        {
            await live.Runtime.SetMutedAsync(AsBool(command.Payload, "muted") ?? false).ConfigureAwait(false);
            return Accept(command.EventId);
        }

        if (command.Type.StartsWith("playback.", StringComparison.Ordinal)
            && Guid.TryParse(command.ResponseId, out var responseId))
        {
            var kind = command.Type switch
            {
                "playback.started" => "started",
                "playback.progress" => "progress",
                "playback.completed" => "completed",
                "playback.stopped" => "stopped",
                _ => "progress"
            };
            var consumed = AsInt64(command.Payload, "consumedSamples") ?? 0;
            var textEnd = (int)(AsInt64(command.Payload, "textEndExclusive") ?? 0);
            await live.Runtime.SubmitPlaybackAsync(responseId, kind, consumed, textEnd).ConfigureAwait(false);
        }

        return Accept(command.EventId);
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

    private static CommandAck Validate(ClientCommand command, bool attach)
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

    private bool TryLive(string connectionId, ClientCommand command, out Live live, out CommandAck? reject)
    {
        live = null!;
        reject = null;
        if (!_connections.TryGetValue(connectionId, out var sessionId)
            || !_live.TryGetValue(sessionId, out var found)
            || !string.Equals(command.SessionId, sessionId.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            reject = Reject(command.EventId, "Session", "NotFound", "Session is not attached.", false, null);
            return false;
        }

        if (!string.IsNullOrEmpty(command.AttachmentId)
            && !string.Equals(command.AttachmentId, found.AttachmentId.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            reject = Reject(command.EventId, "Session", "StaleCommand", "Attachment lease is invalid.", false, null);
            return false;
        }

        if (found.LastEventId == command.EventId)
        {
            reject = Accept(command.EventId);
            live = found;
            return false;
        }

        if (command.Sequence <= found.LastSequence)
        {
            reject = Reject(command.EventId, "Session", "StaleCommand", "Backwards command sequence.", false, null);
            return false;
        }

        found.LastSequence = command.Sequence;
        found.LastEventId = command.EventId;
        if (found.ControlCount++ > 1024)
        {
            reject = Reject(command.EventId, "Session", "StaleCommand", "Control window exceeded.", false, null);
            return false;
        }

        live = found;
        return true;
    }

    private static string? AsString(Dictionary<string, object?> payload, string key) =>
        payload.TryGetValue(key, out var value) ? value?.ToString() : null;

    private static long? AsInt64(Dictionary<string, object?> payload, string key)
    {
        if (!payload.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        return value switch
        {
            long number => number,
            int number => number,
            uint number => number,
            short number => number,
            _ => long.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : null
        };
    }

    private static bool? AsBool(Dictionary<string, object?> payload, string key)
    {
        if (!payload.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        return value switch
        {
            bool flag => flag,
            string text when bool.TryParse(text, out var parsed) => parsed,
            _ => bool.TryParse(value.ToString(), out var parsed) ? parsed : null
        };
    }

    private static double? AsDouble(Dictionary<string, object?> payload, string key)
    {
        if (!payload.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        return value switch
        {
            double number => number,
            float number => number,
            int number => number,
            long number => number,
            _ => double.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : null
        };
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
        public long LastSequence { get; set; }
        public string? LastEventId { get; set; }
        public int ControlCount { get; set; }
        private long _serverSequence;

        public long NextSequence() => Interlocked.Increment(ref _serverSequence);

        public void ResetControl()
        {
            LastSequence = 0;
            LastEventId = null;
            ControlCount = 0;
            _serverSequence = 0;
        }
    }
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
