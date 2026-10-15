using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Diagnostics;

namespace AgentCore.Application.Execution;

public sealed record RunAttemptDiagnostic(int Attempt, string Code, string? Reason, string? Channel);
public sealed record RunExecutionDiagnostic(int RemainingBudgetMs, string? LastTool, int RecordedToolResults,
    IReadOnlyList<RunAttemptDiagnostic> FailedAttempts);

public static class RunDiagnosticProjection
{
    public static RunAttemptDiagnostic Failure(int attempt, ProviderFailure failure) => new(attempt, failure.Code.ToString(),
        failure.FailureReason is { } reason && DiagnosticDetailAllowlist.FailureReasons.Contains(reason) ? reason : null,
        failure.ResponseChannel is { } channel && DiagnosticDetailAllowlist.ResponseChannels.Contains(channel) ? channel : null);

    public static RunExecutionDiagnostic? From(AgentRun run)
    {
        if (run.Checkpoint is not { } checkpoint || !AgentRunToolCallCheckpoint.TryRead(checkpoint, out var messages)) return null;
        var attempts = AgentRunToolCallCheckpoint.ReadExecutionState(checkpoint).FailedAttempts ?? [];
        var tools = messages!.Where(m => m.Role == ModelRole.Tool).ToArray();
        return new(checkpoint.RemainingOverallBudgetMs, tools.LastOrDefault()?.Name, tools.Length,
            attempts.TakeLast(AgentRunLimits.DefaultMaxAttempts).ToArray());
    }
}
