using System.Text.Json;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
namespace AgentCore.Application.Security;

public static class TenantAutomationReadReferences
{
    public static IReadOnlyList<TenantAutomationReadReference> Merge(IEnumerable<TenantAutomationReadReference>? before, IEnumerable<TenantAutomationReadReference>? after)
    {
        var values = (before ?? []).Concat(after ?? []).Distinct().ToArray();
        if (values.Length > 64) throw new AccessDeniedException("automation_source_budget"); return values;
    }
    public static IReadOnlyList<TenantAutomationReadReference> Read(AgentRunCheckpoint? checkpoint) => Merge(
        AgentRunToolCallCheckpoint.ReadExecutionState(checkpoint).TenantAutomationReadSources,
        AgentRunToolCallCheckpoint.TryRead(checkpoint, out var messages) ? Read(messages!) : []);
    public static IReadOnlyList<TenantAutomationReadReference> Read(IEnumerable<ModelMessage> messages)
    {
        var values = new List<TenantAutomationReadReference>();
        foreach (var message in messages.Where(m => m.Role == ModelRole.Tool && (TenantAutomationReadContract.IsTool(m.Name ?? "") || m.Name == ToolCatalog.RequestAuthorization)))
        {
            using var doc = JsonDocument.Parse(message.Text); var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) continue;
            if (message.Name == ToolCatalog.RequestAuthorization && root.TryGetProperty("automationReadObservation", out var nested)) root = nested;
            if (root.ValueKind != JsonValueKind.Object) continue;
            if (!root.TryGetProperty("tenantAutomationRead", out var flag) || flag.ValueKind != JsonValueKind.True) continue;
            var source = root.GetProperty("automationReadSource").Deserialize<TenantAutomationReadReference>() ?? throw new AccessDeniedException("automation_source_unavailable");
            if (source.TenantId == Guid.Empty || !TenantMemoryOperationRules.ValidHash(source.ObservationHash)) throw new AccessDeniedException("automation_source_unavailable");
            ValidateShape(source); values.Add(source);
        }
        return Merge([], values);
    }
    public static PreparedAutomationRead Shape(TenantAutomationReadReference source, Guid session, Guid rootSession) => new(session, rootSession, source.ToolName, source.ArgumentsJson,
        source.SourcesJson, source.EventSourcesJson, "{}", TenantAutomationReadRules.Hash(source.ToolName, source.ArgumentsJson, source.SourcesJson, source.EventSourcesJson, "{}"));
    public static void ValidateShape(TenantAutomationReadReference source) => TenantAutomationReadRules.Validate(Shape(source, new Guid("00000000-0000-0000-0000-000000000001"), new Guid("00000000-0000-0000-0000-000000000001")));
    public static bool Matches(PreparedAutomationRead value, TenantAutomationReadReference source) => value.ToolName == source.ToolName && value.ArgumentsJson == source.ArgumentsJson
        && value.SourcesJson == source.SourcesJson && value.EventSourcesJson == source.EventSourcesJson && value.ObservationHash == source.ObservationHash;
}
