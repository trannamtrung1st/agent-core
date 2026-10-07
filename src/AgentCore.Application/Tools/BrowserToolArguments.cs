using System.Text.Json;
using System.Text.RegularExpressions;
using AgentCore.Application.Ports;

namespace AgentCore.Application.Tools;

public static class BrowserToolArguments
{
    private static readonly Regex OpaqueRef = new(
        "^el_[A-Za-z0-9_-]{22}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex PageRef = new(
        "^pg_[A-Za-z0-9_-]{22}$",
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
        if (!TryNavigate(args, out var operation, out var parsed, out errorJson)
            || operation != "goto"
            || parsed is null)
        {
            url = string.Empty;
            return false;
        }

        url = parsed;
        return true;
    }

    public static bool TryNavigate(JsonElement args, out string operation, out string? url, out string errorJson)
    {
        operation = "goto";
        url = null;
        if (!TryRejectProperties(args, out errorJson))
        {
            return false;
        }

        if (args.TryGetProperty("operation", out var operationProperty))
        {
            if (operationProperty.ValueKind != JsonValueKind.String
                || operationProperty.GetString() is not { Length: > 0 } named
                || !BrowserToolLimits.NavigateOperations.Contains(named, StringComparer.Ordinal))
            {
                errorJson = Error("unsupported_operation", "Browser navigation is not supported.", "unsupported_operation");
                return false;
            }

            operation = named;
        }

        var history = operation is "back" or "forward" or "reload";
        if (!HasOnly(args, history ? ["operation"] : args.TryGetProperty("operation", out _) ? ["operation", "url"] : ["url"], out errorJson))
        {
            return false;
        }

        if (history)
        {
            return true;
        }

        if (!TryString(args, "url", out var parsedUrl))
        {
            errorJson = Error("invalid", "url is required.");
            return false;
        }

        if (parsedUrl.Length > BrowserToolLimits.MaxUrlLength)
        {
            errorJson = Error("invalid", "url must be at most 2048 characters.");
            return false;
        }

        url = parsedUrl;
        return true;
    }

