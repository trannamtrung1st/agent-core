using AgentCore.Application.Ports;
using AgentCore.Application.Admin;
using AgentCore.Infrastructure.Admin;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Experience;

namespace AgentCore.Infrastructure.Persistence;

public sealed partial class InMemoryExperienceStore(InMemoryAdminEventStore? events = null, IWorkItemStore? work = null) : IExperienceStore
{
    private readonly object gate = new();
    private readonly Dictionary<Guid, ExperienceSettings> settings = [];
    private readonly Dictionary<Guid, AgentExperience> records = [];
    public ValueTask<ExperienceSettings> SettingsAsync(Guid id, CancellationToken ct = default)
    { lock (gate) return ValueTask.FromResult(settings.GetValueOrDefault(id) ?? new(id, false, 0)); }
    public ValueTask<ExperienceSettings> ConfigureAsync(Guid id, long expectedRevision, bool enabled, CancellationToken ct = default, AdminEventAppend? audit = null)
    {
        lock (gate)
        {
            var current = settings.GetValueOrDefault(id) ?? new(id, false, 0);
            if (current.Revision != expectedRevision) throw AgentCoreErrors.Conflict("Experience settings revision is stale.");
            var saved = current with { Enabled = enabled, Revision = current.Revision + 1 };
            if (audit is not null) events?.AppendWithinLock(audit);
            settings[id] = saved; return ValueTask.FromResult(saved);
        }
    }
    public ValueTask<AgentExperience> AdmitAsync(AgentExperience proposed, CancellationToken ct = default)
    {
        lock (gate)
        {
            var existing = records.Values.FirstOrDefault(r => r.AgentInstanceId == proposed.AgentInstanceId
                && r.SourceKind == proposed.SourceKind && r.SourceId == proposed.SourceId && r.ThroughCursor == proposed.ThroughCursor);
            if (existing is not null) return ValueTask.FromResult(existing);
            records.Add(proposed.ExperienceId, proposed); return ValueTask.FromResult(proposed);
        }
    }
    public ValueTask<AgentExperience?> GetAsync(Guid id, Guid recordId, CancellationToken ct = default)
    { lock (gate) return ValueTask.FromResult(records.GetValueOrDefault(recordId) is { } r && r.AgentInstanceId == id ? r : null); }
    public ValueTask<IReadOnlyList<AgentExperience>> ListAsync(Guid id, int limit, CancellationToken ct = default)
    { lock (gate) return ValueTask.FromResult<IReadOnlyList<AgentExperience>>(records.Values.Where(r => r.AgentInstanceId == id)
        .OrderByDescending(r => r.CreatedAtUtc).ThenBy(r => r.ExperienceId).Take(Math.Clamp(limit, 1, 100)).ToArray()); }
    public async ValueTask<IReadOnlyList<AgentExperience>> PendingAsync(int limit, CancellationToken ct = default)
    {
        AgentExperience[] pending;
        lock (gate) pending = records.Values.Where(r => r.Content is null && r.Visibility != ExperienceVisibility.Deleted)
            .OrderBy(r => r.CreatedAtUtc).ToArray();
        var missing = new List<AgentExperience>();
        foreach (var record in pending)
        {
            ct.ThrowIfCancellationRequested();
            if (work is not null && await work.GetAsync(new(record.AgentInstanceId, record.ProfileId), record.GenerationWorkItemId, ct) is not null) continue;
            missing.Add(record);
            if (missing.Count >= Math.Clamp(limit, 1, 100)) break;
        }
        return missing;
    }
    public ValueTask<AgentExperience> CompleteAsync(Guid id, Guid recordId, ExperienceContent content, CancellationToken ct = default)
    {
        content.Validate(); lock (gate)
        {
            var r = Required(id, recordId);
            if (r.Content is null && r.Visibility != ExperienceVisibility.Deleted)
                records[recordId] = r = r with { Content = content, Revision = r.Revision + 1 };
            return ValueTask.FromResult(r);
        }
    }
    public ValueTask<AgentExperience> SetVisibilityAsync(Guid id, Guid recordId, long expectedRevision, ExperienceVisibility visibility, CancellationToken ct = default, AdminEventAppend? audit = null)
    {
        lock (gate)
        {
            var r = Required(id, recordId);
            if (r.Revision != expectedRevision) throw AgentCoreErrors.Conflict("Experience revision is stale.");
            if (visibility == ExperienceVisibility.Superseded || (r.Visibility == ExperienceVisibility.Superseded && visibility != ExperienceVisibility.Deleted))
                throw AgentCoreErrors.Validation("Superseded experience cannot be restored or assigned manually.");
            if (r.Visibility == ExperienceVisibility.Deleted) throw AgentCoreErrors.Validation("Deleted experience cannot be restored.");
            if (audit is not null) events?.AppendWithinLock(audit);
            records[recordId] = r = r with { Visibility = visibility, Content = visibility == ExperienceVisibility.Deleted ? null : r.Content, Revision = r.Revision + 1 };
            return ValueTask.FromResult(r);
        }
    }
    public ValueTask ResetAsync(Guid id, CancellationToken ct = default, AdminEventAppend? audit = null)
    { lock (gate) { if (audit is not null) events?.AppendWithinLock(audit); foreach (var r in records.Values.Where(r => r.AgentInstanceId == id).ToArray())
        records[r.ExperienceId] = r with { Visibility = ExperienceVisibility.Deleted, Content = null, Revision = r.Revision + 1 }; }
        return ValueTask.CompletedTask; }
    internal void Purge(Guid id)
    { lock (gate) { settings.Remove(id); maintenanceSettings.Remove(id); continuitySettings.Remove(id); foreach (var r in records.Values.Where(r => r.AgentInstanceId == id).ToArray()) records.Remove(r.ExperienceId); } }
    private AgentExperience Required(Guid id, Guid recordId) => records.GetValueOrDefault(recordId) is { } r && r.AgentInstanceId == id
        ? r : throw AgentCoreErrors.NotFound("Experience was not found.");
}
