using System.Diagnostics;
using AgentCore.Application.Observability;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using Microsoft.Extensions.Logging;

namespace AgentCore.Infrastructure.Email;

// Per-operation observations only; no grant, mailbox content or Session state is retained.
internal sealed class EmailOperationObservation(string operation, bool synthetic, TimeProvider time, Guid accountId, CancellationToken cancellation = default) : IDisposable
{
    private readonly long started = time.GetTimestamp();
    private readonly Activity? activity = StartActivity(operation, synthetic, accountId);
    private string outcome = "failed";

    public void Complete(string result = "success") => outcome = result;
    public void Correlate(Guid id) => activity?.SetTag("credential.id", id);
    public void Fail(Exception exception) => outcome = exception switch
    {
        OperationCanceledException => "cancelled",
        AgentCoreException { Code: "email_oauth_reconnect_required" or "email_oauth_scope_missing" } => "reconnect_required",
        AgentCoreException { Code: "email_oauth_temporary_failure" or "email_provider_unavailable" } => "temporary_failure",
        _ => "failed"
    };

    public void Dispose()
    {
        if (outcome == "failed" && cancellation.IsCancellationRequested) outcome = "cancelled";
        activity?.SetTag("email.outcome", outcome);
        activity?.SetStatus(outcome is "success" or "cached" or "throttled" or "denied" or "cancelled" ? ActivityStatusCode.Ok : ActivityStatusCode.Error);
        RuntimeTelemetry.RecordEmailOperation("gmail", operation, outcome, synthetic, time.GetElapsedTime(started).TotalMilliseconds);
        activity?.Dispose();
    }

    private static Activity? StartActivity(string operation, bool synthetic, Guid accountId)
    {
        var activity = RuntimeTelemetry.Activity.StartActivity("email." + operation);
        activity?.SetTag("email.provider", "gmail");
        activity?.SetTag("email.profile", synthetic ? "synthetic" : "real");
        if (accountId != Guid.Empty) activity?.SetTag("credential.id", accountId);
        return activity;
    }

    internal static AgentCoreException Failure(Exception exception, ILogger? logger, IDiagnosticIdSource? ids,
        string operation, Guid accountId, Guid? agentId = null)
    {
        var safe = exception as AgentCoreException ?? new AgentCoreException("email_provider_unavailable", "Google email is temporarily unavailable. Check access in Connections → Credentials.", 502);
        if (safe.DiagnosticId is not null) return safe; // Preserve the originating reference across nested refresh/read/check boundaries.
        var diagnostic = HandledFailureLog.Warning(logger, exception, "email." + operation, safe.Code,
            new DiagnosticContext(ProviderAlias: "gmail", CredentialId: accountId, AgentInstanceId: agentId), diagnostics: ids);
        return diagnostic is null ? safe : new(safe.Code, safe.Message, safe.StatusCode, safe.Fatal)
        { DiagnosticId = diagnostic, RetryAfterMs = safe.RetryAfterMs, ValidationField = safe.ValidationField, ValidationCode = safe.ValidationCode };
    }
}