    public static bool TryObserve(JsonElement args, out BrowserObserveOptions? options, out string errorJson)
    {
        options = null;
        if (!TryRejectProperties(args, out errorJson))
        {
            return false;
        }

        if (!HasOnly(args, ["waitFor", "timeoutMs", "role", "name"], out errorJson))
        {
            return false;
        }

        string? waitFor = null;
        if (args.TryGetProperty("waitFor", out var waitProperty))
        {
            if (waitProperty.ValueKind != JsonValueKind.String
                || waitProperty.GetString() is not { Length: > 0 } mode
                || !BrowserToolLimits.ObserveWaitModes.Contains(mode, StringComparer.Ordinal))
            {
                errorJson = Error("invalid", "Browser wait is not supported.", "unsupported_wait");
                return false;
            }

            waitFor = mode;
        }

        int? timeout = null;
        if (args.TryGetProperty("timeoutMs", out var timeoutProperty))
        {
            if (waitFor is null)
            {
                errorJson = Error("invalid", "timeoutMs requires waitFor.", "timeout_without_wait");
                return false;
            }

            if (timeoutProperty.ValueKind != JsonValueKind.Number
                || !timeoutProperty.TryGetInt32(out var parsed)
                || parsed < BrowserToolLimits.MinObserveTimeoutMs
                || parsed > BrowserToolLimits.MaxObserveTimeoutMs)
            {
                errorJson = Error("invalid", "timeoutMs must be from 100 to 5000.", "timeout_out_of_range");
                return false;
            }

            timeout = parsed;
        }

        string? role = null;
        string? name = null;
        if (string.Equals(waitFor, "role", StringComparison.Ordinal))
        {
            if (!TryString(args, "role", out var parsedRole)
                || parsedRole.Length > BrowserToolLimits.MaxRoleLength
                || !BrowserToolLimits.ObserveRoles.Contains(parsedRole, StringComparer.Ordinal))
            {
                errorJson = Error("invalid", "role is required for this wait.", "missing_role");
                return false;
            }

            role = parsedRole;
            if (args.TryGetProperty("name", out _))
            {
                if (!TryString(args, "name", out var parsedName)
                    || parsedName.Length > BrowserToolLimits.MaxAccessibleNameLength)
                {
                    errorJson = Error("invalid", "name must be a bounded accessible name.", "invalid_name");
                    return false;
                }

                name = parsedName;
            }
        }
        else if (args.TryGetProperty("role", out _) || args.TryGetProperty("name", out _))
        {
            errorJson = Error("invalid", "role is only valid when waitFor is role.", "unsupported_property");
            return false;
        }

        if (waitFor is not null)
        {
            options = new BrowserObserveOptions(waitFor, timeout, role, name);
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
        out string errorJson) =>
        TryAct(args, out operation, out reference, out value, out _, out _, out _, out errorJson);

    public static bool TryAct(
        JsonElement args,
        out string operation,
        out string reference,
        out string? value,
        out string? direction,
        out int delta,
        out string? targetRef,
        out string errorJson)
    {
        operation = string.Empty;
        reference = string.Empty;
        value = null;
        direction = null;
        delta = 0;
        targetRef = null;
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
            "fill_credential" => new[] { "operation", "ref", "credentialRef" },
            "fill" or "select" => new[] { "operation", "ref", "value" },
            "press" => new[] { "operation", "ref", "key" },
            "upload" => new[] { "operation", "ref", "artifactId" },
            "scroll" => new[] { "operation", "direction", "delta", "ref" },
            "drag" => new[] { "operation", "ref", "targetRef" },
            _ => new[] { "operation", "ref" }
        };
        if (!HasOnly(args, allowed, out errorJson))
        {
            return false;
        }

        if (operation == "scroll")
        {
            if (!TryString(args, "direction", out var parsedDirection)
                || !BrowserToolLimits.ScrollDirections.Contains(parsedDirection, StringComparer.Ordinal))
            {
                errorJson = Error("invalid", "direction is required.", "missing_direction");
                return false;
            }

            direction = parsedDirection;
            delta = BrowserToolLimits.DefaultScrollDelta;
            if (args.TryGetProperty("delta", out var deltaProperty))
            {
                if (deltaProperty.ValueKind != JsonValueKind.Number
                    || !deltaProperty.TryGetInt32(out var parsedDelta)
                    || parsedDelta < 1
                    || parsedDelta > BrowserToolLimits.MaxScrollDelta)
                {
                    errorJson = Error("invalid", "delta must be from 1 to 2000.", "delta_out_of_range");
                    return false;
                }

                delta = parsedDelta;
            }

            if (!args.TryGetProperty("ref", out _))
            {
                return true;
            }
        }

        if (!TryString(args, "ref", out reference))
        {
            errorJson = Error("invalid", "ref is required.", "missing_ref");
            return false;
        }

        if (reference.Length > BrowserToolLimits.MaxRefLength
            || LooksLikeSelector(reference)
            || !OpaqueRef.IsMatch(reference))
        {
            errorJson = LooksLikeSelector(reference)
                ? Error("unsupported_operation", "Browser selectors are not supported.", "invalid_ref")
                : Error("invalid", reference.Length > BrowserToolLimits.MaxRefLength
                    ? "ref must be at most 128 characters."
                    : "ref must be an opaque element reference.", "invalid_ref");
            return false;
        }

        if (operation == "fill_credential")
        {
            if (!TryString(args, "credentialRef", out var alias) || alias.Length > 64)
            { errorJson = Error("invalid", "credentialRef is required."); return false; }
            value = alias;
        }
        else if (operation is "fill" or "select")
        {
            if (!TryString(args, "value", out var text))
            {
                errorJson = Error("invalid", "value is required.", "missing_value");
                return false;
            }

            var max = operation == "fill" ? BrowserToolLimits.MaxFillLength : BrowserToolLimits.MaxSelectLength;
            if (text.Length > max)
            {
                errorJson = Error("invalid", $"value must be at most {max} characters.", "value_too_long");
                return false;
            }

            value = text;
        }
        else if (operation == "press")
        {
            if (!TryString(args, "key", out var key))
            {
                errorJson = Error("unsupported_operation", "Browser key is not supported.", "missing_key");
                return false;
            }

            if (!BrowserToolLimits.PressKeys.Contains(key, StringComparer.Ordinal))
            {
                errorJson = Error("unsupported_operation", "Browser key is not supported.", "unsupported_key");
                return false;
            }

            value = key;
        }
        else if (operation == "upload")
        {
            if (!TryString(args, "artifactId", out var artifactId))
            {
                errorJson = Error("invalid", "artifactId is required.", "missing_artifact_id");
                return false;
            }

            if (artifactId.Length > 80
                || artifactId.Contains('/')
                || artifactId.Contains('\\')
                || artifactId.Contains(':')
                || Uri.TryCreate(artifactId, UriKind.Absolute, out _))
            {
                errorJson = Error("invalid", "artifactId must be an artifact or definition resource id.", "invalid_artifact_id");
                return false;
            }

            value = artifactId;
        }
        else if (operation == "drag")
        {
            if (!TryString(args, "targetRef", out var parsedTarget)
                || parsedTarget.Length > BrowserToolLimits.MaxRefLength
                || LooksLikeSelector(parsedTarget)
                || !OpaqueRef.IsMatch(parsedTarget))
            {
                errorJson = Error("invalid", "targetRef must be an opaque element reference.", "invalid_ref");
                return false;
            }

            targetRef = parsedTarget;
        }

        return true;
    }

    public static bool TryPages(JsonElement args, out string operation, out string? pageId, out string errorJson)
    {
        operation = string.Empty;
        pageId = null;
        if (!TryRejectProperties(args, out errorJson))
        {
            return false;
        }

        if (!TryString(args, "operation", out operation)
            || !BrowserToolLimits.PageOperations.Contains(operation, StringComparer.Ordinal))
        {
            errorJson = Error("unsupported_operation", "Browser page operation is not supported.", "unsupported_operation");
            return false;
        }

        var needsId = operation is "switch" or "close";
        if (!HasOnly(args, needsId ? ["operation", "pageId"] : ["operation"], out errorJson))
        {
            return false;
        }

        if (!needsId)
        {
            return true;
        }

        if (!TryString(args, "pageId", out var parsed)
            || !PageRef.IsMatch(parsed))
        {
            errorJson = Error("invalid", "pageId must be an opaque page reference.", "invalid_page");
            return false;
        }

        pageId = parsed;
        return true;
    }

    public static bool TryCapture(JsonElement args, out string errorJson)
    {
        if (!TryRejectProperties(args, out errorJson))
        {
            return false;
        }

        return HasOnly(args, [], out errorJson);
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
                errorJson = Error("unsupported_operation", "Browser scripting and selectors are not supported.", "unsupported_property");
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
                errorJson = Error("invalid", "Browser arguments contain an unsupported property.", "unsupported_property");
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

    private static string Error(string code, string message, string? reason = null) =>
        reason is null
            ? JsonSerializer.Serialize(new { error = code, message })
            : JsonSerializer.Serialize(new { error = code, message, reason });
}
