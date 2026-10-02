using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentCore.Application.Tools;

public static class BrowserToolArguments
{
    private static readonly Regex OpaqueRef = new(
        "^el_[A-Za-z0-9_-]{22}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly HashSet<string> AuthorityProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "headless",
        "interactionMode",
        "targetOrigins",
        "origins",
        "allowlist",
        "enabled",
        "fixturePort",
        "mode"
    };

    private static readonly HashSet<string> ScriptProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "script",
        "selector",
        "xpath",
        "javascript",
        "evaluate",
        "path",
        "file",
        "filepath",
        "filename",
        "command"
    };

    public static bool TryNavigate(JsonElement args, out string url, out string errorJson)
    {
        url = string.Empty;
        if (!TryRejectProperties(args, out errorJson))
        {
            return false;
        }

        if (!HasOnly(args, "url", out errorJson))
        {
            return false;
        }

        if (!TryString(args, "url", out url))
        {
            errorJson = Error("invalid", "url is required.");
            return false;
        }

        if (url.Length > BrowserToolLimits.MaxUrlLength)
        {
            errorJson = Error("invalid", "url must be at most 2048 characters.");
            return false;
        }

        return true;
    }

    public static bool TryObserve(JsonElement args, out string errorJson)
    {
        if (!TryRejectProperties(args, out errorJson))
        {
            return false;
        }

        if (args.EnumerateObject().Any())
        {
            errorJson = Error("invalid", "observe accepts an empty object.");
            return false;
        }

        return true;
    }

    public static bool TryCanonicalizeClose(string? argumentsJson, out string errorJson)
    {
        errorJson = string.Empty;
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            return true;
        }

        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            if (document.RootElement.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                return true;
            }

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                errorJson = Error("invalid", "close accepts an object.");
                return false;
            }

            return TryClose(document.RootElement, out errorJson);
        }
        catch (JsonException)
        {
            errorJson = Error("invalid", "Tool arguments were malformed.");
            return false;
        }
    }

    public static bool TryClose(JsonElement args, out string errorJson)
    {
        if (args.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            errorJson = string.Empty;
            return true;
        }

        if (args.ValueKind != JsonValueKind.Object)
        {
            errorJson = Error("invalid", "close accepts an object.");
            return false;
        }

        return TryRejectProperties(args, out errorJson);
    }

    public static bool TryAct(
        JsonElement args,
        out string operation,
        out string reference,
        out string? value,
        out string errorJson)
    {
        operation = string.Empty;
        reference = string.Empty;
        value = null;
        if (!TryRejectProperties(args, out errorJson))
        {
            return false;
        }

        if (!TryString(args, "operation", out operation)
            || !BrowserToolLimits.Operations.Contains(operation, StringComparer.Ordinal))
        {
            errorJson = Error("unsupported_operation", "Browser operation is not supported.");
            return false;
        }

        var allowed = operation switch
        {
            "fill" or "select" => new[] { "operation", "ref", "value" },
            "press" => new[] { "operation", "ref", "key" },
            _ => new[] { "operation", "ref" }
        };
        if (!HasOnly(args, allowed, out errorJson))
        {
            return false;
        }

        if (!TryString(args, "ref", out reference))
        {
            errorJson = Error("invalid", "ref is required.");
            return false;
        }

        if (reference.Length > BrowserToolLimits.MaxRefLength)
        {
            errorJson = Error("invalid", "ref must be at most 128 characters.");
            return false;
        }

        if (LooksLikeSelector(reference))
        {
            errorJson = Error("unsupported_operation", "Browser selectors are not supported.");
            return false;
        }

        if (!OpaqueRef.IsMatch(reference))
        {
            errorJson = Error("invalid", "ref must be an opaque element reference.");
            return false;
        }

        if (operation is "fill" or "select")
        {
            if (!TryString(args, "value", out var text))
            {
                errorJson = Error("invalid", "value is required.");
                return false;
            }

            var max = operation == "fill" ? BrowserToolLimits.MaxFillLength : BrowserToolLimits.MaxSelectLength;
            if (text.Length > max)
            {
                errorJson = Error("invalid", $"value must be at most {max} characters.");
                return false;
            }

            value = text;
        }
        else if (operation == "press")
        {
            if (!TryString(args, "key", out var key)
                || !BrowserToolLimits.PressKeys.Contains(key, StringComparer.Ordinal))
            {
                errorJson = Error("unsupported_operation", "Browser key is not supported.");
                return false;
            }

            value = key;
        }

        return true;
    }

    private static bool TryRejectProperties(JsonElement args, out string errorJson)
    {
        foreach (var property in args.EnumerateObject())
        {
            if (AuthorityProperties.Contains(property.Name))
            {
                errorJson = Error("forbidden", "Browser arguments cannot change host policy.");
                return false;
            }

            if (ScriptProperties.Contains(property.Name))
            {
                errorJson = Error("unsupported_operation", "Browser scripting and selectors are not supported.");
                return false;
            }

            if (property.Value.ValueKind == JsonValueKind.String
                && LooksLikePlaywrightCommand(property.Value.GetString()))
            {
                errorJson = Error("unsupported_operation", "Browser scripting is not supported.");
                return false;
            }
        }

        errorJson = string.Empty;
        return true;
    }

    private static bool HasOnly(JsonElement args, string allowed, out string errorJson) =>
        HasOnly(args, [allowed], out errorJson);

    private static bool HasOnly(JsonElement args, string[] allowed, out string errorJson)
    {
        foreach (var property in args.EnumerateObject())
        {
            if (!allowed.Contains(property.Name, StringComparer.Ordinal))
            {
                errorJson = Error("invalid", "Browser arguments contain an unsupported property.");
                return false;
            }
        }

        errorJson = string.Empty;
        return true;
    }

    private static bool TryString(JsonElement args, string name, out string value)
    {
        value = string.Empty;
        if (!args.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool LooksLikeSelector(string value) =>
        value.Contains('#', StringComparison.Ordinal)
        || value.Contains("//", StringComparison.Ordinal)
        || value.StartsWith('.')
        || value.Contains('[')
        || value.Contains('>')
        || value.Contains("xpath", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikePlaywrightCommand(string? value) =>
        !string.IsNullOrEmpty(value)
        && (value.Contains("page.evaluate", StringComparison.OrdinalIgnoreCase)
            || value.Contains("locator(", StringComparison.OrdinalIgnoreCase)
            || value.Contains("querySelector", StringComparison.OrdinalIgnoreCase)
            || value.Contains("Playwright", StringComparison.OrdinalIgnoreCase));

    private static string Error(string code, string message) =>
        JsonSerializer.Serialize(new { error = code, message });
}
