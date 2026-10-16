using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Diagnostics;

namespace AgentCore.Application.Execution;

public sealed record RunAttemptDiagnostic(int Attempt, string Code, string? Reason, string? Channel,
    int? MaxOutputTokens = null, string? Transport = null);
public sealed record RunProtocolRecovery(string Reason, string? Channel, string Eligibility,
    string? Strategy = null, string? Outcome = null);
public sealed record RunExecutionDiagnostic(int RemainingBudgetMs, string? LastTool, int RecordedToolResults,
    IReadOnlyList<RunAttemptDiagnostic> FailedAttempts, RunProtocolRecovery? ProtocolRecovery = null);

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
        var recovery = AgentRunToolCallCheckpoint.ReadExecutionState(checkpoint).ProtocolRecovery;
        // Older checkpoints also recorded general provider failures here. Keep their
        // attempt diagnostics, but reserve the recovery panel for semantic protocol context.
        if (recovery is { Strategy: null }
            && recovery.Channel is not (ProviderResponseChannel.StructuredOutput or ProviderResponseChannel.ResponseFunction or ProviderResponseChannel.MarkerCompatibility)
            && !(recovery.Channel is null && recovery.Reason is ProviderFailureReason.MissingDisplayText or ProviderFailureReason.InvalidBlocks))
            recovery = null;
        if (run.IsTerminal && recovery is { Strategy: not null, Outcome: "started" or "retryPending" })
            recovery = recovery with { Outcome = run.Status == AgentRunStatus.Cancelled ? "cancelled" : "failed" };
        return new(checkpoint.RemainingOverallBudgetMs, tools.LastOrDefault()?.Name, tools.Length,
            attempts.TakeLast(AgentRunLimits.DefaultMaxAttempts).ToArray(),
            recovery);
    }
}
