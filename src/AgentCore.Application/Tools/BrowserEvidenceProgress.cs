using System.Text;
using System.Text.Json;
using AgentCore.Application.Ports;

namespace AgentCore.Application.Tools;

/// <summary>
/// Tracks unchanged successful observations and equivalent failures against an observed modal.
/// Modal recovery is rebuilt from durable tool receipts; successful transitions clear its bound.
/// </summary>
internal sealed class BrowserEvidenceProgress
{
    internal const int StopAfterRepeatedEvidence = 2;

    private string? _fingerprint;
    private readonly HashSet<string> _searchEvidence = new(StringComparer.Ordinal);

    private readonly Dictionary<string, int> _blockedFailures = new(StringComparer.Ordinal);
    private int _semanticMisses;
    private bool _captureUnavailable;
    private int _captures;
    private string? _semanticPage;
    private readonly HashSet<string> _unusableSnapshots = new(StringComparer.Ordinal);
    private bool _coordinateUnavailable;
    private int _unavailableCaptures;
    private const string CoordinateInstruction = "The latest screenshot has coordinateEvidence=false. Do not call browser.mouse with that snapshotId. Use an already-authorized semantic observation/action or a bounded wait justified by the safe capture diagnostics. Avoid repeating ineffective captures without evidence of change. If independently requested, browser.close remains actionable even when logout fails; report logout verification and browser closure separately.";

    internal string? SemanticRecoveryInstruction(IEnumerable<string> eligibleTools, bool vision, bool screenshotAvailable, int captureAllowance,
        IEnumerable<string>? projectedTools = null)
    {
        if (DialogPending) return null;
        if (_coordinateUnavailable) return CoordinateInstruction + (_unavailableCaptures >= 2
            ? " Multiple captures have lacked coordinate authority; stop the equivalent visual recovery strategy and report a truthful partial result if no supported alternative can progress." : "");
        if (_semanticMisses < 2) return null;
        var tools = eligibleTools.ToHashSet(StringComparer.Ordinal);
        var visual = vision && screenshotAvailable && !_captureUnavailable && _captures < captureAllowance
            && tools.Contains(ToolCatalog.BrowserScreenshot);
        var projected = projectedTools?.ToHashSet(StringComparer.Ordinal);
        if (projected is not null && !projected.Contains(ToolCatalog.BrowserScreenshot) && !projected.Contains(ToolCatalog.CapabilitiesLoad))
            visual = false;
        return "Semantic targeting has not located this control. Avoid more equivalent guesses. "
            + (visual ? (projected is not null && !projected.Contains(ToolCatalog.BrowserScreenshot)
                ? "Use capabilities.load to load the already-authorized screenshot capability before visual inspection. " : "")
                + "Inspect the rendered layout with an authorized screenshot, then prefer a freshly identified semantic target. "
                + (tools.Contains(ToolCatalog.BrowserVisionMouse) ? "If necessary use fresh screenshot-bound coordinates. " : "Coordinate actions are unavailable. ")
                : "Visual recovery is unavailable for the current model, authority or remaining capture allowance. Use different justified semantic evidence or report a truthful partial result. ")
            + "target_missing is not evidence of a native dialog; use browser.dialog only after dialog_pending. Verify application outcomes independently.";
    }
    internal bool DialogPending { get; private set; }
    internal bool DialogRecoveryExhausted => _blockedFailures.Values.Any(count => count >= 3);
    internal string? RepeatedDialogInstruction => _blockedFailures.Values.Any(count => count >= 2)
        ? "The repeated blocked strategy made no progress and is suppressed. Choose a different justified dialog operation, inspect current state, close if authorized/requested, or report a truthful partial result." : null;
    internal const string DialogInstruction = "A native dialog blocks ordinary page operations. Use only already-authorized browser.dialog inspect/accept/dismiss or browser.close for recovery. Resolve only a confirmation justified by the requested action; never replay its triggering action. If recovery is unavailable, report a truthful partial result. dialog_missing or a verified resolution restores page operations.";

    internal BrowserEvidenceProgress(IEnumerable<ModelMessage>? receipts = null)
    {
        if (receipts is null) return;
        var calls = new Dictionary<string, ModelToolCall>(StringComparer.Ordinal);
        foreach (var message in receipts)
        {
            foreach (var call in message.ToolCalls ?? []) calls[call.Id] = call;
            if (message.Role == ModelRole.Tool && message.ToolCallId is { } id && calls.TryGetValue(id, out var completed))
                NoteResult(completed, message.Text);
        }
    }

    internal static bool DialogRecoveryTool(string name) => name is ToolCatalog.BrowserDialog or ToolCatalog.BrowserClose or ToolCatalog.BrowserConfiguration;

