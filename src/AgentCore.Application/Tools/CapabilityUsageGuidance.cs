using System.Text;
using AgentCore.Application.Ports;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Tools;

/// <summary>Small registry-derived orientation; executable schemas still use the normal projection.</summary>
public static class CapabilityUsageGuidance
{
    public const int MaxCatalogCharacters = 8192;
    public const string CatalogPrefix = "Eligible tools available on demand (schemas not loaded yet).";
    public const string OperatingInstructions = """
        Use an offered tool directly when it fits the task. Loading a Skill is not a prerequisite: Skills supply procedures, not permission or tool support. A tool missing from the current schemas is not evidence that the capability is unsupported. When capabilities.load is offered, discover a missing interface with a concrete goal or exact tool name before reporting it unavailable. After load_matched, call the loaded tool using its schema on the next request; after load_already_projected, call it directly. Follow nextStep and do not repeat ineffective queries. load_no_match means this query found no eligible match, not that every tool in the family is unsupported; load_unavailable means a current execution restriction. Tool results are authoritative for that operation: invalid arguments or a wrong/missing/ambiguous target require correction or a fresh observation, not a claim that the entire capability is unsupported. Recover only within the remaining budget and when no uncertain effect would be replayed. Stop on policy/provider denial or required human intervention and report the specific blocker. Loading never changes authorization, execution eligibility or approval requirements.
        """;

    public static string BuildCatalog(AgentDefinition definition, AgentContext context, IToolConfigurationGate gate,
        IReadOnlyList<ModelToolDefinition> offered, IReadOnlyList<ModelToolDefinition>? eligible = null)
    {
        if (!offered.Any(t => t.Name == ToolCatalog.CapabilitiesLoad)) return string.Empty;
        var projected = offered.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        var groups = (eligible ?? ToolCatalog.Eligible(definition, context, gate))
            .Where(t => !projected.Contains(t.Name) && ToolRegistry.Get(t.Name).Discoverable)
            .Select(t => ToolRegistry.Get(t.Name)).GroupBy(t => t.Category)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => g.OrderBy(t => t.Name, StringComparer.Ordinal).ToArray()).ToArray();
        if (groups.Length == 0) return string.Empty;

        var text = new StringBuilder(CatalogPrefix);
        text.Append(" Use capabilities.load; these descriptions are metadata, not callable schemas or grants.\n");
        text.Append("Families: ");
        text.AppendJoin(", ", groups.Select(g => $"{g[0].Category} ({g.Length})"));
        text.AppendLine(". Exact names below can be used as discovery queries.");
        const string clipped = "Additional entries omitted to keep this guide small. Discover by concrete goal within an eligible family.\n";
        // Round robin keeps every family visible even when a large registry reaches the text bound.
        for (var index = 0; index < groups.Max(g => g.Length); index++)
        {
            foreach (var group in groups.Where(g => index < g.Length))
            {
                var tool = group[index];
                var line = $"- {tool.Name}: {Brief(tool)}\n";
                if (text.Length + line.Length + clipped.Length > MaxCatalogCharacters)
                    return text.Append(clipped).ToString();
                text.Append(line);
            }
        }
        return text.ToString();
    }

    private static string Brief(ToolDescriptor tool)
    {
        var summary = tool.Summary;
        if (summary.StartsWith(tool.Name + ": ", StringComparison.Ordinal)) summary = summary[(tool.Name.Length + 2)..];
        const int limit = 120;
        return summary.Length <= limit ? summary : summary[..limit].TrimEnd() + "…";
    }
}
