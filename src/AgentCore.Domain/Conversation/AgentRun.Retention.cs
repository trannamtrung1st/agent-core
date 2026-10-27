namespace AgentCore.Domain.Conversation;

public sealed partial class AgentRun
{
    public const string RetiredConversationCheckpoint = "{\"retiredConversation\":true}";
    public const string RetiredConversationText = "Conversation content removed.";

    public bool RequiresEffectReconciliation => SideEffect.Disposition is AgentRunSideEffectDisposition.Prepared
        or AgentRunSideEffectDisposition.InFlight or AgentRunSideEffectDisposition.Indeterminate;

    // Called only by the owning deletion/cleanup transaction. Immutable admission,
    // approval references, action hashes, outcomes and budget counters remain factual.
    public AgentRun RetireConversationContent(DateTimeOffset at, bool preserveResult = false)
    {
        if (!IsTerminal || RequiresEffectReconciliation) return this;
        var known = KnownEffectSummary is null ? null : AgentRunKnownEffects.IsHistoricalCompletedEffect(KnownEffectSummary)
            ? KnownEffectSummary : RetiredConversationText;
        if ((Checkpoint is null || Checkpoint.PayloadJson == RetiredConversationCheckpoint)
            && Progress is null && (preserveResult || Result is null || Result.Text == RetiredConversationText)
            && (Failure is null || Failure.Summary == RetiredConversationText) && known == KnownEffectSummary
            && SideEffect.ExternalEffect?.DirectOperation is null && (Admission.GoalWork is null || Admission.GoalWork.TaskJson.Length == 0)) return this;
        var checkpoint = Checkpoint is null ? null : new AgentRunCheckpoint(RetiredConversationCheckpoint,
            Checkpoint.StepCount, Checkpoint.OutputBytes, Checkpoint.RemainingOverallBudgetMs, Checkpoint.ActiveExecutionMs, Checkpoint.BudgetRecordedAtUtc);
        var result = preserveResult || Result is null ? Result : new AgentRunResult(RetiredConversationText,
            Result.CompletedAtUtc, Result.AttentionRequired, Result.OutcomeKind, Result.OutcomeEntryId, Result.ContinuationRunId);
        var failure = Failure is null ? null : new AgentRunFailure(Failure.Code, RetiredConversationText, Failure.FailedAtUtc, Failure.DiagnosticId);
        var effect = SideEffect.ExternalEffect is { DirectOperation: not null } origin
            ? new AgentRunSideEffect(SideEffect.Disposition, SideEffect.ToolCallId, SideEffect.ActionHash, SideEffect.UpdatedAtUtc, origin with { DirectOperation = null }) : SideEffect;
        var admission = Admission.GoalWork is null ? Admission : new AgentRunAdmission(Admission.Activation, Admission.DefinitionId, Admission.DefinitionVersion, Admission.PinnedPersona,
            Admission.ResponseId, Admission.OutputContract, Admission.ExecutionBudget, Admission.Configuration, Admission.ComposerInput, Admission.TaskIntent, Admission.ExecutionContext, Admission.ApprovalResume,
            Admission.GoalWork with { TaskJson = "" });
        return new(AgentRunId, Owner, admission, PinnedModel, Status, checked(Revision + 1), AttemptCount, MaxAttempts,
            NextRetryAtUtc, Claim, CancellationRequested, CancellationRequestedAtUtc, known, null, checkpoint, result, failure, effect,
            CreatedAtUtc, at < UpdatedAtUtc ? UpdatedAtUtc : at, PinnedSkillCatalog, ActiveSkillKeys, SkillLoadCount,
            LoadedCapabilityIds, CapabilityLoadCount, Wait, WaitCount, TotalWaitSeconds, ApprovalRequestId);
    }
}
