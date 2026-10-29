using System.Text.Json;
using AgentCore.Application.Ports;

namespace AgentCore.Application.Tools;

/// <summary>Projects continuation tools and observes explicit pump-local origin evidence.
/// Does not authorize execution, perform I/O, or own Session state.</summary>
internal static class BrowserContinuationPolicy
{
    internal static ModelRequest ProjectTools(ModelRequest request, bool pageBlocked, bool terminalOnly, bool dialogPending = false)
    {
        if (terminalOnly)
        {
            return request with
            {
                Tools = null,
                ToolChoice = ModelToolChoice.Auto,
                ToolChoiceName = null
            };
        }

        if ((!pageBlocked && !dialogPending) || request.Tools is not { Count: > 0 } tools)
        {
            return request;
        }

        bool Allowed(string name) => (!dialogPending || !name.StartsWith("browser.", StringComparison.Ordinal) || BrowserEvidenceProgress.DialogRecoveryTool(name))
            && (!pageBlocked || name is not (ToolCatalog.BrowserSnapshot or ToolCatalog.BrowserFind) && !BrowserToolCatalog.IsInteraction(name));
        var offered = tools.Where(tool => Allowed(tool.Name))
            .Select(tool => BrowserControlEvidence.RestrictAuthorization(tool, Allowed)).OfType<ModelToolDefinition>().ToArray();
        if (offered.SequenceEqual(tools))
        {
            return request;
        }

        return request with
        {
            Tools = offered.Length == 0 ? null : offered,
            ToolChoice = ModelToolChoice.Auto,
            ToolChoiceName = null
        };
    }

    internal static bool TryRefuse(
        ModelToolCall call,
        HashSet<string> blockedOrigins,
        string? pageOrigin,
        out string json)
    {
        json = string.Empty;
        if (blockedOrigins.Count == 0
            || !ToolCatalog.IsBrowserTool(call.Name) || call.Name is ToolCatalog.BrowserClose or ToolCatalog.BrowserTabs)
        {
            return false;
        }

        if (string.Equals(call.Name, ToolCatalog.BrowserNavigate, StringComparison.Ordinal))
        {
            if (!TryNavigateOrigin(call.ArgumentsJson, out var origin) || !blockedOrigins.Contains(origin))
            {
                return false;
            }

            json = BlockedBrowserJson(origin);
            return true;
        }

        if (pageOrigin is null || !blockedOrigins.Contains(pageOrigin))
        {
            return false;
        }

        json = BlockedBrowserJson(pageOrigin);
        return true;
    }

    internal static bool Observe(
        string tool,
        string? argumentsJson,
        string? json,
        HashSet<string> blockedOrigins,
        ref string? pageOrigin)
    {
        if (string.Equals(tool, ToolCatalog.BrowserClose, StringComparison.Ordinal))
        {
            if (TryBrowserCloseStatus(json) is "closed" or "already_closed")
            {
                pageOrigin = null;
                return true;
            }

            return false;
        }

        string? finalOrigin = null;
        string? error = null;
        if (TryBrowserResultUrl(json, out var url, out error) && TryBrowserOrigin(url, out var parsedOrigin))
        {
            finalOrigin = parsedOrigin;
        }

        var intervention = finalOrigin is not null
            && string.Equals(error, "user_intervention_required", StringComparison.Ordinal);
        if (intervention)
        {
            blockedOrigins.Add(finalOrigin!);
            pageOrigin = finalOrigin;
            if (string.Equals(tool, ToolCatalog.BrowserNavigate, StringComparison.Ordinal)
                && TryNavigateOrigin(argumentsJson, out var requested))
            {
                blockedOrigins.Add(requested);
            }

            return false;
        }

        if (finalOrigin is not null
            && error is null
            && ToolCatalog.IsBrowserTool(tool))
        {
            pageOrigin = finalOrigin;
        }

        return false;
    }

    internal static bool SuccessfulUnblockedResult(
        string tool,
        string? json,
        string? pageOrigin,
        HashSet<string> blockedOrigins) =>
        ToolCatalog.IsBrowserTool(tool)
        && pageOrigin is not null
        && !blockedOrigins.Contains(pageOrigin)
        && !BrowserResultHasError(json);

    private static bool BrowserResultHasError(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return true;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return true;
            }

            return document.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.String
                && !string.IsNullOrEmpty(error.GetString());
        }
        catch (JsonException)
        {
            return true;
        }
    }

    private static string? TryBrowserCloseStatus(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("status", out var status)
                || status.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            return status.GetString();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string BlockedBrowserJson(string origin) =>
        JsonSerializer.Serialize(new
        {
            error = "user_intervention_required",
            url = origin,
            message = "This site is waiting for you. Other sites can still be opened in this turn."
        });

    private static bool TryNavigateOrigin(string? argumentsJson, out string origin)
    {
        origin = string.Empty;
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            if (!document.RootElement.TryGetProperty("url", out var url)
                || url.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            return TryBrowserOrigin(url.GetString(), out origin);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryBrowserResultUrl(string? json, out string url, out string? error)
    {
        url = string.Empty;
        error = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("url", out var urlProperty)
                || urlProperty.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(urlProperty.GetString()))
            {
                return false;
            }

            url = urlProperty.GetString()!;
            if (document.RootElement.TryGetProperty("error", out var errorProperty)
                && errorProperty.ValueKind == JsonValueKind.String)
            {
                error = errorProperty.GetString();
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryBrowserOrigin(string? url, out string origin)
    {
        origin = string.Empty;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
        {
            return false;
        }

        origin = uri.GetLeftPart(UriPartial.Authority);
        return origin.Length > 0;
    }

}
