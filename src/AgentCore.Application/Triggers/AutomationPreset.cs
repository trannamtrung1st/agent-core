using AgentCore.Domain.Triggers;

namespace AgentCore.Application.Triggers;

public sealed record AutomationPreset(string PresetId, int PresetVersion, string Name, string Description, string Instructions,
    string TriggerKind, string? CoreEventKey = null, string? FilterExpression = null, EventDispatch? Dispatch = null);
public sealed record AutomationPresetOption(AutomationPreset Template, bool Eligible, IReadOnlyList<string> Prerequisites);
