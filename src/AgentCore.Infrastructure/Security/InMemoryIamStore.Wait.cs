using AgentCore.Application.Agents;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore
{


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
        RequireTenantExecutionSourcesAsync(run, _tenants[tenant], new IamAuthorizer(this), this, ct).AsTask().GetAwaiter().GetResult();
    }
}
