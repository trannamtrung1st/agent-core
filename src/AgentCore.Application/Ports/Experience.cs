using AgentCore.Domain.Experience;
using AgentCore.Application.Admin;

namespace AgentCore.Application.Ports;

public interface IExperienceStore
{
    ValueTask<IdentityMaintenanceSettings> MaintenanceSettingsAsync(Guid instanceId, CancellationToken ct = default);
    ValueTask<IdentityMaintenanceSettings> ConfigureMaintenanceAsync(Guid instanceId, long expectedRevision, bool allow, CancellationToken ct = default, AdminEventAppend? audit = null);
    ValueTask<AgentExperience> ConsolidateAsync(IReadOnlyList<AgentExperience> sources, AgentExperience result, CancellationToken ct = default);

    ValueTask<ExperienceSettings> SettingsAsync(Guid instanceId, CancellationToken ct = default);
    ValueTask<ExperienceSettings> ConfigureAsync(Guid instanceId, long expectedRevision, bool enabled, CancellationToken ct = default, AdminEventAppend? audit = null);
    ValueTask<AgentExperience> AdmitAsync(AgentExperience proposed, CancellationToken ct = default);
    ValueTask<AgentExperience?> GetAsync(Guid instanceId, Guid experienceId, CancellationToken ct = default);
    ValueTask<IReadOnlyList<AgentExperience>> ListAsync(Guid instanceId, int limit, CancellationToken ct = default);
    ValueTask<IReadOnlyList<AgentExperience>> PendingAsync(int limit, CancellationToken ct = default);
    ValueTask<AgentExperience> CompleteAsync(Guid instanceId, Guid experienceId, ExperienceContent content, CancellationToken ct = default);
    ValueTask<AgentExperience> SetVisibilityAsync(Guid instanceId, Guid experienceId, long expectedRevision, ExperienceVisibility visibility, CancellationToken ct = default, AdminEventAppend? audit = null);
    ValueTask ResetAsync(Guid instanceId, CancellationToken ct = default, AdminEventAppend? audit = null);
}
