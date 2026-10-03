using System.Text.Json;

namespace AgentCore.Application.Tools;

/// <summary>
/// Counts successful browser results that repeat the same page URL and visible text.
/// Element refs are ignored because every observation regenerates them.
/// </summary>
internal sealed class BrowserEvidenceProgress
{
    internal const int StopAfterRepeatedEvidence = 2;

    private string? _url;
    private string? _visible;

    internal int Repeated { get; private set; }

    internal bool ShouldStop => Repeated >= StopAfterRepeatedEvidence;

    internal void Reset()
    {
        _url = null;
        _visible = null;
        Repeated = 0;
    }

    internal void Note(string tool, string? json)
    {
        if (tool is not (ToolCatalog.BrowserNavigate or ToolCatalog.BrowserObserve or ToolCatalog.BrowserAct))
        {
            return;
        }

        if (!TryRead(json, out var url, out var visible))
        {
            return;
        }

        if (_url is not null
            && string.Equals(_url, url, StringComparison.Ordinal)
            && string.Equals(_visible, visible, StringComparison.Ordinal))
        {
            Repeated++;
            return;
        }

        _url = url;
        _visible = visible;
        Repeated = 0;
    }

    private static bool TryRead(string? json, out string url, out string visible)
    {
        url = string.Empty;
        visible = string.Empty;
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

            url = root.TryGetProperty("url", out var urlProperty) && urlProperty.ValueKind == JsonValueKind.String
                ? urlProperty.GetString() ?? string.Empty
                : string.Empty;
            visible = root.TryGetProperty("visibleText", out var textProperty) && textProperty.ValueKind == JsonValueKind.String
                ? textProperty.GetString() ?? string.Empty
                : string.Empty;
            return url.Length > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
