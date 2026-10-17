namespace AgentCore.Application.Ports;

public interface ITenantRunDispatcher : IAgentRunDispatcher;
public interface ITenantRunExecution
{
    ValueTask<int> ExecuteRunnableAsync(int limit, CancellationToken ct = default);
}
