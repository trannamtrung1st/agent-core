using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore
{
    private IReadOnlySet<AccessScope> ChildCatalog(Guid tenant, Guid instance, TenantAgentConfigurationChange? change, CancellationToken ct)
    {
        lock (instances.CredentialGate) lock (definitions.TenantGate)
        {
            var snapshot = ReadTenantAgentConfigurationAsync(tenant, instance, ct).Result;
            if (snapshot is null) return new HashSet<AccessScope>();
            if (change is not null) snapshot = TenantAgentChildScopes.After(snapshot, change);
            var owner = snapshot.Agent.Instance; AgentDefinition? definition;
            IReadOnlyList<AgentDefinitionPublicationResource> resources;
            if (Guid.TryParse(owner.DefinitionId, out var id))
            {
                definition = definitions.TenantPublication(tenant, id, owner.ActiveVersion)?.Payload;
                resources = definitions.TenantPublicationResources(tenant, id, owner.ActiveVersion);
            }
            else
            {
                definition = builtIns?.GetAsync(owner.DefinitionId, owner.ActiveVersion, ct).GetAwaiter().GetResult();
                resources = definition is not null && inheritedResources is not null ? inheritedResources.ListAsync(definition, ct).GetAwaiter().GetResult() : [];
            }
            return TenantAgentChildScopes.Catalog(tenant, snapshot, definition, resources);
        }
    }
}
