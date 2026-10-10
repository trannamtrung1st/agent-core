using System.Text;
using System.Text.Json.Nodes;

namespace AgentCore.Application.Tools;

internal static class BrowserCaptureProjection
{
    public static string Fit(int budget, string json)
    {
        if (Encoding.UTF8.GetByteCount(json) <= budget) return json;
        var root = JsonNode.Parse(json)!.AsObject();
        if (root["observation"] is { } observation)
        {
            root.Remove("observation");
            root["observationTruncated"] = true;
            var headroom = budget - Encoding.UTF8.GetByteCount(root.ToJsonString()) - 20;
            var fitted = BrowserResultProjection.Fit(headroom, observation.ToJsonString());
            if (fitted is not null) root["observation"] = JsonNode.Parse(fitted);
        }
        if (Encoding.UTF8.GetByteCount(root.ToJsonString()) > budget) root.Remove("guidance");
        if (Encoding.UTF8.GetByteCount(root.ToJsonString()) > budget) root.Remove("observation");
        return ToolJsonResults.FitToBudget(budget, root.ToJsonString());
    }
}
