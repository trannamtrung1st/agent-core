using AgentCore.Application.Admin;

namespace AgentCore.Application.Ports;

public interface IAdminLifecycleDeletion
{
    ValueTask DeleteInstanceAsync(AdminInstanceDeleteCommand command, CancellationToken cancellationToken = default);

    /// <summary>Retry byte cleanup for committed instance-deletion receipts; never purge a live owner.</summary>
    ValueTask RecoverWorkspaceCleanupAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

    ValueTask DeleteDefinitionAsync(AdminDefinitionDeleteCommand command, CancellationToken cancellationToken = default);
}
