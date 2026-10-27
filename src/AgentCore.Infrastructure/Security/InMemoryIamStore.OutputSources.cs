using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Infrastructure.Persistence;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore
{
    private void RequireTenantOutputSources(AgentRun parent, CancellationToken ct)
    {
        RequireSpaceResourceReadSources(parent, ct);
        RequireContextReadSources(parent, ct);
        RequirePublicWebSources(parent, ct);
        RequireHttpSources(parent, ct);
        RequireStoredProfileSource(parent, ct);
        RequireStoredMemorySources(parent, ct);
        RequireTenantEventRead(parent, ct);
        RequireCredentialReadSources(parent, ct);
        RequireAccountReadSources(parent, ct);
        RequireAccountActionSources(parent, ct);
        if (parent.Admission.TaskReport is not null)
            RequireTaskReportSourcesAsync(parent, ct).AsTask().GetAwaiter().GetResult();
        else if (parent.Admission.OutputContract == AgentRunOutputContract.CompletionReport)
        {
            var source = TenantReportReadRules.SourceAsync(parent,
                (id, _) => ValueTask.FromResult(sessionBacking.ReadRetainedTenantOperationRun(parent.Owner.TenantId!.Value, id)),
                (id, _) => ValueTask.FromResult(CompletionSource(parent.Owner, id)), ct).AsTask().GetAwaiter().GetResult();
            RequireTenantReportRead(_tenants[parent.Owner.TenantId!.Value], source, ct);
            RequireTenantReportDelivery(_tenants[parent.Owner.TenantId.Value], source, parent, ct);
        }
    }
}
