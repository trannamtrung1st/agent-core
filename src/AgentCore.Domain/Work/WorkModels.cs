using System.Text;

namespace AgentCore.Domain.Work;

public static class WorkLimits
{
    public const int MaxPersonaNameCharacters = 80;
    public const int MaxDefinitionIdCharacters = 128;
    public const int MaxModelFieldCharacters = 128;
    public const int MaxReasoningEffortCharacters = 64;
    public const int MaxEvidenceBytes = 4096;
    public const int MaxDedupeKeyCharacters = 200;
    public const int MaxProgressCharacters = 500;
    public const int MaxFailureSummaryCharacters = 500;
    public const int MaxFailureCodeCharacters = 64;
    public const int MaxResultCharacters = 16_000;
    public const int MaxCheckpointBytes = 65_536;
    public const int MaxPreviewCharacters = 2_000;
    public const int MaxPreparedActionBytes = 8_192;
    public const int MaxToolNameCharacters = 128;
    public const int ActionHashCharacters = 64;
    public const int MaxKnownEffectCharacters = 500;
    public const int DefaultMaxAttempts = 3;
    public const int MinMaxAttempts = 1;
    public const int MaxMaxAttempts = 8;
    public const int MaxListLimit = 100;
}

public enum WorkItemStatus
{
    Queued = 0,
    Running = 1,
    WaitingForApproval = 2,
    WaitingToRetry = 3,
    Completed = 4,
    Failed = 5,
    Cancelled = 6
}

public enum WorkSourceKind
{
    Schedule = 0,
    ApplicationEvent = 1
}

public enum WorkSideEffectDisposition
{
    None = 0,
    Prepared = 1,
    InFlight = 2,
    Succeeded = 3,
    DefinitelyFailed = 4,
    Indeterminate = 5
}

public enum WorkApprovalDecision
{
    Pending = 0,
    Approved = 1,
    Rejected = 2,
    Expired = 3,
    Cancelled = 4
}

public enum WorkTransitionFailure
{
    StaleRevision,
    StaleGeneration,
    Terminal,
    Illegal,
    NotClaimable,
    ApprovalMismatch,
    Rejected
}

public sealed class WorkItemTransitionException : Exception
{
    public WorkItemTransitionException(WorkTransitionFailure failure, string message)
        : base(message)
    {
        Failure = failure;
    }

    public WorkTransitionFailure Failure { get; }
}

public readonly record struct WorkOwner
{
    public WorkOwner(Guid agentInstanceId, Guid profileId)
    {
        if (agentInstanceId == Guid.Empty)
        {
            throw new ArgumentException("Work owner requires an Agent Instance.", nameof(agentInstanceId));
        }

        if (profileId == Guid.Empty)
        {
            throw new ArgumentException("Work owner requires a trusted profile.", nameof(profileId));
        }

        AgentInstanceId = agentInstanceId;
        ProfileId = profileId;
    }

    public Guid AgentInstanceId { get; }

    public Guid ProfileId { get; }
}

