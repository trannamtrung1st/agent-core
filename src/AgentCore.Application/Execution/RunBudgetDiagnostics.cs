using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Execution;

public sealed record RunBudgetDiagnostics(string Class, string Source, int MaxSteps, int DurationSeconds,
    int PerToolSeconds, int StepsConsumed, int ActiveExecutionMs, string Phase, string? TerminationReason,
    string CleanupStatus, bool ClosureConfirmed);

public static class RunBudgetDiagnosticProjection
{
    public static RunBudgetDiagnostics? From(AgentRun run)
    {
        if (run.Admission.ExecutionBudget is not { } pin) return null; // Do not invent historical admission provenance.
        AgentRunToolCallCheckpoint.TryRead(run.Checkpoint, out var messages);
        messages ??= [];
        var finalization = RunFinalization.Restore(messages);
        var cleanup = messages.Any(m => m.Role == ModelRole.System && m.Text == RunFinalization.CleanupMarker);
        var closure = false; var blocked = false; var verified = false;
        var inCleanup = false;
        foreach (var receipt in messages)
        {
            if (receipt.Role == ModelRole.System && receipt.Text == RunFinalization.CleanupMarker) inCleanup = true;
            if (receipt.Role != ModelRole.Tool || receipt.Name?.StartsWith("browser.", StringComparison.Ordinal) != true) continue;
            try
            {
                using var doc = JsonDocument.Parse(receipt.Text); var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;
                closure |= receipt.Name == ToolCatalog.BrowserClose && root.TryGetProperty("status", out var status)
                    && status.GetString() is "closed" or "already_closed";
                verified |= inCleanup && receipt.Name == "browser.verify" && root.TryGetProperty("applicationOutcomeVerified", out var verification)
                    && verification.ValueKind == JsonValueKind.True;
                blocked |= inCleanup && root.TryGetProperty("error", out var error) && error.GetString() is "forbidden" or "target_denied" or "close_failed" or "close_uncertain";
            }
            catch (JsonException) { }
        }
        var remaining = run.Checkpoint?.RemainingOverallBudgetMs ?? pin.Profile.DurationSeconds * 1000;
        return new(pin.Class.ToString(), pin.Source, pin.Profile.MaxSteps, pin.Profile.DurationSeconds, pin.Profile.PerToolSeconds,
            run.Checkpoint?.StepCount ?? 0, run.Checkpoint?.ActiveExecutionMs ?? Math.Max(0, pin.Profile.DurationSeconds * 1000 - remaining),
            finalization is not null ? "finalization" : cleanup ? "cleanup" : "work",
            finalization ?? (blocked && pin.RequestedCleanup ? "cleanupBlocked" : null) ?? run.Failure?.Code switch { "tool-step-limit" => "stepLimit", "run-deadline" => "runDeadline", "tool-output-limit" => "outputLimit", _ => null },
            !pin.RequestedCleanup ? "notRequested" : blocked ? "blocked" : closure && verified ? "completed" : "unverified", closure);
    }
}
