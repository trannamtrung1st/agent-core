using System.Text.Json;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public static partial class TenantApprovalRules
{
    public static PreparedTenantOperation PrepareCompletionRead(Guid id, Guid instance, PreparedCompletionRead value, long instanceRevision, long personaRevision)
    {
        if (id == Guid.Empty || instance == Guid.Empty || value.ParentSessionId == Guid.Empty || value.ChildSessionId == Guid.Empty
            || value.ParentSessionId == value.ChildSessionId || value.InitialRunId == Guid.Empty || value.SourceRunId == Guid.Empty || value.SourceRevision < 1
            || instanceRevision < 1 || personaRevision < 1 || string.IsNullOrWhiteSpace(value.Objective) || value.Objective.Length > 4000
            || value.ObservationJson.Length is < 2 or > 20000 || value.ObservationHash != TenantCompletionReadRules.Hash(value.ObservationJson)
            || TenantCompletionReadRules.Artifacts(value).Count > 3 || TenantCompletionReadRules.Artifacts(value).Select(a => a.ArtifactId).Distinct().Count() != TenantCompletionReadRules.Artifacts(value).Count
            || TenantCompletionReadRules.Artifacts(value).Any(a => a.SessionId != value.ChildSessionId)) throw new ArgumentException("Invalid exact completion observation.");
        using var json = JsonDocument.Parse(value.ObservationJson);
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.GetProperty("tenantCompletionRead").GetBoolean()
            || root.GetProperty("backgroundSessionId").GetGuid() != value.ChildSessionId || root.GetProperty("agentRunId").GetGuid() != value.InitialRunId
            || root.GetProperty("sourceAgentRunId").GetGuid() != value.SourceRunId || root.GetProperty("parentSessionId").GetGuid() != value.ParentSessionId)
            throw new ArgumentException("Completion observation identity does not match.");
        var action = JsonSerializer.Serialize(new { operation = "background.inspect", permission = "agent-runs.read", resourceType = "agent-run",
            resourceId = value.SourceRunId.ToString("D"), completion = value, expectedInstanceRevision = instanceRevision, expectedPersonaRevision = personaRevision });
        return new(id, instance, null, instanceRevision, personaRevision, action, TenantCompletionReadRules.Hash(action), CompletionRead: value);
    }
}
