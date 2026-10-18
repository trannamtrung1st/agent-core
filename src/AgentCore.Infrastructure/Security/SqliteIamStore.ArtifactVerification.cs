using AgentCore.Application.Security;
using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore
{
    private async ValueTask RequireArtifactVerificationOperation(AgentCoreDbContext db, TenantApprovalRequest request, AgentRun run, TenantAgent agent, CancellationToken ct)
    {
        var value = request.Operation.ArtifactVerification!;
        var session = await ReadSandboxSession(db, request.TenantId, value.SessionId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        var item = await SqliteArtifactStore.Owned(db, request.TenantId, value.SessionId).SingleOrDefaultAsync(a => a.ArtifactId == value.ArtifactId.ToString("D"), ct).ConfigureAwait(false)
            ?? throw new AccessDeniedException("resource_unavailable");
        var current = AgentCore.Application.Agents.InstanceSettingsResolver.Resolve(await TenantAgentDefinitionBaseline(db, agent, ct).ConfigureAwait(false), agent.Instance.SettingsOverrides);
        TenantArtifactVerificationRules.Require(value, run, agent, current, session, SqliteArtifactStore.FromRow(item));
        await TenantArtifactVerificationRules.PermissionsAsync((await ReadTenantAsync(db, request.TenantId, ct).ConfigureAwait(false))!, value, agent.PrincipalId, new IamAuthorizer(this), ct).ConfigureAwait(false);
    }
}
