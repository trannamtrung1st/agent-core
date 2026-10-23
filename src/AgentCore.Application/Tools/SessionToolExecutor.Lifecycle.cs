using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Tools;

public sealed partial class SessionToolExecutor
{
    internal ValueTask<bool> CleanupTerminalWorkingAsync(Guid sessionId, CancellationToken ct) =>
        workingCleanup?.CleanupAsync(sessionId, ct) ?? ValueTask.FromResult(false);
    internal async ValueTask<bool> FinalizeRequestResourcesAsync(Guid sessionId, CancellationToken ct)
    {
        var lease = sandboxStore is null ? null : await sandboxStore.GetLeaseAsync(sessionId, ct);
        if (lease is null || lease.State == AgentCore.Domain.Conversation.SandboxLeaseState.Terminated) return true;
        if (lease.ActiveExecutionId is not null || lease.State != AgentCore.Domain.Conversation.SandboxLeaseState.Ready || sandboxService is null) return false;
        await sandboxService.StopSessionAsync(sessionId, ct, lease.Generation, lease.LastExecutionId, true);
        return (await sandboxStore!.GetLeaseAsync(sessionId, ct))?.State == AgentCore.Domain.Conversation.SandboxLeaseState.Terminated;
    }
}
