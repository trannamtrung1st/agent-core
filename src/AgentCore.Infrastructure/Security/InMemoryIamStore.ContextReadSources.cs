using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Spaces;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore
{
    private void RequireContextReadSources(AgentRun run, CancellationToken ct)
    {
        if (run.Admission.Configuration?.SpaceContext is not { EnvironmentId: { } environment } pin) return;
        var state = _tenants[pin.TenantId]; var iam = new IamAuthorizer(this);
        RequireSpaceExecutionSource(run, state, iam, ct).GetAwaiter().GetResult();
        var agent = FindTenantAgentAsync(pin.TenantId, run.AgentInstanceId, ct).Result ?? throw new AccessDeniedException("executor_unavailable");
        TenantContextReadRules.RequireAsync(run, state, agent, TenantAgentDefinitionBaseline(agent), iam,
            name => ValueTask.FromResult(_variables.GetValueOrDefault((pin.TenantId, environment, name))),
            () => ValueTask.FromResult(ContextCredentialBindings(run, ct)),
            id => ValueTask.FromResult(_secrets.GetValueOrDefault((pin.TenantId, id))?.Status == SecretStatus.Active
                && _spaceMemberships.GetValueOrDefault((pin.TenantId, pin.SpaceId, run.AgentInstanceId))?.AddedBindings.Any(b => b.Kind == SpaceBindingKind.Secret && b.ResourceKey == id.ToString("D")) == true), () => ValueTask.FromResult<long?>(_environments.GetValueOrDefault((pin.TenantId, pin.SpaceId, environment)) is { DeletedAt: null } profile ? profile.Revision : null), ct).AsTask().GetAwaiter().GetResult();
    }
}
