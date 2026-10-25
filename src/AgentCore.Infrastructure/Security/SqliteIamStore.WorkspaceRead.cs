using AgentCore.Application.Security;
using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore
{
    private async ValueTask RequireWorkspaceReadOperation(AgentCoreDbContext db, TenantApprovalRequest request, AgentRun run, TenantAgent agent, CancellationToken ct)
    {
        await RequireWorkspaceReadSource(db, request.TenantId, request.Operation.WorkspaceRead!, run, agent, ct).ConfigureAwait(false);
    }
    private async ValueTask RequireWorkspaceReadSource(AgentCoreDbContext db, Guid tenant, PreparedWorkspaceRead value, AgentRun run, TenantAgent agent, CancellationToken ct)
    {
        var session = await ReadSandboxSession(db, tenant, value.SessionId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        var current = AgentCore.Application.Agents.InstanceSettingsResolver.Resolve(await TenantAgentDefinitionBaseline(db, agent, ct).ConfigureAwait(false), agent.Instance.SettingsOverrides);
        TenantWorkspaceReadOperationRules.Require(value, run, agent, current, session);
        if (value.AttachmentMembersJson is not null) foreach (var member in TenantApprovalRules.AttachmentReadMembers(value))
        {
            var item = await db.Set<AttachmentRecordRow>().AsNoTracking().SingleOrDefaultAsync(a => a.TenantId == tenant && a.SessionId == value.SessionId.ToString("D") && a.AttachmentId == member.AttachmentId.ToString("D") && a.ContentDeletedAtUtc == null, ct).ConfigureAwait(false);
            if (item is null || item.State != nameof(AttachmentState.Bound) || item.EntryId != member.EntryId.ToString("D") || item.Sha256Hex != member.Sha256Hex) throw new SecurityConflictException("attachment_source_changed");
        }
        if (value.ResourceKey is not null || value.ResourceMembersJson is not null)
        {
            var members = value.ResourceMembersJson is null ? new[] { new PreparedResourceReadMember(value.ResourceKey!, value.ResourceRevision!.Value) } : TenantApprovalRules.ResourceReadMembers(value);
            foreach (var member in members)
                await RequireResourcePin(db, member.Key, member.Revision, run, agent, ct).ConfigureAwait(false);
        }
        await TenantWorkspaceReadOperationRules.PermissionsAsync((await ReadTenantAsync(db, tenant, ct).ConfigureAwait(false))!, agent.Instance.InstanceId, value, TenantWorkspaceRules.Context(run), agent.PrincipalId, new IamAuthorizer(this), ct).ConfigureAwait(false);
    }
}
