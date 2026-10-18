using AgentCore.Application.Security;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore
{
    private static async Task<bool> OwnsWorkspace(AgentCoreDbContext db, AccessScope scope, CancellationToken ct)
    {
        if (!TenantWorkspaceRules.TryParse(scope, out var working, out var id)) return false;
        var key = id.ToString("D");
        return working ? await OwnedSessions(db, scope.TenantId!.Value).AnyAsync(s => s.SessionId == key, ct).ConfigureAwait(false)
            : await OwnedAgents(db, scope.TenantId!.Value).AnyAsync(a => a.Instance.InstanceId == key, ct).ConfigureAwait(false);
    }
}
