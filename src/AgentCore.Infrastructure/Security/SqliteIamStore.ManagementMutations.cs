using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
namespace AgentCore.Infrastructure.Security;
public sealed partial class SqliteIamStore
{
    private async ValueTask<(TenantManagementReader Reader, TenantAgentConfigurationChange Change)> RequireManagementMutation(AgentCoreDbContext db, TenantApprovalRequest request, AgentRun run, CancellationToken ct)
    {
        var value = request.Operation.ManagementMutation ?? throw new AccessDeniedException("management_unavailable");
        var reader = await ManagementReader(db, run, null, ct).ConfigureAwait(false); var iam = new IamAuthorizer(this);
        await TenantManagementReadRules.RawAsync(reader, iam, ct).ConfigureAwait(false);
        await TenantManagementMutationRules.SecondaryAsync(reader, value, iam, reader.Agent.PrincipalId, ct).ConfigureAwait(false);
        await TenantManagementMutationRules.PrimaryAsync(request, reader, iam, ct).ConfigureAwait(false);
        return (reader, TenantManagementMutationRules.Change(reader, value));
    }
}
