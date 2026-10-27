using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Ports;

public interface IAgentRunAuthority
{
    ValueTask<AgentDefinition?> CurrentDefinitionAsync(AgentRun run, CancellationToken cancellationToken = default);
}
public interface ITaskRunBudgetAuthority
{
    ValueTask<TaskExecutionAllowance?> TaskAllowanceAsync(AgentRun run, CancellationToken ct = default);
}