    internal string? Refuse(ModelToolCall call, JsonElement args)
    {
        // Explicitly context-only images cannot acquire authority by retrying mouse.
        // Unknown IDs still go through native freshness validation; malformed calls keep normal validation.
        if (call.Name == ToolCatalog.BrowserVisionMouse && args.ValueKind == JsonValueKind.Object
            && _unusableSnapshots.Contains(Read(args, "snapshotId"))
            && BrowserToolArguments.TryRequest(Guid.Empty, call.Name, args, out _, out _))
            return JsonSerializer.Serialize(new { error = "coordinate_evidence_unavailable", message = CoordinateInstruction,
                strategySuppressed = true, effectAttempted = false });
        // Malformed calls retain their existing validation/recovery path.
        if (!DialogPending || !call.Name.StartsWith("browser.", StringComparison.Ordinal)
            || !BrowserToolArguments.TryRequest(Guid.Empty, call.Name, args, out _, out _)) return null;
        if (DialogRecoveryTool(call.Name))
        {
            var prefix = call.Name + ":" + Read(args, "operation") + ":";
            var failed = _blockedFailures.FirstOrDefault(pair => pair.Value >= 2 && pair.Key.StartsWith(prefix, StringComparison.Ordinal));
            return failed.Key is null ? null : JsonSerializer.Serialize(new
            { error = failed.Key[prefix.Length..], message = RepeatedDialogInstruction, strategySuppressed = true });
        }
        return JsonSerializer.Serialize(new { error = "dialog_pending", message = DialogInstruction, strategySuppressed = true });
    }

    internal void NoteResult(ModelToolCall call, string json)
    {
        if (!call.Name.StartsWith("browser.", StringComparison.Ordinal)) return;
        try
        {
            using var receipt = JsonDocument.Parse(json);
            var root = receipt.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;
            var error = Read(root, "error");
            if (error == "target_missing" || call.Name == ToolCatalog.BrowserFind && error == "not_found" || call.Name == ToolCatalog.BrowserFind && error.Length == 0
                && root.TryGetProperty("matches", out var matches) && matches.ValueKind == JsonValueKind.Array && matches.GetArrayLength() == 0)
                _semanticMisses = Math.Min(3, _semanticMisses + 1);
            else if (error.Length == 0)
            {
                if (call.Name is ToolCatalog.BrowserScreenshot)
                {
                    _captures++; _semanticMisses = 0;
                    if (root.TryGetProperty("coordinateEvidence", out var coordinate) && coordinate.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    {
                        _coordinateUnavailable = !coordinate.GetBoolean();
                        _unavailableCaptures = _coordinateUnavailable ? _unavailableCaptures + 1 : 0;
                        if (_coordinateUnavailable && Read(root, "snapshotId") is { Length: > 0 } id) _unusableSnapshots.Add(id);
                    }
                }
                else if (call.Name == ToolCatalog.BrowserFind || call.Name == ToolCatalog.BrowserNavigate || BrowserToolCatalog.IsInteraction(call.Name))
                { _semanticMisses = 0; _coordinateUnavailable = false; _unavailableCaptures = 0; }
                else if (TryFingerprint(json, out var page))
                { if (_semanticPage is not null && _semanticPage != page) _semanticMisses = 0; _semanticPage = page; }
            }
            if (call.Name == ToolCatalog.BrowserScreenshot && error is "capture_limit" or "forbidden" or "unsupported_operation" or "provider_unavailable")
                _captureUnavailable = true;
            using var arguments = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
            var operation = arguments.RootElement.ValueKind == JsonValueKind.Object ? Read(arguments.RootElement, "operation") : "";
            if (error == "dialog_pending")
            {
                // A resolution followed by another modal is a new blocking condition.
                if (call.Name == ToolCatalog.BrowserDialog && operation is "accept" or "dismiss") ClearDialog();
                DialogPending = true;
                CountBlocked("dialog_pending");
            }
            else if (error == "dialog_missing" && call.Name == ToolCatalog.BrowserDialog
                || error.Length == 0 && call.Name != ToolCatalog.BrowserConfiguration
                    && (call.Name != ToolCatalog.BrowserDialog || operation != "inspect")) ClearDialog();
            else if (DialogPending && error.Length > 0 && error is not ("invalid" or "invalid_target" or "invalid_frame" or "forbidden" or "invalid_tool_strategy_blocked")
                && DialogRecoveryTool(call.Name)) CountBlocked(call.Name + ":" + operation + ":" + error);
        }
        catch (JsonException) { }
    }

    private void CountBlocked(string key) => _blockedFailures[key] = Math.Min(3, _blockedFailures.GetValueOrDefault(key) + 1);
    private void ClearDialog() { if (DialogPending) Reset(); DialogPending = false; _blockedFailures.Clear(); }

    internal int Repeated { get; private set; }

    internal bool ShouldStop => Repeated >= StopAfterRepeatedEvidence;

    internal void Reset()
    {
        _fingerprint = null;
        Repeated = 0;
    }

    internal void Note(string tool, string? json)
    {
        if (tool == ToolCatalog.BrowserFind && json is not null)
        {
            try
            {
                using var found = JsonDocument.Parse(json);
                if (!found.RootElement.TryGetProperty("error", out _) && found.RootElement.TryGetProperty("matches", out var matches)
                    && matches.ValueKind == JsonValueKind.Array && matches.GetArrayLength() > 0)
                {
                    var evidence = string.Join("\n", matches.EnumerateArray().Select(e => string.Join("|", Read(e, "role"), Read(e, "name"), Actions(e), State(e, "value"), State(e, "checked"), State(e, "selectedText"))));
                    if (_searchEvidence.Add(evidence)) Reset();
                }
            }
            catch (JsonException) { }
            return;
        }

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
            var content = Read(root, "content");
            var builder = new StringBuilder();
            builder.Append(content).Append('\n');
            builder.Append(urlProperty.GetString()).Append('\n').Append(settled).Append('\n');
            if (root.TryGetProperty("targets", out var elements) && elements.ValueKind == JsonValueKind.Array)
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
