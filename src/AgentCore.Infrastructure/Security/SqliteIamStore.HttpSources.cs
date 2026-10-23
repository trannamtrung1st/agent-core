using AgentCore.Application.Ports;
using AgentCore.Application.Agents;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore
{
    private async ValueTask RequireHttpSources(AgentCoreDbContext db, AgentRun run, CancellationToken ct)
    {
        var tenant = run.Owner.TenantId!.Value;
        foreach (var reference in TenantHttpReferences.Read(run))
        {
            var request = (await db.Set<TenantApprovalRecord>().AsNoTracking().SingleOrDefaultAsync(r => r.TenantId == tenant && r.RequestId == reference.RequestId, ct).ConfigureAwait(false))?.Domain();
            var receipt = (await db.Set<TenantOperationReceiptRecord>().AsNoTracking().SingleOrDefaultAsync(r => r.TenantId == tenant && r.OperationId == reference.OperationId, ct).ConfigureAwait(false))?.Domain();
            var decision = await db.Set<TenantApprovalDecision>().AsNoTracking().SingleOrDefaultAsync(r => r.TenantId == tenant && r.RequestId == reference.RequestId, ct).ConfigureAwait(false);
            if (request?.Operation.HttpRequest is null || receipt is null || decision is null) throw new AccessDeniedException("http_source_changed");
            var parent = await OwnedAgents(db, tenant).AsNoTracking().SingleOrDefaultAsync(a => a.Instance.InstanceId == run.AgentInstanceId.ToString("D"), ct).ConfigureAwait(false);
            if (parent is null) throw new AccessDeniedException("executor_unavailable"); var agent = Owned(parent);
            var session = await ReadSandboxSession(db, tenant, run.SessionId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
            TenantHttpOperationRules.Require(request.Operation.HttpRequest, run, agent, InstanceSettingsResolver.Resolve(await TenantAgentDefinitionBaseline(db, agent, ct).ConfigureAwait(false),agent.Instance.SettingsOverrides), session);
            if (agent.Instance.Revision != request.Operation.ExpectedInstanceRevision || agent.Instance.PersonaRevision != request.Operation.ExpectedPersonaRevision) throw new AccessDeniedException("http_source_changed");
            var grant = await db.Set<TenantOperationGrant>().AsNoTracking().SingleOrDefaultAsync(g => g.TenantId == tenant && g.RequestId == reference.RequestId, ct).ConfigureAwait(false);
            TenantHttpReferences.Receipt(reference, request, receipt, decision, grant, run);
            var state = (await ReadTenantAsync(db, tenant, ct).ConfigureAwait(false))!;
            await TenantHttpOperationRules.PermissionsAsync(state, run, agent, new IamAuthorizer(this), request.Kind == ApprovalKind.Review, ct).ConfigureAwait(false);
            await RequireTenantExecutionSourcesAsync(db, run, state, new IamAuthorizer(this), this, ct).ConfigureAwait(false);
            await TenantHttpOperationRules.ReviewerAsync(state, request, decision.HumanPrincipalId, new IamAuthorizer(this), ct).ConfigureAwait(false);
        }
    }
    private async ValueTask RequireHttpOperation(AgentCoreDbContext db,TenantApprovalRequest request,AgentRun run,TenantAgent agent,CancellationToken ct,AgentRun? source=null)
    {
        TenantApprovalRules.Validate(request,run,agent,source:source);
        var value=request.Operation.HttpRequest ?? throw new SecurityConflictException("operation_mismatch");
        var session=await ReadSandboxSession(db,request.TenantId,value.SessionId,ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        TenantHttpOperationRules.Require(value,run,agent,InstanceSettingsResolver.Resolve(await TenantAgentDefinitionBaseline(db,agent,ct).ConfigureAwait(false),agent.Instance.SettingsOverrides),session);
        var state=(await ReadTenantAsync(db,request.TenantId,ct).ConfigureAwait(false))!;
        await TenantHttpOperationRules.PermissionsAsync(state,run,agent,new IamAuthorizer(this),request.Kind==ApprovalKind.Review,ct).ConfigureAwait(false);
        await RequireTenantExecutionSourcesAsync(db,run,state,new IamAuthorizer(this),this,ct).ConfigureAwait(false);
    }
}
