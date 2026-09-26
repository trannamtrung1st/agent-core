using AgentCore.Application.Agents;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Ports;

public interface IRoleKnowledgeContentResolver
{
    ValueTask<string?> ReadContentAsync(
        AgentDefinition definition,
        string identity,
        CancellationToken cancellationToken = default);
}

public sealed class FileOnlyRoleKnowledgeContentResolver(IApprovedKnowledgeCatalog catalog) : IRoleKnowledgeContentResolver
{
    public ValueTask<string?> ReadContentAsync(
        AgentDefinition definition,
        string identity,
        CancellationToken cancellationToken = default) =>
        catalog.ReadContentAsync(identity, cancellationToken);
}
