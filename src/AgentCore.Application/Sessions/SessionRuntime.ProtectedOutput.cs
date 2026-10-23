using AgentCore.Application.Events;
using AgentCore.Application.Ports;
using AgentCore.Application.Execution;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Sessions;

public sealed partial class SessionRuntime
{
    private async Task RefuseProtectedOutputAsync(EventContext context, CancellationToken ct, string source)
    {
        var subject = source switch {
            "public_web" => "public web source", "automation" => "Automation source", "experience" => "Experience", "continuity" => "historical source", "memory" => "learned-memory",
            "profile" => "Profile", "conversation" => "conversation", _ => "account"
        };
        var message = $"Current {subject} access{(source is "profile" or "conversation" or "account" ? "" : " or state")} no longer permits this result. Start new work after reviewing access.";
        var bound = _boundAgentRun!;
        var current = await _agentRuns.GetAsync(bound.Owner, bound.AgentRunId, ct).ConfigureAwait(false)
            ?? throw AgentCoreErrors.Persistence("The protected output's Run was not found.");
        if (!current.IsTerminal && current.Claim?.Generation == bound.Claim?.Generation)
            current = await _agentRuns.ApplyAsync(current.Owner, current.AgentRunId,
                new AgentRunCommand.Fail(current.Revision, _time.GetUtcNow(), current.Claim!.Generation, "protected-output-unavailable",
                    message, false, null), ct).ConfigureAwait(false);
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
                message, false, null)), ct).ConfigureAwait(false);
        }
        finally { _boundAgentRun = null; }
    }
}
