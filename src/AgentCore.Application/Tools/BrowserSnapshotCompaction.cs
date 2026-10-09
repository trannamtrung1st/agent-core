using System.Text.Json;
using AgentCore.Application.Ports;

namespace AgentCore.Application.Tools;

internal static class BrowserSnapshotCompaction
{
    internal const int RecentFullObservations = 1;
    internal const int DuplicateReceiptChars = 240;
    internal const int PageEvidenceChars = 1200;
    internal const int RecentDiscoveries = 8;

    internal static void Compact(List<ModelMessage> messages)
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

        if (full.Count <= RecentFullObservations)
        {
            return;
        }

        var keepFrom = full.Count - RecentFullObservations;
        var latestByUrl = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var ordinal = 0; ordinal < keepFrom; ordinal++)
        {
            var index = full[ordinal];
            latestByUrl[PageUrl(messages[index].Text, index)] = index;
        }

        for (var ordinal = 0; ordinal < keepFrom; ordinal++)
        {
            var index = full[ordinal];
            var message = messages[index];
            var urlKey = PageUrl(message.Text, index);
            var rich = latestByUrl[urlKey] == index;
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
        return JsonSerializer.Serialize(new
        {
            untrustedBrowserContent = true, compacted = true, status = "ok",
            matchCount = root.TryGetProperty("matchCount", out var count) && count.ValueKind == JsonValueKind.Number && count.TryGetInt32(out var value) ? value : root.GetProperty("matches").GetArrayLength(),
            guidance = "An earlier browser.find search succeeded; its detailed evidence was compacted. Observe or act using current direct semantic targets."
        });
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
            return root.TryGetProperty("untrustedBrowserContent", out var marker)
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
        bool? effectAttempted = null, effectConfirmedBySdk = null, applicationOutcomeVerified = null;
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            status = Read(root, "status") == "ok" ? "ok" : "unknown";
            url = Read(root, "url");
            title = Read(root, "title");
            visible = Read(root, "content");
            effectAttempted = ReadBoolean(root, "effectAttempted");
            effectConfirmedBySdk = ReadBoolean(root, "effectConfirmedBySdk");
            applicationOutcomeVerified = ReadBoolean(root, "applicationOutcomeVerified");
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
        return settled is bool settledValue
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
