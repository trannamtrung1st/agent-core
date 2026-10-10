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
    private async ValueTask<ToolExecutionResult> ExecuteBrowserAsync(AgentDefinition definition, Guid sessionId,
        string name, JsonElement args, ToolExecutionAdmission? admission, int remainingOutputBytes, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        ToolExecutionResult Fail(string code, string message) => FitResult(remainingOutputBytes, FinishBrowser(name, started, Error(code, message)));
        if (BrowserContractCutover.Diagnostic(definition, admission?.PinnedSkillCatalog, admission?.ActiveSkillKeys) is { } diagnostic)
            return Fail("browser_contract_retired", diagnostic);
        if (!BrowserToolCatalog.TryGet(name, out var metadata)) return Fail("unsupported_operation", "Unknown browser feature.");
        if (!BrowserToolArguments.TryRequest(sessionId, name, args, out var request, out var argumentError))
            return Fail(argumentError, argumentError == "invalid_frame" ? BrowserFailureMessage(argumentError) : BrowserToolArguments.ArgumentGuidance(args));
        var denied = await BindBrowserAsync(sessionId, admission, ct).ConfigureAwait(false);
        if (denied is not null) return TextResult(FinishBrowser(name, started, denied));
        if (browser is null || !browser.IsAvailable && metadata.Feature != BrowserFeature.Configuration) return Fail("provider_unavailable", "Browser is unavailable.");
        if (!BrowserToolArguments.WithinOperationalLimits(request.Command, browser.HostPolicy.Limits)) return Fail("invalid", "Input exceeds the effective host browser budget.");
        if (!browser.Provider.Supports(metadata.Feature)) return Fail("unsupported_operation", "The active provider does not support this feature.");
        if (metadata.Feature == BrowserFeature.VisionMouse && admission?.SupportsVision != true) return Fail("forbidden", "Coordinate actions require a vision model.");
        if (metadata.Feature is BrowserFeature.FillCredential or BrowserFeature.Geolocation
            && admission is not { Detached: false, TriggerKind: TriggerKind.UserTurn }) return Fail("forbidden", "This feature requires a direct attached user turn.");
        if (request.Command is BrowserNavigate { Operation: "goto" } navigate
            && !BrowserTargetPolicy.EvaluateDestination(navigate.Url, browser.HostPolicy.NavigationOrigins, browser.HostPolicy.PolicyMode).Allowed)
            return Fail("target_denied", BrowserFailureMessage("target_denied"));
        if (name == ToolCatalog.BrowserScreenshot)
            return await CaptureBrowserAsync(sessionId, args, admission, remainingOutputBytes, ct);
        if (name == ToolCatalog.BrowserUpload)
        {
            var uploads = new List<BrowserUpload>();
            foreach (var id in args.GetProperty("artifactIds").EnumerateArray())
            {
                var resolved = await ResolveBrowserUploadAsync(definition, sessionId, id.GetString(), ct);
                if (resolved.ErrorJson is not null) return Fail("invalid", "The Artifact cannot be uploaded.");
                uploads.Add(resolved.Upload!);
            }
            request = request with { Command = ((BrowserUploadCommand)request.Command) with { Uploads = uploads } };
        }
        if (name == ToolCatalog.BrowserFillCredential)
        {
            if (admission is not { Detached: false, TriggerKind: TriggerKind.UserTurn, AgentInstanceId: Guid owner }
                || credentials is null || browser is not IBrowserPasswordSink sink)
                return Fail("forbidden", "Protected credential use requires direct Chat.");
            var filled = await sink.FillCredentialAsync(sessionId, ((BrowserFillCredential)request.Command).Target,
                (origin, token) => credentials.ResolvePasswordAsync(owner, ((BrowserFillCredential)request.Command).CredentialRef, origin, token), ct);
            return FitResult(remainingOutputBytes, FinishBrowser(name, started,
                await PresentBrowserAsync(sessionId, admission, filled, ct)));
        }
        return await Command();

        async ValueTask<ToolExecutionResult> Command()
        {
            BrowserResult result;
            try
            {
                result = await browser.ExecuteAsync(request, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                RecordBrowser(name, started, "canceled");
                throw;
            }
            if (result.ErrorCode is not null)
            {
                if (result.ErrorCode == "ambiguous_target" && result.DataJson is not null)
                {
                    var detail = System.Text.Json.Nodes.JsonNode.Parse(result.DataJson)!.AsObject(); detail["error"] = result.ErrorCode;
                    detail["untrustedBrowserContent"] = true;
                    return FitResult(remainingOutputBytes, FinishBrowser(name, started, detail.ToJsonString()));
                }
                return FitResult(remainingOutputBytes, FinishBrowser(name, started, FromBrowserProvider(result)));
            }
            if (result.Bytes is { Length: > 0 } bytes)
            {
                if (bytes.Length > browser.HostPolicy.Limits.DownloadBytes) return Fail("download_too_large", "Browser output exceeds the effective byte budget.");
                var stored = await StoreBrowserBytesAsync(sessionId, admission, result.FileName ?? "browser-output", result.ContentType ?? "application/octet-stream", bytes, ct);
                if (stored.Error is not null) return TextResult(FinishBrowser(name, started, stored.Error));
                var json = JsonSerializer.Serialize(new { status = "ok", artifactId = stored.ArtifactId, byteSize = bytes.Length, contentType = result.ContentType });
                return new ToolExecutionResult(FinishBrowser(name, started, json), admission?.SupportsVision == true && result.ContentType?.StartsWith("image/", StringComparison.Ordinal) == true
                    ? [new ModelImageContent(result.ContentType, bytes, result.FileName ?? "browser-output")] : []);
            }
            if (result.Observation is not null)
                return FitResult(remainingOutputBytes, FinishBrowser(name, started, await PresentBrowserAsync(sessionId, admission, result, ct)));
            return FitResult(remainingOutputBytes, FinishBrowser(name, started, SerializeBrowserData(result)));
        }
    }

    private static readonly HashSet<string> BrowserErrorCodes = new(StringComparer.Ordinal)
    {
        "forbidden",
        "invalid",
        "target_denied",
        "target_missing",
        "invalid_target",
        "invalid_frame",
        "credential_target_invalid",
        "stale_frame",
        "stale_visual_evidence",
        "action_not_confirmed",
        "ambiguous_target",
        "not_found",
        "non_actionable_target",
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
        "close_failed",
        "close_uncertain",
        "last_tab",
        "no_popup",
        "capture_invalid",
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

    private const int MaxBrowserUploadBytes = 8 * 1024 * 1024;

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

    private async Task<ToolExecutionResult> CaptureBrowserAsync(
        Guid sessionId,
        JsonElement args,
        ToolExecutionAdmission? admission,
        int remainingOutputBytes,
        CancellationToken cancellationToken)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,
            browser is IBrowserCaptureLifetime retiring ? retiring.CaptureLifetime : CancellationToken.None);
        cancellationToken = lifetime.Token;
        var started = Stopwatch.GetTimestamp();
        var denied = await BindBrowserAsync(sessionId, admission, cancellationToken).ConfigureAwait(false);
        if (denied is not null)
        {
            return FitResult(remainingOutputBytes, FinishBrowser(ToolCatalog.BrowserScreenshot, started, denied));
        }

        if (!TryConsumeCapture(admission, sessionId))
        {
            return FitResult(remainingOutputBytes, FinishBrowser(
                ToolCatalog.BrowserScreenshot,
                started,
                Error("capture_limit", "The capture budget is exhausted. Continue with semantic observation; do not retry screenshots in this scope.")));
        }

        if (browser is null || !browser.IsAvailable)
        {
            ReleaseCapture(admission, sessionId);
            return FitResult(remainingOutputBytes, FinishBrowser(
                ToolCatalog.BrowserScreenshot,
                started,
                Error("provider_unavailable", "Browser is unavailable.")));
        }

        try
        {
            var captured = await browser.ExecuteAsync(BrowserToolArguments.Request(sessionId,
                ToolCatalog.BrowserScreenshot, args), cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(captured.ErrorCode) || captured.Bytes is not { Length: > 0 })
            {
                ReleaseCapture(admission, sessionId);
                var code = captured.ErrorCode is not null && BrowserErrorCodes.Contains(captured.ErrorCode)
                    ? captured.ErrorCode
                    : "provider_unavailable";
                return FitResult(remainingOutputBytes, FinishBrowser(ToolCatalog.BrowserScreenshot, started, Error(code, "Browser capture failed. Use semantic observation; refresh visual evidence only after resolving the cause within the remaining budget.")));
            }

            if (captured.Bytes.Length > browser.HostPolicy.Limits.CaptureBytes)
            {
                ReleaseCapture(admission, sessionId);
                return FitResult(remainingOutputBytes, FinishBrowser(
                    ToolCatalog.BrowserScreenshot,
                    started,
                    Error("capture_too_large", "The captured image exceeds the byte cap.")));
            }

            var stored = await StoreBrowserBytesAsync(sessionId, admission, "screenshot." + (captured.ContentType ?? "image/png").Split('/')[1], captured.ContentType ?? "image/png", captured.Bytes, cancellationToken).ConfigureAwait(false);
            if (stored.Error is not null)
            {
                ReleaseCapture(admission, sessionId);
                return FitResult(remainingOutputBytes, FinishBrowser(ToolCatalog.BrowserScreenshot, started, stored.Error));
            }

            var text = JsonSerializer.Serialize(new
            {
                contentType = captured.ContentType,
                byteSize = captured.Bytes.Length,
                width = captured.Width,
                height = captured.Height,
                redactions = captured.RedactionCount,
                privacyMode = browser.HostPolicy.ScreenshotPolicy.Mode.ToString(),
                privacyRevision = browser.HostPolicy.ScreenshotPolicy.Revision,
                artifactId = stored.ArtifactId,
                imageDelivered = admission?.SupportsVision == true,
                observationUnavailable = captured.Observation?.ObservationUnavailable == true,
                snapshotId = captured.Observation?.SnapshotId,
                tabRef = captured.Observation?.TabRef,
                observation = captured.Observation is null ? (JsonElement?)null : JsonSerializer.Deserialize<JsonElement>(SerializeBrowserSnapshot(captured.Observation, captured)),
                coordinateEvidence = admission?.SupportsVision == true && captured.DataJson is not null && JsonSerializer.Deserialize<JsonElement>(captured.DataJson).TryGetProperty("coordinateEvidence", out var coordinate) && coordinate.ValueKind == JsonValueKind.True,
                guidance = admission?.SupportsVision == true
                    ? (captured.Observation?.ObservationUnavailable == true ? "Semantic observation is unavailable; use this image as visual context and independently refresh semantics. " : "Combine this image with its semantic observation. ") + "Prefer semantic actions. coordinateEvidence identifies fresh viewport pixels; mouse still requires vision, capability and interaction authority. Reobserve and independently verify outcomes."
                    : "Artifact only: this model did not receive image content. Continue with semantic observation; do not claim visual understanding."
            });
            text = BrowserCaptureProjection.Fit(remainingOutputBytes, text);
            var delivered = admission?.SupportsVision == true && text.Length > 0 && JsonSerializer.Deserialize<JsonElement>(text).TryGetProperty("artifactId", out _);
            cancellationToken.ThrowIfCancellationRequested();
            return new ToolExecutionResult(
                FinishBrowser(ToolCatalog.BrowserScreenshot, started, text),
                delivered ? [new ModelImageContent(captured.ContentType ?? "image/png", captured.Bytes, "screenshot")] : []);
        }
        catch (OperationCanceledException)
        {
            ReleaseCapture(admission, sessionId);
            RecordBrowser(ToolCatalog.BrowserScreenshot, started, "canceled");
            throw;
        }
        catch
        {
            ReleaseCapture(admission, sessionId);
            RecordBrowser(ToolCatalog.BrowserScreenshot, started, "provider_unavailable");
            throw;
        }
    }

    private bool TryConsumeCapture(ToolExecutionAdmission? admission, Guid sessionId)
    {
        var key = CaptureKey(admission, sessionId);
        lock (_captureGate)
        {
            var count = _captureCounts.GetValueOrDefault(key);
            if (count >= browser!.HostPolicy.Limits.CapturesPerScope)
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
        BrowserResult result,
        CancellationToken cancellationToken)
    {
        if (admission?.Detached == true && result.Observation?.HasPasswordField == true)
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

        if (bytes.Length > browser!.HostPolicy.Limits.DownloadBytes)
            return new DownloadReceipt("download_too_large", fileName, null, null, null);

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
            if (count >= browser!.HostPolicy.Limits.DownloadsPerScope)
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
        if (admission is { Detached: true } && admission.OwnedSessionId != sessionId)
            return (null, Error("forbidden", "Capture requires the admitted Session scope."));

        if (artifacts is null)
        {
            return (null, Error("provider_unavailable", "Capture storage is unavailable."));
        }

        var record = await artifacts
            .CreateAsync(sessionId, fileName, contentType, bytes, null, null, cancellationToken, admission?.AgentRunId)
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
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private sealed record DownloadReceipt(
        string? error,
        string? fileName,
        string? contentType,
        int? byteSize,
        string? artifactId);

    private static string SerializeBrowserData(BrowserResult result)
    {
        var data = result.DataJson is null ? new System.Text.Json.Nodes.JsonObject { ["status"] = result.Status ?? "ok" }
            : System.Text.Json.Nodes.JsonNode.Parse(result.DataJson)!.AsObject();
        data["effectAttempted"] = result.EffectAttempted;
        data["effectConfirmedBySdk"] = result.EffectConfirmedBySdk;
        data["applicationOutcomeVerified"] = result.ApplicationOutcomeVerified;
        return data.ToJsonString();
    }

    private string FromBrowserProvider(BrowserResult result)
    {
        if (string.IsNullOrEmpty(result.ErrorCode))
        {
            if (result.Observation is null)
            {
                return Error("provider_unavailable", "Browser is unavailable.");
            }

            if (result.Observation.Intervention != BrowserInterventionKind.None)
            {
                return SerializeBrowserIntervention(result.Observation, result);
            }

            return SerializeBrowserSnapshot(result.Observation, result);
        }

        var code = BrowserErrorCodes.Contains(result.ErrorCode) ? result.ErrorCode : "provider_unavailable";
        if (code == "credential_target_invalid")
            return JsonSerializer.Serialize(new { error = code, failureScope = "target", capabilitySupported = true,
                message = "Protected credential fill is supported, but the selected element is not a password input.",
                nextStep = "Observe the sign-in form and target its existing-account password input by label or placeholder, then use browser.fill_credential. Never enter passwords using ordinary text tools.",
                effectAttempted = result.EffectAttempted, effectConfirmedBySdk = result.EffectConfirmedBySdk,
                applicationOutcomeVerified = result.ApplicationOutcomeVerified });
        if (code == "unsupported_operation" && result.AllowedActions is { Count: > 0 })
        {
            var allowed = result.AllowedActions
                .Where(action => BrowserToolLimits.TargetActions.Contains(action, StringComparer.Ordinal))
                .Distinct(StringComparer.Ordinal)
                .Take(BrowserToolLimits.TargetActions.Length)
                .ToArray();
            if (allowed.Length > 0)
            {
                return JsonSerializer.Serialize(new
                {
                    error = "unsupported_operation",
                    message = "This element does not support that operation.",
                    allowedActions = allowed, effectAttempted = result.EffectAttempted,
                    effectConfirmedBySdk = result.EffectConfirmedBySdk, applicationOutcomeVerified = result.ApplicationOutcomeVerified
                });
            }
        }

        return JsonSerializer.Serialize(new { error = code, message = BrowserFailureMessage(code),
            effectAttempted = result.EffectAttempted, effectConfirmedBySdk = result.EffectConfirmedBySdk,
            applicationOutcomeVerified = result.ApplicationOutcomeVerified });
    }

    private string SerializeBrowserSnapshot(BrowserSnapshot observation, BrowserResult result) => JsonSerializer.Serialize(new
    {
        status = "ok", effectAttempted = result.EffectAttempted, effectConfirmedBySdk = result.EffectConfirmedBySdk, applicationOutcomeVerified = result.ApplicationOutcomeVerified, untrustedBrowserContent = true, snapshotId = observation.SnapshotId,
        tabRef = observation.TabRef, url = ClipBrowser(observation.Url, BrowserToolLimits.MaxUrlLength), title = ClipBrowser(observation.Title, BrowserToolLimits.MaxTitleLength),
        content = ToolJsonResults.ClipUtf8Prefix(observation.Content, browser!.HostPolicy.Limits.SnapshotBytes),
        targets = observation.Targets.Select(e => new { target = e.Target, role = e.Role, name = ClipBrowser(e.Name, BrowserToolLimits.MaxAccessibleNameLength), actions = e.Actions, state = ControlState(e.State) }),
        truncated = observation.ContentTruncated || Encoding.UTF8.GetByteCount(observation.Content) > browser!.HostPolicy.Limits.SnapshotBytes,
        hasMore = observation.ContentTruncated || Encoding.UTF8.GetByteCount(observation.Content) > browser!.HostPolicy.Limits.SnapshotBytes,
        observationUnavailable = observation.ObservationUnavailable,
        settled = observation.Settled, scope = observation.Scope, frameRef = observation.FrameRef, frames = observation.Frames, boxes = observation.Boxes,
        guidance = BrowserToolArguments.TargetGuidance
    }, DownloadJson);

    private static string SerializeBrowserIntervention(BrowserSnapshot observation, BrowserResult result)
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
            effectAttempted = result.EffectAttempted, effectConfirmedBySdk = result.EffectConfirmedBySdk,
            applicationOutcomeVerified = result.ApplicationOutcomeVerified,
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
            "invalid_target" => BrowserToolArguments.TargetGuidance,
            "invalid_frame" => "Use one current snapshot fr_ frame ID. If target.frameRef is also supplied it must match frameRef; omit frameRef for the main page.",
            "stale_visual_evidence" => "Coordinate evidence is missing or stale. Use a semantic target or obtain a fresh viewport screenshot and its snapshotId for the active tab; reobserve and verify the outcome.",
            "stale_frame" => "This frame is no longer current. Observe the current permitted frame inventory.",
            "action_not_confirmed" => "The page changed during the operation. Observe current state; do not replay an uncertain effect.",
            "target_missing" => "No current rendered target matches. Observe the page, narrow the semantics or render virtualized content before acting. Change evidence after a small number of justified alternatives; avoid equivalent guesses. This does not indicate a native dialog.",
            "ambiguous_target" => "The target is ambiguous. Narrow role/name or use within with a unique row/group and literal hasText.",
            "not_found" => "No rendered target matches. Change the query or scroll the region to reveal virtualized content.",
            "non_actionable_target" => "The target has no actions. Search for its actionable descendant with browser.find.",
            "target_unreachable" => "The host refused the connection. Do not retry that host.",
            "dialog_pending" => "A native dialog is pending. Use authorized browser.dialog inspect to see its redacted type, then accept or dismiss only when justified by the requested action. Do not repeat the triggering click. Verify the application state afterwards; close does not prove sign-out.",
            "dialog_missing" => "No native dialog is pending. Observe the page before continuing; do not repeat an uncertain action.",
            "close_failed" => "Native browser closure failed. The owned context was retained; closure and sign-out are not confirmed. Inspect or explicitly retry cleanup within the remaining budget.",
            "close_uncertain" => "Native browser closure was not confirmed within the bound. Do not claim closure or sign-out. Observe the owned context before deciding whether further cleanup is safe.",
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

    public async ValueTask<OccurrenceBrowserScope> OpenOccurrenceBrowserAsync(Guid sessionId, Guid agentInstanceId, CancellationToken ct)
    {
        if (sessionId != Guid.Empty && agentInstanceId != Guid.Empty && browser is IBrowserProfileBinding binding)
            binding.BindSession(sessionId, agentInstanceId);
        IAsyncDisposable? lease = null;
        if (sessionId != Guid.Empty && agentInstanceId != Guid.Empty && browser is { IsAvailable: true }
            && _configurationGate.IsConfigured(ToolCatalog.BrowserNavigate) && browser is IBrowserContextUse use)
            lease = await use.EnterUnattendedAsync(agentInstanceId, null, ct);
        return new OccurrenceBrowserScope(browser, sessionId, lease);
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
    private readonly Guid _sessionId;
    private readonly IAsyncDisposable? _lease;

    public OccurrenceBrowserScope(IBrowser? browser, Guid sessionId, IAsyncDisposable? lease)
    {
        _browser = browser;
        _sessionId = sessionId;
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

        if (_sessionId != Guid.Empty && _browser is IBrowserLease sessionLease)
        {
            await sessionLease.ReleaseAsync(_sessionId).ConfigureAwait(false);
        }
    }
}
