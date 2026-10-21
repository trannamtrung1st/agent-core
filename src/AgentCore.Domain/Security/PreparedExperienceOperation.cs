using System.Text.Json.Serialization;
using AgentCore.Domain.Experience;

namespace AgentCore.Domain.Security;

public sealed record PreparedExperienceOperation(Guid SessionId, Guid RootSessionId, Guid? UserPrincipalId,
    string ToolName, string ArgumentsJson, string SourcesJson, string SourceLabelsJson, string ObservationHash,
    long ExperienceRevision, long MaintenanceRevision,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] TenantExperienceSourceWitness? Source = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? ResultExperienceId = null);
