using AgentCore.Application.Execution;
using AgentCore.Domain.Conversation;

namespace AgentCore.Api.Realtime;

public sealed partial class TenantSessionLiveDelivery
{
    private async ValueTask<bool> CanReleaseProfileAsync(AgentRun run, CancellationToken ct)
    {
        var source = AgentRunToolCallCheckpoint.ReadExecutionState(run.Checkpoint).TenantProfileSource;
        if (source is null) return true;
        if (profiles is null) return false;
        try { await profiles.RequireAsync(run, source, ct).ConfigureAwait(false); return true; }
        catch (Exception error) when (error is AgentCore.Application.Security.AccessDeniedException or AgentCore.Application.Security.SecurityConflictException) { return false; }
    }
}
