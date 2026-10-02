using System.Text.Json;
using AgentCore.Application.Ports;

namespace AgentCore.Application.Tools;

internal static class BrowserObservationCompaction
{
    internal const int RecentFullObservations = 3;
    internal const int ReceiptVisibleText = 240;

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

        if (full.Count <= RecentFullObservations)
        {
            return;
        }

        var keepFrom = full.Count - RecentFullObservations;
        for (var ordinal = 0; ordinal < keepFrom; ordinal++)
        {
            var index = full[ordinal];
            var message = messages[index];
            messages[index] = message with { Text = Receipt(message.Text), Parts = null };
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
                && root.TryGetProperty("elements", out var elements)
                && elements.ValueKind == JsonValueKind.Array;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string Receipt(string text)
    {
        var url = string.Empty;
        var title = string.Empty;
        var visible = string.Empty;
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            url = Read(root, "url");
            title = Read(root, "title");
            visible = Read(root, "visibleText");
        }
        catch (JsonException)
        {
        }

        if (visible.Length > ReceiptVisibleText)
        {
            visible = visible[..ReceiptVisibleText];
        }

        return JsonSerializer.Serialize(new
        {
            untrustedBrowserContent = true,
            compacted = true,
            url,
            title,
            visibleText = visible
        });
    }

    private static string Read(JsonElement root, string name) =>
        root.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : string.Empty;
}
