using AgentCore.Application.Ports;

namespace AgentCore.Application.Admin;

public sealed class AdminHistoryService(IAdminEventStore events)
{
    public ValueTask<IReadOnlyList<AdminEvent>> ListEventsAsync(
        AdminEventListQuery query,
        CancellationToken cancellationToken = default) =>
        events.ListAsync(query, cancellationToken);
}