public sealed class WorkProvenance
{
    public WorkProvenance(
        Guid sourceOccurrenceId,
        WorkSourceKind sourceKind,
        Guid? registrationId,
        Guid? sourceSessionId,
        Guid? sourceEventId,
        string dedupeKey,
        DateTimeOffset? scheduledAtUtc,
        DateTimeOffset observedAtUtc,
        string evidenceJson,
        string definitionId,
        int definitionVersion,
        string personaName)
    {
        if (sourceOccurrenceId == Guid.Empty)
        {
            throw new ArgumentException("Source occurrence is required.", nameof(sourceOccurrenceId));
        }

        if (!Enum.IsDefined(sourceKind))
        {
            throw new ArgumentException("Source kind is not valid.", nameof(sourceKind));
        }

        WorkText.RequireOptionalId(registrationId, "Registration");
        WorkText.RequireOptionalId(sourceSessionId, "Source session");
        WorkText.RequireOptionalId(sourceEventId, "Source event");
        WorkTime.RequireUtc(observedAtUtc, "Observed");
        WorkTime.RequireUtc(scheduledAtUtc, "Scheduled");
        if (definitionVersion < 1)
        {
            throw new ArgumentException("Definition version must be at least 1.");
        }

        SourceOccurrenceId = sourceOccurrenceId;
        SourceKind = sourceKind;
        RegistrationId = registrationId;
        SourceSessionId = sourceSessionId;
        SourceEventId = sourceEventId;
        DedupeKey = WorkText.RequireDedupeKey(dedupeKey);
        ScheduledAtUtc = scheduledAtUtc;
        ObservedAtUtc = observedAtUtc;
        EvidenceJson = WorkText.RequireUtf8(evidenceJson, WorkLimits.MaxEvidenceBytes, "Occurrence evidence");
        DefinitionId = WorkText.RequireToken(definitionId, WorkLimits.MaxDefinitionIdCharacters, "Definition");
        DefinitionVersion = definitionVersion;
        PersonaName = WorkText.RequireLine(personaName, WorkLimits.MaxPersonaNameCharacters, "Persona");
    }

    public Guid SourceOccurrenceId { get; }

    public WorkSourceKind SourceKind { get; }

    public Guid? RegistrationId { get; }

    public Guid? SourceSessionId { get; }

    public Guid? SourceEventId { get; }

    public string DedupeKey { get; }

    public DateTimeOffset? ScheduledAtUtc { get; }

    public DateTimeOffset ObservedAtUtc { get; }

    public string EvidenceJson { get; }

    public string DefinitionId { get; }

    public int DefinitionVersion { get; }

    public string PersonaName { get; }
}

public sealed class WorkModelPin
{
    public WorkModelPin(string catalogKey, string providerAlias, string modelId, string? reasoningEffort)
    {
        CatalogKey = WorkText.RequireToken(catalogKey, WorkLimits.MaxModelFieldCharacters, "Model catalog key");
        ProviderAlias = WorkText.RequireToken(providerAlias, WorkLimits.MaxModelFieldCharacters, "Model provider");
        ModelId = WorkText.RequireToken(modelId, WorkLimits.MaxModelFieldCharacters, "Model");
        ReasoningEffort = reasoningEffort is null
            ? null
            : WorkText.RequireToken(reasoningEffort, WorkLimits.MaxReasoningEffortCharacters, "Reasoning effort");
    }

    public string CatalogKey { get; }

    public string ProviderAlias { get; }

    public string ModelId { get; }

    public string? ReasoningEffort { get; }
}

public sealed class WorkClaim
{
    public WorkClaim(Guid generation, DateTimeOffset claimedAtUtc, DateTimeOffset leaseExpiresAtUtc)
    {
        if (generation == Guid.Empty)
        {
            throw new ArgumentException("Execution generation is required.", nameof(generation));
        }

        WorkTime.RequireUtc(claimedAtUtc, "Claim");
        WorkTime.RequireUtc(leaseExpiresAtUtc, "Claim lease");
        if (leaseExpiresAtUtc <= claimedAtUtc)
        {
            throw new ArgumentException("Claim lease must be later than the claim time.");
        }

        Generation = generation;
        ClaimedAtUtc = claimedAtUtc;
        LeaseExpiresAtUtc = leaseExpiresAtUtc;
    }

    public Guid Generation { get; }

    public DateTimeOffset ClaimedAtUtc { get; }

    public DateTimeOffset LeaseExpiresAtUtc { get; }
}

public sealed class WorkProgress
{
    public WorkProgress(string summary, DateTimeOffset updatedAtUtc)
    {
        WorkTime.RequireUtc(updatedAtUtc, "Progress");
        Summary = WorkText.RequireLine(summary, WorkLimits.MaxProgressCharacters, "Progress");
        UpdatedAtUtc = updatedAtUtc;
    }

    public string Summary { get; }

    public DateTimeOffset UpdatedAtUtc { get; }
}

