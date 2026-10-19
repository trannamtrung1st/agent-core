using AgentCore.Application.Ports;
using AgentCore.Application.Security;

namespace AgentCore.Application.Execution;

// Same coordinator and Session Runtime, with tenant-scoped persistence/dispatch bindings.
public sealed class TenantRunExecution(ITenantRuntimeStore storage, ITenantRunStore runs, ITenantRunDispatcher dispatcher,
    IIdGenerator ids, TimeProvider time, TenantBackgroundCompletionService? completions = null) : ITenantRunExecution
{
    private readonly AgentRunCoordinator _coordinator = new(new TenantAgentRunStore(storage, runs, ids), dispatcher, ids, time);
    public async ValueTask<int> ExecuteRunnableAsync(int limit, CancellationToken ct = default)
    {
        if (completions is not null) await completions.AdmitPendingAsync(limit, ct).ConfigureAwait(false);
        return await _coordinator.ExecuteRunnableAsync(limit, ct).ConfigureAwait(false);
    }
}
