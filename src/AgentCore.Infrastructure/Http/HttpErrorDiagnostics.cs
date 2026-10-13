using System.Diagnostics;
using System.Text.Json;
using AgentCore.Application.Ports;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace AgentCore.Infrastructure.Http;

public sealed class HttpErrorDiagnosticsOptions
{
    public bool Enabled { get; set; } = true;
}

/// <summary>Provider bodies stay in Infrastructure; only bounded, recognized metadata enters logs.</summary>
internal sealed class HttpErrorDiagnostics(HttpErrorDiagnosticsOptions options, ILogger<HttpErrorDiagnostics> logger,
    IDiagnosticIdSource diagnosticIds)
{
    internal const int MaxBodyBytes = 16 * 1024;
    internal static readonly HttpRequestOptionsKey<bool> ParsingEnabled = new("AgentCore.HttpErrorParsingEnabled");
    internal static readonly HttpRequestOptionsKey<HttpErrorResponseSnapshot> Inspection = new("AgentCore.HttpErrorInspection");
    public bool Enabled => options.Enabled && logger.IsEnabled(LogLevel.Warning);

    // Free-form codes, messages, details and descriptions may echo secrets or user content.
    private static readonly HashSet<string> SafeCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "invalid_request_error", "invalid_request", "invalid_grant", "invalid_client", "unauthorized_client",
        "access_denied", "unsupported_grant_type", "invalid_scope", "invalid_token", "insufficient_scope",
        "authentication_error", "invalid_api_key", "permission_denied", "permission_error", "permission_denied_error",
        "rate_limit_exceeded", "rate_limit_error", "rateLimitExceeded", "userRateLimitExceeded", "dailyLimitExceeded",
        "quota_exceeded", "insufficient_quota", "resource_exhausted", "billing_hard_limit_reached",
        "model_not_found", "not_found", "not_found_error", "context_length_exceeded", "content_filter",
        "server_error", "internal_error", "internal", "backendError", "service_unavailable", "unavailable",
        "overloaded_error", "badRequest", "forbidden", "authError", "UNAUTHENTICATED", "INVALID_ARGUMENT"
    };

    internal static (string Code, string Type, string Reason, string State) Parse(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length == 0) return ("", "", "unknown", "empty");
        if (bytes.Length > MaxBodyBytes) return ("", "", "unknown", "too_large");
        try
        {
            using var doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return ("", "", "unknown", "unsupported");
            var error = root.TryGetProperty("error", out var nested) && nested.ValueKind == JsonValueKind.Object ? nested : root;
            string Scalar(JsonElement node, string key) => node.TryGetProperty(key, out var v) ? v.ValueKind switch
            {
                JsonValueKind.String => v.GetString() ?? "",
                JsonValueKind.Number => v.GetRawText(),
                _ => ""
            } : "";
            string Safe(string value) => SafeCodes.Contains(value) ? value.ToLowerInvariant()
                : int.TryParse(value, out var number) && number is >= 100 and <= 599 ? number.ToString(System.Globalization.CultureInfo.InvariantCulture) : "";
            var code = Safe(Scalar(error, "code"));
            if (code.Length == 0) code = Safe(Scalar(root, "error")); // OAuth string envelope.
            var type = Safe(Scalar(error, "type"));
            if (type.Length == 0) type = Safe(Scalar(error, "status")); // Google RPC envelope.
            var reason = "unknown";
            if (error.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
                foreach (var item in errors.EnumerateArray().Take(4))
                    if (item.ValueKind == JsonValueKind.Object && Safe(Scalar(item, "reason")) is { Length: > 0 } safe) { reason = safe; break; }
            // Classify selected phrases; never log the actual provider text.
            var message = Scalar(error, "message") + " " + Scalar(root, "error_description");
            foreach (var (phrase, category) in new (string, string)[]
            {
                ("insufficient credit", "insufficient_credit"), ("insufficient balance", "insufficient_credit"),
                ("quota", "quota_exceeded"), ("rate limit", "rate_limited"), ("too many requests", "rate_limited"),
                ("context length", "context_length_exceeded"), ("invalid message sequence", "invalid_message_sequence"),
                ("expired", "credential_expired"), ("revoked", "credential_revoked"),
                ("overloaded", "provider_overloaded"), ("content policy", "content_policy")
            })
                if (reason == "unknown" && message.Contains(phrase, StringComparison.OrdinalIgnoreCase)) { reason = category; break; }
            return (code, type, reason, "parsed");
        }
        catch (JsonException) { return ("", "", "unknown", "invalid_json"); }
    }

    public void Log(int status, string integration, ReadOnlyMemory<byte> bytes, string? readState = null)
    {
        if (!Enabled || status < 400) return;
        var parsed = readState is null ? Parse(bytes) : (Code: "", Type: "", Reason: "unknown", State: readState);
        logger.LogWarning("Third-party HTTP error. DiagnosticId {DiagnosticId} TraceId {TraceId} Operation {Operation} Integration {Integration} HttpStatus {HttpStatus} ProviderCode {ProviderCode} ProviderType {ProviderType} FailureReason {FailureReason} ParseState {ParseState}",
            diagnosticIds.NewId(), Activity.Current?.TraceId.ToString(), "http.upstream.error", integration, status, parsed.Code, parsed.Type, parsed.Reason, parsed.State);
    }
}

internal sealed record HttpErrorResponseSnapshot(ReadOnlyMemory<byte> Body, string? ReadState);

internal static class HttpErrorDiagnosticsRegistration
{
    public static void AddHttpErrorDiagnostics(this IServiceCollection services)
    {
        services.AddLogging();
        services.TryAddSingleton(p => p.GetService<IConfiguration>()?.GetSection("Observability:HttpErrorResponses").Get<HttpErrorDiagnosticsOptions>() ?? new());
        services.TryAddSingleton<HttpErrorDiagnostics>();
    }

    public static IHttpClientBuilder WithErrorDiagnostics(this IHttpClientBuilder builder) =>
        builder.AddHttpMessageHandler(p => new HttpErrorDiagnosticsHandler(p.GetRequiredService<HttpErrorDiagnostics>(), builder.Name));
}
