using System.Text;
using AgentCore.Domain.Definitions;

namespace AgentCore.Domain.Conversation;

public static class AgentRunLimits
{
    public const int MaxPersonaNameCharacters = 80;
    public const int MaxDefinitionIdCharacters = 128;
    public const int MaxModelFieldCharacters = 128;
    public const int MaxReasoningEffortCharacters = 64;
    public const int MaxEvidenceBytes = 8192;
    public const int MaxDedupeKeyCharacters = 200;
    public const int MaxProgressCharacters = 500;
    public const int MaxFailureSummaryCharacters = 500;
    public const int MaxFailureCodeCharacters = 64;
    public const int MaxResultCharacters = 16_000;
    public const int MaxImmediateChildren = 2;
    public const int MaxActiveBackgroundRuns = 8;
    public const int MaxBackgroundObjectiveCharacters = 4000;
    public const int MaxCheckpointBytes = 65_536;
    public const int MaxPreviewCharacters = 12_000;
    public const int MaxPreparedActionBytes = 8_192;
    public const int MaxToolNameCharacters = 128;
    public const int ActionHashCharacters = 64;
    public const int MaxKnownEffectCharacters = 500;
    public const int DefaultMaxAttempts = 3;
    public const int MinMaxAttempts = 1;
    public const int MaxMaxAttempts = 8;
    public const int MaxListLimit = 100;
}

public enum AgentRunStatus
{
    Queued = 0,
    Running = 1,
    WaitingForApproval = 2,
    WaitingToRetry = 3,
    Completed = 4,
    Failed = 5,
    Cancelled = 6
}

public enum AgentRunSideEffectDisposition
{
    None = 0,
    Prepared = 1,
    InFlight = 2,
    Succeeded = 3,
    DefinitelyFailed = 4,
    Indeterminate = 5
}

public enum AgentRunApprovalDecision
{
    Pending = 0,
    Approved = 1,
    Rejected = 2,
    Expired = 3,
    Cancelled = 4
}

public enum AgentRunTransitionFailure
{
    StaleRevision,
    StaleGeneration,
    Terminal,
    Illegal,
    NotClaimable,
    ApprovalMismatch,
    Rejected
}

public sealed class AgentRunTransitionException : Exception
{
    public AgentRunTransitionException(AgentRunTransitionFailure failure, string message)
        : base(message)
    {
        Failure = failure;
    }

    public AgentRunTransitionFailure Failure { get; }
}

public readonly record struct AgentRunOwner
{
    public AgentRunOwner(Guid agentInstanceId, Guid profileId)
    {
        if (agentInstanceId == Guid.Empty)
        {
            throw new ArgumentException("AgentRun owner requires an Agent Instance.", nameof(agentInstanceId));
        }

        if (profileId == Guid.Empty)
        {
            throw new ArgumentException("AgentRun owner requires a trusted profile.", nameof(profileId));
        }

        AgentInstanceId = agentInstanceId;
        ProfileId = profileId;
    }

    public Guid AgentInstanceId { get; }

    public Guid ProfileId { get; }
}

public sealed class AgentRunModelPin
{
    public AgentRunModelPin(string catalogKey, string providerAlias, string modelId, string? reasoningEffort)
    {
        CatalogKey = AgentRunText.RequireToken(catalogKey, AgentRunLimits.MaxModelFieldCharacters, "Model catalog key");
        ProviderAlias = AgentRunText.RequireToken(providerAlias, AgentRunLimits.MaxModelFieldCharacters, "Model provider");
        ModelId = AgentRunText.RequireToken(modelId, AgentRunLimits.MaxModelFieldCharacters, "Model");
        ReasoningEffort = reasoningEffort is null
            ? null
            : AgentRunText.RequireToken(reasoningEffort, AgentRunLimits.MaxReasoningEffortCharacters, "Reasoning effort");
    }

    public string CatalogKey { get; }

    public string ProviderAlias { get; }

    public string ModelId { get; }

    public string? ReasoningEffort { get; }
}

