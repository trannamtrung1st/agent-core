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
        var value = request.Operation.WorkspaceRead!;
        var session = await ReadSandboxSession(db, request.TenantId, value.SessionId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        var current = AgentCore.Application.Agents.InstanceSettingsResolver.Resolve(await TenantAgentDefinitionBaseline(db, agent, ct).ConfigureAwait(false), agent.Instance.SettingsOverrides);
        TenantWorkspaceReadOperationRules.Require(value, run, agent, current, session);
        if (value.AttachmentMembersJson is not null) foreach (var member in TenantApprovalRules.AttachmentReadMembers(value))
        {
            var item = await db.Set<AttachmentRecordRow>().AsNoTracking().SingleOrDefaultAsync(a => a.TenantId == request.TenantId && a.SessionId == value.SessionId.ToString("D") && a.AttachmentId == member.AttachmentId.ToString("D") && a.ContentDeletedAtUtc == null, ct).ConfigureAwait(false);
            if (item is null || item.State != nameof(AttachmentState.Bound) || item.EntryId != member.EntryId.ToString("D") || item.Sha256Hex != member.Sha256Hex) throw new SecurityConflictException("attachment_source_changed");
        }
        if (value.ResourceKey is not null || value.ResourceMembersJson is not null) TenantWorkspaceReadOperationRules.RequireResourceRevision(value, run, agent, await SqliteAgentInstanceStore.ReadResourcesAsync(db, agent.Instance.InstanceId, ct).ConfigureAwait(false));
        await TenantWorkspaceReadOperationRules.PermissionsAsync((await ReadTenantAsync(db, request.TenantId, ct).ConfigureAwait(false))!, agent.Instance.InstanceId, value, agent.PrincipalId, new IamAuthorizer(this), ct).ConfigureAwait(false);
    }
}
