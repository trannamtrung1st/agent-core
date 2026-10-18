using AgentCore.Application.Security;
using AgentCore.Domain.Security;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore
{
    private bool OwnsWorkspace(AccessScope scope, CancellationToken ct)
    {
        if (!TenantWorkspaceRules.TryParse(scope, out var working, out var id)) return false;
        return working ? sessionBacking.ReadTenantSession(scope.TenantId!.Value, id) is not null
            : FindTenantAgentAsync(scope.TenantId!.Value, id, ct).Result is not null;
    }
}