public sealed class AgentRunClaim
{
    public AgentRunClaim(Guid generation, DateTimeOffset claimedAtUtc, DateTimeOffset leaseExpiresAtUtc)
    {
        if (generation == Guid.Empty)
        {
            throw new ArgumentException("Execution generation is required.", nameof(generation));
        }

        AgentRunTime.RequireUtc(claimedAtUtc, "Claim");
        AgentRunTime.RequireUtc(leaseExpiresAtUtc, "Claim lease");
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

public sealed class AgentRunProgress
{
    public AgentRunProgress(string summary, DateTimeOffset updatedAtUtc)
    {
        AgentRunTime.RequireUtc(updatedAtUtc, "Progress");
        Summary = AgentRunText.RequireLine(summary, AgentRunLimits.MaxProgressCharacters, "Progress");
        UpdatedAtUtc = updatedAtUtc;
    }

    public string Summary { get; }

    public DateTimeOffset UpdatedAtUtc { get; }
}

public sealed class AgentRunCheckpoint
{
    public AgentRunCheckpoint(string payloadJson, int stepCount, long outputBytes, int remainingOverallBudgetMs)
    {
        if (stepCount < 0 || outputBytes < 0 || remainingOverallBudgetMs < 0)
        {
            throw new ArgumentException("Checkpoint counters cannot be negative.");
        }

        PayloadJson = AgentRunText.RequireUtf8(payloadJson, AgentRunLimits.MaxCheckpointBytes, "Checkpoint");
        StepCount = stepCount;
        OutputBytes = outputBytes;
        RemainingOverallBudgetMs = remainingOverallBudgetMs;
    }

    public string PayloadJson { get; }

    public int StepCount { get; }

    public long OutputBytes { get; }

    public int RemainingOverallBudgetMs { get; }
}

public enum AgentRunOutcomeKind
{
    Response,
    NoAction,
    NeedsAttention
}

public sealed class AgentRunResult
{
    public AgentRunResult(string text, DateTimeOffset completedAtUtc, bool attentionRequired = false, AgentRunOutcomeKind outcomeKind = AgentRunOutcomeKind.Response, Guid? outcomeEntryId = null)
    {
        AgentRunTime.RequireUtc(completedAtUtc, "Result");
        if (!Enum.IsDefined(outcomeKind) || (attentionRequired && outcomeKind == AgentRunOutcomeKind.NoAction))
            throw new ArgumentException("Outcome classification is not valid.");
        AgentRunText.RequireOptionalId(outcomeEntryId, "Outcome entry");
        if (outcomeKind == AgentRunOutcomeKind.NoAction && outcomeEntryId is not null)
            throw new ArgumentException("Quiet outcomes have no fabricated conversation entry.");
        if (outcomeKind == AgentRunOutcomeKind.Response && outcomeEntryId is null)
            throw new ArgumentException("A response outcome requires its durable Session entry.");
        Text = outcomeKind == AgentRunOutcomeKind.NoAction && string.IsNullOrWhiteSpace(text)
            ? string.Empty : AgentRunText.RequireMultiline(text, AgentRunLimits.MaxResultCharacters, "Result");
        OutcomeKind = attentionRequired ? AgentRunOutcomeKind.NeedsAttention : outcomeKind;
        OutcomeEntryId = outcomeEntryId;
        CompletedAtUtc = completedAtUtc;
        AttentionRequired = OutcomeKind == AgentRunOutcomeKind.NeedsAttention;
    }

    public AgentRunOutcomeKind OutcomeKind { get; }

    public Guid? OutcomeEntryId { get; }

    public string Text { get; }

    public DateTimeOffset CompletedAtUtc { get; }

    public bool AttentionRequired { get; }
}

public static class AgentRunAttentionKey
{
    public static string Format(Guid agentRunId, long revision)
    {
        if (agentRunId == Guid.Empty)
        {
            throw new ArgumentException("Agent run id is required.", nameof(agentRunId));
        }

        if (revision < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(revision), "Revision is required.");
        }

        return $"{agentRunId:D}:{revision}";
    }
}

public sealed class AgentRunFailure
{
    public AgentRunFailure(string code, string summary, DateTimeOffset failedAtUtc, Guid? diagnosticId = null)
    {
        AgentRunTime.RequireUtc(failedAtUtc, "Failure");
        Code = AgentRunText.RequireFailureCode(code);
        Summary = AgentRunText.RequireLine(summary, AgentRunLimits.MaxFailureSummaryCharacters, "Failure");
        FailedAtUtc = failedAtUtc;
        if (diagnosticId == Guid.Empty)
        {
            throw new ArgumentException("Diagnostic id is required.", nameof(diagnosticId));
        }

        DiagnosticId = diagnosticId;
    }

    public string Code { get; }

    public string Summary { get; }

    public DateTimeOffset FailedAtUtc { get; }

    public Guid? DiagnosticId { get; }
}

public sealed class AgentRunSideEffect
{
    public static AgentRunSideEffect None { get; } = new(AgentRunSideEffectDisposition.None, null, null, null);

