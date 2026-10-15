using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;

namespace AgentCore.Infrastructure.Persistence;

public sealed class InMemoryStateIsolationConfigurationStore(InMemoryAgentInstanceStore instances, InMemoryExperienceStore experience)
    : IStateIsolationConfigurationStore
{
    public ValueTask ApplyAsync(IsolationMetadataUpdate update, DateTimeOffset now, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (instances.CredentialGate)
        lock (experience.IsolationGate)
        {
            var owner = instances.FindAsync(update.InstanceId).GetAwaiter().GetResult() ?? throw AgentCoreErrors.NotFound("Agent Instance was not found.");
            var learned = experience.SettingsAsync(update.InstanceId).GetAwaiter().GetResult();
            var maintenance = experience.MaintenanceSettingsAsync(update.InstanceId).GetAwaiter().GetResult();
            if (owner.Revision != update.Expected.Instance || learned.Revision != update.Expected.Experience || maintenance.Revision != update.Expected.Maintenance)
                throw AgentCoreErrors.Conflict("State & Isolation owners changed. Refresh the preview before applying.");
            if (owner.Lifecycle != AgentCore.Domain.Definitions.AgentInstanceLifecycle.Active)
                throw AgentCoreErrors.Forbidden("Archived Instance configuration is read-only.");
            // All failure-prone validation/audit precedes metadata assignment. These synchronous owner writes
            // are protected by both owners' locks and cannot be interrupted midway by caller cancellation.
            instances.UpdateWithExpectedRevisionAsync(new(update.InstanceId, owner.Revision, SetSettingsOverrides: true,
                SettingsOverrides: update.Settings, History: update.Audit), now).GetAwaiter().GetResult();
            if (learned.Enabled != update.ExperienceEnabled)
                experience.ConfigureAsync(update.InstanceId, learned.Revision, update.ExperienceEnabled).GetAwaiter().GetResult();
            if (maintenance.AllowAgentConsolidation != update.AllowMaintenance)
                experience.ConfigureMaintenanceAsync(update.InstanceId, maintenance.Revision, update.AllowMaintenance).GetAwaiter().GetResult();
        }
        return ValueTask.CompletedTask;
    }
}
