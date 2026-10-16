using AgentCore.Application.Security;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore
{
    private bool OwnsDefinitionResource(Guid tenant, DefinitionResourceReference reference)
    {
        lock (definitions.TenantGate)
        {
            if (reference.DraftId is { } draft)
            {
                var snapshot = definitions.TenantSnapshot(tenant, draft);
                return snapshot?.Draft.DefinitionId == reference.DefinitionId.ToString("D") && snapshot.Resources.Any(r => r.ResourceId == reference.ResourceId);
            }
            return definitions.TenantPublicationResources(tenant, reference.DefinitionId, reference.Version!.Value).Any(r => r.ResourceId == reference.ResourceId);
        }
    }
}
