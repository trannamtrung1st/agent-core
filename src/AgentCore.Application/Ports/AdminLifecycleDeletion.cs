using AgentCore.Application.Admin;

namespace AgentCore.Application.Ports;

public interface IAdminLifecycleDeletion
{
    ValueTask DeleteInstanceAsync(AdminInstanceDeleteCommand command, CancellationToken cancellationToken = default);

    ValueTask DeleteDefinitionAsync(AdminDefinitionDeleteCommand command, CancellationToken cancellationToken = default);
}
