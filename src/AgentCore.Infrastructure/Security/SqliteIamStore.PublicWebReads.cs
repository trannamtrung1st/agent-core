using AgentCore.Application.Security;
using AgentCore.Application.Agents;
using AgentCore.Domain.Conversation;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore
{
    private async Task RequirePublicWebSources(AgentCoreDbContext db,AgentRun run,CancellationToken ct)
    {
        try
        {
            var tools=TenantPublicWebReadRules.StoredTools(run); if(tools.Count==0)return;
            var tenant=run.Owner.TenantId!.Value; var state=(await ReadTenantAsync(db,tenant,ct).ConfigureAwait(false))!; var iam=new IamAuthorizer(this);
            var row=await OwnedAgents(db,tenant).AsNoTracking().SingleOrDefaultAsync(a=>a.Instance.InstanceId==run.AgentInstanceId.ToString("D"),ct).ConfigureAwait(false);
            if(row is null)throw new AccessDeniedException("executor_unavailable"); var agent=Owned(row);
            var session=await CompletionSession(db,tenant,run.SessionId,ct).ConfigureAwait(false) ?? throw new AccessDeniedException("session_unavailable");
            TenantSessionRules.RequireExecutor(state,agent); await RequireTenantExecutionSourcesAsync(db,run,state,iam,this,ct).ConfigureAwait(false);
            TenantPublicWebReadRules.Require(run,agent,InstanceSettingsResolver.Resolve(await TenantAgentDefinitionBaseline(db,agent,ct).ConfigureAwait(false),agent.Instance.SettingsOverrides),session,tools);
            await TenantPublicWebReadRules.PermissionsAsync(state,run,agent,iam,ct).ConfigureAwait(false);
        }
        catch(Exception e) when(e is AccessDeniedException or SecurityConflictException or ArgumentException or System.Text.Json.JsonException or InvalidOperationException)
        { throw new AccessDeniedException("public_web_output_unavailable"); }
    }
}
