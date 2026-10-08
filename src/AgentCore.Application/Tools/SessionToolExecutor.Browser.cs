using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentCore.Application.Observability;
using AgentCore.Application.Ports;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Tools;

public sealed partial class SessionToolExecutor
{
    private static readonly HashSet<string> BrowserErrorCodes = new(StringComparer.Ordinal)
    {
        "forbidden",
        "invalid",
        "target_denied",
        "stale_reference",
        "timeout",
        "provider_unavailable",
        "unsupported_operation",
        "profile_busy",
        "profile_unavailable",
        "user_intervention_required",
        "target_unreachable",
        "stale_tab",
        "dialog_pending",
        "dialog_missing",
        "last_tab",
        "no_popup",
        "capture_too_large",
        "capture_limit",
        "download_rejected",
        "download_too_large",
        "download_limit"
    };

    private readonly Dictionary<string, int> _captureCounts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _downloadCounts = new(StringComparer.Ordinal);
    private readonly object _captureGate = new();

    private async Task<string?> BindBrowserAsync(Guid sessionId, ToolExecutionAdmission? admission, CancellationToken ct)
    {
        if (admission?.AgentInstanceId is Guid id && id != Guid.Empty)
        {
            if (_agentInstances is not null && await _agentInstances.FindAsync(id, ct) is not { Lifecycle: AgentInstanceLifecycle.Active })
                return Error("forbidden", "An active Agent Instance is required.");
            if (browser is IBrowserProfileBinding binding) binding.BindSession(sessionId, id);
        }
        else if (admission?.Detached == true) return Error("forbidden", "An Agent Instance is required.");
        return null;
    }

