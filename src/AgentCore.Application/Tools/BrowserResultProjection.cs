using System.Text;
using System.Text.Json.Nodes;

namespace AgentCore.Application.Tools;

/// <summary>Presentation only: native semantic evidence fits the current byte budget.</summary>
internal static class BrowserResultProjection
{
    internal static string? Fit(int budget, string json)
    {
        JsonObject? root;
        try { root = JsonNode.Parse(json) as JsonObject; }
        catch (System.Text.Json.JsonException) { return null; }
        if (root?["untrustedBrowserContent"]?.ToString() != "true"
            || root["error"] is not null && root["error"]?.ToString() != "ambiguous_target") return null;
        var key = root["targets"] is JsonArray ? "targets" : root["matches"] is JsonArray ? "matches" : null;
        if (key is null) return null;
        var source = (JsonArray)root[key]!;
        var entries = source.OfType<JsonObject>().ToArray();
        var content = root["content"]?.GetValue<string>() ?? "";
        root["truncated"] = true;
        root["hasMore"] = true;
        root["guidance"] = "Act using a direct semantic target; use optional browser.find for deep content, within for duplicate controls.";
        root[key] = new JsonArray();
        if (key == "targets") { root["content"] = ""; root["truncated"] = true; }
        root.Remove("boxes");
        // Keep success and identity even when the remaining headroom cannot fit long metadata.
        foreach (var name in new[] { "url", "title" })
            if (root[name] is JsonValue value && value.TryGetValue<string>(out var text))
                root[name] = ToolJsonResults.ClipUtf8Prefix(text, Math.Max(32, budget / 8));
        var minimal = root.ToJsonString();
        if (Bytes(minimal) > budget)
        {
            root["guidance"] = "Observe or use a direct target.";
            minimal = root.ToJsonString();
            foreach (var optional in new[] { "scope", "frames", "title", "url" })
            {
                if (Bytes(minimal) <= budget) break;
                if (optional is "title" or "url") root[optional] = ToolJsonResults.ClipUtf8Prefix(root[optional]?.GetValue<string>() ?? "", 32);
                else root.Remove(optional);
                minimal = root.ToJsonString();
            }
            if (Bytes(minimal) > budget) return null; // A metadata envelope is impossible in this headroom.
        }
        if (key == "targets")
        {
            // Measure the serialized envelope, rather than reserving eight bytes for
            // every content byte. Native ARIA text is the observation itself; that
            // conservative estimate hid table values even in narrowly scoped reads.
            var low = 0;
            var high = Encoding.UTF8.GetByteCount(content);
            while (low < high)
            {
                var middle = low + (high - low + 1) / 2;
                root["content"] = ToolJsonResults.ClipUtf8Prefix(content, middle);
                if (Bytes(root.ToJsonString()) <= budget) low = middle;
                else high = middle - 1;
            }
            root["content"] = ToolJsonResults.ClipUtf8Prefix(content, low);
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
            if (root.ContainsKey("nextOffset")) root["nextOffset"] = (root["offset"]?.GetValue<int>() ?? 0) + selected.Count;
        }
        var result = root.ToJsonString();
        // Additional match metadata consumes budget too.
        while (Bytes(result) > budget && selected.Count > 0)
        {
            selected.RemoveAt(selected.Count - 1);
            if (key == "matches")
            {
                root["returnedCount"] = selected.Count;
                if (root.ContainsKey("nextOffset")) root["nextOffset"] = (root["offset"]?.GetValue<int>() ?? 0) + selected.Count;
            }
            result = root.ToJsonString();
        }
        return Bytes(result) <= budget ? result : null;
    }

    private static int Bytes(string value) => Encoding.UTF8.GetByteCount(value);
}
