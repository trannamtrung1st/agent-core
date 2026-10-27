using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Evaluations;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Evaluations;

public static class EvaluationSandboxRules
{
    public static void Reservation(EvaluationSandboxExecution value, EvaluationAttempt attempt, AgentRun source, ScriptVersion script, DateTimeOffset now)
    {
        var request = value.Approval; var command = request.Operation.SandboxCommand;
        if (value.ExecutionId == Guid.Empty || value.Revision != 1 || value.Phase != EvaluationSandboxPhase.AwaitingReview
            || value.DispatchClaim is not null || value.ProviderId is not null || value.CleanupPending || value.Outcome is not null
            || value.TenantId != attempt.TenantId || value.AttemptId != attempt.AttemptId || value.ActorId != attempt.ActorId
            || attempt.Status != EvaluationStatus.Running || attempt.SubjectKind != EvaluationSubjectKind.AgentRun || !source.IsTerminal
            || source.AgentRunId != attempt.SubjectId || source.Revision != attempt.SubjectRevision || source.AgentRunId != value.SubjectRunId
            || source.Revision != value.SubjectRevision || source.SessionId != value.SubjectSessionId || source.AgentInstanceId != value.InstanceId
            || script.ScriptId != value.Script.ScriptId || script.Version != value.Script.Version || script.ContentHash != value.Script.ContentHash
            || script.SandboxInput != value.InputKind || script.Kind != ScriptKind.Sandbox || script.SandboxProfileId != value.Profile.Id || !script.Hooks.Contains(attempt.Hook)
            || value.InputJson != EvaluationSandboxAuthority.Input(source, value.InputKind) || value.InputHash != EvaluationRules.Hash(value.InputJson)
            || value.HostScope.Length is < 1 or > 128 || value.CreatedAt > now || value.ExpiresAt <= now || value.ExpiresAt > now.AddHours(1)
            || request.Kind != ApprovalKind.Review || request.Status != TenantApprovalStatus.Pending || request.Revision != 1
            || request.TenantId != value.TenantId || request.ExecutorPrincipalId != value.ExecutorId || value.ExecutorId != source.Owner.ExecutorPrincipalId || request.RunId != source.AgentRunId
            || request.SessionId != source.SessionId || request.RootActivationId != source.Admission.Activation.RootActivationId || request.ExpiresAt != value.ExpiresAt
            || command?.Evaluation != new SandboxEvaluationReference(value.AttemptId, value.ExecutionId, value.CheckId, value.Script, value.InputHash)
            || command.ExpectedGeneration is not null || command.ExpectedLeaseRevision != 0 || command.WorkspaceAccess is not null
            || command.HomeAccess != AgentCore.Domain.Definitions.HomeAccess.ReadOnly || request.Operation.SpaceContext is not null
            || TenantApprovalRules.Rebuild(request.Operation) != request.Operation)
            throw new ArgumentException("Invalid independent evaluation Sandbox reservation.");
        var body = TenantApprovalRules.SandboxCommand(command);
        if (body.Command != script.Body || body.TimeoutSeconds != script.SandboxTimeoutSeconds || body.Background || body.Cwd != "/working")
            throw new ArgumentException("Review must bind the exact saved Script and execution limits.");
    }
    public static void Change(EvaluationSandboxExecution next, EvaluationSandboxExecution before, DateTimeOffset now)
    {
        if (next.Revision != before.Revision + 1 || next with { Revision = before.Revision, Phase = before.Phase, CancellationRequestedAt = before.CancellationRequestedAt, FindingPublished = before.FindingPublished,
            DispatchClaim = before.DispatchClaim, DispatchExpiresAt = before.DispatchExpiresAt, ProviderId = before.ProviderId,
            CleanupPending = before.CleanupPending, CleanupPasses = before.CleanupPasses, RetryCleanupAt = before.RetryCleanupAt,
            Outcome = before.Outcome, ReasonCode = before.ReasonCode, ExitCode = before.ExitCode, DurationMs = before.DurationMs,
            OutputHash = before.OutputHash, TerminationConfirmed = before.TerminationConfirmed, InputJson = before.InputJson } != before
            || before.ProviderId is not null && next.ProviderId != before.ProviderId
            || next.InputJson != before.InputJson && (next.Phase != EvaluationSandboxPhase.Settled || next.InputJson is not null)
            || before.CancellationRequestedAt is not null && next.CancellationRequestedAt != before.CancellationRequestedAt
            || before.FindingPublished && !next.FindingPublished
            || next.Phase > before.Phase + 1 && next.Phase != EvaluationSandboxPhase.Settled
            || next.Phase < before.Phase || before.Phase == EvaluationSandboxPhase.Settled && (next.Outcome != before.Outcome || next.ExitCode != before.ExitCode || next.ReasonCode != before.ReasonCode)
            || before.DispatchClaim is not null && (next.DispatchClaim != before.DispatchClaim || next.DispatchExpiresAt != before.DispatchExpiresAt)
            || before.Phase == EvaluationSandboxPhase.AwaitingReview && next.Phase == EvaluationSandboxPhase.Reserved
                && (next.DispatchClaim is null || next.DispatchExpiresAt <= now || next.DispatchExpiresAt > now.AddMinutes(2))
            || next.Phase == EvaluationSandboxPhase.Dispatching && (next.DispatchClaim is null || !next.CleanupPending)
            || next.Phase == EvaluationSandboxPhase.Settled && (next.InputJson is not null || next.Outcome is null)
            || next.Outcome == EvaluationOutcome.Passed && (next.ExitCode != 0 || !next.TerminationConfirmed || next.CleanupPending)
            || next.Outcome == EvaluationOutcome.Failed && (next.ExitCode is null or 0 || !next.TerminationConfirmed || next.CleanupPending))
            throw new ArgumentException("Evaluation execution identity, no-replay fence or immutable result changed.");
    }
    public static AuthorizationAudit? TechnicalAudit(EvaluationSandboxExecution before, EvaluationSandboxExecution next, Guid id, DateTimeOffset now) =>
        before.Phase == next.Phase && before.CleanupPending == next.CleanupPending && before.CancellationRequestedAt == next.CancellationRequestedAt ? null
        : new(id, next.TenantId, next.ActorId, "evaluations.run", next.ExecutionId.ToString("D"), "recorded", next.ReasonCode ?? (next.CancellationRequestedAt is not null ? "sandbox_evaluation_cancellation_requested" : "sandbox_evaluation_" + next.Phase.ToString().ToLowerInvariant()), now);
    public static EvaluationFinding Finding(EvaluationFinding before, EvaluationSandboxExecution execution) => before with {
        Outcome = execution.Outcome ?? EvaluationOutcome.Inconclusive, ReasonCode = execution.ReasonCode ?? "sandbox_evaluation_inconclusive",
        Summary = $"Independent Sandbox execution {execution.ExecutionId:D}: exit {(execution.ExitCode?.ToString() ?? "unknown")}; duration {(execution.DurationMs?.ToString() ?? "unknown")} ms. Termination {(execution.TerminationConfirmed ? "confirmed" : "unconfirmed")}. Cleanup at settlement {(execution.CleanupPending ? "pending" : "confirmed")}. This evidence covers only the saved assertion."
    };

}
