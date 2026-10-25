using AgentCore.Application.Security;
using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore
{
    private void RequireWorkspaceReadOperation(TenantApprovalRequest request, AgentRun run, TenantAgent agent)
    {
        RequireWorkspaceReadSource(request.TenantId, request.Operation.WorkspaceRead!, run, agent);
    }
    private void RequireWorkspaceReadSource(Guid tenant, PreparedWorkspaceRead value, AgentRun run, TenantAgent agent)
    {
        var session = sessionBacking.ReadTenantSession(tenant, value.SessionId) ?? throw new AccessDeniedException("resource_unavailable");
        var current = AgentCore.Application.Agents.InstanceSettingsResolver.Resolve(TenantAgentDefinitionBaseline(agent), agent.Instance.SettingsOverrides);
        TenantWorkspaceReadOperationRules.Require(value, run, agent, current, session);
        if (value.AttachmentMembersJson is not null) foreach (var member in TenantApprovalRules.AttachmentReadMembers(value))
        {
            var item = attachmentBacking?.RetainedTenant(tenant, value.SessionId, member.AttachmentId);
            if (item is not { State: AttachmentState.Bound } || item.EntryId != member.EntryId || item.Sha256Hex != member.Sha256Hex) throw new SecurityConflictException("attachment_source_changed");
        }
        if (value.ResourceKey is not null || value.ResourceMembersJson is not null)
        {
            var members = value.ResourceMembersJson is null ? new[] { new PreparedResourceReadMember(value.ResourceKey!, value.ResourceRevision!.Value) } : TenantApprovalRules.ResourceReadMembers(value);
            foreach (var member in members)
                RequireResourcePin(member.Key, member.Revision, run, agent, CancellationToken.None);
        }
        TenantWorkspaceReadOperationRules.PermissionsAsync(_tenants[tenant], agent.Instance.InstanceId, value, TenantWorkspaceRules.Context(run), agent.PrincipalId, new IamAuthorizer(this), CancellationToken.None).GetAwaiter().GetResult();
    }
    private bool WorkspaceApprovalVisible(TenantApprovalRequest request, TenantWorkspaceReadApprovalVisibility? visible, TenantAttachmentReadApprovalVisibility? attachmentVisible)
    {
        var value = request.Operation.WorkspaceRead!;
        var session = sessionBacking.ReadTenantSession(request.TenantId, value.SessionId);
        var agent = FindTenantAgentAsync(request.TenantId, request.Operation.InstanceId).Result;
        return session is not null && session.Snapshot.DurablyDeletedAt is null && agent is not null && agent.PrincipalId == request.ExecutorPrincipalId
            && session.Owner.AgentInstanceId == agent.Instance.InstanceId && session.Owner.ExecutorPrincipalId == agent.PrincipalId
            && (value.AttachmentMembersJson is null || TenantApprovalRules.AttachmentReadMembers(value).All(m =>
                attachmentBacking?.RetainedTenant(request.TenantId, value.SessionId, m.AttachmentId) is { State: AttachmentState.Bound } item
                && item.EntryId == m.EntryId && item.Sha256Hex == m.Sha256Hex && (attachmentVisible?.Attachments is null || attachmentVisible.Attachments.Contains(m.AttachmentId)))
                && (attachmentVisible?.Sessions is null || attachmentVisible.Sessions.Contains(value.SessionId)))
            && (value.ResourceMembersJson is null || CollectionVisible(request, visible))
            && (value.ResourceKey is null || OwnsResourceAsync(TenantResourceScopes.Source(request.TenantId, agent.Instance.InstanceId, value.ResourceKey)).Result)
            && (value.AttachmentMembersJson is not null || value.ResourceMembersJson is not null || (value.ResourceKey is null ? visible?.Workspaces is null || visible.Workspaces.Contains(request.ResourceId)
                : visible?.Resources is null || visible.Resources.Contains(request.ResourceId)))
            && (value.AttachmentMembersJson is not null || value.Working || (value.ResourceKey is not null || value.ResourceMembersJson is not null
                ? visible?.ResourceAgents is null || visible.ResourceAgents.Contains(agent.Instance.InstanceId)
                : visible?.HomeSpaces is null || visible.HomeSpaces.Contains(TenantWorkspaceRules.ForCaptured(request.TenantId, request.SessionId, AgentCore.Application.Tools.TenantWorkspaceReadContract.Parse(value.ToolName, value.ArgumentsJson).Path, TenantWorkspaceRules.Context(request.Operation)).OwnerId)))
            && (visible?.Sessions is null || visible.Sessions.Contains(value.SessionId));
    }

    private bool CollectionVisible(TenantApprovalRequest request, TenantWorkspaceReadApprovalVisibility? visible)
    {
        var owned = ChildCatalog(request.TenantId, request.Operation.InstanceId, null, CancellationToken.None);
        return TenantApprovalRules.ResourceReadMembers(request.Operation.WorkspaceRead!).All(member => {
            var scope = TenantResourceScopes.Source(request.TenantId, request.Operation.InstanceId, member.Key);
            return (TenantResourceScopes.TrySpace(scope, out _, out _) ? OwnsResourceAsync(scope).Result : owned.Contains(scope)) && (visible?.Resources is null || visible.Resources.Contains(scope.ResourceId!));
        });
    }

}
