using System.Text.Json;
using AgentCore.Application.Sessions;
using AgentCore.Application.Work;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Work;

namespace AgentCore.Infrastructure.Persistence;

internal static class WorkStoreMapping
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static WorkItemRecord ToRecord(WorkItem item)
    {
        var row = new WorkItemRecord();
        Apply(row, item);
        return row;
    }

    public static void Apply(WorkItemRecord row, WorkItem item)
    {
        row.WorkItemId = Id(item.WorkItemId);
        row.AgentInstanceId = Id(item.Owner.AgentInstanceId);
        row.ProfileId = Id(item.Owner.ProfileId);
        row.Status = (int)item.Status;
        row.Revision = item.Revision;
        row.AttemptCount = item.AttemptCount;
        row.MaxAttempts = item.MaxAttempts;
        row.NextRetryAtUtc = Unix(item.NextRetryAtUtc);
        row.ClaimGeneration = item.Claim is null ? null : Id(item.Claim.Generation);
        row.ClaimedAtUtc = item.Claim is null ? null : item.Claim.ClaimedAtUtc.ToUnixTimeMilliseconds();
        row.ClaimLeaseExpiresAtUtc = item.Claim is null ? null : item.Claim.LeaseExpiresAtUtc.ToUnixTimeMilliseconds();
        row.CancellationRequested = item.CancellationRequested;
        row.CancellationRequestedAtUtc = Unix(item.CancellationRequestedAtUtc);
        row.KnownEffectSummary = item.KnownEffectSummary;
        row.ProgressSummary = item.Progress?.Summary;
        row.ProgressUpdatedAtUtc = item.Progress is null ? null : item.Progress.UpdatedAtUtc.ToUnixTimeMilliseconds();
        row.CheckpointJson = item.Checkpoint?.PayloadJson;
        row.CheckpointStepCount = item.Checkpoint?.StepCount;
        row.CheckpointOutputBytes = item.Checkpoint?.OutputBytes;
        row.CheckpointRemainingOverallBudgetMs = item.Checkpoint?.RemainingOverallBudgetMs;
        row.ResultText = item.Result?.Text;
        row.ResultCompletedAtUtc = item.Result is null ? null : item.Result.CompletedAtUtc.ToUnixTimeMilliseconds();
        row.FailureCode = item.Failure?.Code;
        row.FailureSummary = item.Failure?.Summary;
        row.FailureAtUtc = item.Failure is null ? null : item.Failure.FailedAtUtc.ToUnixTimeMilliseconds();
        row.SideEffectDisposition = (int)item.SideEffect.Disposition;
        row.SideEffectToolCallId = item.SideEffect.ToolCallId;
        row.SideEffectActionHash = item.SideEffect.ActionHash;
        row.SideEffectUpdatedAtUtc = Unix(item.SideEffect.UpdatedAtUtc);
        row.CurrentApprovalId = item.Approval is null ? null : Id(item.Approval.ApprovalId);
        row.SourceOccurrenceId = Id(item.Provenance.SourceOccurrenceId);
        row.SourceKind = (int)item.Provenance.SourceKind;
        row.RegistrationId = OptionalId(item.Provenance.RegistrationId);
        row.SourceSessionId = OptionalId(item.Provenance.SourceSessionId);
        row.SourceEventId = OptionalId(item.Provenance.SourceEventId);
        row.DedupeKey = item.Provenance.DedupeKey;
        row.ScheduledAtUtc = Unix(item.Provenance.ScheduledAtUtc);
        row.ObservedAtUtc = item.Provenance.ObservedAtUtc.ToUnixTimeMilliseconds();
        row.EvidenceJson = item.Provenance.EvidenceJson;
        row.DefinitionId = item.Provenance.DefinitionId;
        row.DefinitionVersion = item.Provenance.DefinitionVersion;
        row.PersonaName = item.Provenance.PersonaName;
        row.PinnedPersonaJson = item.Provenance.PinnedPersona is null
            ? null
            : JsonSerializer.Serialize(item.Provenance.PinnedPersona, Json);
        row.ModelCatalogKey = item.Model.CatalogKey;
        row.ModelProviderAlias = item.Model.ProviderAlias;
        row.ModelId = item.Model.ModelId;
        row.ModelReasoningEffort = item.Model.ReasoningEffort;
        row.CreatedAtUtc = item.CreatedAtUtc.ToUnixTimeMilliseconds();
        row.UpdatedAtUtc = item.UpdatedAtUtc.ToUnixTimeMilliseconds();
    }

    public static WorkApprovalRecord ToApprovalRecord(WorkApproval approval)
    {
        var row = new WorkApprovalRecord();
        Apply(row, approval);
        return row;
    }

    public static void Apply(WorkApprovalRecord row, WorkApproval approval)
    {
        row.ApprovalId = Id(approval.ApprovalId);
        row.WorkItemId = Id(approval.WorkItemId);
        row.ExecutionGeneration = Id(approval.ExecutionGeneration);
        row.CheckpointRevision = approval.CheckpointRevision;
        row.ToolName = approval.ToolName;
        row.PreparedActionJson = approval.PreparedActionJson;
        row.ActionHash = approval.ActionHash;
        row.Preview = approval.Preview;
        row.ExpiresAtUtc = approval.ExpiresAtUtc.ToUnixTimeMilliseconds();
        row.Decision = (int)approval.Decision;
        row.DecidedAtUtc = Unix(approval.DecidedAtUtc);
        row.Consumed = approval.Consumed;
        row.Revision = approval.Revision;
        row.CreatedAtUtc = approval.CreatedAtUtc.ToUnixTimeMilliseconds();
    }

    public static WorkItem ToWorkItem(WorkItemRecord row, WorkApprovalRecord? approval)
    {
        var claim = row.ClaimGeneration is null
            ? null
            : new WorkClaim(
                Guid.Parse(row.ClaimGeneration),
                FromUnix(row.ClaimedAtUtc),
                FromUnix(row.ClaimLeaseExpiresAtUtc));
        var progress = row.ProgressSummary is null
            ? null
            : new WorkProgress(row.ProgressSummary, FromUnix(row.ProgressUpdatedAtUtc));
        var checkpoint = row.CheckpointJson is null
            ? null
            : new WorkCheckpoint(
                row.CheckpointJson,
                row.CheckpointStepCount ?? 0,
                row.CheckpointOutputBytes ?? 0,
                row.CheckpointRemainingOverallBudgetMs ?? 0);
        var result = row.ResultText is null
            ? null
            : new WorkResult(row.ResultText, FromUnix(row.ResultCompletedAtUtc));
        var failure = row.FailureCode is null
            ? null
            : new WorkFailure(row.FailureCode, row.FailureSummary ?? "", FromUnix(row.FailureAtUtc));
        var sideEffectDisposition = (WorkSideEffectDisposition)row.SideEffectDisposition;
        WorkSideEffect sideEffect;
        if (sideEffectDisposition == WorkSideEffectDisposition.None)
        {
            sideEffect = WorkSideEffect.None;
        }
        else
        {
            var approvalModel = approval is null ? null : ToApproval(approval);
            var toolCallId = row.SideEffectToolCallId;
            if (string.IsNullOrWhiteSpace(toolCallId))
            {
                toolCallId = DurableToolCallCheckpoint.TryResolveLegacyToolCallId(
                    checkpoint,
                    sideEffectDisposition,
                    row.SideEffectActionHash,
                    approvalModel);
                if (string.IsNullOrWhiteSpace(toolCallId)
                    && sideEffectDisposition is WorkSideEffectDisposition.InFlight or WorkSideEffectDisposition.Succeeded)
                {
                    sideEffectDisposition = WorkSideEffectDisposition.Indeterminate;
                }
            }

            sideEffect = new WorkSideEffect(
                sideEffectDisposition,
                toolCallId,
                row.SideEffectActionHash,
                FromUnix(row.SideEffectUpdatedAtUtc));
        }
        return new WorkItem(
            Guid.Parse(row.WorkItemId),
            new WorkOwner(Guid.Parse(row.AgentInstanceId), Guid.Parse(row.ProfileId)),
            new WorkProvenance(
                Guid.Parse(row.SourceOccurrenceId),
                (WorkSourceKind)row.SourceKind,
                ParseOptional(row.RegistrationId),
                ParseOptional(row.SourceSessionId),
                ParseOptional(row.SourceEventId),
                row.DedupeKey,
                OptionalUnix(row.ScheduledAtUtc),
                FromUnix(row.ObservedAtUtc),
                row.EvidenceJson,
                row.DefinitionId,
                row.DefinitionVersion,
                row.PersonaName,
                DeserializePinnedPersona(row.PinnedPersonaJson)),
            new WorkModelPin(row.ModelCatalogKey, row.ModelProviderAlias, row.ModelId, row.ModelReasoningEffort),
            (WorkItemStatus)row.Status,
            row.Revision,
            row.AttemptCount,
            row.MaxAttempts,
            OptionalUnix(row.NextRetryAtUtc),
            claim,
            row.CancellationRequested,
            OptionalUnix(row.CancellationRequestedAtUtc),
            row.KnownEffectSummary,
            progress,
            checkpoint,
            result,
            failure,
            sideEffect,
            approval is null ? null : ToApproval(approval),
            FromUnix(row.CreatedAtUtc),
            FromUnix(row.UpdatedAtUtc));
    }

    public static WorkApproval ToApproval(WorkApprovalRecord row) =>
        new(
            Guid.Parse(row.ApprovalId),
            Guid.Parse(row.WorkItemId),
            Guid.Parse(row.ExecutionGeneration),
            row.CheckpointRevision,
            row.ToolName,
            row.PreparedActionJson,
            row.ActionHash,
            row.Preview,
            FromUnix(row.ExpiresAtUtc),
            (WorkApprovalDecision)row.Decision,
            OptionalUnix(row.DecidedAtUtc),
            row.Consumed,
            row.Revision,
            FromUnix(row.CreatedAtUtc));

    public static Exception Map(Exception exception) => exception switch
    {
        WorkItemTransitionException transition when transition.Failure is
            WorkTransitionFailure.StaleRevision
            or WorkTransitionFailure.StaleGeneration
            or WorkTransitionFailure.ApprovalMismatch
            or WorkTransitionFailure.Rejected =>
            AgentCoreErrors.Conflict(transition.Message),
        WorkItemTransitionException transition => AgentCoreErrors.Validation(transition.Message),
        ArgumentException argument => AgentCoreErrors.Validation(argument.Message),
        _ => exception
    };

    private static string Id(Guid value) => value.ToString("D");

    private static string? OptionalId(Guid? value) => value?.ToString("D");

    private static Guid? ParseOptional(string? value) => value is null ? null : Guid.Parse(value);

    private static long? Unix(DateTimeOffset? value) => value?.ToUnixTimeMilliseconds();

    private static DateTimeOffset FromUnix(long value) => DateTimeOffset.FromUnixTimeMilliseconds(value);

    private static DateTimeOffset FromUnix(long? value) =>
        FromUnix(value ?? throw new InvalidOperationException("Work timestamp is missing."));

    private static DateTimeOffset? OptionalUnix(long? value) =>
        value is null ? null : DateTimeOffset.FromUnixTimeMilliseconds(value.Value);

    private static AgentIdentity? DeserializePinnedPersona(string? json) =>
        string.IsNullOrWhiteSpace(json)
            ? null
            : JsonSerializer.Deserialize<AgentIdentity>(json, Json);
}
