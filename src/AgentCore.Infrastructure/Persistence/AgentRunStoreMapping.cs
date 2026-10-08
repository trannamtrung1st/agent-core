using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentCore.Application.Sessions;
using AgentCore.Application.Observability;
using AgentCore.Application.Execution;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Triggers;

namespace AgentCore.Infrastructure.Persistence;

internal static class AgentRunStoreMapping
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static void ObserveAdmission(SessionSnapshot session, AgentRun run, bool created)
    {
        RuntimeTelemetry.RecordActivation(created ? "admitted" : "duplicate");
        if (created) RuntimeTelemetry.RecordAgentRun("created");
        if (session.Origin.InitialBackgroundAgentRunId == run.AgentRunId && created)
            RuntimeTelemetry.RecordBackgroundSession("admitted");
        if (run.Admission.Activation.Kind == ActivationKind.BackgroundCompleted)
            RuntimeTelemetry.RecordBackgroundSession(created ? "completion-emitted" : "completion-deduped");
    }
    internal static void ObserveTransition(AgentRun before, AgentRun after, AgentRunCommand command)
    {
        if (before.Revision == after.Revision) return;
        if (command is AgentRunCommand.SuspendWait) RuntimeTelemetry.RecordAgentRun("waiting-signal");
        if (command is AgentRunCommand.ResumeWait)
        {
            RuntimeTelemetry.RecordAgentRun("wait-wake");
            if (before.Wait is { Mode: AgentRunWaitMode.Background } wait && AgentRunToolCallCheckpoint.TryRead(after.Checkpoint!, out var messages) && messages!.LastOrDefault(m => m.ToolCallId == wait.ToolCallId && m.Role == AgentCore.Application.Ports.ModelRole.Tool)?.Text.Contains("\"reason\":\"timeout\"", StringComparison.Ordinal) == true) RuntimeTelemetry.RecordAgentRun("wait-timeout");
        }
        if (before.Status == AgentRunStatus.WaitingForSignal && after.Status == AgentRunStatus.Cancelled) RuntimeTelemetry.RecordAgentRun("wait-cancelled");
        if (command is AgentRunCommand.Recover) RuntimeTelemetry.RecordAgentRun("recovered");
        if (command is AgentRunCommand.Claim)
        { RuntimeTelemetry.RecordAgentRun("claimed"); RuntimeTelemetry.RecordAgentRun("attempt"); }
        if (before.Status != after.Status)
        {
            var outcome = after.Status switch {
                AgentRunStatus.WaitingToRetry => "retry", AgentRunStatus.WaitingForApproval => "approval",
                AgentRunStatus.Completed => "completed", AgentRunStatus.Failed => "failed", AgentRunStatus.Cancelled => "cancelled", _ => null };
            if (outcome is not null) RuntimeTelemetry.RecordAgentRun(outcome);
        }
        if (before.SideEffect.Disposition != after.SideEffect.Disposition && after.SideEffect.Disposition == AgentRunSideEffectDisposition.Indeterminate)
            RuntimeTelemetry.RecordAgentRun("unconfirmed-effect");
    }

    public static void ValidateGenericAdmission(AgentRun run)
    {
        if (run.Admission.Activation.Kind is ActivationKind.ImmediateBackground or ActivationKind.BackgroundCompleted)
            throw AgentCoreErrors.Validation("This Activation requires its atomic, fenced admission method.");
    }

    public static void ValidateImmediateAdmission(AgentRun run, Guid expectedParentGeneration)
    {
        if (run.Admission.Activation.Kind != ActivationKind.ImmediateBackground || expectedParentGeneration == Guid.Empty)
            throw AgentCoreErrors.Validation("Immediate background admission requires its parent claim generation.");
    }

    // Indexed relational identity is authoritative; payload holds the immutable pins and bounded state.
    private sealed record RunPayload(AgentRunAdmission Admission, AgentRunModelPin PinnedModel,
        int AttemptCount, int MaxAttempts, AgentRunClaim? Claim, bool CancellationRequested,
        DateTimeOffset? CancellationRequestedAtUtc, string? KnownEffectSummary,
        AgentRunProgress? Progress, AgentRunCheckpoint? Checkpoint, AgentRunResult? Result,
        AgentRunFailure? Failure, AgentRunSideEffect SideEffect, AgentRunApproval? Approval,
        IReadOnlyList<EffectiveSkill> PinnedSkillCatalog, IReadOnlyList<string> ActiveSkillKeys,
        int SkillLoadCount, IReadOnlyList<string> LoadedCapabilityIds, int CapabilityLoadCount,
        AgentRunWait? Wait = null, int WaitCount = 0, double TotalWaitSeconds = 0);

    public static AgentRunRecord ToRecord(AgentRun run)
    {
        var row = new AgentRunRecord();
        Apply(row, run);
        return row;
    }

    public static void Apply(AgentRunRecord row, AgentRun run)
    {
        row.AgentRunId = run.AgentRunId.ToString("D");
        row.ActivationId = run.ActivationId.ToString("D");
        row.SessionId = run.SessionId.ToString("D");
        row.AgentInstanceId = run.AgentInstanceId.ToString("D");
        row.ProfileId = run.ProfileId.ToString("D");
        row.Status = (int)run.Status;
        row.Revision = run.Revision;
        row.NextRetryAtUtc = run.NextRetryAtUtc?.ToUnixTimeMilliseconds();
        row.LeaseExpiresAtUtc = run.Claim?.LeaseExpiresAtUtc.ToUnixTimeMilliseconds();
        row.ApprovalExpiresAtUtc = run.Approval?.ExpiresAtUtc.ToUnixTimeMilliseconds();
        row.CreatedAtUtc = run.CreatedAtUtc.ToUnixTimeMilliseconds();
        row.UpdatedAtUtc = run.UpdatedAtUtc.ToUnixTimeMilliseconds();
        row.PayloadJson = JsonSerializer.Serialize(new RunPayload(run.Admission, run.PinnedModel,
            run.AttemptCount, run.MaxAttempts, run.Claim, run.CancellationRequested,
            run.CancellationRequestedAtUtc, run.KnownEffectSummary, run.Progress, run.Checkpoint,
            run.Result, run.Failure, run.SideEffect, run.Approval, run.PinnedSkillCatalog,
            run.ActiveSkillKeys, run.SkillLoadCount, run.LoadedCapabilityIds, run.CapabilityLoadCount, run.Wait, run.WaitCount, run.TotalWaitSeconds), Json);
    }

    public static AgentRun ToDomain(AgentRunRecord row)
    {
        var p = JsonSerializer.Deserialize<RunPayload>(row.PayloadJson, Json)
            ?? throw AgentCoreErrors.Persistence("AgentRun state is missing.");
        if (p.Admission.Activation.ActivationId.ToString("D") != row.ActivationId
            || p.Admission.Activation.SessionId.ToString("D") != row.SessionId)
            throw AgentCoreErrors.Persistence("AgentRun identity does not match its admission.");
        return new AgentRun(Guid.Parse(row.AgentRunId),
            new AgentRunOwner(Guid.Parse(row.AgentInstanceId), Guid.Parse(row.ProfileId)),
            p.Admission, p.PinnedModel, (AgentRunStatus)row.Status, row.Revision, p.AttemptCount, p.MaxAttempts,
            row.NextRetryAtUtc is { } retry ? DateTimeOffset.FromUnixTimeMilliseconds(retry) : null,
            p.Claim, p.CancellationRequested, p.CancellationRequestedAtUtc, p.KnownEffectSummary,
            p.Progress, p.Checkpoint, p.Result, p.Failure, p.SideEffect, p.Approval,
            DateTimeOffset.FromUnixTimeMilliseconds(row.CreatedAtUtc), DateTimeOffset.FromUnixTimeMilliseconds(row.UpdatedAtUtc),
            p.PinnedSkillCatalog, p.ActiveSkillKeys, p.SkillLoadCount, p.LoadedCapabilityIds, p.CapabilityLoadCount, p.Wait, p.WaitCount, p.TotalWaitSeconds);
    }

    public static ActivationRecord ToActivationRecord(SessionSnapshot snapshot, AgentRun run) => new()
    {
        ActivationId = run.ActivationId.ToString("D"), SessionId = run.SessionId.ToString("D"),
        AgentInstanceId = run.AgentInstanceId.ToString("D"), ProfileId = run.ProfileId.ToString("D"),
        DedupeKey = run.Admission.Activation.DedupeKey, BackgroundSourceKey = BackgroundSourceKey(snapshot, run),
        AdmissionHash = AdmissionHash(snapshot, run),
        PayloadJson = JsonSerializer.Serialize(run.Admission.Activation, Json),
        AdmittedAtUtc = run.Admission.Activation.AdmittedAtUtc.ToUnixTimeMilliseconds()
    };

    public static string? BackgroundSourceKey(SessionSnapshot snapshot, AgentRun run)
    {
        if (run.Admission.Activation.Kind == ActivationKind.BackgroundCompleted)
            return $"completion:{run.Admission.Activation.SourceSessionId:D}:{run.Admission.Activation.SourceAgentRunId:D}";
        if (run.Admission.Activation.TriggerOccurrenceId is { } durableOccurrence && run.Admission.Activation.DedupeKey.StartsWith("automation:", StringComparison.Ordinal))
            return $"occurrence:{durableOccurrence:D}";
        if (snapshot.Origin.InitialBackgroundAgentRunId != run.AgentRunId) return null;
        if (run.Admission.Activation.TriggerOccurrenceId is { } occurrenceId) return $"occurrence:{occurrenceId:D}";
        return snapshot.Origin.Kind switch
        {
            SessionOriginKind.AutomationOccurrence => $"occurrence:{snapshot.Origin.TriggerOccurrenceId:D}",
            SessionOriginKind.ImmediateBackground => $"immediate:{snapshot.Origin.OriginatingSessionId:D}:{snapshot.Origin.OriginatingAgentRunId:D}:{run.Admission.Activation.DedupeKey}",
            SessionOriginKind.ManualBackground => $"manual:{run.Admission.Activation.DedupeKey}",
            _ => null
        };
    }

    public static string AdmissionHash(SessionSnapshot snapshot, AgentRun run)
    {
        var a = run.Admission.Activation;
        var entries = a.SourceEntryIds.Select(id => snapshot.Entries.Single(entry => entry.EntryId == id))
            .Select(entry => new { entry.Role, entry.Text, entry.SourceAdmissionFingerprint, entry.Attachments }).ToArray();
        var payload = JsonSerializer.Serialize(new
        {
            run.Owner, run.Admission.OutputContract, a.Kind, a.EvidenceJson, a.SourceEventId, a.TriggerOccurrenceId, a.SourceSessionId, a.SourceAgentRunId,
            SourceFingerprint = BackgroundSourceKey(snapshot, run) is null ? a.SourceFingerprint : null,
            run.DefinitionId, run.DefinitionVersion, run.PinnedPersona, run.PinnedModel, run.PinnedSkillCatalog,
            run.ActiveSkillKeys, run.MaxAttempts, OriginKind = snapshot.Origin.Kind, snapshot.Origin.AutomationId,
            snapshot.Origin.OriginatingSessionId, snapshot.Origin.OriginatingAgentRunId,
            snapshot.Origin.ReportCompletionToOrigin, Entries = entries
        }, Json);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }

    public static void ValidateOutcome(SessionSnapshot snapshot, AgentRun run, AgentRunCommand.Complete completion, ConversationEntry? draft)
    {
        if (snapshot.SessionId != run.SessionId || snapshot.AgentInstanceId != run.AgentInstanceId || snapshot.ProfileId != run.ProfileId)
            throw AgentCoreErrors.Conflict("AgentRun outcome belongs to another Session owner.");
        if (completion.OutcomeEntryId is { } entryId && snapshot.Entries.SingleOrDefault(entry => entry.EntryId == entryId) is not
            { Role: ConversationRole.Assistant, Status: EntryStatus.Completed } completed)
            throw AgentCoreErrors.Conflict("AgentRun response must commit its completed Session entry.");
        if (completion.OutcomeEntryId is { } responseEntry && snapshot.Entries.Single(entry => entry.EntryId == responseEntry).ResponseId != run.ResponseId)
            throw AgentCoreErrors.Conflict("AgentRun response identity differs from its outcome entry.");
        if (draft is not null && (completion.OutcomeKind != AgentRunOutcomeKind.NoAction
            || draft.Role != ConversationRole.Assistant || draft.Status != EntryStatus.Streaming
            || draft.ResponseId != run.ResponseId || !string.IsNullOrWhiteSpace(draft.Text)
            || snapshot.Entries.Any(entry => entry.EntryId == draft.EntryId)))
            throw AgentCoreErrors.Conflict("Quiet completion may remove only its empty response draft.");
    }

    public static void ValidateAdmission(SessionSnapshot snapshot, AgentRun run)
    {
        if (!run.IsInitialQueued || snapshot.SessionId != run.SessionId
            || snapshot.AgentInstanceId != run.AgentInstanceId || snapshot.ProfileId != run.ProfileId
            || snapshot.Definition.Id != run.DefinitionId || snapshot.Definition.Version != run.DefinitionVersion
            || snapshot.DurablyDeletedAt is not null || snapshot.ArchivedAt is not null
            || snapshot.Status == SessionStatus.Paused && !SessionPauseSemantics.IsTransportResumable(snapshot.PauseReason)
            || snapshot.Status is SessionStatus.Ended or SessionStatus.Ending
            || snapshot.LifecycleStatus is SessionLifecycleStatus.Completed or SessionLifecycleStatus.Cancelled or SessionLifecycleStatus.Expired or SessionLifecycleStatus.Ended)
            throw AgentCoreErrors.Validation("AgentRun admission requires an eligible owned Session and initial queued run.");
        foreach (var id in run.Admission.Activation.SourceEntryIds)
        {
            var source = snapshot.Entries.SingleOrDefault(entry => entry.EntryId == id);
            if (source is null || source.Status != EntryStatus.Completed
                || run.Admission.Activation.Kind == ActivationKind.UserTurn && source.Role != ConversationRole.User)
                throw AgentCoreErrors.Validation("Activation sources must be accepted Session entries.");
        }
        if (snapshot.Origin.InitialBackgroundAgentRunId == run.AgentRunId)
        {
            var a = run.Admission.Activation;
            var matches = snapshot.Origin.Kind switch
            {
                SessionOriginKind.ImmediateBackground => a.Kind == ActivationKind.ImmediateBackground
                    && a.SourceSessionId == snapshot.Origin.OriginatingSessionId
                    && a.SourceAgentRunId == snapshot.Origin.OriginatingAgentRunId,
                SessionOriginKind.AutomationOccurrence or SessionOriginKind.SourceOccurrence => a.Kind is ActivationKind.ScheduledWork or ActivationKind.ApplicationEvent or ActivationKind.ManualBackground
                    && a.TriggerOccurrenceId == snapshot.Origin.TriggerOccurrenceId,
                SessionOriginKind.ManualBackground => a.Kind == ActivationKind.ManualBackground,
                _ => false
            };
            if (!matches) throw AgentCoreErrors.Validation("Initial background activation does not match Session origin.");
        }
    }

    public static void ValidateCompletionSource(SessionSnapshot parent, AgentRun report, AgentRun? child, SessionSnapshot? childSession, bool primary = true)
    {
        if (report.Admission.Activation.Kind != ActivationKind.BackgroundCompleted || child is null || childSession is null
            || !child.IsTerminal || child.Result?.OutcomeKind == AgentRunOutcomeKind.NoAction
            || child.Owner != report.Owner || childSession.AgentInstanceId != report.AgentInstanceId || childSession.ProfileId != report.ProfileId
            || !childSession.Origin.MayReportCompletion(child.AgentRunId) || childSession.Origin.OriginatingSessionId != parent.SessionId
            || primary && (report.Admission.Activation.SourceSessionId != child.SessionId || report.Admission.Activation.SourceAgentRunId != child.AgentRunId)
            || childSession.DurablyDeletedAt is not null || parent.DurablyDeletedAt is not null || parent.ArchivedAt is not null
            || SessionLifecycle.IsTerminal(parent.LifecycleStatus) || parent.Status is SessionStatus.Ended or SessionStatus.Ending
            || parent.Status == SessionStatus.Paused && !SessionPauseSemantics.IsTransportResumable(parent.PauseReason))
            throw AgentCoreErrors.Conflict("Completion report source or parent is no longer eligible.");
    }

    public static void ValidateBackgroundBudget(AgentRun child, IEnumerable<AgentRun> runs)
    {
        var owned = runs.Where(run => run.Owner == child.Owner).ToArray();
        if (owned.Count(run => run.Admission.Activation.Kind == ActivationKind.ImmediateBackground
            && run.Admission.Activation.SourceAgentRunId == child.Admission.Activation.SourceAgentRunId) >= AgentRunLimits.MaxImmediateChildren
            || owned.Count(run => !run.IsTerminal && run.Admission.Activation.Kind is ActivationKind.ImmediateBackground
                or ActivationKind.ManualBackground or ActivationKind.ScheduledWork or ActivationKind.ApplicationEvent) >= AgentRunLimits.MaxActiveBackgroundRuns)
            throw AgentCoreErrors.Conflict("Background work capacity reached.");
    }

    public static void ValidateImmediateSource(AgentRun child, AgentRun? source, bool sourceSessionEligible)
    {
        if (child.Admission.Activation.Kind != ActivationKind.ImmediateBackground) return;
        if (!sourceSessionEligible || source is null || source.Owner != child.Owner
            || source.SessionId != child.Admission.Activation.SourceSessionId
            || source.Status != AgentRunStatus.Running || source.CancellationRequested
            || source.Claim!.LeaseExpiresAtUtc <= child.CreatedAtUtc
            || source.Admission.Activation.Kind != ActivationKind.UserTurn
            || source.DefinitionId != child.DefinitionId || source.DefinitionVersion != child.DefinitionVersion
            || source.PinnedPersona != child.PinnedPersona
            || source.PinnedModel.CatalogKey != child.PinnedModel.CatalogKey
            || source.PinnedModel.ProviderAlias != child.PinnedModel.ProviderAlias
            || source.PinnedModel.ModelId != child.PinnedModel.ModelId
            || source.PinnedModel.ReasoningEffort != child.PinnedModel.ReasoningEffort)
            throw AgentCoreErrors.Validation("Immediate background admission requires an active owned user-turn source.");
    }

    public static void ValidateOccurrenceAdmission(SessionSnapshot snapshot, AgentRun run,
        TriggerOccurrence occurrence, long expectedRoutingRevision)
    {
        var activation = run.Admission.Activation;
        var existing = occurrence.ExecutionTarget.Kind == AutomationExecutionTargetKind.ExistingSession;
        if ((existing ? snapshot.SessionId != occurrence.ExecutionTarget.SessionId || run.Admission.OutputContract != AgentRunOutputContract.ConversationResponse
                || activation.SourceEntryIds.Count != 0
            : snapshot.Origin.InitialBackgroundAgentRunId != run.AgentRunId || run.Admission.OutputContract != AgentRunOutputContract.BackgroundOutcome)
            || occurrence.OccurrenceId != activation.TriggerOccurrenceId
            || occurrence.Owner.AgentInstanceId != run.AgentInstanceId || occurrence.Owner.ProfileId != run.ProfileId
            || !existing && occurrence.AutomationId != snapshot.Origin.AutomationId
            || occurrence.SourceEventId != activation.SourceEventId
            || occurrence.SourceKind == TriggerSourceKind.Schedule && activation.Kind != ActivationKind.ScheduledWork
            || occurrence.SourceKind == TriggerSourceKind.ApplicationEvent && activation.Kind != ActivationKind.ApplicationEvent
            || occurrence.SourceKind == TriggerSourceKind.ManualInvocation && activation.Kind != ActivationKind.ManualBackground)
            throw AgentCoreErrors.Validation("Occurrence and child admission source/owner must match.");
        if (occurrence.Disposition == OccurrenceRoutingDisposition.AcceptedDurable)
        {
            if (occurrence.ExecutionSessionId is null || occurrence.AcceptedAgentRunId is null)
                throw AgentCoreErrors.Conflict("Occurrence already belongs to a different execution admission.");
            return;
        }
        if (occurrence.Disposition != OccurrenceRoutingDisposition.AwaitingDurableWork)
            throw AgentCoreErrors.Validation("Only an awaiting occurrence may admit durable execution.");
        if (occurrence.RoutingRevision != expectedRoutingRevision)
            throw AgentCoreErrors.Conflict("Occurrence routing revision is stale.");
        if (existing) return;
        var pin = occurrence.ModelPin;
        if (pin is null || pin.CatalogKey != run.PinnedModel.CatalogKey || pin.ProviderAlias != run.PinnedModel.ProviderAlias
            || pin.ModelId != run.PinnedModel.ModelId || pin.ReasoningEffort != run.PinnedModel.ReasoningEffort)
            throw AgentCoreErrors.Validation("Child model must match its admitted occurrence pin.");
    }

    public static void ValidateLiveOccurrenceAdmission(SessionSnapshot snapshot, AgentRun run, TriggerOccurrence occurrence)
    {
        var pin = occurrence.ModelPin;
        if (snapshot.Origin.InitialBackgroundAgentRunId == run.AgentRunId || occurrence.AutomationId is not null
            || occurrence.LiveSessionId != snapshot.SessionId || occurrence.OccurrenceId != run.Admission.Activation.TriggerOccurrenceId
            || run.Admission.Activation.SourceEventId != occurrence.OccurrenceId
            || run.Admission.Activation.DedupeKey != $"signal:{occurrence.OccurrenceId:D}"
            || occurrence.Owner.AgentInstanceId != run.AgentInstanceId || occurrence.Owner.ProfileId != run.ProfileId
            || pin is null || pin.CatalogKey != run.PinnedModel.CatalogKey || pin.ProviderAlias != run.PinnedModel.ProviderAlias
            || pin.ModelId != run.PinnedModel.ModelId || pin.ReasoningEffort != run.PinnedModel.ReasoningEffort
            || occurrence.SourceKind == TriggerSourceKind.Schedule && run.Admission.Activation.Kind != ActivationKind.ScheduledWork
            || occurrence.SourceKind == TriggerSourceKind.ApplicationEvent && run.Admission.Activation.Kind != ActivationKind.ApplicationEvent
            || occurrence.SourceKind == TriggerSourceKind.ManualInvocation)
            throw AgentCoreErrors.Validation("Live occurrence admission must match its native source, Session, owner and model pin.");
        if (occurrence.Disposition is not (OccurrenceRoutingDisposition.LivePrepared or OccurrenceRoutingDisposition.AcceptedLive)
            || occurrence.LiveEvaluationCompletedAtUtc is not null && occurrence.AcceptedAgentRunId is null)
            throw AgentCoreErrors.Conflict("Live occurrence evaluation is already settled or no longer admitted.");
    }

    public static Exception Map(Exception exception) => exception switch
    {
        AgentRunTransitionException e when e.Failure is AgentRunTransitionFailure.StaleRevision
            or AgentRunTransitionFailure.StaleGeneration or AgentRunTransitionFailure.NotClaimable
            => AgentCoreErrors.Conflict(e.Message),
        AgentRunTransitionException e => AgentCoreErrors.Validation(e.Message),
        ArgumentException => AgentCoreErrors.Validation("AgentRun transition is invalid."),
        _ => exception
    };
}
