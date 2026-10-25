using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore
{
    private async ValueTask RequireCredentialReadSources(AgentCoreDbContext db, AgentRun run, CancellationToken ct)
    {
        try
        {
            foreach (var source in TenantCredentialReadReferences.Read(run))
            {
                var tenant = run.Owner.TenantId!.Value; var state = (await ReadTenantAsync(db, tenant, ct).ConfigureAwait(false))!; var iam = new IamAuthorizer(this);
                var row = await OwnedAgents(db, tenant).AsNoTracking().SingleOrDefaultAsync(a => a.Instance.InstanceId == run.AgentInstanceId.ToString("D"), ct).ConfigureAwait(false);
                if (row is null) throw new AccessDeniedException("executor_unavailable"); var agent = Owned(row);
                TenantSessionRules.RequireExecutor(state, agent);
                await RequireTenantExecutionSourcesAsync(db, run, state, iam, this, ct).ConfigureAwait(false);
                if (source.RequestId is { } requestId)
                {
                    var request = (await db.Set<TenantApprovalRecord>().AsNoTracking().SingleOrDefaultAsync(r => r.TenantId == tenant && r.RequestId == requestId, ct).ConfigureAwait(false))?.Domain();
                    var receipt = (await db.Set<TenantOperationReceiptRecord>().AsNoTracking().SingleOrDefaultAsync(r => r.TenantId == tenant && r.OperationId == source.OperationId, ct).ConfigureAwait(false))?.Domain();
                    var decision = await db.Set<TenantApprovalDecision>().AsNoTracking().SingleOrDefaultAsync(r => r.TenantId == tenant && r.RequestId == requestId, ct).ConfigureAwait(false);
                    if (request?.Operation.CredentialRead is null || receipt is null || decision is null
                        || await db.Set<TenantOperationGrant>().AnyAsync(g => g.TenantId == tenant && g.RequestId == requestId, ct).ConfigureAwait(false)) throw new AccessDeniedException("credential_source_changed");
                    TenantCredentialReadReferences.Receipt(source, request, receipt, decision, run);
                    await RequireCredentialReadOperation(db, request, run, agent, ct).ConfigureAwait(false);
                    await TenantCredentialReadOperationRules.ReviewerAsync(state, request.Operation.CredentialRead, request.OriginalInitiatorPrincipalId, decision.HumanPrincipalId, iam, ct).ConfigureAwait(false);
                }
                else
                {
                    var session = await ReadSandboxSession(db, tenant, run.SessionId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
                    var bindings = new List<TenantCredentialBinding>();
                    foreach (var witness in source.Witnesses)
                        bindings.Add(await ContextCredentialTarget(db, run, witness.BindingId, witness.CredentialId, ct).ConfigureAwait(false));
                    var read = TenantCredentialReadReferences.Direct(run, source, bindings);
                    TenantCredentialReadOperationRules.Require(read, run, agent, session, await TenantAgentDefinitionBaseline(db, agent, ct).ConfigureAwait(false), bindings);
                    await TenantCredentialReadOperationRules.PermissionsAsync(state, read, agent.PrincipalId, iam, ct).ConfigureAwait(false);
                    foreach (var (permission, scope) in TenantCredentialReadOperationRules.Targets(tenant, read))
                        if (await TenantSandboxReadOperationRules.NeedsReviewAsync(state, run.Admission.ExecutionContext!.OriginalInitiatorPrincipalId, permission, scope, iam, ct).ConfigureAwait(false))
                            throw new AccessDeniedException("credential_read_review_required");
                }
            }
        }
        catch (Exception e) when (e is AccessDeniedException or SecurityConflictException or ArgumentException or System.Text.Json.JsonException or InvalidOperationException)
        { throw new AccessDeniedException("account_metadata_unavailable"); }
    }
}
