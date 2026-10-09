using System.Text.Json;
using System.Text.RegularExpressions;
using AgentCore.Application.Ports;

namespace AgentCore.Application.Tools;

public static class BrowserToolArguments
{
    internal const string TargetGuidance = "Use a minimal target object. For a label: {\"target\":{\"by\":\"label\",\"value\":\"Email\"}}. For role, value is an ARIA role and name is its accessible name. Example: {\"target\":{\"by\":\"role\",\"value\":\"textbox\",\"name\":\"Email\"}}. exact defaults to true. For repeated Edit buttons use within={by:role,value:row,hasText:Pump 002}. Omit unused fields, including name for non-role targets and frameRef for the main page. One bounded within scope only. Actions need no browser.find call. Never invent frameRef; use only a current snapshot frame ID.";
    internal static readonly System.Text.Json.JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public static string ToolName(BrowserOperation operation) => operation switch
    {
        BrowserOperation.Navigate => "browser.navigate",
        BrowserOperation.Snapshot => "browser.snapshot",
        BrowserOperation.Find => "browser.find",
        BrowserOperation.Click => "browser.click",
        BrowserOperation.Hover => "browser.hover",
        BrowserOperation.Drag => "browser.drag",
        BrowserOperation.Drop => "browser.drop",
        BrowserOperation.Type => "browser.type",
        BrowserOperation.FillForm => "browser.fill_form",
        BrowserOperation.SelectOption => "browser.select_option",
        BrowserOperation.PressKey => "browser.press_key",
        BrowserOperation.Upload => "browser.upload",
        BrowserOperation.FillCredential => "browser.fill_credential",
        BrowserOperation.WaitFor => "browser.wait_for",
        BrowserOperation.Tabs => "browser.tabs",
        BrowserOperation.Dialog => "browser.dialog",
        BrowserOperation.Resize => "browser.resize",
        BrowserOperation.Close => "browser.close",
        BrowserOperation.Screenshot => "browser.screenshot",
        BrowserOperation.ConsoleMessages => "browser.console_messages",
        BrowserOperation.NetworkRequests => "browser.network_requests",
        BrowserOperation.NetworkRequest => "browser.network_request",
        BrowserOperation.Route => "browser.route",
        BrowserOperation.Routes => "browser.routes",
        BrowserOperation.Unroute => "browser.unroute",
        BrowserOperation.NetworkState => "browser.network_state",
        BrowserOperation.Cookies => "browser.cookies",
        BrowserOperation.LocalStorage => "browser.local_storage",
        BrowserOperation.SessionStorage => "browser.session_storage",
        BrowserOperation.Verify => "browser.verify",
        BrowserOperation.GenerateLocator => "browser.generate_locator",
        BrowserOperation.Mouse => "browser.mouse",
        BrowserOperation.Highlight => "browser.highlight",
        BrowserOperation.EmulateMedia => "browser.emulate_media",
        BrowserOperation.GetConfig => "browser.get_config",
        BrowserOperation.SetGeolocation => "browser.set_geolocation",
        BrowserOperation.Scroll => "browser.scroll",
        _ => ""
    };

    private static BrowserRequest Parse(Guid sessionId, string tool, JsonElement args)
    {
        var operation = Enum.GetValues<BrowserOperation>().Single(o => ToolName(o) == tool);
        var type = operation switch
        {
            BrowserOperation.Navigate => typeof(BrowserNavigate),
            BrowserOperation.Snapshot => typeof(BrowserObserve),
            BrowserOperation.Find => typeof(BrowserFind),
            BrowserOperation.Click => typeof(BrowserClick),
            BrowserOperation.Hover => typeof(BrowserHover),
            BrowserOperation.Drag => typeof(BrowserDrag),
            BrowserOperation.Drop => typeof(BrowserDrop),
            BrowserOperation.Type => typeof(BrowserTypeText),
            BrowserOperation.FillForm => typeof(BrowserFillForm),
            BrowserOperation.SelectOption => typeof(BrowserSelectOption),
            BrowserOperation.PressKey => typeof(BrowserPressKey),
            BrowserOperation.Upload => typeof(BrowserUploadCommand),
            BrowserOperation.FillCredential => typeof(BrowserFillCredential),
            BrowserOperation.WaitFor => typeof(BrowserWaitFor),
            BrowserOperation.Tabs => typeof(BrowserTabs),
            BrowserOperation.Dialog => typeof(BrowserDialog),
            BrowserOperation.Resize => typeof(BrowserResize),
            BrowserOperation.Close => typeof(BrowserClose),
            BrowserOperation.Screenshot => typeof(BrowserScreenshot),
            BrowserOperation.ConsoleMessages => typeof(BrowserConsoleMessages),
            BrowserOperation.NetworkRequests => typeof(BrowserNetworkRequests),
            BrowserOperation.NetworkRequest => typeof(BrowserNetworkRequest),
            BrowserOperation.Route => typeof(BrowserRoute),
            BrowserOperation.Routes => typeof(BrowserRoutes),
            BrowserOperation.Unroute => typeof(BrowserUnroute),
            BrowserOperation.NetworkState => typeof(BrowserNetworkState),
            BrowserOperation.Cookies => typeof(BrowserCookies),
            BrowserOperation.LocalStorage => typeof(BrowserLocalStorage),
            BrowserOperation.SessionStorage => typeof(BrowserSessionStorage),
            BrowserOperation.Verify => typeof(BrowserVerify),
            BrowserOperation.GenerateLocator => typeof(BrowserGenerateLocator),
            BrowserOperation.Mouse => typeof(BrowserMouse),
            BrowserOperation.Highlight => typeof(BrowserHighlight),
            BrowserOperation.EmulateMedia => typeof(BrowserEmulateMedia),
            BrowserOperation.GetConfig => typeof(BrowserGetConfig),
            BrowserOperation.SetGeolocation => typeof(BrowserSetGeolocation),
            BrowserOperation.Scroll => typeof(BrowserScroll),
            _ => throw new ArgumentException("Unknown browser operation.")
        };
        var command = (BrowserCommand)JsonSerializer.Deserialize(args, type, JsonOptions)!;
        if (command is BrowserEmulateMedia media)
            command = media with
            {
                MediaSpecified = args.TryGetProperty("media", out _),
                ColorSchemeSpecified = args.TryGetProperty("colorScheme", out _),
                ReducedMotionSpecified = args.TryGetProperty("reducedMotion", out _),
                ForcedColorsSpecified = args.TryGetProperty("forcedColors", out _),
                ContrastSpecified = args.TryGetProperty("contrast", out _)
            };
        return new(sessionId, command);
    }

