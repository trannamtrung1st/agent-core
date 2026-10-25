using AgentCore.Application.Ports;
using AgentCore.Infrastructure.Security;

namespace AgentCore.Infrastructure.Persistence;

public sealed partial class InMemoryTriggerStore
{
    internal ModelReferenceCandidate[] ModelReferences(Guid tenant)
    {
        lock (_state.Gate) return _state.Registrations.Values.Where(a => a.Owner.TenantId == tenant && a.ModelOverrideOfferingId is not null)
            .Select(a => new ModelReferenceCandidate(new(a.AutomationId, "override", Guid.Parse(a.ModelOverrideOfferingId!), a.Name,
                "Automation override", a.Revision, a.Status.ToString()), a.Owner.AgentInstanceId)).ToArray();
    }
}
