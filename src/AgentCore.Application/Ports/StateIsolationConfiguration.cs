using System.Text.Json;
using AgentCore.Application.Admin;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Ports;

public sealed record IsolationOwnerRevisions(long Instance, long Experience, long Maintenance);
public sealed record IsolationMetadataUpdate(Guid InstanceId, IsolationOwnerRevisions Expected,
    InstanceSettingsOverrides Settings, bool ExperienceEnabled, bool AllowMaintenance, AdminEventAppend Audit);

public interface IStateIsolationConfigurationStore
{
    ValueTask ApplyAsync(IsolationMetadataUpdate update, DateTimeOffset now, CancellationToken ct = default);
}
