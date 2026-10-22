using System.Text.Json;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
namespace AgentCore.Application.Security;

public static class TenantManagementReadReferences
{
    public static IReadOnlyList<TenantManagementReadReference> Merge(IEnumerable<TenantManagementReadReference>? before, IEnumerable<TenantManagementReadReference>? after)
    {
        var values = (before ?? []).Concat(after ?? []).Distinct().ToArray();
        if (values.Length > 64) throw new AccessDeniedException("management_source_budget"); return values;
    }
    public static IReadOnlyList<TenantManagementReadReference> Read(AgentRunCheckpoint? checkpoint) => Merge(
        AgentRunToolCallCheckpoint.ReadExecutionState(checkpoint).TenantManagementReadSources,
        AgentRunToolCallCheckpoint.TryRead(checkpoint, out var messages) ? Read(messages!) : []);
    public static IReadOnlyList<TenantManagementReadReference> Read(IEnumerable<ModelMessage> messages)
    {
        var values = new List<TenantManagementReadReference>();
        foreach (var message in messages.Where(m => m.Role == ModelRole.Tool && (TenantManagementReadContract.IsTool(m.Name ?? "") || m.Name == ToolCatalog.RequestAuthorization)))
        {
            using var doc = JsonDocument.Parse(message.Text); var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) continue;
            if (message.Name == ToolCatalog.RequestAuthorization && root.TryGetProperty("managementReadObservation", out var nested)) root = nested;
            if (root.ValueKind != JsonValueKind.Object) continue;
            if (!root.TryGetProperty("tenantManagementRead", out var flag) || flag.ValueKind != JsonValueKind.True) continue;
            var source = root.GetProperty("managementReadSource").Deserialize<TenantManagementReadReference>() ?? throw new AccessDeniedException("management_source_unavailable");
            if (source.TenantId == Guid.Empty || !TenantMemoryOperationRules.ValidHash(source.ObservationHash)) throw new AccessDeniedException("management_source_unavailable");
            ValidateShape(source); values.Add(source);
        }
        return Merge([], values);
    }
    public static PreparedManagementRead Shape(TenantManagementReadReference source, Guid session, Guid rootSession) => new(session, rootSession, source.ToolName, source.ArgumentsJson,
        source.InstanceRevision, source.ConfigurationHash, source.StableConfigurationHash, "{}", TenantManagementReadRules.Hash(source.ToolName, source.ArgumentsJson, source.InstanceRevision, source.ConfigurationHash, source.StableConfigurationHash, "{}"));
    public static void ValidateShape(TenantManagementReadReference source) => TenantManagementReadRules.Validate(Shape(source, new Guid("00000000-0000-0000-0000-000000000001"), new Guid("00000000-0000-0000-0000-000000000001")));
    public static bool Matches(PreparedManagementRead value, TenantManagementReadReference source) => value.ToolName == source.ToolName && value.ArgumentsJson == source.ArgumentsJson
        && value.InstanceRevision == source.InstanceRevision && value.ConfigurationHash == source.ConfigurationHash && value.StableConfigurationHash == source.StableConfigurationHash && value.ObservationHash == source.ObservationHash;
}