    public static BrowserRequest Request(Guid sessionId, string tool, JsonElement args) =>
        TryRequest(sessionId, tool, args, out var request, out var error) ? request
            : throw new ArgumentException(error + ": " + TargetGuidance);

    public static bool TryValidateClose(string? json, out string errorJson)
    {
        errorJson = "";
        if (string.IsNullOrWhiteSpace(json))
        { errorJson = "{\"error\":\"invalid\",\"message\":\"browser.close requires exactly {}. Omit every parameter.\"}"; return false; }
        try
        {
            using var document = JsonDocument.Parse(json);
            var value = document.RootElement;
            if (value.ValueKind == JsonValueKind.Object && !value.EnumerateObject().Any()) return true;
        }
        catch (JsonException) { }
        errorJson = "{\"error\":\"invalid\",\"message\":\"browser.close requires exactly {}. Omit every parameter.\"}";
        return false;
    }

    public static bool TryRequest(Guid sessionId, string tool, JsonElement args, out BrowserRequest request, out string error)
    {
        request = null!; error = "invalid";
        if (!BrowserToolCatalog.TryGet(tool, out var metadata)) { error = "unsupported_operation"; return false; }
        if (args.ValueKind == JsonValueKind.Object && args.EnumerateObject().Any(p => p.Name is "origins" or "targetOrigins" or "headless" or "enabled" or "interactionMode"))
        { error = "forbidden"; return false; }
        if (tool == ToolCatalog.BrowserSnapshot && args.ValueKind == JsonValueKind.Object && args.TryGetProperty("frameRef", out var frame)
            && (frame.ValueKind != JsonValueKind.String || !Regex.IsMatch(frame.GetString()!, "^fr_[a-f0-9]{32}$", RegexOptions.CultureInvariant)))
        { error = "invalid_frame"; return false; }
        using var schema = JsonDocument.Parse(metadata.ParametersJson);
        if (!ValidateBrowserShape(args, schema.RootElement)) { if (InvalidTargetReason(args) is not null) error = "invalid_target"; return false; }
        try { request = Parse(sessionId, tool, args); }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException) { return false; }
        if (request.Command is BrowserNavigate { Operation: "goto", Url: null or "" }) return false;
        if (request.Command is BrowserObserve observe && (observe.FrameRef is not null && !Regex.IsMatch(observe.FrameRef, "^fr_[a-f0-9]{32}$", RegexOptions.CultureInvariant)
            || observe.FrameRef is not null && observe.Target?.FrameRef is not null && observe.FrameRef != observe.Target.FrameRef))
        { error = "invalid_frame"; return false; }
        if (!ValidTargets(args)) { error = "invalid_target"; return false; }
        if (request.Command is BrowserFillForm form && form.Fields.Any(f => (f.Value is not null) == (f.Checked is not null))) return false;
        if (request.Command is BrowserWaitFor wait && (wait.Condition == "target" && wait.Target is null
            || wait.Condition is "text" or "textGone" && wait.Text is null || wait.Condition == "url" && wait.Url is null)) return false;
        if (request.Command is BrowserVerify verify && verify.Target is null && string.IsNullOrWhiteSpace(verify.Text)) return false;
        error = ""; return true;
    }

    private static bool ValidTargets(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
            foreach (var property in value.EnumerateObject())
            {
                if (property.Name is "target" or "destination" && !ValidTarget(JsonSerializer.Deserialize<BrowserTarget>(property.Value, JsonOptions))) return false;
                if (!ValidTargets(property.Value)) return false;
            }
        if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) if (!ValidTargets(item)) return false;
        return true;
    }

    private static bool ValidateBrowserShape(JsonElement value, JsonElement schema)
    {
        if (schema.TryGetProperty("oneOf", out var variants))
            return variants.EnumerateArray().Count(variant => ValidateBrowserShape(value, variant)) == 1;
        var types = schema.GetProperty("type");
        if (types.ValueKind == JsonValueKind.Array && value.ValueKind == JsonValueKind.Null)
            return types.EnumerateArray().Any(t => t.GetString() == "null");
        var type = types.ValueKind == JsonValueKind.Array ? types.EnumerateArray().First(t => t.GetString() != "null").GetString() : types.GetString();
        if (type == "object")
        {
            if (value.ValueKind != JsonValueKind.Object) return false;
            if (schema.TryGetProperty("required", out var required) && required.EnumerateArray().Any(r => !value.TryGetProperty(r.GetString()!, out _))) return false;
            var properties = schema.GetProperty("properties");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
                if (!seen.Add(property.Name) || !properties.TryGetProperty(property.Name, out var child) || !ValidateBrowserShape(property.Value, child)) return false;
        }
        else if (type == "string")
        {
            if (value.ValueKind != JsonValueKind.String) return false;
            var length = value.GetString()!.Length;
            if (schema.TryGetProperty("maxLength", out var max) && length > max.GetInt32() || schema.TryGetProperty("minLength", out var min) && length < min.GetInt32()) return false;
            if (schema.TryGetProperty("enum", out var choices) && !choices.EnumerateArray().Any(c => c.GetString() == value.GetString())) return false;
        }
        else if (type is "integer" or "number")
        {
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !double.IsFinite(number)) return false;
            if (type == "integer" && !value.TryGetInt32(out _)) return false;
            if (schema.TryGetProperty("minimum", out var min) && number < min.GetDouble() || schema.TryGetProperty("maximum", out var max) && number > max.GetDouble()) return false;
        }
        else if (type == "boolean" && value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
        else if (type == "array")
        {
            if (value.ValueKind != JsonValueKind.Array) return false;
            if (schema.TryGetProperty("minItems", out var min) && value.GetArrayLength() < min.GetInt32() || schema.TryGetProperty("maxItems", out var max) && value.GetArrayLength() > max.GetInt32()) return false;
            foreach (var item in value.EnumerateArray()) if (!ValidateBrowserShape(item, schema.GetProperty("items"))) return false;
        }
        return true;
    }

    public static bool ValidTarget(BrowserTarget? target) => TargetError(target) is null;

    private static string? TargetError(BrowserTarget? target)
    {
        if (target is null) return "target_missing_fields";
        if (target.Name is not null && target.By != "role" || target.Within is { Name: not null, By: not "role" }) return "name_requires_role";
        if (!ValidCriterion(target.By, target.Value, target.Name, target.HasText)
            || target.Within is { } scope && !ValidCriterion(scope.By, scope.Value, scope.Name, scope.HasText)) return "target_invalid_criterion";
        return target.FrameRef is not null && !Regex.IsMatch(target.FrameRef, "^fr_[a-f0-9]{32}$", RegexOptions.CultureInvariant) ? "frame_requires_current_id" : null;
    }

    internal static string? InvalidTargetReason(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
            foreach (var property in value.EnumerateObject())
            {
                if (property.Name is "target" or "destination")
                {
                    try
                    {
                        var error = TargetError(JsonSerializer.Deserialize<BrowserTarget>(property.Value, JsonOptions));
                        if (error is not null) return error;
                    }
                    catch (JsonException) { return "target_invalid_shape"; }
                }
                var nested = InvalidTargetReason(property.Value);
                if (nested is not null) return nested;
            }
        if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray())
            {
                var error = InvalidTargetReason(item);
                if (error is not null) return error;
            }
        return null;
    }

    internal static string ArgumentGuidance(JsonElement args) => InvalidTargetReason(args) switch
    {
        "name_requires_role" => "For by=label/text/placeholder/altText/title/testId, put the literal label in value and omit name. Example: {\"target\":{\"by\":\"label\",\"value\":\"Email\"}}.",
        "frame_requires_current_id" => "Omit frameRef for the main page. For an iframe, use only its current snapshot fr_ ID; never a label, URL, main or guessed ID.",
        _ => TargetGuidance
    };

    private static bool ValidCriterion(string by, string value, string? name, string? hasText) =>
        by is "role" or "text" or "label" or "placeholder" or "altText" or "title" or "testId"
        && !string.IsNullOrWhiteSpace(value) && value.Length <= 200
        && (name is null || by == "role" && !string.IsNullOrWhiteSpace(name) && name.Length <= 200)
        && (hasText is null || !string.IsNullOrWhiteSpace(hasText) && hasText.Length <= 200);
}
