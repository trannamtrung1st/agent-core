using AgentCore.Application.Admin;
using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Definitions;
using System.Text;

namespace AgentCore.Infrastructure.Definitions;

public sealed class DefinitionBoundKnowledgeContentResolver(
    IApprovedKnowledgeCatalog fileCatalog,
    IBuiltInAgentDefinitionStore builtIns,
    IAgentDefinitionAdminStore admin,
    DefinitionPublicationResourceReader publicationResources) : IRoleKnowledgeContentResolver
{
    public async ValueTask<string?> ReadContentAsync(
        AgentDefinition definition,
        string identity,
        CancellationToken cancellationToken = default)
    {
        var reference = RoleEnvironments.Of(definition).KnowledgeList.SingleOrDefault(s => s.Identity == identity);
        var pinned = definition.ExecutionResources?.SingleOrDefault(r => r.Key == identity
            || r.Key.StartsWith("definition:", StringComparison.Ordinal) && reference is not null && r.LogicalPath == KnowledgeSourcePaths.ResolveBackingPath(reference));
        if (pinned is not null)
        {
            if (!DefinitionResourcePolicies.IsTextualKnowledgeMediaType(pinned.MediaType)) throw AgentCoreErrors.Forbidden("Binary resources cannot be retrieved as knowledge text.");
            return Encoding.UTF8.GetString(await publicationResources.ReadPinnedAsync(pinned, cancellationToken));
        }
        // A removed binding must not fall back to a built-in file. Historical Runs can still
        // carry approved file bindings from before the resource manifest included them.
        if (definition.ExecutionResources is not null && reference is null || identity.StartsWith("instance:", StringComparison.Ordinal)) return null;
        var effectiveSource = await AgentDefinitionExactSourceResolver.ResolveAsync(builtIns, admin,
            definition.Id, definition.Version, cancellationToken).ConfigureAwait(false);
        if (effectiveSource?.Kind != DefinitionDraftSourceKind.ForkDurable)
        {
            return await fileCatalog.ReadContentAsync(identity, cancellationToken).ConfigureAwait(false);
        }

        var source = RoleEnvironments.Of(definition).KnowledgeList
            .FirstOrDefault(item => string.Equals(item.Identity, identity, StringComparison.Ordinal));
        var logicalPath = DefinitionPublicationResourceReader.NormalizeLogicalPath(
            KnowledgeSourcePaths.ResolveBackingPath(identity, source?.ResourcePath));
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

        if (!DefinitionResourcePolicies.IsTextualKnowledgeMediaType(resource.MediaType))
        {
            throw AgentCoreErrors.Forbidden("Knowledge content is not available as text for this resource.");
        }

        var bytes = await publicationResources.ReadContentAsync(resource, cancellationToken).ConfigureAwait(false);
        return bytes is null ? null : Encoding.UTF8.GetString(bytes);
    }
}
