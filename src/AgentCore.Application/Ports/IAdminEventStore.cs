using AgentCore.Application.Admin;

namespace AgentCore.Application.Ports;

public interface IAdminEventStore
{
    ValueTask<AdminEvent?> TryGetByOperationIdAsync(
        Guid operationId,
        CancellationToken cancellationToken = default);

    ValueTask<AdminEvent> AppendAsync(AdminEventAppend append, CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<AdminEvent>> ListAsync(
        AdminEventListQuery query,
        CancellationToken cancellationToken = default);
}
