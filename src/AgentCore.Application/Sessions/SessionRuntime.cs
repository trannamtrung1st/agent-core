using System.Threading.Channels;
using AgentCore.Application.Events;
using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using Microsoft.Extensions.Logging;

namespace AgentCore.Application.Sessions;

public sealed class SessionRuntime : IAsyncDisposable
{
    private readonly Channel<SessionInput> _mailbox = Channel.CreateBounded<SessionInput>(
        new BoundedChannelOptions(256)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait
        });

    private readonly ILanguageModel _languageModel;
    private readonly IMemoryStore _store;
    private readonly ISessionOutput _output;
    private readonly IIdGenerator _ids;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _loop;
    private readonly object _idleGate = new();

    private SessionSnapshot _snapshot;
    private Guid _epoch;
    private Guid? _activeResponseId;
    private Guid? _activeEntryId;
    private string _generated = string.Empty;
    private bool _responseTerminal;
    private CancellationTokenSource? _responseCts;
    private int _inflight;
    private TaskCompletionSource _idle = CompletedIdle();

    public SessionRuntime(
        SessionSnapshot snapshot,
        ILanguageModel languageModel,
        IMemoryStore store,
        ISessionOutput output,
        IIdGenerator ids,
        TimeProvider time,
        ILogger logger)
    {
        _snapshot = snapshot;
        _languageModel = languageModel;
        _store = store;
        _output = output;
        _ids = ids;
        _time = time;
        _logger = logger;
        _epoch = _ids.NewId();
        _loop = Task.Run(() => RunAsync(_lifetime.Token));
    }

    public Guid SessionId => _snapshot.SessionId;

    public SessionSnapshot Snapshot => _snapshot;

    public async Task SubmitUserTextAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        if (text.Length > 8000)
        {
            throw AgentCoreErrors.Validation("Text exceeds 8000 UTF-16 code units.");
        }

        var context = NewContext();
        BeginWork();
        await _mailbox.Writer.WriteAsync(new UserTextReceived(context, text), cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task CancelActiveResponseAsync(CancellationToken cancellationToken = default)
    {
        if (_activeResponseId is not { } responseId)
        {
            return;
        }

        var context = NewContext();
        BeginWork();
        await _mailbox.Writer.WriteAsync(new CancelResponseReceived(context, responseId), cancellationToken)
            .ConfigureAwait(false);
    }

    public Task WaitUntilIdleAsync(CancellationToken cancellationToken = default)
    {
        TaskCompletionSource idle;
        lock (_idleGate)
        {
            idle = _idle;
        }

        return idle.Task.WaitAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        _mailbox.Writer.TryComplete();
        await _lifetime.CancelAsync().ConfigureAwait(false);
        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _responseCts?.Cancel();
        _lifetime.Dispose();
        _responseCts?.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var input in _mailbox.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    switch (input)
                    {
                        case UserTextReceived user:
                            await HandleUserTextAsync(user, cancellationToken).ConfigureAwait(false);
                            break;
                        case ModelResultReceived model:
                            await HandleModelAsync(model, cancellationToken).ConfigureAwait(false);
                            model.Processed.TrySetResult();
                            break;
                        case CancelResponseReceived cancel:
                            await HandleCancelAsync(cancel, cancellationToken).ConfigureAwait(false);
                            break;
                        case EndSessionReceived:
                            await EndAsync(cancellationToken).ConfigureAwait(false);
                            break;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Mailbox processing failed for session {SessionId}", SessionId);
                }
                finally
                {
                    EndWork();
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task HandleUserTextAsync(UserTextReceived input, CancellationToken cancellationToken)
    {
        if (_snapshot.Status is SessionStatus.Ended or SessionStatus.Ending)
        {
            return;
        }

        if (_activeResponseId is { } live)
        {
            await SupersedeAsync(input.Context, live, cancellationToken).ConfigureAwait(false);
        }

        var now = _time.GetUtcNow();
        var userEntry = new ConversationEntry(
            input.Context.EventId,
            NextSequence(),
            input.Context.EventId,
            ConversationRole.User,
            input.Text,
            ResponseId: null,
            EntryStatus.Completed,
            _snapshot.Mode,
            input.Text.Length,
            input.Text.Length,
            now);

        await PersistAsync(Append(userEntry) with { Status = SessionStatus.Created }, cancellationToken)
            .ConfigureAwait(false);

        var responseId = _ids.NewId();
        var entryId = _ids.NewId();
        var sequence = NextSequence();
        var assistant = new ConversationEntry(
            entryId,
            sequence,
            SourceEventId: null,
            ConversationRole.Assistant,
            string.Empty,
            responseId,
            EntryStatus.Streaming,
            _snapshot.Mode,
            0,
            0,
            now);

        _activeResponseId = responseId;
        _activeEntryId = entryId;
        _generated = string.Empty;
        _responseTerminal = false;
        _responseCts = new CancellationTokenSource();
        await PersistAsync(Append(assistant), cancellationToken).ConfigureAwait(false);

        await PublishAsync(
                new SessionOutput(
                    input.Context,
                    responseId,
                    new ResponseStartedOutput(entryId, sequence, "UserTurn")),
                cancellationToken)
            .ConfigureAwait(false);

        var request = new ModelRequest(
            responseId,
            [new ModelMessage(ModelRole.User, input.Text)],
            _snapshot.Definition.ConversationPolicy.MaxOutputTokens);

        BeginWork();
        var responseToken = _responseCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await PumpModelAsync(request, input.Context, responseToken).ConfigureAwait(false);
            }
            finally
            {
                EndWork();
            }
        }, CancellationToken.None);
    }

    private async Task PumpModelAsync(ModelRequest request, EventContext cause, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var evt in _languageModel.GenerateAsync(request, cancellationToken).ConfigureAwait(false))
            {
                var processed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var context = NewContext(cause.EventId);
                BeginWork();
                var admitted = _mailbox.Writer.TryWrite(new ModelResultReceived(context, request.ResponseId, evt, processed));
                if (!admitted)
                {
                    EndWork();
                    processed.TrySetResult();
                    break;
                }

                await processed.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            var processed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var failed = new ModelFailed(new ProviderFailure(ProviderErrorCode.Cancelled, "Generation cancelled."));
            var context = NewContext(cause.EventId);
            BeginWork();
            if (!_mailbox.Writer.TryWrite(new ModelResultReceived(context, request.ResponseId, failed, processed)))
            {
                EndWork();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Language model pump failed for {ResponseId}", request.ResponseId);
            var processed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var failed = new ModelFailed(new ProviderFailure(ProviderErrorCode.Unknown, "Generation failed."));
            BeginWork();
            if (!_mailbox.Writer.TryWrite(
                    new ModelResultReceived(NewContext(cause.EventId), request.ResponseId, failed, processed)))
            {
                EndWork();
            }
        }
    }

    private async Task HandleModelAsync(ModelResultReceived input, CancellationToken cancellationToken)
    {
        if (_activeResponseId != input.ResponseId || _responseTerminal)
        {
            return;
        }

        switch (input.Event)
        {
            case ModelTextDelta delta:
                var start = _generated.Length;
                _generated += delta.Text;
                await PublishAsync(
                        new SessionOutput(input.Context, input.ResponseId, new TextDeltaOutput(start, delta.Text)),
                        cancellationToken)
                    .ConfigureAwait(false);
                UpdateStreamingAssistant();
                break;
            case ModelCompleted:
                await CompleteAsync(input, failed: false, cancellationToken).ConfigureAwait(false);
                break;
            case ModelFailed:
                await CompleteAsync(input, failed: true, cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    private async Task HandleCancelAsync(CancelResponseReceived input, CancellationToken cancellationToken)
    {
        if (_activeResponseId != input.ResponseId)
        {
            return;
        }

        await SupersedeAsync(input.Context, input.ResponseId, cancellationToken).ConfigureAwait(false);
    }

    private async Task SupersedeAsync(EventContext context, Guid responseId, CancellationToken cancellationToken)
    {
        _responseCts?.Cancel();
        if (_responseTerminal)
        {
            ClearActive();
            return;
        }

        _responseTerminal = true;
        UpdateAssistant(EntryStatus.Interrupted, received: _generated.Length);
        await PersistAsync(_snapshot, cancellationToken).ConfigureAwait(false);
        await PublishAsync(
                new SessionOutput(context, responseId, new ResponseCompletedOutput(true, HeardTextEndExclusive: 0)),
                cancellationToken)
            .ConfigureAwait(false);
        ClearActive();
    }

    private async Task CompleteAsync(ModelResultReceived input, bool failed, CancellationToken cancellationToken)
    {
        _responseTerminal = true;
        var status = failed ? EntryStatus.Failed : EntryStatus.Completed;
        var received = _generated.Length;
        UpdateAssistant(status, received);
        await PersistAsync(_snapshot, cancellationToken).ConfigureAwait(false);
        if (!failed)
        {
            await PublishAsync(
                    new SessionOutput(input.Context, input.ResponseId, new TextCompletedOutput(received)),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        await PublishAsync(
                new SessionOutput(
                    input.Context,
                    input.ResponseId,
                    new ResponseCompletedOutput(failed, HeardTextEndExclusive: _snapshot.Mode == SessionMode.Text ? received : 0)),
                cancellationToken)
            .ConfigureAwait(false);
        ClearActive();
    }

    private void UpdateStreamingAssistant() => UpdateAssistant(EntryStatus.Streaming, _generated.Length);

    private void UpdateAssistant(EntryStatus status, int received)
    {
        if (_activeEntryId is not { } entryId)
        {
            return;
        }

        var entries = _snapshot.Entries.Select(entry =>
                entry.EntryId == entryId
                    ? entry with
                    {
                        Text = _generated,
                        Status = status,
                        ReceivedTextEndExclusive = received,
                        HeardTextEndExclusive = _snapshot.Mode == SessionMode.Text ? received : entry.HeardTextEndExclusive
                    }
                    : entry)
            .ToArray();
        _snapshot = _snapshot with { Entries = entries, UpdatedAt = _time.GetUtcNow() };
    }

    private void ClearActive()
    {
        _activeResponseId = null;
        _activeEntryId = null;
        _responseCts?.Dispose();
        _responseCts = null;
    }

    private async Task EndAsync(CancellationToken cancellationToken)
    {
        _snapshot = _snapshot with { Status = SessionStatus.Ended, UpdatedAt = _time.GetUtcNow() };
        await PersistAsync(_snapshot, cancellationToken).ConfigureAwait(false);
    }

    private SessionSnapshot Append(ConversationEntry entry)
    {
        var entries = _snapshot.Entries.Concat([entry]).ToArray();
        return _snapshot with
        {
            Entries = entries,
            UpdatedAt = _time.GetUtcNow()
        };
    }

    private long NextSequence() =>
        _snapshot.Entries.Count == 0 ? 1 : _snapshot.Entries[^1].Sequence + 1;

    private async Task PersistAsync(SessionSnapshot snapshot, CancellationToken cancellationToken)
    {
        var next = snapshot with { Revision = _snapshot.Revision + 1, UpdatedAt = _time.GetUtcNow() };
        await _store.SaveAsync(next, _snapshot.Revision, cancellationToken).ConfigureAwait(false);
        _snapshot = next;
    }

    private async Task PublishAsync(SessionOutput output, CancellationToken cancellationToken) =>
        await _output.PublishAsync(output, cancellationToken).ConfigureAwait(false);

    private EventContext NewContext(Guid? causationId = null)
    {
        var id = _ids.NewId();
        return new EventContext(id, SessionId, _epoch, _time.GetUtcNow(), id, causationId);
    }

    private void BeginWork()
    {
        lock (_idleGate)
        {
            _inflight++;
            if (_idle.Task.IsCompleted)
            {
                _idle = NewIdle();
            }
        }
    }

    private void EndWork()
    {
        lock (_idleGate)
        {
            _inflight--;
            if (_inflight == 0 && _mailbox.Reader.Count == 0)
            {
                _idle.TrySetResult();
            }
        }
    }

    private static TaskCompletionSource NewIdle() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static TaskCompletionSource CompletedIdle()
    {
        var idle = NewIdle();
        idle.TrySetResult();
        return idle;
    }
}
