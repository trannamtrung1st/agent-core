using AgentCore.Application.Security;
using AgentCore.Domain.Security;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Conversation;
namespace AgentCore.Application.Ports;

public sealed record TenantManagementReader(AgentRun Run, AgentRun Root, TenantSecurityState Security, TenantAgent Agent, AgentDefinition Baseline, AgentDefinition Current);
public sealed record TenantManagementReadObservation(TenantManagementReader Reader, PreparedManagementRead Value, bool SourcesChanged = false);
// Read facet of the existing tenant Instance/IAM/Run transaction, not another configuration owner.
public interface ITenantManagementReadStore
{
    ValueTask<TenantManagementReadObservation> ObserveTenantManagementReadAsync(TenantRunReference reference, Guid? generation,
        string tool, string arguments, PreparedManagementRead? expected = null, Guid? inspectionHuman = null, CancellationToken ct = default);
    ValueTask RequireTenantManagementReadSourcesAsync(TenantRunReference reference, IReadOnlyList<TenantManagementReadReference> sources,
        Guid? generation, CancellationToken ct = default);
}
