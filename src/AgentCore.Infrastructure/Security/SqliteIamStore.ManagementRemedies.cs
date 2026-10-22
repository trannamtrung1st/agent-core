using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;

namespace AgentCore.Infrastructure.Security;
public sealed partial class SqliteIamStore
{
    private async Task RequireManagementRemedy(AgentCoreDbContext db, TenantManagementReader reader, PreparedManagementMutation value, CancellationToken ct)
    {
        if (value.Remedy is not { } remedy) return;
        if (!TenantManagementReadReferences.Read(reader.Run.Checkpoint).Contains(remedy.Inspection)) throw new AccessDeniedException("configuration_remedy_source_required");
        await RequireManagementReadSources(db, reader.Run, [remedy.Inspection], ct).ConfigureAwait(false);
        var observed = await ObserveManagementRead(db, reader.Run, remedy.Inspection.ToolName, remedy.Inspection.ArgumentsJson, null, null, ct).ConfigureAwait(false);
        var actual = TenantConfigurationRemedyRules.Compile(reader, TenantManagementMutationContract.Parse("configuration.propose", remedy.ArgumentsJson), observed.Value);
        if (!TenantConfigurationRemedyRules.Matches(value, actual)) throw new AccessDeniedException("configuration_remedy_changed");
    }
}
