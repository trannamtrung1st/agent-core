using AgentCore.Application.Security;
using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore
{
    private void RequireAttachmentReadOperation(TenantApprovalRequest request, AgentRun run, TenantAgent agent)
    {
        RequireAttachmentReadSource(request.TenantId, request.Operation.AttachmentRead!, run, agent);
    }
    private void RequireAttachmentReadSource(Guid tenant, PreparedAttachmentRead value, AgentRun run, TenantAgent agent)
    {
        var session = sessionBacking.ReadTenantSession(tenant, value.SessionId) ?? throw new AccessDeniedException("resource_unavailable");
        var item = attachmentBacking?.FindTenantAsync(tenant, value.SessionId, value.AttachmentId).Result ?? throw new AccessDeniedException("resource_unavailable");
        var current = AgentCore.Application.Agents.InstanceSettingsResolver.Resolve(TenantAgentDefinitionBaseline(agent), agent.Instance.SettingsOverrides);
        TenantAttachmentReadRules.Require(value, run, agent, current, session, item);
        TenantAttachmentReadRules.PermissionsAsync(_tenants[tenant], value, agent.PrincipalId, new IamAuthorizer(this), CancellationToken.None).GetAwaiter().GetResult();
    }
}
