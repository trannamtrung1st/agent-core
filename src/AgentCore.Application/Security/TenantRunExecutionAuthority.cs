using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Security;

public sealed class TenantRunExecutionAuthority(TenantOperationAuthorizer operations) : IAgentRunAuthority
{
    public async ValueTask<AgentDefinition?> CurrentDefinitionAsync(AgentRun run, CancellationToken cancellationToken = default)
    {
        if (run.Owner.TenantId is not { } tenant || run.Admission.ExecutionContext is null) return null;
        try
        {
            var (current, _) = await operations.ResolveAsync(new(tenant, run.AgentRunId), cancellationToken).ConfigureAwait(false);
            return current.Status == AgentRunStatus.Running && current.Claim?.Generation == run.Claim?.Generation ? run.Admission.Configuration?.Definition : null;
        }
        catch (AccessDeniedException) { return null; }
    }
}
