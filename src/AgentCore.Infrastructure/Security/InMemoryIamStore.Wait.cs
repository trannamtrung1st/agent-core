using AgentCore.Application.Agents;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore
{
    private IReadOnlyList<BackgroundCompletionCandidate> TenantWaitChildren(AgentRun run, AgentRunWait wait, bool requireAll = false)
    {
        var state = _tenants[run.Owner.TenantId!.Value]; var result = new List<BackgroundCompletionCandidate>();
        foreach (var id in wait.BackgroundSessionIds)
        {
            var child = TenantWaitRules.ChildAsync(run, sessionBacking.ReadTenantSession(state.Tenant.TenantId, id),
                (runId, _) => ValueTask.FromResult(sessionBacking.ReadRetainedTenantOperationRun(state.Tenant.TenantId, runId)), CancellationToken.None).AsTask().GetAwaiter().GetResult();
            if (child is not null && AuthorizeCompletionSource(run, child, CancellationToken.None) is { } allowed) result.Add(allowed);
        }
        if (requireAll && result.Count != wait.BackgroundSessionIds.Count) throw AgentCore.Application.Sessions.AgentCoreErrors.NotFound("Owned readable initial background child was not found.");
        return result;
    }

    private void RequireTenantWait(AgentRun run, AgentRunCommand.SuspendWait suspend, CancellationToken ct)
    {
        RequireRuntimeOwner(run.Owner); var tenant = run.Owner.TenantId!.Value;
        _ = sessionBacking.LoadTenantRuntime(new(run.Owner, run.SessionId, run.AgentRunId, suspend.Generation), (clock ?? TimeProvider.System).GetUtcNow())
            ?? throw new AccessDeniedException("session_unavailable");
        TenantWaitRules.RequireSession(sessionBacking.ReadTenantSession(tenant, run.SessionId) ?? throw new AccessDeniedException("session_unavailable"));
        var agent = FindTenantAgentAsync(tenant, run.AgentInstanceId, ct).Result!;
        var baseline = Guid.TryParse(agent.Instance.DefinitionId, out var definition)
            ? definitions.TenantPublication(tenant, definition, agent.Instance.ActiveVersion)?.Payload
            : builtIns?.GetAsync(agent.Instance.DefinitionId, agent.Instance.ActiveVersion, ct).AsTask().GetAwaiter().GetResult();
        if (baseline is null) throw new AccessDeniedException("definition_unavailable");
        TenantWaitRules.Require(run, InstanceSettingsResolver.Resolve(baseline, agent.Instance.SettingsOverrides), suspend);
        TenantExecutionLineage.RequireRequesterAsync(run, _tenants[tenant], new IamAuthorizer(this), this, ct).AsTask().GetAwaiter().GetResult();
    }
}
