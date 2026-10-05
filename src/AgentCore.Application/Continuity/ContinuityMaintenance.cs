using AgentCore.Application.Experience;
using AgentCore.Application.Agents;
using AgentCore.Application.Tools;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Experience;

namespace AgentCore.Application.Continuity;

/// <summary>Host policy over durable snapshots; never mutates a live Session or its outcome.</summary>
public sealed class ContinuityMaintenance(IAgentInstanceStore instances, IMemoryStore history,
    IExperienceStore experiences, ExperienceService experience)
{
    public static readonly TimeSpan Cadence = TimeSpan.FromMinutes(5);
    public async ValueTask RunOnceAsync(CancellationToken ct = default)
    {
        foreach (var instance in await instances.ListAsync(100, ct))
        {
            if (instance.Compatibility || instance.Lifecycle != AgentInstanceLifecycle.Active
                || !(await experiences.SettingsAsync(instance.InstanceId, ct)).Enabled) continue;
            foreach (var s in await history.ListOwnedSessionsAsync(instance.InstanceId, LocalUserProfile.Id, 100, true, ct))
            {
                var entries = await history.ReadHistoryAsync(s.SessionId, Math.Max(0, s.DurableLastEntrySequence - 100), 100, ct);
                var cutoff = ExperienceService.StableSessionCheckpoint(entries);
                if (cutoff == 0) continue;
                // Full deterministic identity lookup includes pending, suppressed and deleted records.
                var key = $"experience:{instance.InstanceId:D}:{ExperienceSourceKind.Session}:{s.SessionId:D}:{cutoff}";
                var id = AgentCore.Application.Triggers.TriggerScheduleAdmission.OccurrenceId(key);
                if (await experiences.GetAsync(instance.InstanceId, id, ct) is not null) continue;
                await experience.TrySessionBoundaryAsync(instance.InstanceId, s.SessionId, ct);
            }
        }
    }
}
