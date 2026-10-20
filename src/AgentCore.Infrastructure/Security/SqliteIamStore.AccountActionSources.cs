using AgentCore.Application.Ports;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore
{
    private async ValueTask RequireAccountActionSources(AgentCoreDbContext db, AgentRun run, CancellationToken ct)
    {
        var tenant = run.Owner.TenantId!.Value;
        foreach (var reference in TenantAccountActionReferences.Read(run))
        {
            var request = (await db.Set<TenantApprovalRecord>().AsNoTracking().SingleOrDefaultAsync(r => r.TenantId == tenant && r.RequestId == reference.RequestId, ct).ConfigureAwait(false))?.Domain();
            var receipt = (await db.Set<TenantOperationReceiptRecord>().AsNoTracking().SingleOrDefaultAsync(r => r.TenantId == tenant && r.OperationId == reference.OperationId, ct).ConfigureAwait(false))?.Domain();
            var decision = await db.Set<TenantApprovalDecision>().AsNoTracking().SingleOrDefaultAsync(r => r.TenantId == tenant && r.RequestId == reference.RequestId, ct).ConfigureAwait(false);
            if (request?.Operation.AccountAction is null || receipt is null || decision is null) throw new AccessDeniedException("account_source_changed");
            var parent = await OwnedAgents(db, tenant).AsNoTracking().SingleOrDefaultAsync(a => a.Instance.InstanceId == run.AgentInstanceId.ToString("D"), ct).ConfigureAwait(false);
            if (parent is null) throw new AccessDeniedException("executor_unavailable"); var agent = Owned(parent);
            var session = await ReadSandboxSession(db, tenant, run.SessionId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
            var bound = await CredentialOperationTarget(db, tenant, run.AgentInstanceId, request.Operation.AccountAction.Credential.BindingId, request.Operation.AccountAction.Credential.CredentialId, ct).ConfigureAwait(false);
            TenantAccountActionRules.Require(request.Operation.AccountAction, run, agent, session, await TenantAgentDefinitionBaseline(db, agent, ct).ConfigureAwait(false), bound, !TenantCredentialOAuthRules.IsOAuth(bound.Credential.Value));
            if (agent.Instance.Revision != request.Operation.ExpectedInstanceRevision || agent.Instance.PersonaRevision != request.Operation.ExpectedPersonaRevision) throw new AccessDeniedException("account_source_changed");
            var grant = await db.Set<TenantOperationGrant>().AsNoTracking().SingleOrDefaultAsync(g => g.TenantId == tenant && g.RequestId == reference.RequestId, ct).ConfigureAwait(false);
            TenantAccountActionReferences.Receipt(reference, request, receipt, decision, grant, run);
            var state = (await ReadTenantAsync(db, tenant, ct).ConfigureAwait(false))!;
            await TenantAccountActionRules.PermissionsAsync(state, run.AgentInstanceId, request.Operation.AccountAction, agent.PrincipalId, new IamAuthorizer(this), request.Kind == ApprovalKind.Review, ct).ConfigureAwait(false);
            await RequireTenantExecutionSourcesAsync(db, run, state, new IamAuthorizer(this), this, ct).ConfigureAwait(false);
            await TenantAccountActionRules.ReviewerAsync(state, request, decision.HumanPrincipalId, new IamAuthorizer(this), ct).ConfigureAwait(false);
        }
    }
    private async ValueTask<TenantCredentialBinding> RequireAccountActionOperation(AgentCoreDbContext db, TenantApprovalRequest request, AgentRun run,
        TenantAgent agent, CancellationToken ct, AgentRun? source = null, bool exactSecret = true)
    {
        var action = request.Operation.AccountAction ?? throw new SecurityConflictException("operation_mismatch");
        TenantApprovalContinuationRules.Require(request, run, source);
        var session = await ReadSandboxSession(db, request.TenantId, action.SessionId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        var bound = await CredentialOperationTarget(db, request.TenantId, agent.Instance.InstanceId, action.Credential.BindingId, action.Credential.CredentialId, ct).ConfigureAwait(false);
        TenantAccountActionRules.Require(action, run, agent, session, await TenantAgentDefinitionBaseline(db, agent, ct).ConfigureAwait(false), bound, exactSecret);
        var state = (await ReadTenantAsync(db, request.TenantId, ct).ConfigureAwait(false))!;
        await TenantAccountActionRules.PermissionsAsync(state, agent.Instance.InstanceId, action, agent.PrincipalId, new IamAuthorizer(this), request.Kind == ApprovalKind.Review, ct).ConfigureAwait(false);
        if (request.Snapshot?.Credential != TenantAccountReadRules.Snapshot(agent, bound).Credential) throw new SecurityConflictException("account_source_changed");
        await RequireTenantExecutionSourcesAsync(db, run, state, new IamAuthorizer(this), this, ct).ConfigureAwait(false);
        if (action.ToolName == AgentCore.Application.Tools.ToolCatalog.EmailSend)
        {
            var row = await db.Set<EmailDraftRecord>().AsNoTracking().SingleOrDefaultAsync(d => d.DraftId == action.Draft!.DraftId, ct).ConfigureAwait(false);
            TenantAccountActionRules.RequireDraft(action, agent.Instance.InstanceId, row is null ? null : new(Guid.Parse(row.AgentInstanceId), Guid.Parse(row.SessionId),
                JsonSerializer.Deserialize<EmailDraftSnapshot>(row.DraftJson)!, DateTimeOffset.FromUnixTimeMilliseconds(row.CreatedAtUtc)));
        }
        await RequireAccountReadSources(db, run, ct).ConfigureAwait(false);
        return bound;
    }
}
