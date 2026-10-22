using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
namespace AgentCore.Infrastructure.Security;
public sealed partial class InMemoryIamStore
{
    private (TenantManagementReader Reader, TenantAgentConfigurationChange Change) RequireManagementMutation(TenantApprovalRequest request, AgentRun run, CancellationToken ct)
    {
        var value = request.Operation.ManagementMutation ?? throw new AccessDeniedException("management_unavailable");
        var reader = ManagementReader(run, null, ct); var iam = new IamAuthorizer(this);
        TenantManagementReadRules.RawAsync(reader, iam, ct).AsTask().GetAwaiter().GetResult();
        TenantManagementMutationRules.SecondaryAsync(reader, value, iam, reader.Agent.PrincipalId, ct).AsTask().GetAwaiter().GetResult();
        TenantManagementMutationRules.PrimaryAsync(request, reader, iam, ct).AsTask().GetAwaiter().GetResult();
        return (reader, TenantManagementMutationRules.Change(reader, value));
    }
}
