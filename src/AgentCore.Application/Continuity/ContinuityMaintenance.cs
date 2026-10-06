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
    IExperienceStore experiences, ExperienceService experience, IContinuityMaintenanceStore settings,
    ContinuityMaintenancePolicy policy, TimeProvider time)
{
    public async ValueTask RunOnceAsync(CancellationToken ct = default)
    {
        Guid? instanceCursor = null;
        while (true)
        {
            var instancePage = await instances.ListMaintenancePageAsync(instanceCursor, 100, ct);
            if (instancePage.Count == 0) break;
            instanceCursor = instancePage[^1].InstanceId;
            foreach (var instance in instancePage)
            {
                if (instance.Compatibility || instance.Lifecycle != AgentInstanceLifecycle.Active
                    || !(await experiences.SettingsAsync(instance.InstanceId, ct)).Enabled) continue;
                var cadence = await settings.ReadAsync(instance.InstanceId, ct);
                var now = time.GetUtcNow();
                if (cadence.LastMaintenanceAtUtc is { } last
                    && now - last < TimeSpan.FromSeconds(policy.Effective(cadence.IntervalSeconds))) continue;
                if (!await settings.TryClaimAsync(cadence, now, ct)) continue;
                Guid? sessionCursor = null;
                while (true)
                {
                    var sessionPage = await history.ListOwnedActivePageAsync(instance.InstanceId, LocalUserProfile.Id, sessionCursor, 100, ct);
                    if (sessionPage.Count == 0) break;
                    sessionCursor = sessionPage[^1].SessionId;
                    foreach (var s in sessionPage)
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
    }
}
