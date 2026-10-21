using System.Text.Json;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Experience;

namespace AgentCore.Application.Security;

public static class TenantExperienceReferences
{
    public static IReadOnlyList<T> Merge<T>(IReadOnlyList<T>? prior, IReadOnlyList<T>? next)
    {
        var values = (prior ?? []).Concat(next ?? []).Distinct().ToArray(); if (values.Length > 100) throw new AccessDeniedException("experience_context_capacity"); return Array.AsReadOnly(values);
    }
    public static IReadOnlyList<TenantExperienceSourceReference> Sources(AgentRunCheckpoint? checkpoint) => Merge(
        AgentRunToolCallCheckpoint.ReadExecutionState(checkpoint).TenantExperienceSourceReads,
        AgentRunToolCallCheckpoint.TryRead(checkpoint, out var messages) ? Sources(messages!) : []);
    public static IReadOnlyList<TenantExperienceFactSource> Facts(AgentRunCheckpoint? checkpoint) => Merge(
        AgentRunToolCallCheckpoint.ReadExecutionState(checkpoint).TenantExperienceSources,
        AgentRunToolCallCheckpoint.TryRead(checkpoint, out var messages) ? Facts(messages!) : []);
    private static IEnumerable<JsonElement> Observations(IEnumerable<ModelMessage> messages)
    {
        foreach (var message in messages.Where(m => m.Role == ModelRole.Tool && (m.Name is ToolCatalog.ContinuityGet or ToolCatalog.ContinuitySearch or ToolCatalog.RequestAuthorization || m.Name == TenantExperienceToolContract.Source)))
        {
            using var doc = JsonDocument.Parse(message.Text); var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) continue;
            if (message.Name == ToolCatalog.RequestAuthorization)
            {
                if (root.TryGetProperty("experienceSourceObservation", out var source) && source.ValueKind == JsonValueKind.Object) root = source;
                else if (root.TryGetProperty("continuityObservation", out var continuity) && continuity.ValueKind == JsonValueKind.Object) root = continuity;
            }
            yield return root.Clone();
        }
    }
    public static IReadOnlyList<TenantExperienceSourceReference> Sources(IEnumerable<ModelMessage> messages)
    {
        var values = new List<TenantExperienceSourceReference>();
        foreach (var root in Observations(messages))
        {
            if (!root.TryGetProperty("tenantExperienceSourceRead", out var marker) || marker.ValueKind != JsonValueKind.True) continue;
            var value = root.GetProperty("sourceWitness").Deserialize<TenantExperienceSourceReference>() ?? throw new AccessDeniedException("experience_source_unavailable");
            if (value.TenantId == Guid.Empty) throw new AccessDeniedException("experience_source_unavailable"); TenantExperienceRules.SourceShape(value.Witness); values.Add(value);
        }
        return Merge([], values);
    }
    public static IReadOnlyList<TenantExperienceFactSource> Facts(IEnumerable<ModelMessage> messages)
    {
        var values = new List<TenantExperienceFactSource>();
        foreach (var root in Observations(messages))
        {
            if (!root.TryGetProperty("tenantExperienceRead", out var marker) || marker.ValueKind != JsonValueKind.True) continue;
            var current = root.GetProperty("experienceSources").Deserialize<TenantExperienceFactSource[]>() ?? throw new AccessDeniedException("experience_source_unavailable");
            if (current.Length > 11 || current.Any(i => i.TenantId == Guid.Empty || i.ExperienceId == Guid.Empty || i.Revision < 1 || !TenantMemoryOperationRules.ValidHash(i.ContentHash)))
                throw new AccessDeniedException("experience_source_unavailable"); values.AddRange(current);
        }
        return Merge([], values);
    }
    public static TenantExperienceSourceWitness RecordedSource(TenantMemoryReader reader, ExperienceSourceKind kind, Guid id, long cursor)
    {
        var values = Sources(reader.Run.Checkpoint).Where(i => i.TenantId == reader.Security.Tenant.TenantId && i.Witness.Kind == kind && i.Witness.SourceId == id && i.Witness.ThroughCursor == cursor).Select(i => i.Witness).Distinct().ToArray();
        if (values.Length != 1) throw new AccessDeniedException("experience_source_inspection_required"); return values[0];
    }
}
