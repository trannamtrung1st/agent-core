using AgentCore.Application.Security;
using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore
{
    private async ValueTask RequireBrowserOperation(AgentCoreDbContext db, TenantApprovalRequest request, AgentRun run, TenantAgent agent, CancellationToken ct, bool retainedObservation = false)
    {
        var value = request.Operation.BrowserCommand!;
        var session = await ReadSandboxSession(db, request.TenantId, value.SessionId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        var baseline = await TenantAgentDefinitionBaseline(db, agent, ct).ConfigureAwait(false);
        TenantBrowserOperationRules.Require(value, run, agent, baseline, session);
        foreach (var source in TenantBrowserUploadRules.Sources(value))
        {
            var row = await SqliteArtifactStore.Owned(db, request.TenantId, value.SessionId)
                .SingleOrDefaultAsync(a => a.ArtifactId == source.ArtifactId.ToString("D"), ct).ConfigureAwait(false);
            TenantBrowserUploadRules.Require(request.TenantId, value.SessionId, source, row is null ? null : SqliteArtifactStore.FromRow(row));
        }
        if (value.Credential is { } credential)
        {
            await TenantBrowserCredentialRules.RequireChatLineageAsync(run, async (id, token) => {
                var row = await OwnedRuns(db, request.TenantId).AsNoTracking().SingleOrDefaultAsync(r => r.AgentRunId == id.ToString("D"), token).ConfigureAwait(false);
                return row is null ? null : AgentRunStoreMapping.ToDomain(row);
            }, ct).ConfigureAwait(false);
            var bound = await CredentialOperationTarget(db, request.TenantId, request.Operation.InstanceId, credential.BindingId, credential.CredentialId, ct).ConfigureAwait(false);
            TenantBrowserCredentialRules.RequireBinding(run, agent, bound, baseline); TenantBrowserCredentialRules.RequireWitness(credential, bound);
        }
        if (request.Status == TenantApprovalStatus.Pending && value.Artifacts is not null)
            await RequireBrowserArtifactBudget(db, request, ct).ConfigureAwait(false);
        if (!retainedObservation && request.Snapshot != TenantBrowserOperationRules.Snapshot(agent, value)) throw new SecurityConflictException("browser_target_changed");
        var state = (await ReadTenantAsync(db, request.TenantId, ct).ConfigureAwait(false))!;
        await TenantBrowserOperationRules.RequirePermissionsAsync(state, agent.Instance.InstanceId, value, request.ExecutorPrincipalId, new IamAuthorizer(this), ct,
            request.Kind == ApprovalKind.ExceptionalGrant).ConfigureAwait(false);
    }
}
