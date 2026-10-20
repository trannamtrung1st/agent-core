using AgentCore.Application.Events;
using AgentCore.Application.Ports;
using AgentCore.Application.Execution;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Sessions;

public sealed partial class SessionRuntime
{
    private async Task RefuseProtectedAccountOutcomeAsync(EventContext context, CancellationToken ct)
    {
        var bound = _boundAgentRun!;
        var current = await _agentRuns.GetAsync(bound.Owner, bound.AgentRunId, ct).ConfigureAwait(false)
            ?? throw AgentCoreErrors.Persistence("The protected output's Run was not found.");
        if (!current.IsTerminal && current.Claim?.Generation == bound.Claim?.Generation)
            current = await _agentRuns.ApplyAsync(current.Owner, current.AgentRunId,
                new AgentRunCommand.Fail(current.Revision, _time.GetUtcNow(), current.Claim!.Generation, "protected-output-unavailable",
                    "Current account access no longer permits this result. Start new work after reviewing access.", false, null), ct).ConfigureAwait(false);
        // The failed atomic write saved no answer. Retain only durable history and fence late worker/persist messages.
        _tenantTerminalCommitted = true;
        _responseLifecycle = ResponseLifecycle.Superseded;
        _allowQueuedSuffixAutoDispatch = false;
        _snapshot = _durableSnapshot;
        _boundAgentRun = current;
        ClearAgentRunTerminalPending();
        try
        {
            if (_activeResponseId is { } response)
                await TerminalizeActiveResponseAsync(context, response, ct, "policyChanged", requestPersist: false).ConfigureAwait(false);
            else { _responseTerminal = true; _responseCts?.Cancel(); _brainEvaluationCts?.Cancel(); _ttsCts?.Cancel(); }
            await PublishAsync(new(context, current.ResponseId, new ErrorOutput("Session", "ProtectedOutputUnavailable",
                "Current account access no longer permits this result. Start new work after reviewing access.", false, null)), ct).ConfigureAwait(false);
        }
        finally { _boundAgentRun = null; }
    }
}
