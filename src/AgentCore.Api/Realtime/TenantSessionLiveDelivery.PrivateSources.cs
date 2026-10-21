using AgentCore.Application.Execution;
using AgentCore.Domain.Conversation;

namespace AgentCore.Api.Realtime;

public sealed partial class TenantSessionLiveDelivery
{
    private async ValueTask<bool> CanReleasePrivateSourcesAsync(AgentRun run, CancellationToken ct)
    {
        var state = AgentRunToolCallCheckpoint.ReadExecutionState(run.Checkpoint);
        if (state.TenantProfileSource is null && state.TenantMemorySources is not { Count: > 0 }) return true;
        if (state.TenantProfileSource is not null && profiles is null || state.TenantMemorySources is { Count: > 0 } && memories is null) return false;
        try
        {
            if (state.TenantProfileSource is { } source) await profiles!.RequireAsync(run, source, ct).ConfigureAwait(false);
            if (state.TenantMemorySources is { Count: > 0 } learned) await memories!.RequireAsync(run, learned, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception error) when (error is AgentCore.Application.Security.AccessDeniedException or AgentCore.Application.Security.SecurityConflictException) { return false; }
    }
}
