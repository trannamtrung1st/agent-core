using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Sessions;

public sealed partial class SessionRuntime
{
    private static string? ContinuationTarget(AgentRun? run) => run?.Owner.TenantId is not null
        ? AgentCore.Application.Security.TenantConfigurationContinuationRules.Target(run) : null;
}