    public AgentRunSideEffect(
        AgentRunSideEffectDisposition disposition,
        string? toolCallId,
        string? actionHash,
        DateTimeOffset? updatedAtUtc)
    {
        if (!Enum.IsDefined(disposition))
        {
            throw new ArgumentException("Side-effect disposition is not valid.", nameof(disposition));
        }

        if (disposition == AgentRunSideEffectDisposition.None)
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

            AgentRunTime.RequireUtc(updatedAtUtc.Value, "Side effect");
            actionHash = AgentRunText.RequireActionHash(actionHash);
            if (string.IsNullOrWhiteSpace(toolCallId))
            {
                if (disposition is not (AgentRunSideEffectDisposition.Prepared or AgentRunSideEffectDisposition.Indeterminate))
                {
                    throw new ArgumentException("Side-effect tool call identity is required.");
                }
            }
            else
            {
                toolCallId = toolCallId.Trim();
            }
        }

        Disposition = disposition;
        ToolCallId = toolCallId;
        ActionHash = actionHash;
        UpdatedAtUtc = updatedAtUtc;
    }

    public AgentRunSideEffectDisposition Disposition { get; }

    public string? ToolCallId { get; }

    public string? ActionHash { get; }

    public DateTimeOffset? UpdatedAtUtc { get; }
}

public sealed class AgentRunApproval
{
    public AgentRunApproval(
        Guid approvalId,
        Guid agentRunId,
        Guid executionGeneration,
        long checkpointRevision,
        string toolName,
        string preparedActionJson,
        string actionHash,
        string preview,
        DateTimeOffset expiresAtUtc,
        AgentRunApprovalDecision decision,
        DateTimeOffset? decidedAtUtc,
        bool consumed,
        long revision,
        DateTimeOffset createdAtUtc)
    {
        if (approvalId == Guid.Empty)
        {
            throw new ArgumentException("Approval identifier is required.", nameof(approvalId));
        }

        if (agentRunId == Guid.Empty)
        {
            throw new ArgumentException("Approval requires a agent run.", nameof(agentRunId));
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

        AgentRunTime.RequireUtc(expiresAtUtc, "Approval expiry");
        AgentRunTime.RequireUtc(createdAtUtc, "Approval");
        AgentRunTime.RequireUtc(decidedAtUtc, "Approval decision");
        var consumedRequired = decision == AgentRunApprovalDecision.Approved;
        if (consumed != consumedRequired)
        {
            throw new ArgumentException("Only an approved action is consumed.");
        }

        if (decision == AgentRunApprovalDecision.Pending)
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
        AgentRunId = agentRunId;
        ExecutionGeneration = executionGeneration;
        CheckpointRevision = checkpointRevision;
        ToolName = AgentRunText.RequireToolName(toolName);
        PreparedActionJson = AgentRunText.RequireUtf8(preparedActionJson, AgentRunLimits.MaxPreparedActionBytes, "Prepared action");
        ActionHash = AgentRunText.RequireActionHash(actionHash);
        Preview = AgentRunText.RequireMultiline(preview, AgentRunLimits.MaxPreviewCharacters, "Approval preview");
        ExpiresAtUtc = expiresAtUtc;
        Decision = decision;
        DecidedAtUtc = decidedAtUtc;
        Consumed = consumed;
        Revision = revision;
        CreatedAtUtc = createdAtUtc;
    }

    public Guid ApprovalId { get; }

    public Guid AgentRunId { get; }

    public Guid ExecutionGeneration { get; }

    public long CheckpointRevision { get; }

    public string ToolName { get; }

    public string PreparedActionJson { get; }

    public string ActionHash { get; }

    public string Preview { get; }

    public DateTimeOffset ExpiresAtUtc { get; }

    public AgentRunApprovalDecision Decision { get; }

    public DateTimeOffset? DecidedAtUtc { get; }

    public bool Consumed { get; }

    public long Revision { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    public AgentRunApproval WithDecision(AgentRunApprovalDecision decision, DateTimeOffset decidedAtUtc, long revision) =>
        new(
            ApprovalId,
            AgentRunId,
            ExecutionGeneration,
            CheckpointRevision,
            ToolName,
            PreparedActionJson,
            ActionHash,
            Preview,
            ExpiresAtUtc,
            decision,
            decidedAtUtc,
            consumed: decision == AgentRunApprovalDecision.Approved,
            revision,
            CreatedAtUtc);
}

internal static class AgentRunTime
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

internal static class AgentRunText
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
        if (trimmed.Length > AgentRunLimits.MaxDedupeKeyCharacters)
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
        if (hash is null || hash.Length != AgentRunLimits.ActionHashCharacters)
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
        if (trimmed.Length > AgentRunLimits.MaxFailureCodeCharacters || trimmed[0] == '-' || trimmed[^1] == '-')
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
        if (trimmed.Length > AgentRunLimits.MaxToolNameCharacters)
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
