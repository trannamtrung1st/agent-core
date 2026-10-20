using AgentCore.Application.Agents;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore
{
    private async Task<IReadOnlyList<BackgroundCompletionCandidate>> TenantWaitChildren(AgentCoreDbContext db, AgentRun run,
        AgentRunWait wait, bool requireAll, CancellationToken ct)
    {
        var state = await ReadTenantAsync(db, run.Owner.TenantId!.Value, ct).ConfigureAwait(false);
        var result = new List<BackgroundCompletionCandidate>();
        foreach (var id in wait.BackgroundSessionIds)
        {
            var root = await OwnedSessions(db, run.Owner.TenantId.Value).AsNoTracking().SingleOrDefaultAsync(s => s.SessionId == id.ToString("D"), ct).ConfigureAwait(false);
            var child = await TenantWaitRules.ChildAsync(run, root is null ? null : await SessionView(db, root, ct).ConfigureAwait(false), async (runId, token) => {
                var row = await OwnedRuns(db, run.Owner.TenantId.Value).AsNoTracking().SingleOrDefaultAsync(r => r.AgentRunId == runId.ToString("D"), token).ConfigureAwait(false);
                return row is null ? null : AgentRunStoreMapping.ToDomain(row);
            }, ct).ConfigureAwait(false);
            if (state is not null && child is not null && await AuthorizeCompletionSource(db, run, child, ct).ConfigureAwait(false) is { } allowed) result.Add(allowed);
        }
        if (requireAll && result.Count != wait.BackgroundSessionIds.Count) throw AgentCoreErrors.NotFound("Owned readable initial background child was not found.");
        return result;
    }

    private async Task RequireTenantWait(AgentCoreDbContext db, AgentRun run, AgentRunCommand.SuspendWait suspend, CancellationToken ct)
    {
        var (root, _) = await RequireRuntimeFrame(db, new(run.Owner, run.SessionId, run.AgentRunId, suspend.Generation), ct).ConfigureAwait(false);
        TenantWaitRules.RequireSession(await SessionView(db, root, ct).ConfigureAwait(false));
        var tenant = run.Owner.TenantId!.Value;
        var state = await ReadTenantAsync(db, tenant, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("tenant_unavailable");
        var agent = Owned(await OwnedAgents(db, tenant).SingleAsync(a => a.Instance.InstanceId == run.AgentInstanceId.ToString("D"), ct).ConfigureAwait(false));
        var baseline = Guid.TryParse(agent.Instance.DefinitionId, out var definition)
            ? (await FindDefinitionPublicationAsync(tenant, definition, agent.Instance.ActiveVersion, ct).ConfigureAwait(false))?.Payload
            : builtIns is null ? null : await builtIns.GetAsync(agent.Instance.DefinitionId, agent.Instance.ActiveVersion, ct).ConfigureAwait(false);
        if (baseline is null) throw new AccessDeniedException("definition_unavailable");
        TenantWaitRules.Require(run, InstanceSettingsResolver.Resolve(baseline, agent.Instance.SettingsOverrides), suspend);
        await RequireTenantExecutionSourcesAsync(db, run, state, new IamAuthorizer(this), this, ct).ConfigureAwait(false);
    }
}
