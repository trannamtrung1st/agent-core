using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Triggers;

namespace AgentCore.Infrastructure.Persistence;

public sealed partial class InMemoryTriggerStore
{
    internal InMemoryTriggerStore BindTenantSessions(InMemoryMemoryStore sessions) { BindAgentRuns(sessions); return this; }
    internal bool OwnsTenantAutomation(Guid tenant, Guid id) { lock (_state.Gate) return ReadTenantAutomation(tenant, id) is not null; }
    private IEnumerable<Automation> LegacyRegistrations => _state.Registrations.Values.Where(v => v.Owner.TenantId is null);
    private IEnumerable<TriggerOccurrence> LegacyOccurrences => _state.Occurrences.Values.Where(v => v.Owner.TenantId is null);
    private static void RequireLegacy(TriggerOwner owner)
    { if (owner.TenantId is not null) throw AgentCoreErrors.Forbidden("Tenant Automation requires its canonical transaction."); }
    private bool TryGetLegacyOccurrence(Guid id, out TriggerOccurrence value)
    {
        if (_state.Occurrences.TryGetValue(id, out var found) && found.Owner.TenantId is null) { value = found; return true; }
        value = null!; return false;
    }
    internal Automation? ReadTenantAutomation(Guid tenant, Guid id) => _state.Registrations.TryGetValue(id, out var value) && value.Owner.TenantId == tenant ? value : null;
    internal TriggerOccurrence? ReadTenantOccurrence(Guid tenant, Guid id) => _state.Occurrences.TryGetValue(id, out var value) && value.Owner.TenantId == tenant ? value : null;
    internal IReadOnlyList<Automation> TenantAutomations(Guid? tenant = null) => _state.Registrations.Values.Where(v => v.Owner.TenantId is not null && (tenant is null || v.Owner.TenantId == tenant)).ToArray();
    internal void SaveTenantAutomation(Automation value) => _state.Registrations[value.AutomationId] = value;
    internal bool TenantBusy(Guid automation) => RegistrationBusy(automation);
    internal void CommitTenantOccurrence(Automation next, TriggerOccurrence occurrence)
    {
        _state.Registrations[next.AutomationId] = next;
        _state.Occurrences.Add(occurrence.OccurrenceId, occurrence);
        _state.DedupeKeys.Add(Dedupe(occurrence.Owner, occurrence.DedupeKey), occurrence.OccurrenceId);
    }
}
