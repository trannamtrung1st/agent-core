using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore
{
    private async ValueTask<TenantSession> RequireBackgroundCreationOperation(AgentCoreDbContext db, TenantApprovalRequest request, AgentRun run, TenantAgent agent, CancellationToken ct)
    {
        var state = (await ReadTenantAsync(db, request.TenantId, ct).ConfigureAwait(false))!;
        var session = await ReadSandboxSession(db, request.TenantId, request.SessionId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        var current = InstanceSettingsResolver.Resolve(await TenantAgentDefinitionBaseline(db, agent, ct).ConfigureAwait(false), agent.Instance.SettingsOverrides);
        TenantWorkspaceEffectRules.Session(run, agent, session);
        TenantBackgroundCreationRules.Require(request, run, agent, current);
        await TenantExecutionLineage.RequireRequesterAsync(run, state, new IamAuthorizer(this), this, ct).ConfigureAwait(false);
        await TenantBackgroundCreationRules.ExecutorAsync(state, request, new IamAuthorizer(this), ct).ConfigureAwait(false);
        return session;
    }
    private static async ValueTask StageTenantBackgroundGraph(AgentCoreDbContext db, TenantSessionRoot root, SessionSnapshot snapshot, AgentRun child, CancellationToken ct)
    {
        var all = (await OwnedRuns(db, root.TenantId).AsNoTracking().Where(r => r.AgentInstanceId == root.AgentInstanceId.ToString("D"))
            .ToArrayAsync(ct).ConfigureAwait(false)).Select(AgentRunStoreMapping.ToDomain);
        AgentRunStoreMapping.ValidateBackgroundBudget(child, all);
        db.Sessions.Add(SqliteMemoryStore.ToRecord(snapshot)); db.Add(TenantSessionRecord.Row(root));
        db.Activations.Add(AgentRunStoreMapping.ToActivationRecord(snapshot, child)); db.AgentRuns.Add(AgentRunStoreMapping.ToRecord(child));
        db.ActivationSourceEntries.Add(new() { ActivationId = child.ActivationId.ToString("D"), SessionId = child.SessionId.ToString("D"), EntryId = snapshot.Entries[0].EntryId.ToString("D"), Ordinal = 0 });
    }
}
