using AgentCore.Application.Security;
using AgentCore.Domain.Security;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore
{
    private bool OwnsWorkspace(AccessScope scope, CancellationToken ct)
    {
        if (!TenantWorkspaceRules.TryParse(scope, out var working, out var id)) return false;
        return working ? sessionBacking.ReadTenantSession(scope.TenantId!.Value, id) is not null
            : _spaces.TryGetValue((scope.TenantId!.Value, id), out var space) && space.DeletedAt is null;
    }
}
