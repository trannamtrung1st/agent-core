using AgentCore.Application.Admin;

namespace AgentCore.Application.Ports;

public interface IAdminEventStore
{
    ValueTask<AdminEvent> AppendAsync(AdminEventAppend append, CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<AdminEvent>> ListAsync(
        AdminEventListQuery query,
        CancellationToken cancellationToken = default);
}
