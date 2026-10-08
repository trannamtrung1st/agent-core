using System.Text.Json;
using AgentCore.Application.Ports;

namespace AgentCore.Application.Tools;

internal static class BrowserSnapshotCompaction
{
    internal const int RecentFullObservations = 1;
    internal const int DuplicateReceiptChars = 240;
    internal const int PageEvidenceChars = 1200;

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

        if (full.Count > 0)
        {
            // Compact earlier discovery evidence in history; the provider retains live semantic Locators.
            for (var index = 0; index < full[^1]; index++)
                if (messages[index] is { Role: ModelRole.Tool, Name: "browser.find" } found)
                    messages[index] = found with { Text = Receipt(found.Text, false), Parts = null };
        }

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
        var url = string.Empty;
        var title = string.Empty;
        var visible = string.Empty;
        bool? settled = null;
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            url = Read(root, "url");
            title = Read(root, "title");
            visible = Read(root, "content");
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
                untrustedBrowserContent = true,
                compacted = true,
                url,
                title,
                settled = settledValue,
                contentExcerpt = excerpt
            })
            : JsonSerializer.Serialize(new
            {
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

    private static string Read(JsonElement root, string name) =>
        root.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : string.Empty;
}
