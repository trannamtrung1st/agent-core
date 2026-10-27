using System.Text;
using System.Text.Json.Serialization;
using AgentCore.Domain.Definitions;

namespace AgentCore.Domain.Conversation;

public static class AgentRunLimits
{
    public const int MaxPersonaNameCharacters = 80;
    public const int MaxDefinitionIdCharacters = 128;
    public const int MaxModelFieldCharacters = 128;
    public const int MaxExternalModelIdCharacters = 255;
    public const int MaxReasoningEffortCharacters = 64;
    public const int MaxEvidenceBytes = 8192;
    public const int MaxDedupeKeyCharacters = 200;
    public const int MaxProgressCharacters = 500;
    public const int MaxFailureSummaryCharacters = 500;
    public const int MaxFailureCodeCharacters = 64;
    public const int MaxResultCharacters = 16_000;
    public const int MaxActiveBackgroundRuns = 8;
    public const int MaxBackgroundObjectiveCharacters = 4000;
    public const int MaxCheckpointBytes = 262_144;
    public const int MaxPreviewCharacters = 12_000;
    // A bounded 20-file Drive manifest includes every exact name, ID and version.
    public const int MaxDriveBatchPreviewCharacters = 32_000;
    public const int MaxAutomationPreviewCharacters = 131_072;
    public const int MaxPreparedActionBytes = 8_192;
    public const int MaxDriveBatchPreparedActionBytes = 16_384;
    public const int MaxAutomationPreparedActionBytes = 65_536;
    public const int MaxToolNameCharacters = 128;
    public const int ActionHashCharacters = 64;
    public const int MaxKnownEffectCharacters = 500;
    public const int DefaultMaxAttempts = 3;
    public const int MinMaxAttempts = 1;
    public const int MaxMaxAttempts = 8;
    public const int MaxWaitSeconds = 300;
    public const int MaxWaitTargets = 8;
    public const int MaxWaitCount = 8;
    public const int MaxTotalWaitSeconds = 900;
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
    Cancelled = 6,
    WaitingForSignal = 7
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

public enum AgentRunTransitionFailure
{
    StaleRevision,
    StaleGeneration,
    Terminal,
    Illegal,
    NotClaimable,
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
    [System.Text.Json.Serialization.JsonConstructor]
    public AgentRunOwner(Guid agentInstanceId, Guid? profileIdOrNull, Guid? tenantId = null, Guid? executorPrincipalId = null)
    {
        if (agentInstanceId == Guid.Empty)
        {
            throw new ArgumentException("AgentRun owner requires an Agent Instance.", nameof(agentInstanceId));
        }

        if (tenantId is null ? profileIdOrNull is null || profileIdOrNull == Guid.Empty || executorPrincipalId is not null
            : tenantId == Guid.Empty || executorPrincipalId is null || executorPrincipalId == Guid.Empty || profileIdOrNull is not null)
        {
            throw new ArgumentException("AgentRun owner requires either tenant executor identity or a trusted profile.");
        }

        AgentInstanceId = agentInstanceId;
        ProfileIdOrNull = profileIdOrNull; TenantId = tenantId; ExecutorPrincipalId = executorPrincipalId;
    }

    public Guid AgentInstanceId { get; }

