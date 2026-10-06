using AgentCore.Application.Admin;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Experience;
using AgentCore.Domain.Memory;

namespace AgentCore.Infrastructure.Persistence;

public sealed partial class InMemoryStructuredMemoryStore
{
    public ValueTask<StructuredMemoryItem> ConsolidateAsync(IReadOnlyList<StructuredMemoryItem> sources,
        StructuredMemoryItem result, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        IdentityConsolidationSemantics.Memory(sources, result);
        lock (_gate)
        {
            if (_items.TryGetValue(result.MemoryId, out var existing))
            {
                if (!IdentityConsolidationSemantics.SameOwner(existing, result) || existing.Kind != result.Kind
                    || existing.Subject != result.Subject || existing.Content != result.Content || existing.Status == MemoryItemStatus.Deleted
                    || !(existing.Provenance.DerivedFromMemoryIds ?? []).SequenceEqual(result.Provenance.DerivedFromMemoryIds!))
                    throw AgentCoreErrors.Conflict("Consolidation result is no longer available.");
                return ValueTask.FromResult(existing);
            }
            if (sources.Any(s => !_items.TryGetValue(s.MemoryId, out var current)
                || !IdentityConsolidationSemantics.MemoryUnchanged(current, s)))
                throw AgentCoreErrors.Conflict("Memory sources changed; reload before consolidating.");
            var collision = ActiveConflict(result);
            if (collision is not null && !sources.Any(s => s.MemoryId == collision.MemoryId))
                throw AgentCoreErrors.Conflict("An unrelated active memory already uses this subject.");
            foreach (var source in sources) _items[source.MemoryId] = _items[source.MemoryId] with
                { Status = MemoryItemStatus.Superseded, UpdatedAt = result.UpdatedAt };
            _items.Add(result.MemoryId, result);
            return ValueTask.FromResult(result);
        }
    }
}

public sealed partial class InMemoryExperienceStore
{
    private readonly Dictionary<Guid, IdentityMaintenanceSettings> maintenanceSettings = [];
    public ValueTask<IdentityMaintenanceSettings> MaintenanceSettingsAsync(Guid id, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (gate) return ValueTask.FromResult(maintenanceSettings.GetValueOrDefault(id) ?? new(id, false, 0));
    }
    public ValueTask<IdentityMaintenanceSettings> ConfigureMaintenanceAsync(Guid id, long expectedRevision, bool allow,
        CancellationToken ct = default, AdminEventAppend? audit = null)
    {
        if (expectedRevision < 0) throw AgentCoreErrors.Validation("Maintenance revision is invalid.");
        ct.ThrowIfCancellationRequested();
        lock (gate)
        {
            var current = maintenanceSettings.GetValueOrDefault(id) ?? new(id, false, 0);
            if (current.Revision != expectedRevision) throw AgentCoreErrors.Conflict("Maintenance settings revision is stale.");
            var saved = current with { AllowAgentConsolidation = allow, Revision = current.Revision + 1 };
            if (audit is not null) events?.AppendWithinLock(audit);
            maintenanceSettings[id] = saved;
            return ValueTask.FromResult(saved);
        }
    }
    public ValueTask<AgentExperience> ConsolidateAsync(IReadOnlyList<AgentExperience> sources, AgentExperience result, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        IdentityConsolidationSemantics.Experience(sources, result);
        lock (gate)
        {
            if (records.TryGetValue(result.ExperienceId, out var existing))
            {
                if (existing.AgentInstanceId != result.AgentInstanceId || existing.ProfileId != result.ProfileId
                    || existing.Visibility is ExperienceVisibility.Deleted or ExperienceVisibility.Suppressed
                    || System.Text.Json.JsonSerializer.Serialize(existing.Content) != System.Text.Json.JsonSerializer.Serialize(result.Content)
                    || !(existing.DerivedFromExperienceIds ?? []).SequenceEqual(result.DerivedFromExperienceIds!))
                    throw AgentCoreErrors.Conflict("Consolidation result is no longer available.");
                return ValueTask.FromResult(existing);
            }
            if (sources.Any(s => !records.TryGetValue(s.ExperienceId, out var current) || current.Revision != s.Revision
                || current.AgentInstanceId != result.AgentInstanceId || current.ProfileId != result.ProfileId
                || current.Visibility != ExperienceVisibility.Eligible || current.Content is null))
                throw AgentCoreErrors.Conflict("Experience sources changed; reload before consolidating.");
            foreach (var source in sources) records[source.ExperienceId] = records[source.ExperienceId] with
                { Visibility = ExperienceVisibility.Superseded, Revision = source.Revision + 1 };
            records.Add(result.ExperienceId, result);
            return ValueTask.FromResult(result);
        }
    }
}