public sealed class WorkCheckpoint
{
    public WorkCheckpoint(string payloadJson, int stepCount, long outputBytes, int remainingOverallBudgetMs)
    {
        if (stepCount < 0 || outputBytes < 0 || remainingOverallBudgetMs < 0)
        {
            throw new ArgumentException("Checkpoint counters cannot be negative.");
        }

        PayloadJson = WorkText.RequireUtf8(payloadJson, WorkLimits.MaxCheckpointBytes, "Checkpoint");
        StepCount = stepCount;
        OutputBytes = outputBytes;
        RemainingOverallBudgetMs = remainingOverallBudgetMs;
    }

    public string PayloadJson { get; }

    public int StepCount { get; }

    public long OutputBytes { get; }

    public int RemainingOverallBudgetMs { get; }
}

public sealed class WorkResult
{
    public WorkResult(string text, DateTimeOffset completedAtUtc)
    {
        WorkTime.RequireUtc(completedAtUtc, "Result");
        Text = WorkText.RequireMultiline(text, WorkLimits.MaxResultCharacters, "Result");
        CompletedAtUtc = completedAtUtc;
    }

    public string Text { get; }

    public DateTimeOffset CompletedAtUtc { get; }
}

public sealed class WorkFailure
{
    public WorkFailure(string code, string summary, DateTimeOffset failedAtUtc)
    {
        WorkTime.RequireUtc(failedAtUtc, "Failure");
        Code = WorkText.RequireFailureCode(code);
        Summary = WorkText.RequireLine(summary, WorkLimits.MaxFailureSummaryCharacters, "Failure");
        FailedAtUtc = failedAtUtc;
    }

    public string Code { get; }

    public string Summary { get; }

    public DateTimeOffset FailedAtUtc { get; }
}

public sealed class WorkSideEffect
{
    public static WorkSideEffect None { get; } = new(WorkSideEffectDisposition.None, null, null, null);

    public WorkSideEffect(
        WorkSideEffectDisposition disposition,
        string? toolCallId,
        string? actionHash,
        DateTimeOffset? updatedAtUtc)
    {
        if (!Enum.IsDefined(disposition))
        {
            throw new ArgumentException("Side-effect disposition is not valid.", nameof(disposition));
        }

        if (disposition == WorkSideEffectDisposition.None)
        {
            if (toolCallId is not null || actionHash is not null || updatedAtUtc is not null)
            {
                throw new ArgumentException("An empty side effect cannot carry an action.");
            }
        }
        else
        {
            if (updatedAtUtc is null)
            {
                throw new ArgumentException("Side-effect time is required.");
            }

            WorkTime.RequireUtc(updatedAtUtc.Value, "Side effect");
            if (string.IsNullOrWhiteSpace(toolCallId))
            {
                throw new ArgumentException("Side-effect tool call identity is required.");
            }

            toolCallId = toolCallId.Trim();
            actionHash = WorkText.RequireActionHash(actionHash);
        }

        Disposition = disposition;
        ToolCallId = toolCallId;
        ActionHash = actionHash;
        UpdatedAtUtc = updatedAtUtc;
    }

    public WorkSideEffectDisposition Disposition { get; }

    public string? ToolCallId { get; }

    public string? ActionHash { get; }

    public DateTimeOffset? UpdatedAtUtc { get; }
}

