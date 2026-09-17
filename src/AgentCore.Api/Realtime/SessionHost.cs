using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Channels;
using AgentCore.Api.Mapping;
using AgentCore.Application.Events;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Contracts.Realtime;
using AgentCore.Domain.Conversation;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
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
    private readonly IOwnerCapabilityService _capabilities;
    private readonly ILogger<SessionHost> _logger;
    private readonly ConcurrentDictionary<Guid, Live> _live = new();
    private readonly ConcurrentDictionary<string, Guid> _connections = new();
    private readonly HashSet<Guid> _terminating = [];
    private readonly ConcurrentDictionary<Guid, long> _terminateEpoch = new();
    private readonly ConcurrentDictionary<Guid, Task> _disposing = new();
    private readonly object _gate = new();
    private bool _admitting = true;

    internal Func<Task>? AfterAdmitHold { get; set; }

    internal Func<CancellationToken, Task>? AfterUserTextPersisted { get; set; }

    public SessionHost(
        SessionManager sessions,
        SessionRuntimeFactory factory,
        IHubContext<SessionHub> hubs,
        TimeProvider time,
        IOptions<AgentCoreOptions> options,
        IOwnerCapabilityService capabilities,
        ILogger<SessionHost> logger)
    {
        _sessions = sessions;
        _factory = factory;
        _hubs = hubs;
        _time = time;
        _options = options.Value;
        _capabilities = capabilities;
        _logger = logger;
    }

    public Guid? ActiveResponseId(Guid sessionId) =>
        _live.TryGetValue(sessionId, out var live) ? live.Runtime.ActiveResponseId : null;

    public SessionSnapshot? LiveSnapshot(Guid sessionId) =>
        _live.TryGetValue(sessionId, out var live) ? live.Runtime.Snapshot : null;

    internal Guid? LiveAttachmentId(Guid sessionId) =>
        _live.TryGetValue(sessionId, out var live) ? live.AttachmentId : null;

    internal bool? RuntimeMuted(Guid sessionId) =>
        _live.TryGetValue(sessionId, out var live) ? live.Runtime.Muted : null;

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

        if (!await _capabilities.ValidateAsync(command.Payload?.OwnerCapability, cancellationToken).ConfigureAwait(false))
        {
            return Reject(command.EventId, "Session", "Unauthorized", "Owner capability is missing or invalid.", true, null);
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
        var terminateEpoch = _terminateEpoch.GetOrAdd(sessionId, 0);
        lock (_gate)
        {
            if (_terminating.Contains(sessionId))
            {
                return Reject(command.EventId, "Session", "NotFound", "Session has ended.", true, null);
            }
        }
        var fingerprint = Fingerprint(command);
        try
        {
            var snapshot = await _sessions.GetAsync(sessionId, cancellationToken).ConfigureAwait(false);
            if (snapshot.Status == SessionStatus.Ended)
            {
                return Reject(command.EventId, "Session", "NotFound", "Session has ended.", true, null);
            }

            if (snapshot.ArchivedAt is not null)
            {
                return Reject(command.EventId, "Session", "NotFound", "Session is archived.", true, null);
            }

            if (snapshot.Status == SessionStatus.Paused
                && SessionPauseSemantics.RequiresExplicitResume(snapshot.PauseReason))
            {
                return Reject(
                    command.EventId,
                    "Session",
                    "SessionPaused",
                    "Session is paused; reopen before attach.",
                    false,
                    null);
            }

            while (true)
            {
                if (_disposing.TryGetValue(sessionId, out var pendingDispose))
                {
                    try
                    {
                        await pendingDispose.WaitAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return Reject(
                            command.EventId,
                            "Session",
                            "SessionBusy",
                            "Session is shutting down.",
                            false,
                            1000);
                    }
                }

                Live? live = null;
                var created = false;
                var replay = false;
                CommandAck? replayAck = null;
                var waitDispose = false;
                lock (_gate)
                {
                    if (_disposing.TryGetValue(sessionId, out var stillDisposing) && !stillDisposing.IsCompleted)
                    {
                        waitDispose = true;
                    }
                    else if (_connections.TryGetValue(connectionId, out var owned) && owned != sessionId)
                    {
                        return Reject(
                            command.EventId,
                            "Session",
                            "SessionInUse",
                            "Connection is already attached to another session.",
                            false,
                            null);
                    }

                    else if (_live.TryGetValue(sessionId, out var existing))
                    {
                        live = existing;
                    }
                    else
                    {
                        if (_terminating.Contains(sessionId) || !_admitting)
                        {
                            return Reject(command.EventId, "Session", "NotFound", "Session has ended.", true, null);
                        }

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

                if (waitDispose)
                {
                    continue;
                }

                if (live is null)
                {
                    continue;
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

                if (live.Runtime.Snapshot.Status == SessionStatus.Paused)
                {
                    if (SessionPauseSemantics.IsTransportResumable(snapshot.PauseReason)
                        || SessionPauseSemantics.IsTransportResumable(live.Runtime.Snapshot.PauseReason))
                    {
                        await live.Runtime.ApplyTransportResumedSnapshotAsync(snapshot, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        var resumed = snapshot.Status == SessionStatus.Paused
                            ? await _sessions.ReopenAsync(sessionId, cancellationToken).ConfigureAwait(false)
                            : snapshot;
                        await live.Runtime.ApplyReopenedSnapshotAsync(resumed, cancellationToken).ConfigureAwait(false);
                    }
                }

                var attached = await live.Runtime.AttachAsync(cancellationToken).ConfigureAwait(false);
                if (!attached || live.Evicted || !_live.TryGetValue(sessionId, out var stillAttached) || !ReferenceEquals(stillAttached, live) || live.ConnectionId != connectionId)
                {
                    var extracted = ExtractLive(sessionId, connectionId);
                    if (extracted is not null)
                    {
                        await ShutdownLiveAsync(
                                extracted,
                                sessionId,
                                detachRuntime: true,
                                joinDispatcher: !extracted.InDispatchAction,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }

                    return Reject(
                        command.EventId,
                        "Session",
                        attached ? "NotFound" : "SessionPersistenceUnavailable",
                        attached ? "Session is not attached." : "Persistent save failed.",
                        false,
                        attached ? null : 1000);
                }

                var accepted = Accept(command.EventId);
                await live.Admission.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    lock (_gate)
                    {
                        if (_terminating.Contains(sessionId)
                            || _terminateEpoch.GetOrAdd(sessionId, 0) != terminateEpoch)
                        {
                            return Reject(command.EventId, "Session", "NotFound", "Session has ended.", true, null);
                        }
                    }

                    if (_live.TryGetValue(sessionId, out var still)
                        && ReferenceEquals(still, live)
                        && live.ConnectionId == connectionId)
                    {
                        live.LastAttachAck = accepted;
                        return accepted;
                    }
                }
                finally
                {
                    live.Admission.Release();
                }

                return Reject(command.EventId, "Session", "NotFound", "Session is not attached.", false, null);
            }
        }
        catch (AgentCoreException ex)
        {
            return Reject(command.EventId, "Session", ex.Code, ex.Message, ex.Fatal, ex.RetryAfterMs);
        }
    }

    public bool HasLiveRuntime(Guid sessionId) => _live.ContainsKey(sessionId);

    public bool HasActiveLiveConnection(Guid sessionId) =>
        _live.TryGetValue(sessionId, out var live) && live.ConnectionId is not null;

    public async Task ApplyReopenedSnapshotToLiveAsync(
        Guid sessionId,
        SessionSnapshot reopened,
        CancellationToken cancellationToken = default)
    {
        if (_live.TryGetValue(sessionId, out var live))
        {
            await live.Runtime.ApplyReopenedSnapshotAsync(reopened, cancellationToken).ConfigureAwait(false);
        }
    }

    public Task StageAttachmentsAsync(Guid sessionId, IReadOnlyList<Guid> attachmentIds, CancellationToken cancellationToken)
    {
        if (!_live.TryGetValue(sessionId, out var live))
        {
            return Task.CompletedTask;
        }

        return live.Runtime.StageAttachmentsAsync(attachmentIds, cancellationToken);
    }

    public async Task CancelLiveRuntimeAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        if (!_live.TryGetValue(sessionId, out var live))
        {
            return;
        }

        var extracted = ExtractLive(sessionId, live.ConnectionId);
        if (extracted is null)
        {
            return;
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(5));
        await ShutdownLiveAsync(extracted, sessionId, detachRuntime: true, joinDispatcher: true, budget.Token)
            .ConfigureAwait(false);
    }

    public async Task DeactivateAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        if (_live.TryGetValue(sessionId, out var live))
        {
            await live.Runtime.RequestDeactivateAsync(cancellationToken).ConfigureAwait(false);
        }

        await CancelLiveRuntimeAsync(sessionId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<SessionSnapshot> RenameAsync(
        Guid sessionId,
        string title,
        CancellationToken cancellationToken = default)
    {
        if (_live.TryGetValue(sessionId, out var live))
        {
            if (!await live.Runtime.RequestRenameAsync(title, cancellationToken).ConfigureAwait(false))
            {
                throw AgentCoreErrors.Persistence("Rename failed.");
            }

            return live.Runtime.Snapshot;
        }

        return await _sessions.RenameAsync(sessionId, title, cancellationToken).ConfigureAwait(false);
    }

    public async Task DetachAsync(string connectionId)
    {
        if (!_connections.TryGetValue(connectionId, out var sessionId))
        {
            return;
        }

        var live = ExtractLive(sessionId, connectionId);
        if (live is null)
        {
            return;
        }

        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await ShutdownLiveAsync(live, sessionId, detachRuntime: true, joinDispatcher: true, budget.Token)
            .ConfigureAwait(false);
    }

    public async Task TerminateAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _terminating.Add(sessionId);
        }

        _terminateEpoch.AddOrUpdate(sessionId, 1, static (_, current) => current + 1);

        try
        {
            if (_live.TryGetValue(sessionId, out var live))
            {
                var ended = false;
                try
                {
                    ended = await live.Runtime.RequestEndAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }

                try
                {
                    await live.Runtime.WaitUntilIdleAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }

                if (!ended)
                {
                    SessionSnapshot? current = null;
                    try
                    {
                        current = await _sessions.GetAsync(sessionId, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                    }

                    if (current?.Status != SessionStatus.Ended && !cancellationToken.IsCancellationRequested)
                    {
                        throw AgentCoreErrors.Persistence("Failed to persist session end.");
                    }
                }

                var extracted = ExtractLive(sessionId, live.ConnectionId);
                if (extracted is not null)
                {
                    using var shutdownBudget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await ShutdownLiveAsync(
                            extracted,
                            sessionId,
                            detachRuntime: false,
                            joinDispatcher: !extracted.InDispatchAction,
                            shutdownBudget.Token)
                        .ConfigureAwait(false);
                }
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            try
            {
                await _sessions.EndAsync(sessionId, cancellationToken).ConfigureAwait(false);
            }
            catch (AgentCoreException ex) when (ex.Code == "SessionPersistenceUnavailable")
            {
                var snapshot = await _sessions.GetAsync(sessionId, cancellationToken).ConfigureAwait(false);
                if (snapshot.Status != SessionStatus.Ended)
                {
                    throw;
                }
            }
        }
        finally
        {
            lock (_gate)
            {
                _terminating.Remove(sessionId);
            }
        }
    }

    private Live? ExtractLive(Guid sessionId, string? connectionId)
    {
        Live? found;
        lock (_gate)
        {
            _live.TryGetValue(sessionId, out found);
        }

        if (found is null)
        {
            return null;
        }

        found.Admission.Wait();
        try
        {
            if (connectionId is not null
                && !string.Equals(found.ConnectionId, connectionId, StringComparison.Ordinal))
            {
                return null;
            }

            Live? live = null;
            lock (_gate)
            {
                if (_live.TryGetValue(sessionId, out var current)
                    && ReferenceEquals(current, found)
                    && (connectionId is null
                        || string.Equals(current.ConnectionId, connectionId, StringComparison.Ordinal))
                    && _live.TryRemove(sessionId, out var removed)
                    && ReferenceEquals(removed, found))
                {
                    live = removed;
                    live.Evicted = true;
                    _disposing[sessionId] = live.Disposed.Task;
                }

                if (connectionId is not null)
                {
                    _connections.TryRemove(connectionId, out _);
                }
                else if (live?.ConnectionId is { } attached)
                {
                    _connections.TryRemove(attached, out _);
                }
            }

            if (live is not null)
            {
                live.FailPendingAdmits();
            }

            return live;
        }
        finally
        {
            found.Admission.Release();
        }
    }

    private async Task ShutdownLiveAsync(
        Live live,
        Guid sessionId,
        bool detachRuntime,
        bool joinDispatcher,
        CancellationToken cancellationToken = default)
    {
        if (!joinDispatcher)
        {
            live.AfterRuntimeDispose = () => CompleteDispose(sessionId, live);
            live.DisposeAfterAction = true;
        }

        live.StopDispatch();
        if (joinDispatcher)
        {
            try
            {
                await live.Dispatcher.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        if (detachRuntime)
        {
            try
            {
                await live.Runtime.DetachAsync(cancellationToken).ConfigureAwait(false);
                await live.Runtime.WaitUntilIdleAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        if (joinDispatcher)
        {
            await JoinDisposeAsync(live, sessionId).ConfigureAwait(false);
            return;
        }

        if (live.Dispatcher.IsCompleted && !live.Disposed.Task.IsCompleted)
        {
            await JoinDisposeAsync(live, sessionId).ConfigureAwait(false);
        }
    }

    private async Task JoinDisposeAsync(Live live, Guid sessionId)
    {
        var disposing = live.Runtime.DisposeAsync().AsTask();
        try
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await disposing.WaitAsync(budget.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        if (disposing.IsCompleted)
        {
            CompleteDispose(sessionId, live);
            return;
        }

        _ = disposing.ContinueWith(
            _ => CompleteDispose(sessionId, live),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void CompleteDispose(Guid sessionId, Live live)
    {
        live.Disposed.TrySetResult();
        _disposing.TryRemove(sessionId, out _);
    }

    public async Task DrainAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _admitting = false;
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(5));
        foreach (var sessionId in _live.Keys.ToArray())
        {
            var live = ExtractLive(sessionId, connectionId: null);
            if (live is null)
            {
                continue;
            }

            try
            {
                if (live.Runtime.ActiveResponseId is not null)
                {
                    await live.Runtime.CancelActiveResponseAsync(budget.Token).ConfigureAwait(false);
                }

                await ShutdownLiveAsync(
                        live,
                        sessionId,
                        detachRuntime: true,
                        joinDispatcher: !live.InDispatchAction,
                        budget.Token)
                    .ConfigureAwait(false);
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

        var text = command.Payload?.Text ?? "";
        var attachmentIds = ParseAttachmentIds(command.Payload?.AttachmentIds, command.EventId);
        if (attachmentIds is { Accepted: false })
        {
            return attachmentIds.Reject!;
        }

        var ids = attachmentIds!.Ids;
        if (string.IsNullOrWhiteSpace(text) && ids.Count == 0)
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
                var persisted = await live.Runtime.SubmitPersistedUserTextAsync(
                        text,
                        sourceEventId,
                        cancellationToken,
                        ids)
                    .ConfigureAwait(false);
                if (persisted is null)
                {
                    return Reject(
                        command.EventId,
                        "Transport",
                        "Backpressure",
                        "The session mailbox is full. Stop or retry after the current work drains.",
                        false,
                        1000);
                }

                if (persisted != true)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return Reject(command.EventId, "Session", "NotFound", "Session is not attached.", false, null);
                    }

                    return Reject(
                        command.EventId,
                        "Session",
                        "SessionPersistenceUnavailable",
                        "Failed to persist the user turn.",
                        false,
                        1000);
                }

                if (AfterUserTextPersisted is not null)
                {
                    try
                    {
                        await AfterUserTextPersisted(live.DispatchToken).WaitAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return Reject(command.EventId, "Session", "NotFound", "Session is not attached.", false, null);
                    }
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
            await live.Runtime.WaitUntilIdleAsync(cancellationToken).ConfigureAwait(false);
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

        var ack = await AdmitControlAsync(connectionId, command, async live =>
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

        if (ack.Accepted)
        {
            await DetachAsync(connectionId).ConfigureAwait(false);
        }

        return ack;
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
            || !string.Equals(dto.AttachmentId, live.AttachmentId.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            await PublishAudioProtocolErrorAsync(sessionId, "Do not send PCM until Mode is voice.").ConfigureAwait(false);
            return true;
        }

        if (!Guid.TryParse(dto.StreamId, out var audioStream))
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
        _ = live.Runtime.TryAdmitAudio(frame, audioStream);
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
            var admitted = await live.Runtime.SubmitReceiptAsync(
                responseId,
                payload.TextEndExclusive,
                blockIds: payload.BlockIds)
                .ConfigureAwait(false);
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
            await live.Runtime.WaitUntilMailboxDrainedAsync().ConfigureAwait(false);
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
        return new ValueTask(PublishEventAsync(live, output, evt, cancellationToken));
    }

    private async Task PublishEventAsync(Live live, SessionOutput output, ServerEvent evt, CancellationToken cancellationToken)
    {
        var connectionId = live.ConnectionId;
        if (connectionId is null)
        {
            return;
        }

        await _hubs.Clients.Client(connectionId).SendAsync("SessionEvent", evt, cancellationToken).ConfigureAwait(false);
        if (output.Payload is StateChangedOutput { Status: SessionStatus.Paused, PauseReason: not null } paused)
        {
            var sessionId = output.Context.SessionId;
            _ = Task.Run(async () =>
            {
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    try
                    {
                        await CancelLiveRuntimeAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
                        return;
                    }
                    catch (Exception ex) when (attempt == 0)
                    {
                        _logger.LogWarning(
                            ex,
                            "Paused session cleanup failed for {SessionId}; retrying once.",
                            sessionId);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(
                            ex,
                            "Paused session cleanup failed for {SessionId} after retry.",
                            sessionId);
                    }
                }
            });
            return;
        }

        if (output.Payload is ErrorOutput { Code: "SessionPersistenceUnavailable" }
            && live.Runtime.Snapshot.Status is SessionStatus.Paused)
        {
            live.InvalidateAfterPublish = true;
        }

        if (output.Payload is StateChangedOutput { Status: SessionStatus.Paused } && live.InvalidateAfterPublish)
        {
            live.InvalidateAfterPublish = false;
            InvalidateAttachment(live);
        }
    }

    private void InvalidateAttachment(Live live)
    {
        live.AttachmentId = Guid.NewGuid();
        live.ResetControl();
        var connectionId = live.ConnectionId;
        live.ConnectionId = null;
        if (connectionId is not null)
        {
            _connections.TryRemove(connectionId, out _);
        }
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

            if (!Guid.TryParse(streamId, out var parsedStream)
                || !live.Runtime.TryAdmitBoundary(utteranceId, boundary, activityScore, sampleOffset, durationMs, parsedStream))
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

        var work = admission.Work!;
        var live = admission.Live;
        if (AfterAdmitHold is not null)
        {
            await AfterAdmitHold().ConfigureAwait(false);
            if (live.Evicted)
            {
                work.Ready.TrySetResult();
                return Reject(command.EventId, "Session", "NotFound", "Session is not attached.", false, null);
            }
        }

        work.Action = action;
        work.Ready.TrySetResult();
        return await work.Completed.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<(bool Ok, Live Live, CommandAck? Reject, DispatchWork? Work)> TryAdmitAsync(
        string connectionId,
        IRealtimeCommand command,
        CancellationToken cancellationToken)
    {
        if (!_connections.TryGetValue(connectionId, out var sessionId)
            || !_live.TryGetValue(sessionId, out var found)
            || !string.Equals(command.SessionId, sessionId.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            return (false, null!, Reject(command.EventId, "Session", "NotFound", "Session is not attached.", false, null), null);
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
                return (false, found, Reject(command.EventId, "Session", "NotFound", "Session is not attached.", false, null), null);
            }

            if (string.IsNullOrWhiteSpace(command.AttachmentId)
                || !string.Equals(command.AttachmentId, found.AttachmentId.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                return (false, found, Reject(command.EventId, "Session", "StaleCommand", "Attachment lease is invalid.", false, null), null);
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
                        null), null);
                }

                return (false, found, prior.Ack, null);
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
                        null), null);
                }

                inflight = pending.Ack.Task;
            }
            else if (command.Sequence <= found.LastSequence)
            {
                return (false, found, Reject(command.EventId, "Session", "StaleCommand", "Backwards command sequence.", false, null), null);
            }
            else
            {
                found.LastSequence = command.Sequence;
                found.LastEventId = command.EventId;
                var completed = new TaskCompletionSource<CommandAck>(TaskCreationOptions.RunContinuationsAsynchronously);
                found.InFlight[command.EventId] = new InFlightAdmit(fingerprint, completed);
                var work = new DispatchWork(command, completed);
                if (!found.Enqueue(work))
                {
                    found.InFlight.Remove(command.EventId);
                    return (false, found, Reject(command.EventId, "Session", "NotFound", "Session is not attached.", false, null), null);
                }

                return (true, found, null, work);
            }
        }
        finally
        {
            found.Admission.Release();
        }

        if (inflight is not null)
        {
            return (false, found, await inflight.WaitAsync(cancellationToken).ConfigureAwait(false), null);
        }

        return (false, found, Reject(command.EventId, "Session", "NotFound", "Session is not attached.", false, null), null);
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

    private readonly record struct ParsedAttachments(bool Accepted, IReadOnlyList<Guid> Ids, CommandAck? Reject);

    private ParsedAttachments ParseAttachmentIds(string[]? raw, string eventId)
    {
        if (raw is null || raw.Length == 0)
        {
            return new ParsedAttachments(true, [], null);
        }

        if (raw.Length > AttachmentLimits.MaxPerMessage)
        {
            return new ParsedAttachments(
                false,
                [],
                Reject(eventId, "Validation", "ValidationError", "A message may include at most 10 attachments.", false, null));
        }

        var ids = new List<Guid>(raw.Length);
        foreach (var value in raw)
        {
            if (!Guid.TryParse(value, out var id))
            {
                return new ParsedAttachments(
                    false,
                    [],
                    Reject(eventId, "Validation", "ValidationError", "attachmentIds must be UUIDs.", false, null));
            }

            ids.Add(id);
        }

        if (ids.Distinct().Count() != ids.Count)
        {
            return new ParsedAttachments(
                false,
                [],
                Reject(eventId, "Validation", "ValidationError", "Attachment ids must be unique.", false, null));
        }

        return new ParsedAttachments(true, ids, null);
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

    private sealed class Live
    {
        private readonly Channel<DispatchWork> _dispatch = Channel.CreateUnbounded<DispatchWork>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        private readonly Queue<string> _dedupeOrder = new();
        private readonly CancellationTokenSource _dispatchLifetime = new();
        private long _serverSequence;

        public Live(SessionRuntime runtime)
        {
            Runtime = runtime;
            Dispatcher = Task.Run(DispatchAsync);
        }

        public SessionRuntime Runtime { get; }
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
        public Task Dispatcher { get; }
        public bool Evicted { get; set; }
        public bool InDispatchAction { get; private set; }
        public string? ActiveCommandEventId { get; private set; }
        public bool DisposeAfterAction { get; set; }
        public Action? AfterRuntimeDispose { get; set; }
        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool InvalidateAfterPublish { get; set; }
        public CancellationToken DispatchToken => _dispatchLifetime.Token;

        public long NextSequence() => Interlocked.Increment(ref _serverSequence);

        public bool Enqueue(DispatchWork work)
        {
            if (_dispatch.Writer.TryWrite(work))
            {
                return true;
            }

            var rejected = Reject(work.Command.EventId, "Session", "NotFound", "Session is not attached.", false, null);
            work.Ready.TrySetResult();
            work.Completed.TrySetResult(rejected);
            return false;
        }

        public void FailPendingAdmits()
        {
            foreach (var pending in InFlight.ToArray())
            {
                if (string.Equals(pending.Key, ActiveCommandEventId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                pending.Value.Ack.TrySetResult(
                    Reject(pending.Key, "Session", "NotFound", "Session is not attached.", false, null));
                InFlight.Remove(pending.Key);
            }
        }

        public void StopDispatch()
        {
            Evicted = true;
            try
            {
                _dispatchLifetime.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            _dispatch.Writer.TryComplete();
        }

        private bool StillOwns(IRealtimeCommand command) =>
            !Evicted
            && ConnectionId is not null
            && !string.IsNullOrWhiteSpace(command.AttachmentId)
            && string.Equals(command.AttachmentId, AttachmentId.ToString(), StringComparison.OrdinalIgnoreCase);

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

        private async Task DispatchAsync()
        {
            await foreach (var work in _dispatch.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                try
                {
                    try
                    {
                        await work.Ready.Task.WaitAsync(_dispatchLifetime.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        var cancelled = Reject(work.Command.EventId, "Session", "NotFound", "Session is not attached.", false, null);
                        await FinishAdmitAsync(this, work.Command, cancelled, CancellationToken.None).ConfigureAwait(false);
                        work.Completed.TrySetResult(cancelled);
                        continue;
                    }

                    if (work.Action is null)
                    {
                        var skipped = Reject(work.Command.EventId, "Session", "Unavailable", "Command failed.", false, 1000);
                        await FinishAdmitAsync(this, work.Command, skipped, CancellationToken.None).ConfigureAwait(false);
                        work.Completed.TrySetResult(skipped);
                        continue;
                    }

                    CommandAck ack;
                    try
                    {
                        if (Evicted || !StillOwns(work.Command))
                        {
                            ack = Reject(work.Command.EventId, "Session", "NotFound", "Session is not attached.", false, null);
                        }
                        else
                        {
                            InDispatchAction = true;
                            ActiveCommandEventId = work.Command.EventId;
                            ack = await work.Action(this).ConfigureAwait(false);
                        }
                    }
                    catch (Exception)
                    {
                        ack = Reject(work.Command.EventId, "Session", "Unavailable", "Command failed.", false, 1000);
                        await FinishAdmitAsync(this, work.Command, ack, CancellationToken.None).ConfigureAwait(false);
                        work.Completed.TrySetResult(ack);
                        throw;
                    }
                    finally
                    {
                        InDispatchAction = false;
                        ActiveCommandEventId = null;
                    }

                    await FinishAdmitAsync(this, work.Command, ack, CancellationToken.None).ConfigureAwait(false);
                    work.Completed.TrySetResult(ack);
                }
                catch (Exception)
                {
                    if (!work.Completed.Task.IsCompleted)
                    {
                        var failed = Reject(work.Command.EventId, "Session", "Unavailable", "Command failed.", false, 1000);
                        work.Completed.TrySetResult(failed);
                    }
                }
            }

            if (DisposeAfterAction)
            {
                try
                {
                    await Runtime.DisposeAsync().ConfigureAwait(false);
                }
                finally
                {
                    AfterRuntimeDispose?.Invoke();
                }
            }
        }
    }

    private sealed class DispatchWork(IRealtimeCommand command, TaskCompletionSource<CommandAck> completed)
    {
        public IRealtimeCommand Command { get; } = command;
        public Func<Live, Task<CommandAck>>? Action { get; set; }
        public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<CommandAck> Completed { get; } = completed;
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
            BlockUpsertOutput block => ("agent.block.upsert", new Dictionary<string, object?>
            {
                ["blockId"] = block.BlockId,
                ["kind"] = block.Kind,
                ["text"] = block.Text,
                ["fallbackText"] = block.FallbackText,
                ["attachmentId"] = block.AttachmentId,
                ["artifactId"] = block.ArtifactId
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
                ["streamId"] = state.StreamId?.ToString(),
                ["pauseReason"] = state.PauseReason
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
            ["createdAt"] = entry.CreatedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
            ["attachments"] = entry.Attachments?.Select(item => (object)new Dictionary<string, object?>
            {
                ["attachmentId"] = item.AttachmentId.ToString(),
                ["displayName"] = item.DisplayName,
                ["contentType"] = item.ContentType
            }).ToArray(),
            ["blocks"] = entry.Blocks.Select(block => (object)new Dictionary<string, object?>
            {
                ["blockId"] = block.BlockId,
                ["kind"] = block.Kind,
                ["text"] = block.Text,
                ["fallbackText"] = block.FallbackText,
                ["attachmentId"] = block.AttachmentId,
                ["artifactId"] = block.ArtifactId
            }).ToArray()
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
        nameof(OutputActivity.ProcessingAttachments) => "processingAttachments",
        nameof(OutputActivity.RunningTools) => "runningTools",
        nameof(OutputActivity.WaitingForAgent) => "waitingForAgent",
        nameof(OutputActivity.AgentGenerating) => "agentGenerating",
        nameof(OutputActivity.AgentSpeaking) => "agentSpeaking",
        nameof(OutputActivity.Interrupted) => "interrupted",
        _ => "idle"
    };
}
