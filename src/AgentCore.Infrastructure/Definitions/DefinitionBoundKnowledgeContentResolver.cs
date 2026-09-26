using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Domain.Definitions;
using System.Text;

namespace AgentCore.Infrastructure.Definitions;

public sealed class DefinitionBoundKnowledgeContentResolver(
    IApprovedKnowledgeCatalog fileCatalog,
    IAgentDefinitionAdminStore admin,
    DefinitionPublicationResourceReader publicationResources) : IRoleKnowledgeContentResolver
{
    public async ValueTask<string?> ReadContentAsync(
        AgentDefinition definition,
        string identity,
        CancellationToken cancellationToken = default)
    {
        var publication = await admin.GetPublicationAsync(definition.Id, definition.Version, cancellationToken)
            .ConfigureAwait(false);
        if (publication is null)
        {
            return await fileCatalog.ReadContentAsync(identity, cancellationToken).ConfigureAwait(false);
        }

        var logicalPath = DefinitionPublicationResourceReader.NormalizeLogicalPath($"knowledge/{identity}");
        var resource = await publicationResources.FindByLogicalPathAsync(
                definition.Id,
                definition.Version,
                logicalPath,
                cancellationToken)
            .ConfigureAwait(false);
        if (resource is null)
        {
            return null;
        }

        var bytes = await publicationResources.ReadContentAsync(resource, cancellationToken).ConfigureAwait(false);
        return bytes is null ? null : Encoding.UTF8.GetString(bytes);
    }
}