public sealed class WorkApproval
{
    public WorkApproval(
        Guid approvalId,
        Guid workItemId,
        Guid executionGeneration,
        long checkpointRevision,
        string toolName,
        string preparedActionJson,
        string actionHash,
        string preview,
        DateTimeOffset expiresAtUtc,
        WorkApprovalDecision decision,
        DateTimeOffset? decidedAtUtc,
        bool consumed,
        long revision,
        DateTimeOffset createdAtUtc)
    {
        if (approvalId == Guid.Empty)
        {
            throw new ArgumentException("Approval identifier is required.", nameof(approvalId));
        }

        if (workItemId == Guid.Empty)
        {
            throw new ArgumentException("Approval requires a work item.", nameof(workItemId));
        }

        if (executionGeneration == Guid.Empty)
        {
            throw new ArgumentException("Approval requires an execution generation.", nameof(executionGeneration));
        }

        if (checkpointRevision < 1 || revision < 1)
        {
            throw new ArgumentException("Approval revisions start at 1.");
        }

        if (!Enum.IsDefined(decision))
        {
            throw new ArgumentException("Approval decision is not valid.", nameof(decision));
        }

        WorkTime.RequireUtc(expiresAtUtc, "Approval expiry");
        WorkTime.RequireUtc(createdAtUtc, "Approval");
        WorkTime.RequireUtc(decidedAtUtc, "Approval decision");
        var consumedRequired = decision == WorkApprovalDecision.Approved;
        if (consumed != consumedRequired)
        {
            throw new ArgumentException("Only an approved action is consumed.");
        }

        if (decision == WorkApprovalDecision.Pending)
        {
            if (decidedAtUtc is not null)
            {
                throw new ArgumentException("A pending approval has no decision time.");
            }
        }
        else if (decidedAtUtc is null)
        {
            throw new ArgumentException("A decided approval requires a decision time.");
        }

        ApprovalId = approvalId;
        WorkItemId = workItemId;
        ExecutionGeneration = executionGeneration;
        CheckpointRevision = checkpointRevision;
        ToolName = WorkText.RequireToolName(toolName);
        PreparedActionJson = WorkText.RequireUtf8(preparedActionJson, WorkLimits.MaxPreparedActionBytes, "Prepared action");
        ActionHash = WorkText.RequireActionHash(actionHash);
        Preview = WorkText.RequireMultiline(preview, WorkLimits.MaxPreviewCharacters, "Approval preview");
        ExpiresAtUtc = expiresAtUtc;
        Decision = decision;
        DecidedAtUtc = decidedAtUtc;
        Consumed = consumed;
        Revision = revision;
        CreatedAtUtc = createdAtUtc;
    }

    public Guid ApprovalId { get; }

    public Guid WorkItemId { get; }

    public Guid ExecutionGeneration { get; }

    public long CheckpointRevision { get; }

    public string ToolName { get; }

    public string PreparedActionJson { get; }

    public string ActionHash { get; }

    public string Preview { get; }

    public DateTimeOffset ExpiresAtUtc { get; }

    public WorkApprovalDecision Decision { get; }

    public DateTimeOffset? DecidedAtUtc { get; }

    public bool Consumed { get; }

    public long Revision { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    public WorkApproval WithDecision(WorkApprovalDecision decision, DateTimeOffset decidedAtUtc, long revision) =>
        new(
            ApprovalId,
            WorkItemId,
            ExecutionGeneration,
            CheckpointRevision,
            ToolName,
            PreparedActionJson,
            ActionHash,
            Preview,
            ExpiresAtUtc,
            decision,
            decidedAtUtc,
            consumed: decision == WorkApprovalDecision.Approved,
            revision,
            CreatedAtUtc);
}

public sealed record WorkItemPublicSummary(
    Guid WorkItemId,
    Guid AgentInstanceId,
    Guid ProfileId,
    WorkItemStatus Status,
    long Revision,
    WorkSourceKind SourceKind,
    Guid SourceOccurrenceId,
    string OriginLabel,
    string? ProgressSummary,
    bool NeedsApproval,
    string? ApprovalPreview,
    string? ResultText,
    string? FailureCode,
    string? FailureSummary,
    string? KnownEffectSummary,
    bool CancellationAvailable,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

internal static class WorkTime
{
    public static void RequireUtc(DateTimeOffset value, string name)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException($"{name} timestamp must be UTC.");
        }
    }

    public static void RequireUtc(DateTimeOffset? value, string name)
    {
        if (value is DateTimeOffset timestamp)
        {
            RequireUtc(timestamp, name);
        }
    }
}