    private async Task<string> NavigateBrowserAsync(
        Guid sessionId,
        JsonElement args,
        ToolExecutionAdmission? admission,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var denied = await BindBrowserAsync(sessionId, admission, cancellationToken).ConfigureAwait(false);
        if (denied is not null)
        {
            return FinishBrowser(ToolCatalog.BrowserNavigate, started, denied);
        }

        if (!BrowserToolArguments.TryNavigate(args, out var operation, out var url, out var errorJson))
        {
            return FinishBrowser(ToolCatalog.BrowserNavigate, started, errorJson);
        }

        if (browser is null)
        {
            return FinishBrowser(
                ToolCatalog.BrowserNavigate,
                started,
                Error("provider_unavailable", "Browser is unavailable."));
        }

        Uri? destination = null;
        if (operation == "goto")
        {
            var decision = BrowserTargetPolicy.EvaluateDestination(
                url,
                browser.HostPolicy.NavigationOrigins,
                browser.HostPolicy.PolicyMode);
            if (!decision.Allowed)
            {
                return FinishBrowser(
                    ToolCatalog.BrowserNavigate,
                    started,
                    Error(decision.Code ?? "target_denied", decision.Message ?? "Browser target is not allowed."));
            }

            if (!Uri.TryCreate(url, UriKind.Absolute, out destination))
            {
                return FinishBrowser(
                    ToolCatalog.BrowserNavigate,
                    started,
                    Error("invalid", "url must be an absolute http or https URL."));
            }
        }

        if (!browser.IsAvailable)
        {
            return FinishBrowser(
                ToolCatalog.BrowserNavigate,
                started,
                Error("provider_unavailable", "Browser is unavailable."));
        }

        try
        {
            var result = await browser
                .NavigateAsync(new BrowserNavigateRequest(sessionId, destination, operation), cancellationToken)
                .ConfigureAwait(false);
            return FinishBrowser(
                ToolCatalog.BrowserNavigate,
                started,
                await PresentBrowserAsync(sessionId, admission, result, cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            RecordBrowser(ToolCatalog.BrowserNavigate, started, "canceled");
            throw;
        }
    }

    private async Task<string> SnapshotBrowserAsync(
        Guid sessionId,
        JsonElement args,
        ToolExecutionAdmission? admission,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var denied = await BindBrowserAsync(sessionId, admission, cancellationToken).ConfigureAwait(false);
        if (denied is not null)
        {
            return FinishBrowser(ToolCatalog.BrowserSnapshot, started, denied);
        }

        if (!BrowserToolArguments.TrySnapshot(args, out var observeOptions, out var errorJson))
        {
            return FinishBrowser(ToolCatalog.BrowserSnapshot, started, errorJson);
        }

        if (browser is not { IsAvailable: true })
        {
            return FinishBrowser(
                ToolCatalog.BrowserSnapshot,
                started,
                Error("provider_unavailable", "Browser is unavailable."));
        }

        try
        {
            var result = observeOptions is null
                ? await browser.SnapshotAsync(sessionId, cancellationToken).ConfigureAwait(false)
                : await browser.SnapshotAsync(sessionId, observeOptions, cancellationToken).ConfigureAwait(false);
            return FinishBrowser(
                ToolCatalog.BrowserSnapshot,
                started,
                await PresentBrowserAsync(sessionId, admission, result, cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            RecordBrowser(ToolCatalog.BrowserSnapshot, started, "canceled");
            throw;
        }
    }

    private const int MaxBrowserUploadBytes = 8 * 1024 * 1024;

    private async Task<string> InteractBrowserAsync(
        string toolName,
        AgentDefinition definition,
        Guid sessionId,
        JsonElement args,
        ToolExecutionAdmission? admission,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var denied = await BindBrowserAsync(sessionId, admission, cancellationToken).ConfigureAwait(false);
        if (denied is not null)
        {
            return FinishBrowser(toolName, started, denied);
        }

        if (!BrowserToolArguments.TryInteraction(
                args,
                out var operation,
                out var reference,
                out var value,
                out var direction,
                out var delta,
                out var targetRef,
                out var errorJson))
        {
            return FinishBrowser(toolName, started, errorJson);
        }

        if (browser is null)
        {
            return FinishBrowser(
                toolName,
                started,
                Error("provider_unavailable", "Browser is unavailable."));
        }

        if (browser.HostPolicy.InteractionMode != BrowserInteractionMode.InteractiveDemo)
        {
            return FinishBrowser(
                toolName,
                started,
                Error("forbidden", "Browser actions are not allowed in this interaction mode."));
        }

        if (!browser.IsAvailable)
        {
            return FinishBrowser(
                toolName,
                started,
                Error("provider_unavailable", "Browser is unavailable."));
        }

        try
        {
            var current = await browser.GetCurrentUrlAsync(sessionId, cancellationToken).ConfigureAwait(false);
            if (current is null)
            {
                return FinishBrowser(
                    toolName,
                    started,
                    Error("provider_unavailable", "Browser is unavailable."));
            }

            var decision = BrowserTargetPolicy.EvaluateAct(
                browser.HostPolicy.InteractionMode,
                current.AbsoluteUri,
                browser.HostPolicy.EffectiveInteractionOrigins,
                browser.HostPolicy.PolicyMode);
            if (!decision.Allowed)
            {
                return FinishBrowser(
                    toolName,
                    started,
                    Error(decision.Code ?? "forbidden", decision.Message ?? "Browser actions are not permitted."));
            }

            if (operation == "fill_credential")
            {
                if (admission is not { Detached: false, TriggerKind: TriggerKind.UserTurn, AgentInstanceId: Guid owner }
                    || credentials is null || browser is not IBrowserPasswordSink sink)
                    return FinishBrowser(toolName, started, Error("forbidden", "Protected credential use requires direct Chat."));
                var alias = value!;
                var filled = await sink.FillCredentialAsync(sessionId, reference,
                    (origin, ct) => credentials.ResolvePasswordAsync(owner, alias, origin, ct), cancellationToken);
                return FinishBrowser(toolName, started, await PresentBrowserAsync(sessionId, admission, filled, cancellationToken));
            }

            BrowserUpload? upload = null;
            if (operation == "upload")
            {
                var resolved = await ResolveBrowserUploadAsync(definition, sessionId, value, cancellationToken)
                    .ConfigureAwait(false);
                if (resolved.ErrorJson is not null)
                {
                    return FinishBrowser(toolName, started, resolved.ErrorJson);
                }

                upload = resolved.Upload;
                value = null;
            }

            var result = await browser
                .InteractAsync(
                    new BrowserInteractionRequest(sessionId, operation, reference, value, upload, direction, delta, targetRef),
                    cancellationToken)
                .ConfigureAwait(false);
            return FinishBrowser(
                toolName,
                started,
                await PresentBrowserAsync(sessionId, admission, result, cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            RecordBrowser(toolName, started, "canceled");
            throw;
        }
    }

    private async Task<(BrowserUpload? Upload, string? ErrorJson)> ResolveBrowserUploadAsync(
        AgentDefinition definition,
        Guid sessionId,
        string? artifactId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(artifactId) || !Guid.TryParse(artifactId, out var id))
        {
            return (null, Error("invalid", "The artifact was not found."));
        }

        if (artifacts is not null && artifacts.Exists(sessionId, id))
        {
            var record = await artifacts.GetAsync(sessionId, id, cancellationToken).ConfigureAwait(false);
            if (record is null || record.ByteSize <= 0 || record.ByteSize > MaxBrowserUploadBytes)
            {
                return (null, Error("invalid", "The artifact cannot be uploaded."));
            }

            await using var stream = await artifacts.OpenContentAsync(sessionId, id, cancellationToken).ConfigureAwait(false);
            var bytes = await ReadBoundedUploadAsync(stream, cancellationToken).ConfigureAwait(false);
            if (bytes is null)
            {
                return (null, Error("invalid", "The artifact cannot be uploaded."));
            }

            return (new BrowserUpload(SafeUploadFileName(record.DisplayName), SafeUploadMediaType(record.ContentType), bytes), null);
        }

        if (definitionResources is not null)
        {
            var published = await definitionResources
                .ListPublicationResourcesAsync(definition.Id, definition.Version, cancellationToken)
                .ConfigureAwait(false);
            var match = published.FirstOrDefault(item => item.ResourceId == id);
            if (match is null)
            {
                return (null, Error("invalid", "The artifact was not found."));
            }

            if (match.ByteLength <= 0 || match.ByteLength > MaxBrowserUploadBytes)
            {
                return (null, Error("invalid", "The artifact cannot be uploaded."));
            }

            var content = await definitionResources
                .ReadPublicationResourceContentAsync(definition.Id, definition.Version, id, cancellationToken)
                .ConfigureAwait(false);
            if (content is null || content.Length == 0 || content.Length > MaxBrowserUploadBytes)
            {
                return (null, Error("invalid", "The artifact was not found."));
            }

            return (new BrowserUpload(SafeUploadFileName(match.LogicalPath), SafeUploadMediaType(match.MediaType), content), null);
        }

        return (null, Error("invalid", "The artifact was not found."));
    }

    private static async Task<byte[]?> ReadBoundedUploadAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (buffer.Length + read > MaxBrowserUploadBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.Length == 0 ? null : buffer.ToArray();
    }

    private static string SafeUploadFileName(string? raw)
    {
        var name = Path.GetFileName((raw ?? string.Empty).Replace('\\', '/').Trim());
        if (string.IsNullOrWhiteSpace(name) || name is "." or "..")
        {
            return "upload.bin";
        }

        return name.Length <= 120 ? name : name[^120..];
    }

    private static string SafeUploadMediaType(string? raw)
    {
        var value = (raw ?? string.Empty).Trim();
        var slash = value.IndexOf('/');
        if (slash <= 0 || slash != value.LastIndexOf('/') || value.Length > 100)
        {
            return "application/octet-stream";
        }

        foreach (var character in value)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('/' or '.' or '+' or '-'))
            {
                return "application/octet-stream";
            }
        }

        return value;
    }

    private async Task<string> CloseBrowserAsync(
        Guid sessionId,
        JsonElement args,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        if (!BrowserToolArguments.TryClose(args, out var errorJson))
        {
            return FinishBrowser(ToolCatalog.BrowserClose, started, errorJson);
        }

        if (browser is not { IsAvailable: true })
        {
            return FinishBrowser(
                ToolCatalog.BrowserClose,
                started,
                Error("provider_unavailable", "Browser is unavailable."));
        }

        try
        {
            var result = await browser.CloseAsync(sessionId, cancellationToken).ConfigureAwait(false);
            var status = result.Status is "closed" or "already_closed" or "busy" or "provider_unavailable"
                ? result.Status
                : "provider_unavailable";
            var json = status == "provider_unavailable"
                ? Error("provider_unavailable", "Browser is unavailable.")
                : JsonSerializer.Serialize(new { status });
            return FinishBrowser(ToolCatalog.BrowserClose, started, json);
        }
        catch (OperationCanceledException)
        {
            RecordBrowser(ToolCatalog.BrowserClose, started, "canceled");
            throw;
        }
    }

    private async Task<ToolExecutionResult> CaptureBrowserAsync(
        Guid sessionId,
        JsonElement args,
        ToolExecutionAdmission? admission,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var denied = await BindBrowserAsync(sessionId, admission, cancellationToken).ConfigureAwait(false);
        if (denied is not null)
        {
            return TextResult(FinishBrowser(ToolCatalog.BrowserScreenshot, started, denied));
        }

        if (!TryConsumeCapture(admission, sessionId))
        {
            return TextResult(FinishBrowser(
                ToolCatalog.BrowserScreenshot,
                started,
                Error("capture_limit", "This turn already captured the maximum number of images.")));
        }

        if (browser is null || !browser.IsAvailable)
        {
            ReleaseCapture(admission, sessionId);
            return TextResult(FinishBrowser(
                ToolCatalog.BrowserScreenshot,
                started,
                Error("provider_unavailable", "Browser is unavailable.")));
        }

        try
        {
            var captured = await browser
                .CaptureViewportAsync(new BrowserScreenshotRequest(sessionId, args.TryGetProperty("format", out var format) ? format.GetString()! : "png", args.TryGetProperty("fullPage", out var full) && full.GetBoolean(), args.TryGetProperty("targetRef", out var target) ? target.GetString() : null), cancellationToken)
                .ConfigureAwait(false);
            if (!string.IsNullOrEmpty(captured.ErrorCode) || captured.Png is not { Length: > 0 })
            {
                ReleaseCapture(admission, sessionId);
                var code = captured.ErrorCode is not null && BrowserErrorCodes.Contains(captured.ErrorCode)
                    ? captured.ErrorCode
                    : "provider_unavailable";
                return TextResult(FinishBrowser(ToolCatalog.BrowserScreenshot, started, Error(code, "Browser capture failed.")));
            }

            if (captured.Png.Length > BrowserToolLimits.MaxCaptureBytes)
            {
                ReleaseCapture(admission, sessionId);
                return TextResult(FinishBrowser(
                    ToolCatalog.BrowserScreenshot,
                    started,
                    Error("capture_too_large", "The captured image exceeds the byte cap.")));
            }

            var stored = await StoreBrowserBytesAsync(sessionId, admission, "screenshot." + captured.ContentType.Split('/')[1], captured.ContentType, captured.Png, cancellationToken).ConfigureAwait(false);
            if (stored.Error is not null)
            {
                ReleaseCapture(admission, sessionId);
                return TextResult(FinishBrowser(ToolCatalog.BrowserScreenshot, started, stored.Error));
            }

            var text = JsonSerializer.Serialize(new
            {
                contentType = captured.ContentType,
                byteSize = captured.Png.Length,
                width = captured.Width,
                height = captured.Height,
                redactions = captured.RedactionCount,
                artifactId = stored.ArtifactId
            });
            return new ToolExecutionResult(
                FinishBrowser(ToolCatalog.BrowserScreenshot, started, text),
                admission?.SupportsVision == true ? [new ModelImageContent(captured.ContentType, captured.Png, "screenshot")] : []);
        }
        catch (OperationCanceledException)
        {
            ReleaseCapture(admission, sessionId);
            RecordBrowser(ToolCatalog.BrowserScreenshot, started, "canceled");
            throw;
        }
    }

    private bool TryConsumeCapture(ToolExecutionAdmission? admission, Guid sessionId)
    {
        var key = CaptureKey(admission, sessionId);
        lock (_captureGate)
        {
            var count = _captureCounts.GetValueOrDefault(key);
            if (count >= BrowserToolLimits.MaxCapturesPerScope)
            {
                return false;
            }

            _captureCounts[key] = count + 1;
            return true;
        }
    }

    private void ReleaseCapture(ToolExecutionAdmission? admission, Guid sessionId)
    {
        var key = CaptureKey(admission, sessionId);
        lock (_captureGate)
        {
            ReleaseScope(_captureCounts, key);
        }
    }

    private static string CaptureKey(ToolExecutionAdmission? admission, Guid sessionId) =>
        admission?.CaptureScope ?? sessionId.ToString("D");

    private ValueTask<(string? ArtifactId, string? Error)> StoreCaptureAsync(
        Guid sessionId,
        ToolExecutionAdmission? admission,
        byte[] png,
        CancellationToken cancellationToken) =>
        StoreBrowserBytesAsync(sessionId, admission, "capture.png", "image/png", png, cancellationToken);

    private async Task<string> PresentBrowserAsync(
        Guid sessionId,
        ToolExecutionAdmission? admission,
        BrowserOperationResult result,
        CancellationToken cancellationToken)
    {
        if (admission?.Detached == true && result.Observation?.Elements.Any(e => e.Actions.Contains("fill_credential")) == true)
            result = result with { Observation = result.Observation with { Intervention = BrowserInterventionKind.AuthenticationRequired } };
        var json = FromBrowserProvider(result);
        if (result.Downloads is not { Count: > 0 } downloads)
        {
            return json;
        }

        var reports = new List<DownloadReceipt>(downloads.Count);
        foreach (var download in downloads)
        {
            reports.Add(await ReportDownloadAsync(sessionId, admission, download, cancellationToken).ConfigureAwait(false));
        }

        return MergeDownloads(json, reports);
    }

    private async Task<DownloadReceipt> ReportDownloadAsync(
        Guid sessionId,
        ToolExecutionAdmission? admission,
        BrowserDownload download,
        CancellationToken cancellationToken)
    {
        var fileName = BrowserDownloadPolicy.SanitizeFileName(download.FileName);
        if (!string.IsNullOrEmpty(download.ErrorCode) || download.Bytes is not { Length: > 0 } bytes)
        {
            return new DownloadReceipt(download.ErrorCode ?? "download_rejected", fileName, null, null, null);
        }

        if (!BrowserDownloadPolicy.TryAccept(fileName, bytes, out var contentType, out var error))
        {
            return new DownloadReceipt(error, fileName, null, null, null);
        }

        if (!TryConsumeDownload(admission, sessionId))
        {
            return new DownloadReceipt("download_limit", fileName, null, null, null);
        }

        var stored = await StoreBrowserBytesAsync(
                sessionId,
                admission,
                fileName,
                contentType,
                bytes,
                cancellationToken)
            .ConfigureAwait(false);
        if (stored.Error is not null)
        {
            ReleaseDownload(admission, sessionId);
            return new DownloadReceipt(
                stored.Error.Contains("capture_limit", StringComparison.Ordinal) ? "capture_limit" : "download_rejected",
                fileName,
                null,
                null,
                null);
        }

        return new DownloadReceipt(null, fileName, contentType, bytes.Length, stored.ArtifactId);
    }

    private bool TryConsumeDownload(ToolExecutionAdmission? admission, Guid sessionId)
    {
        var key = CaptureKey(admission, sessionId);
        lock (_captureGate)
        {
            var count = _downloadCounts.GetValueOrDefault(key);
            if (count >= BrowserToolLimits.MaxDownloadsPerScope)
            {
                return false;
            }

            _downloadCounts[key] = count + 1;
            return true;
        }
    }

    private void ReleaseDownload(ToolExecutionAdmission? admission, Guid sessionId)
    {
        var key = CaptureKey(admission, sessionId);
        lock (_captureGate)
        {
            ReleaseScope(_downloadCounts, key);
        }
    }

    private static void ReleaseScope(Dictionary<string, int> counts, string key)
    {
        if (!counts.TryGetValue(key, out var count) || count <= 1)
        {
            counts.Remove(key);
            return;
        }

        counts[key] = count - 1;
    }

    private async ValueTask<(string? ArtifactId, string? Error)> StoreBrowserBytesAsync(
        Guid sessionId,
        ToolExecutionAdmission? admission,
        string fileName,
        string contentType,
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        if (admission is { Detached: true, WorkItemId: Guid workItemId }
            && workCaptures is not null
            && admission.AgentInstanceId is Guid agentInstanceId)
        {
            var saved = await workCaptures
                .SaveAsync(workItemId, agentInstanceId, contentType, bytes, cancellationToken)
                .ConfigureAwait(false);
            return saved.ErrorCode is null
                ? (saved.Capture?.CaptureId.ToString("D"), null)
                : (null, Error(saved.ErrorCode, "The work item cannot store another capture."));
        }

        if (artifacts is null)
        {
            return (null, Error("provider_unavailable", "Capture storage is unavailable."));
        }

        var record = await artifacts
            .CreateAsync(sessionId, fileName, contentType, bytes, null, null, cancellationToken)
            .ConfigureAwait(false);
        return (record.ArtifactId.ToString("D"), null);
    }

    private static string MergeDownloads(string json, IReadOnlyList<DownloadReceipt> downloads)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return json;
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                property.WriteTo(writer);
            }

            writer.WritePropertyName("downloads");
            JsonSerializer.Serialize(writer, downloads, DownloadJson);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static readonly JsonSerializerOptions DownloadJson = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private sealed record DownloadReceipt(
        string? error,
        string? fileName,
        string? contentType,
        int? byteSize,
        string? artifactId);

    private static string FromBrowserProvider(BrowserOperationResult result)
    {
        if (string.IsNullOrEmpty(result.ErrorCode))
        {
            if (result.Observation is null)
            {
                return Error("provider_unavailable", "Browser is unavailable.");
            }

            if (result.Observation.Intervention != BrowserInterventionKind.None)
            {
                return SerializeBrowserIntervention(result.Observation);
            }

            return SerializeBrowserSnapshot(result.Observation);
        }

        var code = BrowserErrorCodes.Contains(result.ErrorCode) ? result.ErrorCode : "provider_unavailable";
        if (code == "unsupported_operation" && result.AllowedActions is { Count: > 0 })
        {
            var allowed = result.AllowedActions
                .Where(action => BrowserToolLimits.Operations.Contains(action, StringComparer.Ordinal))
                .Distinct(StringComparer.Ordinal)
                .Take(BrowserToolLimits.Operations.Length)
                .ToArray();
            if (allowed.Length > 0)
            {
                return JsonSerializer.Serialize(new
                {
                    error = "unsupported_operation",
                    message = "This element does not support that operation.",
                    allowedActions = allowed
                });
            }
        }

        return Error(code, BrowserFailureMessage(code));
    }

    private static string SerializeBrowserSnapshot(BrowserSnapshot observation)
    {
        var elements = (observation.Elements ?? [])
            .Select(element =>
            {
                var item = new Dictionary<string, object?>
                {
                    ["ref"] = ClipBrowser(element.Ref, BrowserToolLimits.MaxRefLength),
                    ["role"] = ClipBrowser(element.Role, BrowserToolLimits.MaxRoleLength),
                    ["name"] = ClipBrowser(element.Name, BrowserToolLimits.MaxAccessibleNameLength),
                    ["actions"] = (element.Actions ?? [])
                        .Where(action => BrowserToolLimits.Operations.Contains(action, StringComparer.Ordinal))
                        .Distinct(StringComparer.Ordinal)
                        .Take(BrowserToolLimits.Operations.Length)
                        .ToArray()
                };
                var state = ControlState(element.State);
                if (state is not null)
                {
                    item["state"] = state;
                }

                return item;
            })
            .ToArray();
        // Bound the serialized response, not the provider's complete discovery index.
        var elementBytes = 2;
        elements = elements.TakeWhile(element =>
        {
            elementBytes += System.Text.Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(element)) + 1;
            return elementBytes <= BrowserToolLimits.MaxSnapshotChars;
        }).ToArray();
        var text = observation.VisibleText ?? string.Empty;
        var truncated = observation.TextTruncated || text.Length > BrowserToolLimits.MaxVisibleTextLength;
        var url = ClipBrowser(observation.Url, BrowserToolLimits.MaxUrlLength);
        var title = ClipBrowser(observation.Title, BrowserToolLimits.MaxTitleLength);
        var visibleText = ClipBrowser(text, BrowserToolLimits.MaxVisibleTextLength);
        return observation.Settled is bool settled
            ? JsonSerializer.Serialize(new
            {
                untrustedBrowserContent = true,
                status = "ok",
                snapshotId = observation.SnapshotId,
                tabRef = observation.TabRef,
                content = ClipBrowser(observation.Content, BrowserToolLimits.MaxSnapshotChars),
                boxes = observation.Boxes?.Select(b => new { @ref = b.Ref, x = b.X, y = b.Y, width = b.Width, height = b.Height }),
                url,
                title,
                visibleText,
                textTruncated = truncated,
                settled,
                elements
            })
            : JsonSerializer.Serialize(new
            {
                untrustedBrowserContent = true,
                status = "ok",
                snapshotId = observation.SnapshotId,
                tabRef = observation.TabRef,
                content = ClipBrowser(observation.Content, BrowserToolLimits.MaxSnapshotChars),
                boxes = observation.Boxes?.Select(b => new { @ref = b.Ref, x = b.X, y = b.Y, width = b.Width, height = b.Height }),
                url,
                title,
                visibleText,
                textTruncated = truncated,
                elements
            });
    }

    private static string SerializeBrowserIntervention(BrowserSnapshot observation)
    {
        var kind = observation.Intervention switch
        {
            BrowserInterventionKind.AccountRegistrationRequired => "registration",
            BrowserInterventionKind.HumanVerificationRequired => "verification",
            _ => "authentication"
        };
        return JsonSerializer.Serialize(new
        {
            error = "user_intervention_required",
            kind,
            url = ClipBrowser(observation.Url, BrowserToolLimits.MaxUrlLength),
            title = ClipBrowser(observation.Title, BrowserToolLimits.MaxTitleLength),
            message = "Complete this step in the browser, then tell me to continue."
        });
    }

    private static Dictionary<string, object?>? ControlState(BrowserControlState? state)
    {
        if (state is null)
        {
            return null;
        }

        var payload = new Dictionary<string, object?>();
        if (state.Value is not null)
        {
            payload["value"] = ClipBrowser(state.Value, BrowserToolLimits.MaxFillLength);
        }

        if (state.Checked is not null)
        {
            payload["checked"] = state.Checked.Value;
        }

        if (state.SelectedText is not null)
        {
            payload["selectedText"] = ClipBrowser(state.SelectedText, BrowserToolLimits.MaxFillLength);
        }

        return payload.Count == 0 ? null : payload;
    }

    private static string ClipBrowser(string? value, int max)
    {
        var text = value ?? string.Empty;
        return text.Length <= max ? text : text[..max];
    }

    private static string BrowserFailureMessage(string code) =>
        code switch
        {
            "forbidden" => "Browser operation is not permitted.",
            "invalid" => "Browser arguments are invalid.",
            "target_denied" => "Browser target is not allowed.",
            "stale_reference" => "Element reference is stale.",
            "target_unreachable" => "The host refused the connection. Do not retry that host.",
            "timeout" => "Browser operation timed out.",
            "unsupported_operation" => "Browser operation is not supported.",
            "profile_busy" => "The browser profile is already in use.",
            "profile_unavailable" => "The browser profile is unavailable.",
            "user_intervention_required" => "Complete this step in the browser, then tell me to continue.",
            _ => "Browser is unavailable."
        };

    private string FinishBrowser(string toolName, long started, string json)
    {
        var outcome = "ok";
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.String
                && error.GetString() is { Length: > 0 } code)
            {
                outcome = code;
            }
        }
        catch (JsonException)
        {
            outcome = "provider_unavailable";
        }

