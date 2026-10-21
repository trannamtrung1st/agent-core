using System.Text.Json;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Security;
public static class TenantHistoricalReferences
{
    public static IReadOnlyList<TenantHistoricalSource> Merge(IReadOnlyList<TenantHistoricalSource>? prior, IReadOnlyList<TenantHistoricalSource>? next)
    {
        var values = (prior ?? []).Concat(next ?? []).Distinct().ToArray();
        if (values.Length > 100) throw new AccessDeniedException("continuity_context_capacity"); return Array.AsReadOnly(values);
    }
    public static IReadOnlyList<TenantHistoricalSource> Read(AgentRunCheckpoint? checkpoint) => Merge(AgentRunToolCallCheckpoint.ReadExecutionState(checkpoint).TenantHistoricalSources,
        checkpoint is not null && AgentRunToolCallCheckpoint.TryRead(checkpoint, out var messages) ? Read(messages!) : []);
    public static IReadOnlyList<TenantHistoricalSource> Read(IEnumerable<ModelMessage> messages)
    {
        var sources = new List<TenantHistoricalSource>();
        foreach (var message in messages.Where(m => m.Role == ModelRole.Tool && m.Name is ToolCatalog.ContinuitySearch or ToolCatalog.ContinuityGet or ToolCatalog.RequestAuthorization))
        {
            using var doc = JsonDocument.Parse(message.Text); var root = doc.RootElement;
            if (message.Name == ToolCatalog.RequestAuthorization && root.ValueKind == JsonValueKind.Object && root.TryGetProperty("continuityObservation", out var body) && body.ValueKind == JsonValueKind.Object) root = body;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("tenantHistoricalRead", out var marker) || marker.ValueKind != JsonValueKind.True) continue;
            var values = root.GetProperty("historicalSources").Deserialize<TenantHistoricalSource[]>() ?? throw new AccessDeniedException("continuity_source_unavailable");
            if (values.Length > 11 || values.Any(s => s.TenantId == Guid.Empty || s.SessionId == Guid.Empty || s.Revision < 1 || s.ThroughEntrySequence < 0
                || s.AfterEntrySequence < 0 || s.Limit is < 1 or > 20 || !TenantMemoryOperationRules.ValidHash(s.ObservationHash))) throw new AccessDeniedException("continuity_source_unavailable");
            sources.AddRange(values);
        }
        return Merge([], sources);
    }
}
