using AgentCore.Application.Ports;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Agents;

public sealed class DefinitionPublicationResourceReader(IAgentDefinitionResourceAdminStore resources)
{
    public async ValueTask<IReadOnlyList<AgentDefinitionPublicationResource>> ListAsync(
        string definitionId,
        int version,
        CancellationToken cancellationToken = default) =>
        await resources.ListPublicationResourcesAsync(definitionId, version, cancellationToken).ConfigureAwait(false);

    public async ValueTask<AgentDefinitionPublicationResource?> FindByLogicalPathAsync(
        string definitionId,
        int version,
        string logicalPath,
        CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeLogicalPath(logicalPath);
        var items = await ListAsync(definitionId, version, cancellationToken).ConfigureAwait(false);
        return items.FirstOrDefault(item =>
            string.Equals(NormalizeLogicalPath(item.LogicalPath), normalized, StringComparison.Ordinal));
    }

    public async ValueTask<byte[]?> ReadContentAsync(
        AgentDefinitionPublicationResource resource,
        CancellationToken cancellationToken = default) =>
        await resources.ReadPublicationResourceContentAsync(
                resource.DefinitionId,
                resource.Version,
                resource.ResourceId,
                cancellationToken)
            .ConfigureAwait(false);

    public static string NormalizeLogicalPath(string logicalPath) =>
        logicalPath.Replace('\\', '/').Trim().TrimStart('/');

}
