using AgentCore.Application.Events;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Sessions;

public sealed partial class SessionRuntime
{
    // Transient trusted Session context: never a workspace file or Agent Instance preference.
    private string _workspaceCwd = "/home";

    private async Task<string?> RequestWorkspaceCwdAsync(EventContext cause, Guid responseId, string? next, CancellationToken ct)
    {
        var completed = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        BeginWork();
        if (!TryMailbox(new WorkspaceCwdRequested(WorkerContext(cause), responseId, cause.Epoch, next, completed)))
        { EndWork(); return null; }
        return await completed.Task.WaitAsync(ct).ConfigureAwait(false);
    }

    private async Task HandleWorkspaceCwdAsync(WorkspaceCwdRequested input, CancellationToken ct)
    {
        if (_deactivated || _responseTerminal || _activeResponseId != input.ResponseId
            || _epoch != input.Epoch || input.Context.Epoch != _epoch
            || !await OwnsWorkerAsync(input.Context, input.ResponseId, ct).ConfigureAwait(false))
        { input.Completed.TrySetResult(null); return; }
        var permitted = await _tools.RestrictExecutionDefinitionAsync(ExecutionDefinition, _snapshot.AgentInstanceId, ct);
        var policy = permitted.StateIsolationPolicy ?? StateIsolationPolicy.Default;
        _workspaceCwd = AgentCore.Application.Agents.StateIsolationAccess.Cwd(policy, _workspaceCwd);
        if (input.Next is not null) _workspaceCwd = AgentCore.Application.Agents.StateIsolationAccess.Cwd(policy, input.Next);
        input.Completed.TrySetResult(_workspaceCwd);
    }
}
