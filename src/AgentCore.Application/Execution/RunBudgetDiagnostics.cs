using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Execution;

public sealed record RunBudgetDiagnostics(string Class, string Source, int MaxSteps, int DurationSeconds,
    int PerToolSeconds, int StepsConsumed, int ActiveExecutionMs, string Phase, string? TerminationReason,
    string CleanupStatus, bool ClosureConfirmed, bool? ClosureRequested, bool? LogoutRequested,
    bool LogoutVerified, bool CleanupBlocked);

public static class RunBudgetDiagnosticProjection
{
    public static RunBudgetDiagnostics? From(AgentRun run)
    {
        if (run.Admission.ExecutionBudget is not { } pin) return null; // Do not invent historical admission provenance.
        AgentRunToolCallCheckpoint.TryRead(run.Checkpoint, out var messages);
        messages ??= [];
        var finalization = RunFinalization.Restore(messages);
        var cleanup = messages.Any(m => m.Role == ModelRole.System && m.Text == RunFinalization.CleanupMarker);
        var closure = false;
        var unresolved = new HashSet<string>(StringComparer.Ordinal);
        var inCleanup = false;
        foreach (var receipt in messages)
        {
            if (receipt.Role == ModelRole.System && receipt.Text == RunFinalization.CleanupMarker) inCleanup = true;
            if (receipt.Role != ModelRole.Tool || receipt.Name?.StartsWith("browser.", StringComparison.Ordinal) != true) continue;
            try
            {
                using var doc = JsonDocument.Parse(receipt.Text); var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;
                var status = ReadString(root, "status");
                var hasError = root.TryGetProperty("error", out _);
                var closed = receipt.Name == ToolCatalog.BrowserClose && !hasError && status is "closed" or "already_closed";
                if (receipt.Name == ToolCatalog.BrowserClose) closure = closed;
                if (!inCleanup) continue;
                if (hasError || status is "close_failed" or "close_uncertain" or "provider_unavailable")
                    unresolved.Add(receipt.Name);
                else if (closed || status == "ok")
                {
                    unresolved.Remove(receipt.Name);
                    // A later SDK-confirmed interaction can establish recovery through an
                    // alternate target/path. Read-only observation alone cannot do so.
                    if (root.TryGetProperty("effectConfirmedBySdk", out var confirmed)
                        && confirmed.ValueKind == JsonValueKind.True && BrowserToolCatalog.IsInteraction(receipt.Name))
                        unresolved.RemoveWhere(name => name != ToolCatalog.BrowserClose);
                }
                // Generic verification proves its requested predicate, not application sign-out.
                // Model wording and successful clicks never create trusted logout evidence.
            }
            catch (JsonException) { }
        }
        var intent = pin.CleanupIntent;
        // A close-only obligation is fulfilled by its authoritative close receipt.
        var blocked = unresolved.Count > 0 && !(closure && intent is { LogoutRequested: false, ClosureRequested: true });
        var cleanupStatus = !pin.RequestedCleanup ? "notRequested" : blocked ? "blocked"
            : intent is { LogoutRequested: false, ClosureRequested: true } && closure ? "completed"
            : intent is { LogoutRequested: true, ClosureRequested: true } && closure ? "partial"
            : "unverified";
        var remaining = run.Checkpoint?.RemainingOverallBudgetMs ?? pin.Profile.DurationSeconds * 1000;
        return new(pin.Class.ToString(), pin.Source, pin.Profile.MaxSteps, pin.Profile.DurationSeconds, pin.Profile.PerToolSeconds,
            run.Checkpoint?.StepCount ?? 0, run.Checkpoint?.ActiveExecutionMs ?? Math.Max(0, pin.Profile.DurationSeconds * 1000 - remaining),
            finalization is not null ? "finalization" : cleanup ? "cleanup" : "work",
            finalization ?? (blocked && pin.RequestedCleanup ? "cleanupBlocked" : null) ?? run.Failure?.Code switch { "tool-step-limit" => "stepLimit", "run-deadline" => "runDeadline", "tool-output-limit" => "outputLimit", _ => null },
            cleanupStatus, closure, intent?.ClosureRequested, intent?.LogoutRequested, false, blocked);
    }
    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
