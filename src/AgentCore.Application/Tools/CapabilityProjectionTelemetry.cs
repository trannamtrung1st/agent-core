using AgentCore.Application.Ports;
using AgentCore.Application.Observability;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Tools;

public static class CapabilityProjectionTelemetry
{
    public static void Record(AgentDefinition definition, AgentContext? context, IToolConfigurationGate gate,
        IReadOnlyList<ModelToolDefinition>? tools, string? model, int loadCalls = 0)
    {
        var projected = tools ?? [];
        var names = projected.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        var skills = definition.SkillList.Where(s => context?.ActiveSkillIds?.Contains(s.Id) == true).SelectMany(s => s.RequiredCapabilities ?? []).ToHashSet();
        var always = definition.Environment?.Projection?.AlwaysCapabilities ?? [];
        var loaded = context?.LoadedCapabilityIds ?? [];
        void Measure(string kind, long count) => RuntimeTelemetry.RecordCapabilityProjection(kind, count, definition.ProviderPreferences.LanguageModel, model);
        Measure("authorizedCapabilityCount", RoleEnvironments.Of(definition).ToolList.Count);
        Measure("eligibleCapabilityCount", definition.Environment?.Capabilities is null ? projected.Count : ToolCatalog.Eligible(definition, context, gate).Count);
        Measure("projectedCapabilityCount", projected.Count);
        Measure("projectedToolSchemaBytes", System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(projected).LongLength);
        Measure("capabilityLoadInvocationCount", loadCalls);
        Measure("coreBootstrapCount", names.Count(n => n is ToolCatalog.CapabilitiesLoad or ToolCatalog.SkillsLoad));
        Measure("definitionAlwaysCount", always.Count(names.Contains));
        Measure("skillProjectedCount", skills.Count(names.Contains));
        Measure("loadedProjectedCount", loaded.Count(names.Contains));
        Measure("contextProjectedCount", names.Count(n => n is not (ToolCatalog.CapabilitiesLoad or ToolCatalog.SkillsLoad) && !always.Contains(n) && !skills.Contains(n) && !loaded.Contains(n)));
    }
}
