using AgentCore.Application.Events;
using AgentCore.Application.Observability;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Sessions;

public sealed partial class SessionRuntime
{
    private sealed record ContinueInChatReceived(EventContext Context, TaskCompletionSource<bool> Committed) : SessionInput(Context);

    public async Task<bool> ContinueInChatAsync(CancellationToken ct = default)
    {
        var committed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        BeginWork();
        if (!Enqueue(new ContinueInChatReceived(NewContext(), committed), urgent: true)) return false;
        return await committed.Task.WaitAsync(ct).ConfigureAwait(false);
    }

    private void HandleContinueInChat(ContinueInChatReceived input)
    {
        if (_snapshot.DurablyDeletedAt is not null || _snapshot.ArchivedAt is not null || SessionLifecycle.IsTerminal(_snapshot.LifecycleStatus)
            || _snapshot.Status is SessionStatus.Ended or SessionStatus.Ending)
        { input.Committed.TrySetResult(false); return; }
        if (_snapshot.Surfaces.HasFlag(SessionSurface.ChatList)) { input.Committed.TrySetResult(true); return; }
        _snapshot = _snapshot with { Surfaces = SessionOrigin.ContinueInChat(_snapshot.Surfaces) };
        RequestPersist(_snapshot, then: _ => { RuntimeTelemetry.RecordBackgroundSession("foregrounded"); return Task.CompletedTask; }, ended: input.Committed);
    }
}
