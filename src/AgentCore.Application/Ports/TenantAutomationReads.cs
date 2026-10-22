using AgentCore.Application.Security;
using AgentCore.Domain.Security;
namespace AgentCore.Application.Ports;

public sealed record TenantAutomationReadObservation(TenantMemoryReader Reader, PreparedAutomationRead Value);
// A narrow read facet of the incumbent IAM/Automation/event/Run transaction.
public interface ITenantAutomationReadStore
{
    ValueTask<TenantAutomationReadObservation> ObserveTenantAutomationReadAsync(TenantRunReference reference, Guid? generation,
        string tool, string arguments, PreparedAutomationRead? expected = null, Guid? inspectionHuman = null, CancellationToken ct = default);
    ValueTask RequireTenantAutomationReadSourcesAsync(TenantRunReference reference, IReadOnlyList<TenantAutomationReadReference> sources,
        Guid? generation, CancellationToken ct = default);
}
