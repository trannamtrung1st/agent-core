using AgentCore.Application.Ports;
using AgentCore.Application.Security;

namespace AgentCore.Application.Execution;

// Same coordinator and Session Runtime, with tenant-scoped persistence/dispatch bindings.
public sealed class TenantRunExecution(ITenantRuntimeStore storage, ITenantRunStore runs, ITenantRunDispatcher dispatcher,
    IIdGenerator ids, TimeProvider time) : ITenantRunExecution
{
    private readonly AgentRunCoordinator _coordinator = new(new TenantAgentRunStore(storage, runs, ids), dispatcher, ids, time);
    public ValueTask<int> ExecuteRunnableAsync(int limit, CancellationToken ct = default) => _coordinator.ExecuteRunnableAsync(limit, ct);
}
