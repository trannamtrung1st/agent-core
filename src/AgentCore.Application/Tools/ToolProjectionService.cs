using AgentCore.Application.Ports;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Tools;

public static class ToolProjectionService
{
    public static IReadOnlyList<ModelToolDefinition> Project(AgentDefinition definition, AgentContext? context, IToolConfigurationGate gate)
    {
        var eligible = ToolCatalog.Eligible(definition, context, gate);
        if (definition.Environment?.Capabilities is null) return eligible;
        var always = definition.Environment.Projection?.AlwaysCapabilities ?? [];
        var skill = (context?.PinnedSkillCatalog ?? []).Where(s => context?.ActiveSkillKeys?.Contains(s.Key, StringComparer.Ordinal) == true)
            .SelectMany(s => s.RequiredCapabilities ?? []).ToHashSet(StringComparer.Ordinal);
        var loaded = context?.LoadedCapabilityIds ?? [];
        return eligible.Where(t => t.Name == ToolCatalog.CapabilitiesLoad
            || always.Contains(t.Name, StringComparer.Ordinal) || skill.Contains(t.Name)
            || loaded.Contains(t.Name, StringComparer.Ordinal)
            || !ToolRegistry.Get(t.Name).Discoverable)
            .DistinctBy(t => t.Name).OrderBy(t => t.Name, StringComparer.Ordinal).ToArray();
    }
}