internal static class WorkText
{
    public static string RequireLine(string? value, int max, string name)
    {
        var trimmed = RequirePresent(value, name);
        if (trimmed.Length > max || HasDisallowedControl(trimmed, allowWhitespace: false))
        {
            throw new ArgumentException($"{name} must be 1-{max} characters without control characters.");
        }

        return trimmed;
    }

    public static string RequireMultiline(string? value, int max, string name)
    {
        if (value is null)
        {
            throw new ArgumentException($"{name} is required.");
        }

        var trimmed = value.Trim();
        if (trimmed.Length == 0 || trimmed.Length > max || HasDisallowedControl(trimmed, allowWhitespace: true))
        {
            throw new ArgumentException($"{name} must be 1-{max} characters.");
        }

        return trimmed;
    }

    public static string RequireToken(string? value, int max, string name)
    {
        var trimmed = RequirePresent(value, name);
        if (trimmed.Length > max || HasDisallowedControl(trimmed, allowWhitespace: false) || trimmed.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException($"{name} must be 1-{max} characters without spaces.");
        }

        return trimmed;
    }

    public static string RequireDedupeKey(string? dedupeKey)
    {
        var trimmed = RequirePresent(dedupeKey, "Dedupe key");
        if (trimmed.Length > WorkLimits.MaxDedupeKeyCharacters)
        {
            throw new ArgumentException("Dedupe key must be 1-200 characters.");
        }

        foreach (var character in trimmed)
        {
            if (character is < '!' or > '~')
            {
                throw new ArgumentException("Dedupe key must be printable ASCII.");
            }
        }

        return trimmed;
    }

    public static string RequireUtf8(string? value, int maxBytes, string name)
    {
        if (value is null || value.Length == 0)
        {
            throw new ArgumentException($"{name} is required.");
        }

        if (Encoding.UTF8.GetByteCount(value) > maxBytes)
        {
            throw new ArgumentException($"{name} exceeds its size limit.");
        }

        return value;
    }

    public static string RequireActionHash(string? hash)
    {
        if (hash is null || hash.Length != WorkLimits.ActionHashCharacters)
        {
            throw new ArgumentException("Action hash must be 64 lowercase hex characters.");
        }

        foreach (var character in hash)
        {
            if (character is not (>= '0' and <= '9' or >= 'a' and <= 'f'))
            {
                throw new ArgumentException("Action hash must be 64 lowercase hex characters.");
            }
        }

        return hash;
    }

    public static string RequireFailureCode(string? code)
    {
        var trimmed = RequirePresent(code, "Failure code");
        if (trimmed.Length > WorkLimits.MaxFailureCodeCharacters || trimmed[0] == '-' || trimmed[^1] == '-')
        {
            throw new ArgumentException("Failure code is not valid.");
        }

        foreach (var character in trimmed)
        {
            if (character is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '-'))
            {
                throw new ArgumentException("Failure code is not valid.");
            }
        }

        return trimmed;
    }

    public static string RequireToolName(string? name)
    {
        var trimmed = RequirePresent(name, "Tool name");
        if (trimmed.Length > WorkLimits.MaxToolNameCharacters)
        {
            throw new ArgumentException("Tool name must be 1-128 characters.");
        }

        foreach (var character in trimmed)
        {
            if (character is not (>= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '_' or '-'))
            {
                throw new ArgumentException("Tool name must be 1-128 characters.");
            }
        }

        return trimmed;
    }

    public static string? OptionalLine(string? value, int max, string name)
    {
        if (value is null)
        {
            return null;
        }

        return RequireLine(value, max, name);
    }

    public static void RequireOptionalId(Guid? id, string name)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException($"{name} identifier is not valid.");
        }
    }

    private static string RequirePresent(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"{name} is required.");
        }

        return value.Trim();
    }

    private static bool HasDisallowedControl(string value, bool allowWhitespace)
    {
        foreach (var character in value)
        {
            if (!char.IsControl(character))
            {
                continue;
            }

            if (allowWhitespace && character is '\n' or '\r' or '\t')
            {
                continue;
            }

            return true;
        }

        return false;
    }
}
