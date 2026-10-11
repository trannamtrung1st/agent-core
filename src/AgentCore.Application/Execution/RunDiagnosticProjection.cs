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
    public const string FailureMarker = "Core recorded provider failure: ";
    public static ModelMessage Failure(int attempt, ProviderFailure failure) => new(ModelRole.System,
        FailureMarker + JsonSerializer.Serialize(new RunAttemptDiagnostic(attempt, failure.Code.ToString(),
            failure.FailureReason is { } reason && DiagnosticDetailAllowlist.FailureReasons.Contains(reason) ? reason : null,
            failure.ResponseChannel is { } channel && DiagnosticDetailAllowlist.ResponseChannels.Contains(channel) ? channel : null)));

    public static RunExecutionDiagnostic? From(AgentRun run)
    {
        if (run.Checkpoint is not { } checkpoint || !AgentRunToolCallCheckpoint.TryRead(checkpoint, out var messages)) return null;
        var attempts = new List<RunAttemptDiagnostic>();
        foreach (var message in messages!.Where(m => m.Role == ModelRole.System && m.Text.StartsWith(FailureMarker, StringComparison.Ordinal)))
        {
            try
            {
                if (JsonSerializer.Deserialize<RunAttemptDiagnostic>(message.Text[FailureMarker.Length..]) is { } failure)
                    attempts.Add(failure);
            }
            catch (JsonException) { }
        }
        var tools = messages!.Where(m => m.Role == ModelRole.Tool).ToArray();
        return new(checkpoint.RemainingOverallBudgetMs, tools.LastOrDefault()?.Name, tools.Length,
            attempts.TakeLast(AgentRunLimits.DefaultMaxAttempts).ToArray());
    }
}
