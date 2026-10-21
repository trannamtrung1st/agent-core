using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;

namespace AgentCore.Application.Security;

public static class TenantMemoryOperationReferences
{
    public static IReadOnlyList<TenantMemorySource> Read(AgentCore.Domain.Conversation.AgentRunCheckpoint? checkpoint)
    {
        var state = AgentCore.Application.Execution.AgentRunToolCallCheckpoint.ReadExecutionState(checkpoint);
        return TenantMemoryService.Merge(state.TenantMemorySources, checkpoint is not null && AgentCore.Application.Execution.AgentRunToolCallCheckpoint.TryRead(checkpoint, out var messages) ? Read(messages!) : []);
    }
    public static IReadOnlyList<TenantMemorySource> Read(IEnumerable<ModelMessage> messages)
    {
        var sources = new List<TenantMemorySource>();
        foreach (var message in messages.Where(m => m.Role == ModelRole.Tool && m.Name is ToolCatalog.ContinuitySearch or ToolCatalog.ContinuityGet or ToolCatalog.MemoryConsolidate or ToolCatalog.RequestAuthorization))
        {
            using var json = JsonDocument.Parse(message.Text); var root = json.RootElement;
            if (message.Name == ToolCatalog.RequestAuthorization && root.TryGetProperty("memoryObservation", out var body) && body.ValueKind == JsonValueKind.Object) root = body;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("tenantMemoryRead", out var marker) || marker.ValueKind != JsonValueKind.True) continue;
            var values = root.GetProperty("memorySources").Deserialize<TenantMemorySource[]>() ?? throw new AccessDeniedException("memory_source_unavailable");
            if (values.Length > 11 || values.Any(s => s.TenantId == Guid.Empty || s.MemoryId == Guid.Empty || !TenantMemoryOperationRules.ValidHash(s.ContentHash))) throw new AccessDeniedException("memory_source_unavailable");
            sources.AddRange(values);
        }
        return TenantMemoryService.Merge([], sources);
    }
}
