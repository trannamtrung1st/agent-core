using System.Globalization;
using System.Text;
using AgentCore.Application.Ports;

namespace AgentCore.Application.Admin;

public static class DefinitionEvaluationHarness
{
    public const string SystemPrefix = "DefinitionEvaluationHarness";

    public static string BuildDirective(DefinitionEvaluationScenario scenario)
    {
        var builder = new StringBuilder(SystemPrefix);
        builder.Append(CultureInfo.InvariantCulture, $" check={scenario.CheckType}");
        if (!string.IsNullOrWhiteSpace(scenario.ToolName))
        {
            builder.Append(CultureInfo.InvariantCulture, $" tool={scenario.ToolName}");
        }

        return builder.ToString();
    }

    public static string BuildResourceManifest(IReadOnlyList<DefinitionDraftSyntheticResourceSnapshot> resources)
    {
        if (resources.Count == 0)
        {
            return "Draft evaluation resources: (none)";
        }

        var lines = resources
            .OrderBy(resource => resource.LogicalPath, StringComparer.Ordinal)
            .Select(resource =>
            {
                var excerpt = Encoding.UTF8.GetString(resource.Content.Span);
                if (excerpt.Length > 120)
                {
                    excerpt = excerpt[..120];
                }

                return $"{resource.LogicalPath} sha256={resource.ContentSha256} bytes={resource.Content.Length} excerpt={excerpt}";
            });
        return "Draft evaluation resources:\n" + string.Join('\n', lines);
    }
}
