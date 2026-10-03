using System.Diagnostics;
using System.Text.Json;
using AgentCore.Application.Observability;
using AgentCore.Application.Ports;
using AgentCore.Domain.Connections;
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
        "target_unreachable"
    };

    private async Task<string?> DenyBrowserUnlessConnectedAsync(
        Guid sessionId,
        ToolExecutionAdmission? admission,
        CancellationToken cancellationToken)
    {
        var scheduled = admission is { Detached: true, TriggerKind: TriggerKind.ScheduledOccurrence };
        if (applicationConnections is null
            || admission?.AgentInstanceId is not Guid agentInstanceId
            || agentInstanceId == Guid.Empty)
        {
            return scheduled
                ? Error("forbidden", "This application connection cannot be used.")
                : null;
        }

        var connection = await applicationConnections
            .GetByAgentAsync(agentInstanceId, cancellationToken)
            .ConfigureAwait(false);
        if (scheduled && connection is not { Status: ApplicationConnectionStatus.Connected })
        {
            if (browser is IBrowserProfileBinding unbound)
            {
                unbound.BindSession(sessionId, null);
            }

            return Error("forbidden", "This application connection cannot be used.");
        }

        if (connection is null || connection.Status == ApplicationConnectionStatus.Connected)
        {
            if (browser is IBrowserProfileBinding binding)
            {
                binding.BindSession(sessionId, agentInstanceId);
            }

            return null;
        }

        if (browser is IBrowserProfileBinding unbind)
        {
            unbind.BindSession(sessionId, null);
        }

        return Error("forbidden", "This application connection cannot be used.");
    }

    private async Task<string> NavigateBrowserAsync(
        Guid sessionId,
        JsonElement args,
        ToolExecutionAdmission? admission,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var denied = await DenyBrowserUnlessConnectedAsync(sessionId, admission, cancellationToken).ConfigureAwait(false);
        if (denied is not null)
        {
            return FinishBrowser(ToolCatalog.BrowserNavigate, started, denied);
        }

        if (!BrowserToolArguments.TryNavigate(args, out var url, out var errorJson))
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

        if (admission is { Detached: true, TriggerKind: TriggerKind.ScheduledOccurrence, TrustedConnection: true }
            && applicationConnections is not null
            && admission.AgentInstanceId is Guid connectedAgent)
        {
            var connected = await applicationConnections.GetByAgentAsync(connectedAgent, cancellationToken)
                .ConfigureAwait(false);
            var leased = BrowserTargetPolicy.EvaluateDestination(
                url,
                connected?.TrustedOrigins,
                BrowserPolicyMode.Restricted);
            if (!leased.Allowed)
            {
                return FinishBrowser(
                    ToolCatalog.BrowserNavigate,
                    started,
                    Error(leased.Code ?? "target_denied", leased.Message ?? "Browser target is not allowed."));
            }
        }

        if (!browser.IsAvailable)
        {
            return FinishBrowser(
                ToolCatalog.BrowserNavigate,
                started,
                Error("provider_unavailable", "Browser is unavailable."));
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var destination))
        {
            return FinishBrowser(
                ToolCatalog.BrowserNavigate,
                started,
                Error("invalid", "url must be an absolute http or https URL."));
        }

        try
        {
            var result = await browser
                .NavigateAsync(new BrowserNavigateRequest(sessionId, destination), cancellationToken)
                .ConfigureAwait(false);
            return FinishBrowser(ToolCatalog.BrowserNavigate, started, FromBrowserProvider(result));
        }
        catch (OperationCanceledException)
        {
            RecordBrowser(ToolCatalog.BrowserNavigate, started, "canceled");
            throw;
        }
    }

    private async Task<string> ObserveBrowserAsync(
        Guid sessionId,
        JsonElement args,
        ToolExecutionAdmission? admission,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var denied = await DenyBrowserUnlessConnectedAsync(sessionId, admission, cancellationToken).ConfigureAwait(false);
        if (denied is not null)
        {
            return FinishBrowser(ToolCatalog.BrowserObserve, started, denied);
        }

        if (!BrowserToolArguments.TryObserve(args, out var observeOptions, out var errorJson))
        {
            return FinishBrowser(ToolCatalog.BrowserObserve, started, errorJson);
        }

        if (browser is not { IsAvailable: true })
        {
            return FinishBrowser(
                ToolCatalog.BrowserObserve,
                started,
                Error("provider_unavailable", "Browser is unavailable."));
        }

        try
        {
            var result = observeOptions is null
                ? await browser.ObserveAsync(sessionId, cancellationToken).ConfigureAwait(false)
                : await browser.ObserveAsync(sessionId, observeOptions, cancellationToken).ConfigureAwait(false);
            return FinishBrowser(ToolCatalog.BrowserObserve, started, FromBrowserProvider(result));
        }
        catch (OperationCanceledException)
        {
            RecordBrowser(ToolCatalog.BrowserObserve, started, "canceled");
            throw;
        }
    }

    private const int MaxBrowserUploadBytes = 8 * 1024 * 1024;

    private async Task<string> ActBrowserAsync(
        AgentDefinition definition,
        Guid sessionId,
        JsonElement args,
        ToolExecutionAdmission? admission,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var denied = await DenyBrowserUnlessConnectedAsync(sessionId, admission, cancellationToken).ConfigureAwait(false);
        if (denied is not null)
        {
            return FinishBrowser(ToolCatalog.BrowserAct, started, denied);
        }

        if (!BrowserToolArguments.TryAct(args, out var operation, out var reference, out var value, out var errorJson))
        {
            return FinishBrowser(ToolCatalog.BrowserAct, started, errorJson);
        }

        if (browser is null)
        {
            return FinishBrowser(
                ToolCatalog.BrowserAct,
                started,
                Error("provider_unavailable", "Browser is unavailable."));
        }

        if (browser.HostPolicy.InteractionMode != BrowserInteractionMode.InteractiveDemo)
        {
            return FinishBrowser(
                ToolCatalog.BrowserAct,
                started,
                Error("forbidden", "Browser actions are not allowed in this interaction mode."));
        }

        if (!browser.IsAvailable)
        {
            return FinishBrowser(
                ToolCatalog.BrowserAct,
                started,
                Error("provider_unavailable", "Browser is unavailable."));
        }

        try
        {
            var current = await browser.GetCurrentUrlAsync(sessionId, cancellationToken).ConfigureAwait(false);
            if (current is null)
            {
                return FinishBrowser(
                    ToolCatalog.BrowserAct,
                    started,
                    Error("provider_unavailable", "Browser is unavailable."));
            }

            var decision = BrowserTargetPolicy.EvaluateAct(
                browser.HostPolicy.InteractionMode,
                current.AbsoluteUri,
                browser.HostPolicy.EffectiveInteractionOrigins,
                browser.HostPolicy.PolicyMode);
            if (decision.Allowed
                && admission is { Detached: true, TriggerKind: TriggerKind.ScheduledOccurrence, TrustedConnection: true }
                && applicationConnections is not null
                && admission.AgentInstanceId is Guid connectedAgent)
            {
                var connected = await applicationConnections.GetByAgentAsync(connectedAgent, cancellationToken)
                    .ConfigureAwait(false);
                decision = BrowserTargetPolicy.EvaluateAct(
                    browser.HostPolicy.InteractionMode,
                    current.AbsoluteUri,
                    connected?.TrustedOrigins,
                    BrowserPolicyMode.Restricted);
            }

            if (!decision.Allowed)
            {
                return FinishBrowser(
                    ToolCatalog.BrowserAct,
                    started,
                    Error(decision.Code ?? "forbidden", decision.Message ?? "Browser actions are not permitted."));
            }

            BrowserUpload? upload = null;
            if (operation == "upload")
            {
                var resolved = await ResolveBrowserUploadAsync(definition, sessionId, value, cancellationToken)
                    .ConfigureAwait(false);
                if (resolved.ErrorJson is not null)
                {
                    return FinishBrowser(ToolCatalog.BrowserAct, started, resolved.ErrorJson);
                }

                upload = resolved.Upload;
                value = null;
            }

            var result = await browser
                .ActAsync(new BrowserActRequest(sessionId, operation, reference, value, upload), cancellationToken)
                .ConfigureAwait(false);
            return FinishBrowser(ToolCatalog.BrowserAct, started, FromBrowserProvider(result));
        }
        catch (OperationCanceledException)
        {
            RecordBrowser(ToolCatalog.BrowserAct, started, "canceled");
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

            return SerializeBrowserObservation(result.Observation);
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

    private static string SerializeBrowserObservation(BrowserObservation observation)
    {
        var elements = (observation.Elements ?? [])
            .Take(BrowserToolLimits.MaxElements)
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
        var text = observation.VisibleText ?? string.Empty;
        var truncated = observation.TextTruncated || text.Length > BrowserToolLimits.MaxVisibleTextLength;
        var url = ClipBrowser(observation.Url, BrowserToolLimits.MaxUrlLength);
        var title = ClipBrowser(observation.Title, BrowserToolLimits.MaxTitleLength);
        var visibleText = ClipBrowser(text, BrowserToolLimits.MaxVisibleTextLength);
        return observation.Settled is bool settled
            ? JsonSerializer.Serialize(new
            {
                untrustedBrowserContent = true,
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
                url,
                title,
                visibleText,
                textTruncated = truncated,
                elements
            });
    }

    private static string SerializeBrowserIntervention(BrowserObservation observation)
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

    private static string FinishBrowser(string toolName, long started, string json)
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

    private static void RecordBrowser(string toolName, long started, string outcome) =>
        RuntimeTelemetry.RecordDiagnostic(
            "browser",
            RuntimeTelemetry.ElapsedMs(started),
            $"{toolName}:{outcome}");

    public async ValueTask<OccurrenceBrowserScope> OpenOccurrenceBrowserAsync(
        Guid workItemId,
        Guid agentInstanceId,
        bool trustedConnection,
        CancellationToken cancellationToken)
    {
        if (workItemId != Guid.Empty
            && agentInstanceId != Guid.Empty
            && browser is IBrowserProfileBinding binding)
        {
            binding.BindSession(workItemId, agentInstanceId);
        }

        IAsyncDisposable? lease = null;
        if (trustedConnection
            && workItemId != Guid.Empty
            && agentInstanceId != Guid.Empty
            && browser is IBrowserContextUse use
            && applicationConnections is not null)
        {
            var connection = await applicationConnections.GetByAgentAsync(agentInstanceId, cancellationToken)
                .ConfigureAwait(false);
            if (connection is { Status: ApplicationConnectionStatus.Connected })
            {
                lease = await use.EnterUnattendedAsync(agentInstanceId, connection.TrustedOrigins, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

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
    private readonly IBrowserSession? _browser;
    private readonly Guid _workItemId;
    private readonly IAsyncDisposable? _lease;

    public OccurrenceBrowserScope(IBrowserSession? browser, Guid workItemId, IAsyncDisposable? lease)
    {
        _browser = browser;
        _workItemId = workItemId;
        _lease = lease;
        BoundApplicationBrowser = lease is not null;
    }

    public bool BoundApplicationBrowser { get; }

    public async ValueTask DisposeAsync()
    {
        if (_lease is not null)
        {
            await _lease.DisposeAsync().ConfigureAwait(false);
        }

        if (_workItemId != Guid.Empty && _browser is IBrowserSessionLease sessionLease)
        {
            await sessionLease.ReleaseAsync(_workItemId).ConfigureAwait(false);
        }
    }
}
