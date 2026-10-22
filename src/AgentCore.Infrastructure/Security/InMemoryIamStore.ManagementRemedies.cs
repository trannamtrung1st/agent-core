using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Security;

namespace AgentCore.Infrastructure.Security;
public sealed partial class InMemoryIamStore
{
    private void RequireManagementRemedy(AgentCore.Application.Ports.TenantManagementReader reader, PreparedManagementMutation value, CancellationToken ct)
    {
        if (value.Remedy is not { } remedy) return;
        if (!TenantManagementReadReferences.Read(reader.Run.Checkpoint).Contains(remedy.Inspection)) throw new AccessDeniedException("configuration_remedy_source_required");
        RequireManagementReadSources(reader.Run, [remedy.Inspection], ct);
        var observed = ObserveManagementRead(reader.Run, remedy.Inspection.ToolName, remedy.Inspection.ArgumentsJson, null, null, ct);
        var actual = TenantConfigurationRemedyRules.Compile(reader, TenantManagementMutationContract.Parse("configuration.propose", remedy.ArgumentsJson), observed.Value);
        if (!TenantConfigurationRemedyRules.Matches(value, actual)) throw new AccessDeniedException("configuration_remedy_changed");
    }
}
