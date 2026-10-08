using System.Text;
using System.Text.Json.Nodes;

namespace AgentCore.Application.Tools;

/// <summary>Presentation only: provider refs and the search index survive byte-budget projection.</summary>
internal static class BrowserResultProjection
{
    internal static string? Fit(int budget, string json)
    {
        JsonObject? root;
        try { root = JsonNode.Parse(json) as JsonObject; }
        catch (System.Text.Json.JsonException) { return null; }
        if (root?["untrustedBrowserContent"]?.ToString() != "true" || root["snapshotId"] is null
            || root["error"] is not null) return null;
        var key = root["elements"] is JsonArray ? "elements" : root["matches"] is JsonArray ? "matches" : null;
        if (key is null) return null;
        var source = (JsonArray)root[key]!;
        var entries = (key == "elements" ? source.OfType<JsonObject>().OrderByDescending(Priority) : source.OfType<JsonObject>()).ToArray();
        var content = root["content"]?.GetValue<string>() ?? "";
        var visible = root["visibleText"]?.GetValue<string>() ?? "";
        root["truncated"] = true;
        root["hasMore"] = true;
        root["indexAvailable"] ??= true;
        root["guidance"] = "Use browser.find to search beyond this projection; inspect a current targetRef before acting.";
        root[key] = new JsonArray();
        if (key == "elements") { root["content"] = ""; root["visibleText"] = ""; root["textTruncated"] = true; }
        root.Remove("boxes");
        // Keep success and identity even when the remaining headroom cannot fit long metadata.
        foreach (var name in new[] { "url", "title" })
            if (root[name] is JsonValue value && value.TryGetValue<string>(out var text))
                root[name] = ToolJsonResults.ClipUtf8Prefix(text, Math.Max(32, budget / 8));
        var minimal = root.ToJsonString();
        if (Bytes(minimal) > budget)
        {
            root["guidance"] = "Use browser.find.";
            minimal = root.ToJsonString();
            foreach (var optional in new[] { "scope", "capturedNodeCount", "indexedCount", "title", "url" })
            {
                if (Bytes(minimal) <= budget) break;
                if (optional is "title" or "url") root[optional] = ToolJsonResults.ClipUtf8Prefix(root[optional]?.GetValue<string>() ?? "", 32);
                else root.Remove(optional);
                minimal = root.ToJsonString();
            }
            if (Bytes(minimal) > budget) return null; // A metadata envelope is impossible in this headroom.
        }
        var reserve = Math.Max(0, budget - Bytes(minimal));
        if (key == "elements")
        {
            root["content"] = ToolJsonResults.ClipUtf8Prefix(content, reserve / 8);
            root["visibleText"] = ToolJsonResults.ClipUtf8Prefix(visible, reserve / 8);
            // JSON escaping may use more bytes than the decoded strings.
            while (Bytes(root.ToJsonString()) > budget)
            {
                root["content"] = ToolJsonResults.ClipUtf8Prefix(root["content"]!.GetValue<string>(), reserve / 16);
                root["visibleText"] = ToolJsonResults.ClipUtf8Prefix(root["visibleText"]!.GetValue<string>(), reserve / 16);
                reserve /= 2;
            }
        }
        var selected = (JsonArray)root[key]!;
        foreach (var entry in entries)
        {
            selected.Add(entry.DeepClone());
            if (Bytes(root.ToJsonString()) > budget) { selected.RemoveAt(selected.Count - 1); if (key == "matches") break; continue; }
        }
        if (key == "matches")
        {
            root["returnedCount"] = selected.Count;
            root["nextOffset"] = (root["offset"]?.GetValue<int>() ?? 0) + selected.Count;
        }
        var result = root.ToJsonString();
        // Additional match metadata consumes budget too.
        while (Bytes(result) > budget && selected.Count > 0)
        {
            selected.RemoveAt(selected.Count - 1);
            if (key == "matches")
            {
                root["returnedCount"] = selected.Count;
                root["nextOffset"] = (root["offset"]?.GetValue<int>() ?? 0) + selected.Count;
            }
            result = root.ToJsonString();
        }
        return Bytes(result) <= budget ? result : null;
    }

    private static int Priority(JsonObject node)
    {
        var role = node["role"]?.ToString();
        return (node["state"] is not null ? 20 : 0) + (role is "textbox" or "searchbox" or "button" or "link" or "combobox" ? 10 : 0)
            + (node["actions"] is JsonArray { Count: > 0 } ? 5 : 0);
    }
    private static int Bytes(string value) => Encoding.UTF8.GetByteCount(value);
}
