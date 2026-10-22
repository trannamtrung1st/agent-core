using AgentCore.Application.Security;
using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore
{
    private async ValueTask RequireAttachmentReadOperation(AgentCoreDbContext db, TenantApprovalRequest request, AgentRun run, TenantAgent agent, CancellationToken ct)
    {
        await RequireAttachmentReadSource(db, request.TenantId, request.Operation.AttachmentRead!, run, agent, ct).ConfigureAwait(false);
    }
    private async ValueTask RequireAttachmentReadSource(AgentCoreDbContext db, Guid tenant, PreparedAttachmentRead value, AgentRun run, TenantAgent agent, CancellationToken ct)
    {
        var session = await ReadSandboxSession(db, tenant, value.SessionId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        var item = await SqliteAttachmentStore.Owned(db, tenant, value.SessionId).SingleOrDefaultAsync(a => a.AttachmentId == value.AttachmentId.ToString("D"), ct).ConfigureAwait(false)
            ?? throw new AccessDeniedException("resource_unavailable");
        var current = AgentCore.Application.Agents.InstanceSettingsResolver.Resolve(await TenantAgentDefinitionBaseline(db, agent, ct).ConfigureAwait(false), agent.Instance.SettingsOverrides);
        TenantAttachmentReadRules.Require(value, run, agent, current, session, SqliteAttachmentStore.Domain(item));
        await TenantAttachmentReadRules.PermissionsAsync((await ReadTenantAsync(db, tenant, ct).ConfigureAwait(false))!, value, agent.PrincipalId, new IamAuthorizer(this), ct).ConfigureAwait(false);
    }
}
