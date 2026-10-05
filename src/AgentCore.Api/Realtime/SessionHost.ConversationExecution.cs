using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;

namespace AgentCore.Api.Realtime;

public sealed partial class SessionHost
{
    public async ValueTask<bool> DispatchConversationTurnAsync(
        ConversationTurnExecution execution,
        CancellationToken cancellationToken = default)
    {
        if (_live.TryGetValue(execution.SessionId, out var live))
        {
            return await live.Runtime
                .DispatchConversationExecutionAsync(execution.ExecutionId, headless: false, cancellationToken)
                .ConfigureAwait(false);
        }

        return await RunHeadlessConversationTurnAsync(execution, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<bool> RunHeadlessConversationTurnAsync(
        ConversationTurnExecution execution,
        CancellationToken cancellationToken)
    {
        var snapshot = await _sessions.LoadRuntimeAsync(execution.SessionId, cancellationToken).ConfigureAwait(false);
        await using var runtime = _factory.Create(snapshot, SilentSessionOutput.Instance);
        if (!await runtime.DispatchConversationExecutionAsync(execution.ExecutionId, headless: true, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        await runtime.WaitUntilAcceptedConversationWorkSettledAsync(cancellationToken).ConfigureAwait(false);
        await runtime.WaitUntilIdleAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }
}
