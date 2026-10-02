using System.Diagnostics;
using System.Text.Json;
using AgentCore.Application.Observability;
using AgentCore.Application.Ports;

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
        "user_intervention_required"
    };

    private async Task<string> NavigateBrowserAsync(
        Guid sessionId,
        JsonElement args,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
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
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        if (!BrowserToolArguments.TryObserve(args, out var errorJson))
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
            var result = await browser.ObserveAsync(sessionId, cancellationToken).ConfigureAwait(false);
            return FinishBrowser(ToolCatalog.BrowserObserve, started, FromBrowserProvider(result));
        }
        catch (OperationCanceledException)
        {
            RecordBrowser(ToolCatalog.BrowserObserve, started, "canceled");
            throw;
        }
    }

    private async Task<string> ActBrowserAsync(
        Guid sessionId,
        JsonElement args,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
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
            if (!decision.Allowed)
            {
                return FinishBrowser(
                    ToolCatalog.BrowserAct,
                    started,
                    Error(decision.Code ?? "forbidden", decision.Message ?? "Browser actions are not permitted."));
            }

            var result = await browser
                .ActAsync(new BrowserActRequest(sessionId, operation, reference, value), cancellationToken)
                .ConfigureAwait(false);
            return FinishBrowser(ToolCatalog.BrowserAct, started, FromBrowserProvider(result));
        }
        catch (OperationCanceledException)
        {
            RecordBrowser(ToolCatalog.BrowserAct, started, "canceled");
            throw;
        }
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
        return Error(code, BrowserFailureMessage(code));
    }

    private static string SerializeBrowserObservation(BrowserObservation observation)
    {
        var elements = (observation.Elements ?? [])
            .Take(BrowserToolLimits.MaxElements)
            .Select(element => new
            {
                @ref = ClipBrowser(element.Ref, BrowserToolLimits.MaxRefLength),
                role = ClipBrowser(element.Role, BrowserToolLimits.MaxRoleLength),
                name = ClipBrowser(element.Name, BrowserToolLimits.MaxAccessibleNameLength)
            })
            .ToArray();
        var text = observation.VisibleText ?? string.Empty;
        var truncated = observation.TextTruncated || text.Length > BrowserToolLimits.MaxVisibleTextLength;
        return JsonSerializer.Serialize(new
        {
            untrustedBrowserContent = true,
            url = ClipBrowser(observation.Url, BrowserToolLimits.MaxUrlLength),
            title = ClipBrowser(observation.Title, BrowserToolLimits.MaxTitleLength),
            visibleText = ClipBrowser(text, BrowserToolLimits.MaxVisibleTextLength),
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
}
