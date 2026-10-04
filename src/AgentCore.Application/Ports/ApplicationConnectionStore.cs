using AgentCore.Domain.Connections;

namespace AgentCore.Application.Ports;

public interface IApplicationConnectionStore
{
    ValueTask<ApplicationConnection?> GetByAgentAsync(
        Guid agentInstanceId,
        CancellationToken cancellationToken = default);

    ValueTask<ApplicationConnection> SaveAsync(
        ApplicationConnection connection,
        long expectedRevision,
        CancellationToken cancellationToken = default);
}
