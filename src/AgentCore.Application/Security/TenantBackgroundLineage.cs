using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Security;

// Follows only persisted exact approval handoffs within one original background task.
public static class TenantBackgroundLineage
{
    public static async ValueTask<AgentRun> InitialAsync(AgentRun leaf, Func<Guid, CancellationToken, ValueTask<AgentRun?>> read, CancellationToken ct)
    {
        var current = leaf; var seen = new HashSet<Guid>();
        for (var depth = 0; depth < 64; depth++)
        {
            if (!seen.Add(current.AgentRunId) || current.Admission.OutputContract != AgentRunOutputContract.BackgroundOutcome)
                throw new AccessDeniedException("tenant_lineage_unavailable");
            if (current.Admission.ExecutionContext?.Origin == ExecutionOrigin.UserMessage && TenantExperienceReviewRules.Selected(current) is not null) return current;
            if (current.Admission.ExecutionContext is { Origin: ExecutionOrigin.ScheduledOccurrence or ExecutionOrigin.ManualInvocation or ExecutionOrigin.CoreEvent or ExecutionOrigin.ApplicationEvent } independent)
            {
                if (!AgentCore.Application.Tools.TenantTaskExecutionContract.IsIndependent(current) || independent.RootActivationId != current.ActivationId
                    || independent.ParentRunId is not null || independent.ParentActivationId is not null || current.Admission.Activation.TriggerOccurrenceId is null)
                    throw new AccessDeniedException("tenant_lineage_unavailable");
                return current;
            }
            if (current.Admission.ExecutionContext?.ParentRunId is not { } parent || current.Admission.ExecutionContext.Origin != ExecutionOrigin.ApprovalContinuation)
                throw new AccessDeniedException("tenant_lineage_unavailable");
            var previous = await read(parent, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("tenant_lineage_unavailable");
            RequireHandoff(previous, current); current = previous;
        }
        throw new AccessDeniedException("tenant_lineage_unavailable");
    }
    public static void RequireHandoff(AgentRun source, AgentRun next)
    {
        if (source.Status != AgentRunStatus.Completed || source.Owner != next.Owner || source.SessionId != next.SessionId || source.Admission.OutputContract != AgentRunOutputContract.BackgroundOutcome
            || next.Admission.OutputContract != source.Admission.OutputContract || source.Result?.OutcomeKind != AgentRunOutcomeKind.Continued
            || source.Result.ContinuationRunId != next.AgentRunId || source.ApprovalRequestId is not { } request)
            throw new AccessDeniedException("tenant_lineage_unavailable");
        var resume = next.Admission.ApprovalResume; var context = next.Admission.ExecutionContext;
        if (resume is null || context is null || resume.SourceRunId != source.AgentRunId || resume.RequestId != request
            || context.Origin != ExecutionOrigin.ApprovalContinuation || context.ParentRunId != source.AgentRunId || context.ParentActivationId != source.ActivationId
            || context.RootActivationId != source.Admission.ExecutionContext?.RootActivationId
            || context.OriginalInitiatorPrincipalId != source.Admission.ExecutionContext?.OriginalInitiatorPrincipalId
            || next.Admission.Activation.Kind != ActivationKind.ApprovalContinuation || next.Admission.Activation.ParentActivationId != source.ActivationId
            || next.Admission.Activation.SourceAgentRunId != source.AgentRunId || next.Admission.Activation.SourceSessionId != source.SessionId
            || next.Admission.Activation.RootActivationId != source.Admission.Activation.RootActivationId
            || next.Admission.TaskIntent != source.Admission.TaskIntent || next.ResponseId != source.ResponseId
            || !resume.SourceInputEntryIds.SequenceEqual(TenantApprovalContinuationRules.Inputs(source)))
            throw new AccessDeniedException("tenant_lineage_unavailable");
    }
}