    [System.Text.Json.Serialization.JsonIgnore]
    public Guid ProfileId => ProfileIdOrNull ?? throw new InvalidOperationException("Tenant execution has no global profile.");
    [System.Text.Json.Serialization.JsonPropertyName("profileId")]
    public Guid? ProfileIdOrNull { get; }
    public Guid? TenantId { get; }
    public Guid? ExecutorPrincipalId { get; }
    public static AgentRunOwner ForTenant(Guid tenantId, Guid instanceId, Guid executorPrincipalId) => new(instanceId, null, tenantId, executorPrincipalId);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class AgentRunModelPin
{
    public AgentRunModelPin(string offeringId, string connectionId, string externalModelId, string? reasoningEffort,
        AgentCore.Domain.Models.ModelBinding? binding = null, AgentCore.Domain.Models.ModelDecisionEvidence? decision = null)
    {
        OfferingId = AgentRunText.RequireToken(offeringId, AgentRunLimits.MaxModelFieldCharacters, "Model catalog key");
        ConnectionId = AgentRunText.RequireToken(connectionId, AgentRunLimits.MaxModelFieldCharacters, "Model provider");
        ExternalModelId = AgentRunText.RequireToken(externalModelId, AgentRunLimits.MaxExternalModelIdCharacters, "External model ID");
        ReasoningEffort = reasoningEffort is null
            ? null
            : AgentRunText.RequireToken(reasoningEffort, AgentRunLimits.MaxReasoningEffortCharacters, "Reasoning effort");
        binding?.RequireIdentity(OfferingId, ConnectionId, ExternalModelId);
        Binding = binding;
        Decision = decision;
    }

    public string OfferingId { get; }

    public string ConnectionId { get; }

    public string ExternalModelId { get; }

    public string? ReasoningEffort { get; }

    public AgentCore.Domain.Models.ModelDecisionEvidence? Decision { get; }
    public AgentCore.Domain.Models.ModelBinding? Binding { get; }
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
    public AgentRunCheckpoint(string payloadJson, int stepCount, long outputBytes, int remainingOverallBudgetMs, int? activeExecutionMs = null, DateTimeOffset? budgetRecordedAtUtc = null, bool awaitingRuntimeAdmission = false, bool awaitingSignalAdmission = false)
    {
        if (stepCount < 0 || outputBytes < 0 || remainingOverallBudgetMs < 0 || activeExecutionMs < 0)
        {
            throw new ArgumentException("Checkpoint counters cannot be negative.");
        }

        PayloadJson = AgentRunText.RequireUtf8(payloadJson, AgentRunLimits.MaxCheckpointBytes, "Checkpoint");
        StepCount = stepCount;
        OutputBytes = outputBytes;
        RemainingOverallBudgetMs = remainingOverallBudgetMs;
        AgentRunTime.RequireUtc(budgetRecordedAtUtc, "Budget clock");
        ActiveExecutionMs = activeExecutionMs;
        BudgetRecordedAtUtc = budgetRecordedAtUtc;
        AwaitingRuntimeAdmission = awaitingRuntimeAdmission;
        if (awaitingSignalAdmission && !awaitingRuntimeAdmission) throw new ArgumentException("Signal admission requires the pending Runtime boundary.");
        AwaitingSignalAdmission = awaitingSignalAdmission;
    }

    public string PayloadJson { get; }

    public int StepCount { get; }

    public long OutputBytes { get; }

    public int RemainingOverallBudgetMs { get; }
    public int? ActiveExecutionMs { get; }
    public DateTimeOffset? BudgetRecordedAtUtc { get; }
    public bool AwaitingRuntimeAdmission { get; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool AwaitingSignalAdmission { get; }
    public AgentRunCheckpoint WithRuntimeAdmissionPending(bool pending) => new(PayloadJson, StepCount, OutputBytes, RemainingOverallBudgetMs, ActiveExecutionMs, BudgetRecordedAtUtc, pending, pending && AwaitingSignalAdmission);
    public AgentRunCheckpoint WithSignalAdmissionPending() => new(PayloadJson, StepCount, OutputBytes, RemainingOverallBudgetMs, ActiveExecutionMs, BudgetRecordedAtUtc, true, true);
    public AgentRunCheckpoint StampBudgetClock(DateTimeOffset? atUtc) => new(PayloadJson, StepCount, OutputBytes, RemainingOverallBudgetMs, ActiveExecutionMs, atUtc, AwaitingRuntimeAdmission, AwaitingSignalAdmission);
    public AgentRunCheckpoint PauseBudgetClock(DateTimeOffset atUtc)
    {
        if (BudgetRecordedAtUtc is not { } recorded || ActiveExecutionMs is not { } active) return this;
        var elapsed = (int)Math.Clamp((atUtc - recorded).TotalMilliseconds, 0, RemainingOverallBudgetMs);
        return new(PayloadJson, StepCount, OutputBytes, RemainingOverallBudgetMs - elapsed, checked(active + elapsed), awaitingRuntimeAdmission: AwaitingRuntimeAdmission, awaitingSignalAdmission: AwaitingSignalAdmission);
    }
}

public enum AgentRunOutcomeKind
{
    Response,
    NoAction,
    NeedsAttention,
    Continued
}

public sealed class AgentRunResult
{
    public AgentRunResult(string text, DateTimeOffset completedAtUtc, bool attentionRequired = false, AgentRunOutcomeKind outcomeKind = AgentRunOutcomeKind.Response, Guid? outcomeEntryId = null, Guid? continuationRunId = null)
    {
        AgentRunTime.RequireUtc(completedAtUtc, "Result");
        if (!Enum.IsDefined(outcomeKind) || (attentionRequired && outcomeKind == AgentRunOutcomeKind.NoAction))
            throw new ArgumentException("Outcome classification is not valid.");
        AgentRunText.RequireOptionalId(outcomeEntryId, "Outcome entry");
        AgentRunText.RequireOptionalId(continuationRunId, "Continuation Run");
        if ((outcomeKind == AgentRunOutcomeKind.Continued) != (continuationRunId is not null) || continuationRunId is not null && (outcomeEntryId is not null || attentionRequired))
            throw new ArgumentException("A continued outcome links exactly one successor and no fabricated response.");
        ContinuationRunId = continuationRunId;
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
    public Guid? ContinuationRunId { get; }

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

public sealed record AgentRunDirectOperation(Guid OperationId, string SourceCallId, string ToolName, string ArgumentsJson,
    string ParametersHash, long InstanceRevision, long PersonaRevision);
public sealed record AgentRunExternalEffect(Guid Generation, long StartedRunRevision, DateTimeOffset StartedAtUtc,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] AgentRunDirectOperation? DirectOperation = null);

public sealed class AgentRunSideEffect
{
    public static AgentRunSideEffect None { get; } = new(AgentRunSideEffectDisposition.None, null, null, null);

    public AgentRunSideEffect(
        AgentRunSideEffectDisposition disposition,
        string? toolCallId,
        string? actionHash,
        DateTimeOffset? updatedAtUtc,
        AgentRunExternalEffect? externalEffect = null)
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

        if (externalEffect is { } origin)
        {
            AgentRunTime.RequireUtc(origin.StartedAtUtc, "External effect start");
            if (origin.Generation == Guid.Empty || origin.StartedRunRevision < 1 || disposition == AgentRunSideEffectDisposition.None
                || toolCallId?.StartsWith("operation:", StringComparison.Ordinal) != true || updatedAtUtc < origin.StartedAtUtc)
                throw new ArgumentException("An external effect requires its exact starting claim and operation.");
            if (origin.DirectOperation is { } direct)
            {
                if (direct.OperationId == Guid.Empty || toolCallId != "operation:" + direct.OperationId.ToString("D")
                    || string.IsNullOrWhiteSpace(direct.SourceCallId) || direct.SourceCallId.Length > 200
                    || string.IsNullOrWhiteSpace(direct.ToolName) || direct.ToolName.Length > 128
                    || string.IsNullOrWhiteSpace(direct.ArgumentsJson) || System.Text.Encoding.UTF8.GetByteCount(direct.ArgumentsJson) > 65536
                    || direct.InstanceRevision < 1 || direct.PersonaRevision < 1)
                    throw new ArgumentException("Invalid direct external operation.");
                _ = AgentRunText.RequireActionHash(direct.ParametersHash);
                using var json = System.Text.Json.JsonDocument.Parse(direct.ArgumentsJson);
                if (json.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) throw new ArgumentException("Exact operation arguments are required.");
            }
        }
        ExternalEffect = externalEffect;
        Disposition = disposition;
        ToolCallId = toolCallId;
        ActionHash = actionHash;
        UpdatedAtUtc = updatedAtUtc;
    }

    public AgentRunSideEffectDisposition Disposition { get; }

    public string? ToolCallId { get; }

    public string? ActionHash { get; }

    public DateTimeOffset? UpdatedAtUtc { get; }
    public AgentRunExternalEffect? ExternalEffect { get; }
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
