using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;

namespace AgentCore.Application.Tools;

internal static class BrowserSnapshotCompaction
{
    internal const int RecentFullObservations = 1;
    internal const int DuplicateReceiptChars = 240;
    internal const int PageEvidenceChars = 1200;
    internal const int RecentDiscoveries = 8;

    internal static void Compact(List<ModelMessage> messages)
    {
        // Reviewed commands carry the same native observation inside a consent receipt.
        // Compact only that untrusted observation; retain the exact consent/result facts
        // and source identifiers so current authority is still checked on every use.
        var projected = messages.ToList();
        var receipts = new Dictionary<int, JsonObject>();
        var calls = new Dictionary<string, ModelToolCall>(StringComparer.Ordinal);
        for (var index = 0; index < messages.Count; index++)
        {
            var message = messages[index];
            foreach (var call in message.ToolCalls ?? []) calls[call.Id] = call;
            if (message.Role != ModelRole.Tool || message.Name != ToolCatalog.RequestAuthorization
                || !calls.TryGetValue(message.ToolCallId ?? "", out var source)) continue;
            try
            {
                if (TenantApprovalToolContract.Parse(source).Browser is not { } command) continue;
                var receipt = JsonNode.Parse(message.Text)!.AsObject();
                if (receipt["approvalStatus"]?.GetValue<string>() != "approved" || receipt["outcome"]?.GetValue<string>() != "completed"
                    || receipt["commandCompleted"]?.GetValue<bool>() != true || receipt["replayAllowed"]?.GetValue<bool>() != false
                    || receipt["browser"] is not JsonObject native || native["observation"] is not JsonObject observation) continue;
                var flattened = observation.DeepClone().AsObject();
                foreach (var key in new[] { "untrustedBrowserContent", "status", "effectAttempted", "effectConfirmedBySdk", "applicationOutcomeVerified" })
                    flattened[key] = native[key]?.DeepClone();
                projected[index] = message with { Name = command.Tool, Text = flattened.ToJsonString() };
                receipts[index] = receipt;
            }
            catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException) { }
        }
        var before = projected.Select(m => m.Text).ToArray();
        CompactObservations(projected);
        for (var index = 0; index < messages.Count; index++)
        {
            if (!receipts.TryGetValue(index, out var receipt)) { messages[index] = projected[index]; continue; }
            if (projected[index].Text == before[index]) continue;
            receipt["browser"]!["observation"] = JsonNode.Parse(projected[index].Text);
            messages[index] = messages[index] with { Text = receipt.ToJsonString(), Parts = null };
        }
    }

    private static void CompactObservations(List<ModelMessage> messages)
    {
        var full = new List<int>();
        for (var index = 0; index < messages.Count; index++)
        {
            if (IsFullObservation(messages[index]))
            {
                full.Add(index);
            }
        }

        // Direct semantic actions need no stored refs. Keep recent discoveries
        // available across observations; failures and ambiguity retain their repair evidence.
        var discoveries = Enumerable.Range(0, messages.Count).Where(i => IsSuccessfulDiscovery(messages[i])).ToArray();
        foreach (var index in discoveries.Take(Math.Max(0, discoveries.Length - RecentDiscoveries)))
            messages[index] = messages[index] with { Text = DiscoveryReceipt(messages[index].Text), Parts = null };

        // Revisit compacted observations too: a previously useful page excerpt may
        // become superseded on a later round. Keep the latest excerpt per page.
        var compacted = Enumerable.Range(0, messages.Count).Where(i => IsCompactedObservation(messages[i])).ToArray();
        var all = compacted.Concat(full).Order().ToArray();
        var latest = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var index in all) latest[PageUrl(messages[index].Text, index)] = index;
        foreach (var index in compacted)
            if (latest[PageUrl(messages[index].Text, index)] != index)
                messages[index] = messages[index] with { Text = Receipt(messages[index].Text, false), Parts = null };
        if (full.Count <= RecentFullObservations) return;

        var keepFrom = full.Count - RecentFullObservations;
        for (var ordinal = 0; ordinal < keepFrom; ordinal++)
        {
            var index = full[ordinal];
            var message = messages[index];
            var urlKey = PageUrl(message.Text, index);
            var rich = latest[urlKey] == index;
            messages[index] = message with { Text = Receipt(message.Text, rich), Parts = null };
        }
    }

    private static bool IsSuccessfulDiscovery(ModelMessage message)
    {
        if (message is not { Role: ModelRole.Tool, Name: "browser.find" }) return false;
        try
        {
            using var document = JsonDocument.Parse(message.Text);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object && !root.TryGetProperty("error", out _)
                && (!root.TryGetProperty("status", out var status) || status.ValueKind == JsonValueKind.String && status.GetString() == "ok")
                && root.TryGetProperty("matches", out var matches) && matches.ValueKind == JsonValueKind.Array && matches.GetArrayLength() > 0;
        }
        catch (JsonException) { return false; }
    }

    private static string DiscoveryReceipt(string text)
    {
        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;
        return PreserveReadScope(text, JsonSerializer.Serialize(new
        {
            untrustedBrowserContent = true, compacted = true, status = "ok",
            matchCount = root.TryGetProperty("matchCount", out var count) && count.ValueKind == JsonValueKind.Number && count.TryGetInt32(out var value) ? value : root.GetProperty("matches").GetArrayLength(),
            guidance = "An earlier browser.find search succeeded; its detailed evidence was compacted. Observe or act using current direct semantic targets."
        }));
    }

    private static bool IsCompactedObservation(ModelMessage message)
    {
        if (message.Role != ModelRole.Tool || message.Name?.StartsWith("browser.", StringComparison.Ordinal) != true) return false;
        try
        {
            using var doc = JsonDocument.Parse(message.Text);
            return doc.RootElement.TryGetProperty("compacted", out var compacted) && compacted.ValueKind == JsonValueKind.True
                && doc.RootElement.TryGetProperty("contentExcerpt", out _);
        }
        catch (JsonException) { return false; }
    }

    private static bool IsFullObservation(ModelMessage message)
    {
        if (message.Role != ModelRole.Tool
            || message.Name is null
            || !message.Name.StartsWith("browser.", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(message.Text)
            || message.Text[0] != '{')
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(message.Text);
            var root = document.RootElement;
            return !root.TryGetProperty("error", out _) && root.TryGetProperty("untrustedBrowserContent", out var marker)
                && marker.ValueKind == JsonValueKind.True
                && root.TryGetProperty("targets", out var elements)
                && elements.ValueKind == JsonValueKind.Array;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string PageUrl(string text, int index)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            var url = Read(document.RootElement, "url");
            return url.Length == 0 ? $"missing:{index}" : url;
        }
        catch (JsonException)
        {
            return $"missing:{index}";
        }
    }

    private static string Receipt(string text, bool rich)
    {
        var status = "unknown";
        var url = string.Empty;
        var title = string.Empty;
        var visible = string.Empty;
        bool? settled = null;
        BrowserInterventionKind? intervention = null;
        bool? effectAttempted = null, effectConfirmedBySdk = null, applicationOutcomeVerified = null;
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            status = Read(root, "status") == "ok" ? "ok" : "unknown";
            url = Read(root, "url");
            title = Read(root, "title");
            visible = Read(root, "content");
            if (visible.Length == 0) visible = Read(root, "contentExcerpt");
            effectAttempted = ReadBoolean(root, "effectAttempted");
            effectConfirmedBySdk = ReadBoolean(root, "effectConfirmedBySdk");
            applicationOutcomeVerified = ReadBoolean(root, "applicationOutcomeVerified");
            if (root.TryGetProperty("intervention", out var control) && control.ValueKind == JsonValueKind.Number
                && control.TryGetInt32(out var kind) && Enum.IsDefined((BrowserInterventionKind)kind)
                && kind != (int)BrowserInterventionKind.None)
                intervention = (BrowserInterventionKind)kind;
            if (root.TryGetProperty("settled", out var settledProperty)
                && settledProperty.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                settled = settledProperty.GetBoolean();
            }
        }
        catch (JsonException)
        {
        }

        var excerpt = Excerpt(visible, rich);
        var result = settled is bool settledValue
            ? JsonSerializer.Serialize(new
            {
                status, effectAttempted, effectConfirmedBySdk, applicationOutcomeVerified,
                untrustedBrowserContent = true,
                compacted = true,
                url,
                title,
                settled = settledValue,
                contentExcerpt = excerpt
            })
            : JsonSerializer.Serialize(new
            {
                status, effectAttempted, effectConfirmedBySdk, applicationOutcomeVerified,
                untrustedBrowserContent = true,
                compacted = true,
                url,
                title,
                contentExcerpt = excerpt
            });
        if (intervention is { } retained)
        {
            var receipt = JsonNode.Parse(result)!.AsObject();
            receipt["intervention"] = (int)retained;
            result = receipt.ToJsonString();
        }
        return PreserveReadScope(text, result);
    }

    private static string PreserveReadScope(string source, string receipt)
    {
        using var doc = JsonDocument.Parse(source);
        if (ReadBoolean(doc.RootElement, "tenantBrowserRead") != true) return receipt;
        var result = System.Text.Json.Nodes.JsonNode.Parse(receipt)!.AsObject();
        result["tenantBrowserRead"] = true; result["readCommandHash"] = Read(doc.RootElement, "readCommandHash");
        return result.ToJsonString();
    }

    private static string Excerpt(string visible, bool rich)
    {
        var budget = rich ? PageEvidenceChars : DuplicateReceiptChars;
        if (visible.Length <= budget)
        {
            return visible;
        }

        if (!rich)
        {
            return visible[..budget];
        }

        const string marker = "\n…\n";
        var edge = (budget - marker.Length) / 2;
        return visible[..edge] + marker + visible[^edge..];
    }

    private static bool? ReadBoolean(JsonElement root, string name) => root.TryGetProperty(name, out var value)
        && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;

    private static string Read(JsonElement root, string name) =>
        root.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : string.Empty;
}
