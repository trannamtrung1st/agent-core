using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Ports;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Tools;

public sealed partial class SessionToolExecutor
{
    private async ValueTask<ToolExecutionResult> ExecuteBrowserV2Async(AgentDefinition definition, Guid sessionId,
        string name, JsonElement args, ToolExecutionAdmission? admission, int remainingOutputBytes, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        ToolExecutionResult Fail(string code, string message) => FitResult(remainingOutputBytes, FinishBrowser(name, started, Error(code, message)));
        if (!BrowserToolCatalog.TryGet(name, out var metadata)) return Fail("unsupported_operation", "Unknown browser feature.");
        using var schema = JsonDocument.Parse(metadata.ParametersJson);
        if (args.ValueKind == JsonValueKind.Object && args.EnumerateObject().Any(p => p.Name is "targetOrigins" or "origins" or "headless" or "enabled" or "interactionMode")) return Fail("forbidden", "Browser arguments cannot change host policy.");
        if (!ValidateBrowserShape(args, schema.RootElement)) return Fail("invalid", "Browser arguments do not match the bounded tool schema.");
        if (!ValidReferences(args)) return Fail("invalid_reference", BrowserFailureMessage("invalid_reference"));
        var denied = await BindBrowserAsync(sessionId, admission, ct).ConfigureAwait(false);
        if (denied is not null) return TextResult(FinishBrowser(name, started, denied));
        if (browser is null || !browser.IsAvailable && metadata.Feature != BrowserFeature.Configuration) return Fail("provider_unavailable", "Browser is unavailable.");
        if (!browser.Provider.Supports(metadata.Feature)) return Fail("unsupported_operation", "The active provider does not support this feature.");
        if (metadata.Feature == BrowserFeature.VisionMouse && admission?.SupportsVision != true) return Fail("forbidden", "Coordinate actions require a vision model.");
        if (metadata.Feature is BrowserFeature.Evaluate or BrowserFeature.FillCredential or BrowserFeature.Geolocation
            && admission is not { Detached: false, TriggerKind: TriggerKind.UserTurn }) return Fail("forbidden", "This feature requires a direct attached user turn.");
        if (name == ToolCatalog.BrowserNavigate) return FitResult(remainingOutputBytes, await NavigateBrowserAsync(sessionId, args, admission, ct));
        if (name == ToolCatalog.BrowserSnapshot && !args.EnumerateObject().Any()) return FitResult(remainingOutputBytes, await SnapshotBrowserAsync(sessionId, args, admission, ct));
        if (name == ToolCatalog.BrowserWait && args.GetProperty("condition").GetString() == "stable")
        {
            var result = await browser.SnapshotAsync(sessionId, new BrowserWaitOptions("stable", args.TryGetProperty("timeoutMs", out var timeout) ? timeout.GetInt32() : null), ct);
            return FitResult(remainingOutputBytes, FinishBrowser(name, started, await PresentBrowserAsync(sessionId, admission, result, ct)));
        }
        if (name == ToolCatalog.BrowserClose) return FitResult(remainingOutputBytes, await CloseBrowserAsync(sessionId, args, ct));
        if (name == ToolCatalog.BrowserScreenshot) return await CaptureBrowserAsync(sessionId, args, admission, ct);
        if (name is ToolCatalog.BrowserClick or ToolCatalog.BrowserHover or ToolCatalog.BrowserDrag or ToolCatalog.BrowserType or ToolCatalog.BrowserFillCredential)
        {
            var node = JsonNode.Parse(args.GetRawText())!.AsObject();
            var operation = metadata.Feature switch { BrowserFeature.Click => "click", BrowserFeature.Hover => "hover", BrowserFeature.Drag => "drag", BrowserFeature.Type => "fill", _ => "fill_credential" };
            // The internal interaction request is typed; the model never sees a multiplexed action schema.
            if (name == ToolCatalog.BrowserType) { node["value"] = node["text"]!.DeepClone(); node.Remove("text"); }
            var submit = node["submit"]?.GetValue<bool>() == true;
            node.Remove("submit");
            node.Remove("slowly");
            if (name == ToolCatalog.BrowserClick && node.Count > 1)
                return await Command();
            node["operation"] = operation;
            using var translated = JsonDocument.Parse(node.ToJsonString());
            if (submit || args.TryGetProperty("slowly", out var slowly) && slowly.GetBoolean()) return await Command();
            return FitResult(remainingOutputBytes, await InteractBrowserAsync(name, definition, sessionId, translated.RootElement, admission, ct));
        }
        if (name == ToolCatalog.BrowserUpload)
        {
            var uploads = new List<BrowserUpload>();
            foreach (var id in args.GetProperty("artifactIds").EnumerateArray())
            {
                var resolved = await ResolveBrowserUploadAsync(definition, sessionId, id.GetString(), ct);
                if (resolved.ErrorJson is not null) return TextResult(FinishBrowser(name, started, resolved.ErrorJson));
                uploads.Add(resolved.Upload!);
            }
            var result = await browser.InteractAsync(new BrowserInteractionRequest(sessionId, "upload", args.GetProperty("ref").GetString()!, null, Uploads: uploads), ct);
            return FitResult(remainingOutputBytes, FinishBrowser(name, started, await PresentBrowserAsync(sessionId, admission, result, ct)));
        }
        return await Command();

        async ValueTask<ToolExecutionResult> Command()
        {
            BrowserCommandResult result;
            try
            {
                result = await browser.ExecuteAsync(new BrowserCommand(sessionId, name, args.Clone()), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                RecordBrowser(name, started, "canceled");
                throw;
            }
            if (result.ErrorCode is not null) return Fail(result.ErrorCode, BrowserFailureMessage(result.ErrorCode));
            if (result.Bytes is { Length: > 0 } bytes)
            {
                if (bytes.Length > BrowserToolLimits.MaxDownloadBytes) return Fail("capture_too_large", "Browser output exceeds the byte budget.");
                var stored = await StoreBrowserBytesAsync(sessionId, admission, result.FileName ?? "browser-output", result.ContentType ?? "application/octet-stream", bytes, ct);
                if (stored.Error is not null) return TextResult(FinishBrowser(name, started, stored.Error));
                var json = JsonSerializer.Serialize(new { status = "ok", artifactId = stored.ArtifactId, byteSize = bytes.Length, contentType = result.ContentType });
                return new ToolExecutionResult(FinishBrowser(name, started, json), admission?.SupportsVision == true && result.ContentType?.StartsWith("image/", StringComparison.Ordinal) == true
                    ? [new ModelImageContent(result.ContentType, bytes, result.FileName ?? "browser-output")] : []);
            }
            if (result.Snapshot is not null)
                return FitResult(remainingOutputBytes, FinishBrowser(name, started, await PresentBrowserAsync(sessionId, admission, new BrowserOperationResult(null, result.Snapshot, Downloads: result.Downloads), ct)));
            return FitResult(remainingOutputBytes, FinishBrowser(name, started, result.DataJson ?? "{\"status\":\"ok\"}"));
        }
    }

    private static bool ValidReferences(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
            foreach (var property in value.EnumerateObject())
            {
                if (property.Name is "ref" or "targetRef" && !BrowserToolArguments.IsOpaqueReference(property.Value.GetString())) return false;
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
}
