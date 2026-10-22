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
            var inputs = await HasResourcePinChanges(db, run, ct).ConfigureAwait(false) ? await ResourcePinInputs(db, await ManagementReader(db, run, null, ct).ConfigureAwait(false), ct).ConfigureAwait(false) : null;
            var resources = inputs?.Snapshot.Resources ?? await SqliteAgentInstanceStore.ReadResourcesAsync(db, agent.Instance.InstanceId, ct).ConfigureAwait(false);
            var members = value.ResourceMembersJson is null ? new[] { new PreparedResourceReadMember(value.ResourceKey!, value.ResourceRevision!.Value) } : TenantApprovalRules.ResourceReadMembers(value);
            foreach (var member in members)
            {
                var pinned = run.Admission.Configuration!.Resources.SingleOrDefault(r => r.Key == member.Key);
                TenantResourceRevisionRules.Require(member.Key, member.Revision, run, agent, resources, inputs is null || pinned is null ? null : RetainedResourcePin(inputs, pinned));
            }
        }
        await TenantWorkspaceReadOperationRules.PermissionsAsync((await ReadTenantAsync(db, tenant, ct).ConfigureAwait(false))!, agent.Instance.InstanceId, value, agent.PrincipalId, new IamAuthorizer(this), ct).ConfigureAwait(false);
    }
}
