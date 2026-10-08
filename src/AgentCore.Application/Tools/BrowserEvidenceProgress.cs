using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentCore.Application.Tools;

/// <summary>
/// Counts consecutive successful observations that repeat the same page evidence.
/// Navigation and browser actions reset the streak. Element refs are ignored.
/// </summary>
internal sealed class BrowserEvidenceProgress
{
    internal const int StopAfterRepeatedEvidence = 2;

    private string? _fingerprint;

    internal int Repeated { get; private set; }

    internal bool ShouldStop => Repeated >= StopAfterRepeatedEvidence;

    internal void Reset()
    {
        _fingerprint = null;
        Repeated = 0;
    }

    internal void Note(string tool, string? json)
    {
        if ((tool == ToolCatalog.BrowserNavigate || BrowserToolCatalog.IsInteraction(tool)))
        {
            if (IsSuccess(json))
            {
                Reset();
            }

            return;
        }

        if (tool is not (ToolCatalog.BrowserSnapshot or ToolCatalog.BrowserWait) || !TryFingerprint(json, out var fingerprint))
        {
            return;
        }

        if (_fingerprint is not null && string.Equals(_fingerprint, fingerprint, StringComparison.Ordinal))
        {
            Repeated++;
            return;
        }

        _fingerprint = fingerprint;
        Repeated = 0;
    }

    private static bool IsSuccess(string? json) => TryFingerprint(json, out _);

    private static bool TryFingerprint(string? json, out string fingerprint)
    {
        fingerprint = string.Empty;
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (root.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.String
                && !string.IsNullOrEmpty(error.GetString()))
            {
                return false;
            }

            if (!root.TryGetProperty("url", out var urlProperty)
                || urlProperty.ValueKind != JsonValueKind.String
                || string.IsNullOrEmpty(urlProperty.GetString()))
            {
                return false;
            }

            var settled = root.TryGetProperty("settled", out var settledProperty)
                && settledProperty.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? settledProperty.GetBoolean() ? "true" : "false"
                    : string.Empty;
            var visible = Read(root, "visibleText");
            var content = Regex.Replace(Read(root, "content"), @"\s*\[ref=[^\]]+\]", "");
            var builder = new StringBuilder();
            builder.Append(content).Append('\n');
            builder.Append(urlProperty.GetString()).Append('\n').Append(settled).Append('\n').Append(visible).Append('\n');
            if (root.TryGetProperty("elements", out var elements) && elements.ValueKind == JsonValueKind.Array)
            {
                var lines = new List<string>();
                foreach (var element in elements.EnumerateArray())
                {
                    if (element.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    lines.Add(string.Join(
                        "|",
                        Read(element, "role"),
                        Read(element, "name"),
                        Actions(element),
                        State(element, "value"),
                        State(element, "checked"),
                        State(element, "selectedText")));
                }

                lines.Sort(StringComparer.Ordinal);
                foreach (var line in lines)
                {
                    builder.Append(line).Append('\n');
                }
            }

            fingerprint = builder.ToString();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string Actions(JsonElement element)
    {
        if (!element.TryGetProperty("actions", out var actions) || actions.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var names = new List<string>();
        foreach (var action in actions.EnumerateArray())
        {
            if (action.ValueKind == JsonValueKind.String && action.GetString() is { Length: > 0 } name)
            {
                names.Add(name);
            }
        }

        names.Sort(StringComparer.Ordinal);
        return string.Join(",", names);
    }

    private static string State(JsonElement element, string name)
    {
        if (!element.TryGetProperty("state", out var state) || state.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        if (!state.TryGetProperty(name, out var property))
        {
            return string.Empty;
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString() ?? string.Empty,
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Number => property.GetRawText(),
            _ => string.Empty
        };
    }

    private static string Read(JsonElement root, string name) =>
        root.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : string.Empty;
}