        RecordBrowser(toolName, started, outcome);
        return json;
    }

    private void RecordBrowser(string toolName, long started, string outcome) =>
        RuntimeTelemetry.RecordBrowserOperation(
            browser?.Provider.ProviderId ?? "unavailable",
            BrowserToolCatalog.TryGet(toolName, out var metadata) ? metadata.Feature.ToString() : "Unknown",
            toolName,
            outcome is "ok" or "canceled" || BrowserErrorCodes.Contains(outcome) ? outcome : "unknown",
            RuntimeTelemetry.ElapsedMs(started));

    public async ValueTask<OccurrenceBrowserScope> OpenOccurrenceBrowserAsync(Guid workItemId, Guid agentInstanceId, CancellationToken ct)
    {
        if (workItemId != Guid.Empty && agentInstanceId != Guid.Empty && browser is IBrowserProfileBinding binding)
            binding.BindSession(workItemId, agentInstanceId);
        IAsyncDisposable? lease = null;
        if (workItemId != Guid.Empty && agentInstanceId != Guid.Empty && browser is { IsAvailable: true }
            && _configurationGate.IsConfigured(ToolCatalog.BrowserNavigate) && browser is IBrowserContextUse use)
            lease = await use.EnterUnattendedAsync(agentInstanceId, [], ct);
        return new OccurrenceBrowserScope(browser, workItemId, lease);
    }

    public void AdoptOccurrenceBrowser(Guid agentInstanceId)
    {
        if (browser is IBrowserContextUse use)
        {
            use.AdoptUnattendedFlow(agentInstanceId);
        }
    }
}

public sealed class OccurrenceBrowserScope : IAsyncDisposable
{
    private readonly IBrowser? _browser;
    private readonly Guid _workItemId;
    private readonly IAsyncDisposable? _lease;

    public OccurrenceBrowserScope(IBrowser? browser, Guid workItemId, IAsyncDisposable? lease)
    {
        _browser = browser;
        _workItemId = workItemId;
        _lease = lease;
        PersistentBrowserLease = lease is not null;
    }

    public bool PersistentBrowserLease { get; }

    public async ValueTask DisposeAsync()
    {
        if (_lease is not null)
        {
            await _lease.DisposeAsync().ConfigureAwait(false);
        }

        if (_workItemId != Guid.Empty && _browser is IBrowserLease sessionLease)
        {
            await sessionLease.ReleaseAsync(_workItemId).ConfigureAwait(false);
        }
    }
}
