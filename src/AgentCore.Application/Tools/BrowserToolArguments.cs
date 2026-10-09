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

    public static bool IsOpaqueReference(string? value) => value is not null && OpaqueRef.IsMatch(value);

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

    public static BrowserRequest Request(Guid sessionId, string tool, JsonElement args)
    {
        var operation = Enum.GetValues<BrowserOperation>().Single(o => ToolName(o) == tool);
        var options = System.Text.Json.JsonSerializer.Deserialize<BrowserOptionsData>(args, JsonOptions)!;
        if (operation == BrowserOperation.EmulateMedia)
            options = options with
            {
                MediaSpecified = args.TryGetProperty("media", out _),
                ColorSchemeSpecified = args.TryGetProperty("colorScheme", out _),
                ReducedMotionSpecified = args.TryGetProperty("reducedMotion", out _),
                ForcedColorsSpecified = args.TryGetProperty("forcedColors", out _),
                ContrastSpecified = args.TryGetProperty("contrast", out _),
            };
        if (operation == BrowserOperation.Find)
            options = options with { Query = System.Text.Json.JsonSerializer.Deserialize<BrowserTargetQuery>(args, JsonOptions) };
        return new(sessionId, operation, options);
    }

    public static bool TryCanonicalizeClose(string? json, out string errorJson)
    {
        errorJson = "";
        if (string.IsNullOrWhiteSpace(json)) return true;
        try
        {
            using var document = JsonDocument.Parse(json);
            var value = document.RootElement;
            if (value.ValueKind == JsonValueKind.Null || value.ValueKind == JsonValueKind.Object && !value.EnumerateObject().Any()) return true;
        }
        catch (JsonException) { }
        errorJson = "{\"error\":\"invalid\",\"message\":\"close accepts an empty object.\"}";
        return false;
    }

    public static bool TryRequest(Guid sessionId, string tool, JsonElement args, out BrowserRequest request, out string error)
    {
        request = null!; error = "invalid";
        if (!BrowserToolCatalog.TryGet(tool, out var metadata)) { error = "unsupported_operation"; return false; }
        if (args.ValueKind == JsonValueKind.Object && args.EnumerateObject().Any(p => p.Name is "origins" or "targetOrigins" or "headless" or "enabled" or "interactionMode"))
        { error = "forbidden"; return false; }
        using var schema = JsonDocument.Parse(metadata.ParametersJson);
        if (!ValidateBrowserShape(args, schema.RootElement)) return false;
        if (!ValidReferences(args)) { error = "invalid_reference"; return false; }
        request = Request(sessionId, tool, args);
        if (request.Operation == BrowserOperation.Navigate && (request.Options.Operation is null or "goto") && string.IsNullOrWhiteSpace(request.Options.Url)) return false;
        if (request.Operation == BrowserOperation.Find && !ValidQuery(request.Options.Query)) return false;
        error = ""; return true;
    }

    private static bool ValidReferences(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
            foreach (var property in value.EnumerateObject())
            {
                if (property.Name is "ref" or "targetRef" or "scopeRef" && !BrowserToolArguments.IsOpaqueReference(property.Value.GetString())) return false;
                if (!ValidReferences(property.Value)) return false;
            }
        if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) if (!ValidReferences(item)) return false;
        return true;
    }

    private static bool ValidateBrowserShape(JsonElement value, JsonElement schema)
    {
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

    public static bool ValidQuery(BrowserTargetQuery? query) => query is not null
        && new[] { query.Role, query.Text, query.Label, query.Placeholder, query.AltText, query.Title, query.TestId }.Count(x => x is not null) == 1
        && (query.Name is null || query.Role is not null)
        && (query.ScopeRef is null || query.FrameRef is null)
        && (query.HasText is null || query.HasText.Length is >= 1 and <= 200);
}
