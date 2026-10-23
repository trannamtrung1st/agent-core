using AgentCore.Application.Security;
using AgentCore.Application.Agents;
using AgentCore.Domain.Conversation;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore
{
    private void RequirePublicWebSources(AgentRun run,CancellationToken ct)
    {
        try
        {
            var tools=TenantPublicWebReadRules.StoredTools(run); if(tools.Count==0)return;
            var tenant=run.Owner.TenantId!.Value; var state=_tenants[tenant]; var iam=new IamAuthorizer(this);
            var agent=FindTenantAgentAsync(tenant,run.AgentInstanceId,ct).Result ?? throw new AccessDeniedException("executor_unavailable");
            var session=sessionBacking.ReadTenantSession(tenant,run.SessionId) ?? throw new AccessDeniedException("session_unavailable");
            TenantSessionRules.RequireExecutor(state,agent); RequireTenantExecutionSourcesAsync(run,state,iam,this,ct).GetAwaiter().GetResult();
            TenantPublicWebReadRules.Require(run,agent,InstanceSettingsResolver.Resolve(TenantAgentDefinitionBaseline(agent),agent.Instance.SettingsOverrides),session,tools);
            TenantPublicWebReadRules.PermissionsAsync(state,run,agent,iam,ct).AsTask().GetAwaiter().GetResult();
        }
        catch(Exception e) when(e is AccessDeniedException or SecurityConflictException or ArgumentException or System.Text.Json.JsonException or InvalidOperationException)
        { throw new AccessDeniedException("public_web_output_unavailable"); }
    }
}
