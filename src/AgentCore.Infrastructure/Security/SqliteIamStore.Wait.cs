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
