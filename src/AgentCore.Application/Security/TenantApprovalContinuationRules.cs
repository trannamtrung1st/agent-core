using System.Text.Json;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public static class TenantApprovalContinuationRules
{
    public static IReadOnlyList<Guid> Inputs(AgentRun run) => run.Admission.ApprovalResume?.SourceInputEntryIds ?? run.Admission.Activation.SourceEntryIds;

    public static bool NeedsNewRun(AgentRun source) => AgentRunToolCallCheckpoint.TryRead(source.Checkpoint, out var messages)
        && AgentRunToolCallCheckpoint.PendingCalls(messages!).Any(c => c.Name == ToolCatalog.RequestAuthorization);

    public static AgentRun Create(AgentRun source, TenantApprovalRequest request, ApprovalProcessedSignal signal, TenantApprovalContinuationIds ids, DateTimeOffset at)
    {
        TenantRunContinuationRules.Require(source, request);
        TenantRunContinuationService.ValidateAuthorizationCheckpoint(source, request);
        if (!NeedsNewRun(source) || source.Status != AgentRunStatus.WaitingForApproval || ids.ActivationId == Guid.Empty || ids.RunId == Guid.Empty
            || ids.RunId == source.AgentRunId || ids.ActivationId == source.ActivationId || signal.RunId != source.AgentRunId
            || signal.RequestId != request.RequestId || signal.TenantId != request.TenantId || signal.DecisionRevision != request.Revision)
            throw new SecurityConflictException("approval_continuation_mismatch");
        var previous = source.Admission.ExecutionContext!;
        var activation = new Activation(ids.ActivationId, source.SessionId, ActivationKind.ApprovalContinuation, [], signal.SignalId, null,
            source.SessionId, source.AgentRunId, "approval-continuation:" + request.RequestId.ToString("D"), at,
            previous.RootActivationId, source.ActivationId, CompletionEvidence(source));
        var context = new TrustedExecutionContext(previous.TenantId, previous.AgentPrincipalId, previous.OriginalInitiatorPrincipalId, source.SessionId,
            ids.RunId, ids.ActivationId, previous.RootActivationId, source.AgentRunId, source.ActivationId, ExecutionOrigin.ApprovalContinuation);
        var old = source.Admission;
        var admission = new AgentRunAdmission(activation, old.DefinitionId, old.DefinitionVersion, old.PinnedPersona, old.ResponseId, old.OutputContract,
            old.ExecutionBudget, old.Configuration, old.ComposerInput, old.TaskIntent, context,
            new(source.AgentRunId, request.RequestId, request.Operation.OperationId, request.Operation.ActionHash, Inputs(source)), old.GoalWork);
        return source.ForkApprovalContinuation(ids.RunId, admission, request.RequestId, at);
    }

    private static string CompletionEvidence(AgentRun source) => TenantConfigurationContinuationRules.Target(source) is not null || TenantContextContinuationRules.InputBoundary(source) is not null || source.Admission.Activation.Kind == ActivationKind.GoalContinuation
        ? source.Admission.Activation.EvidenceJson! : source.Admission.OutputContract == AgentRunOutputContract.CompletionReport
        ? source.Admission.Activation.EvidenceJson ?? throw new AccessDeniedException("completion_input_unavailable")
        : JsonSerializer.Serialize(new AgentRunAdmissionFactory.SignalInput(source.Admission.OutputContract == AgentRunOutputContract.BackgroundOutcome || TenantTaskExecutionContract.IsIndependent(source) ? TriggerKind.ManualInvocation : TriggerKind.UserTurn,
            "Continue the exact saved authorization operation. Use its recorded outcome; do not repeat prior effects.", "approval",
            source.Admission.Activation.EvidenceJson is { } evidence && source.Admission.TaskIntent?.Source == AgentRunTaskSource.Automation
                ? JsonSerializer.Deserialize<AgentRunAdmissionFactory.SignalInput>(evidence)?.InputBoundarySequence : null));

    public static void Require(TenantApprovalRequest request, AgentRun run, AgentRun? source)
    {
        if (request.RunId == run.AgentRunId) return;
        var resume = run.Admission.ApprovalResume; var context = run.Admission.ExecutionContext; var activation = run.Admission.Activation;
        if (source is null || resume is null || context is null || source.AgentRunId != request.RunId || source.Owner != run.Owner
            || source.Status != AgentRunStatus.Completed || source.Result?.OutcomeKind != AgentRunOutcomeKind.Continued
            || source.Result.ContinuationRunId != run.AgentRunId || source.ApprovalRequestId != request.RequestId
            || resume.SourceRunId != source.AgentRunId || resume.RequestId != request.RequestId || resume.OperationId != request.Operation.OperationId || resume.ActionHash != request.Operation.ActionHash
            || activation.Kind != ActivationKind.ApprovalContinuation || activation.SourceAgentRunId != source.AgentRunId || activation.SourceSessionId != source.SessionId
            || activation.ParentActivationId != source.ActivationId || activation.RootActivationId != source.Admission.Activation.RootActivationId
            || context.Origin != ExecutionOrigin.ApprovalContinuation || context.ParentRunId != source.AgentRunId || context.ParentActivationId != source.ActivationId
            || context.OriginalInitiatorPrincipalId != source.Admission.ExecutionContext?.OriginalInitiatorPrincipalId
            || TenantConfigurationContinuationRules.Target(run) != TenantConfigurationContinuationRules.Target(source)
            || TenantContextContinuationRules.InputBoundary(run) != TenantContextContinuationRules.InputBoundary(source)
            || TenantConfigurationContinuationRules.InputBoundary(run) != TenantConfigurationContinuationRules.InputBoundary(source)
            || run.SessionId != source.SessionId || run.ResponseId != source.ResponseId || !resume.SourceInputEntryIds.SequenceEqual(Inputs(source)))
            throw new SecurityConflictException("approval_continuation_mismatch");
    }
}
