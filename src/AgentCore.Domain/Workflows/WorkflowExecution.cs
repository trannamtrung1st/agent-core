using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentCore.Domain.Workflows;

public enum WorkflowExecutionStatus { PendingAdmission, Running, WaitingForAgent, WaitingForVerification, WaitingForHuman,
    WaitingForOperationApproval, Blocked, NeedsAttention, Completed, Failed, Cancelled, Expired }
public enum WorkflowOperationStatus { Reserved, Admitted, Settled, SignalConsumed }
public sealed record WorkflowCancellation(Guid PrincipalId, string RequestKey, string Fingerprint, DateTimeOffset At);
public sealed record WorkflowSourceClosure(string ReasonCode, DateTimeOffset At);
public sealed record WorkflowSourceBudget(int MaxSteps, int DurationSeconds);

/// <summary>Immutable source witnesses. A process continuation is never represented as a claimed Run or human.</summary>
public sealed record WorkflowInvocationSource(WorkflowInvocationKind Kind, Guid SourceId, Guid PrincipalId,
    Guid? OriginalHumanPrincipalId, Guid? AccountId = null, Guid? SessionId = null, Guid? RunId = null,
    Guid? AutomationId = null, Guid? InvokingInstanceId = null, Guid? RootActivationId = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] WorkflowEventCausation? Causation = null)
{
    public string RequestScope => Kind switch {
        WorkflowInvocationKind.HumanManual => "human:" + PrincipalId.ToString("D"),
        WorkflowInvocationKind.ClaimedAgent => "run:" + RunId!.Value.ToString("D"),
        WorkflowInvocationKind.AutomationOccurrence => "occurrence:" + SourceId.ToString("D"),
        _ => throw new ArgumentException("Unknown workflow invocation source.")
    };
    public void Validate()
    {
        if (!Enum.IsDefined(Kind) || !WorkflowGraphRules.Id(SourceId) || !WorkflowGraphRules.Id(PrincipalId)
            || new[] { OriginalHumanPrincipalId, AccountId, SessionId, RunId, AutomationId, InvokingInstanceId, RootActivationId }.Any(id => id == Guid.Empty)
            || Kind == WorkflowInvocationKind.HumanManual && (!WorkflowGraphRules.Id(AccountId) || OriginalHumanPrincipalId != PrincipalId
                || RunId is not null || AutomationId is not null || InvokingInstanceId is not null || RootActivationId is not null || Causation is not null)
            || Kind == WorkflowInvocationKind.ClaimedAgent && (RunId != SourceId || !WorkflowGraphRules.Id(SessionId)
                || !WorkflowGraphRules.Id(InvokingInstanceId) || !WorkflowGraphRules.Id(RootActivationId) || AutomationId is not null || AccountId is not null)
            || Kind == WorkflowInvocationKind.AutomationOccurrence && (!WorkflowGraphRules.Id(AutomationId)
                || !WorkflowGraphRules.Id(InvokingInstanceId) || AccountId is not null || RunId is not null))
            throw new ArgumentException("Workflow invocation requires genuine source witnesses.");
        Causation?.Validate();
    }
}

/// <summary>Business binding and observed projection only. Elsa remains the authoritative graph cursor.</summary>
public sealed record WorkflowExecution(Guid TenantId, Guid ExecutionId, Guid WorkflowId, int Version, string GraphHash,
    WorkflowInvocationSource Source, Guid? CoordinatorInstanceId, string InputsJson, string InputsHash, string RequestKey,
    string Fingerprint, DateTimeOffset CreatedAt, DateTimeOffset DeadlineAt, long Revision,
    WorkflowExecutionStatus Status = WorkflowExecutionStatus.PendingAdmission,
    bool CancellationRequested = false, string? ReasonCode = null, DateTimeOffset? TerminalAt = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] WorkflowCancellation? Cancellation = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] WorkflowSourceBudget? SourceBudget = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] WorkflowSourceClosure? SourceClosure = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? ContentRetiredAt = null);

/// <summary>One logical Core operation per immutable process node; no model-specified retry or step advancement.</summary>
public sealed record WorkflowStepOperation(Guid TenantId, Guid ExecutionId, string NodeId, int Attempt, Guid OperationId,
    string BookmarkId, string GraphHash, WorkflowNodeKind Kind, long Revision, WorkflowOperationStatus Status,
    DateTimeOffset CreatedAt, Guid? TaskId = null, Guid? WorkerRunId = null, Guid? EvaluationAttemptId = null,
    Guid? DecisionActorId = null, string? Outcome = null, bool ProofAccepted = false, string? SelectedEdgeId = null,
    string? ReasonCode = null, DateTimeOffset? SettledAt = null);

public static class WorkflowExecutionRules
{
    public static bool Terminal(WorkflowExecutionStatus status) => status is WorkflowExecutionStatus.Completed or WorkflowExecutionStatus.Failed
        or WorkflowExecutionStatus.Cancelled or WorkflowExecutionStatus.Expired;
    public static void Validate(WorkflowExecution value, WorkflowVersion version)
    {
        value.Source.Validate();
        var graph = version.Graph.Read();
        if (value.TenantId != version.TenantId || value.WorkflowId != version.WorkflowId || value.Version != version.Version || value.GraphHash != version.Graph.Hash
            || !WorkflowGraphRules.Id(value.ExecutionId) || !Enum.IsDefined(value.Status) || value.Revision < 1
            || value.CoordinatorInstanceId == Guid.Empty || !graph.AllowedOrigins.Contains(value.Source.Kind)
            || graph.Nodes.Any(n => n.Kind == WorkflowNodeKind.AgentTask) && (value.CoordinatorInstanceId is null || !graph.CoordinatorInstanceIds.Contains(value.CoordinatorInstanceId.Value))
            || value.Source.Kind != WorkflowInvocationKind.HumanManual && value.CoordinatorInstanceId is not null && value.CoordinatorInstanceId != value.Source.InvokingInstanceId
            || value.CreatedAt.Offset != TimeSpan.Zero || value.DeadlineAt.Offset != TimeSpan.Zero || value.DeadlineAt <= value.CreatedAt
            || value.DeadlineAt - value.CreatedAt > TimeSpan.FromDays(30) || !WorkflowGraphRules.Hash(value.InputsHash) || !WorkflowGraphRules.Hash(value.Fingerprint)
            || value.ContentRetiredAt is null && Hash(value.InputsJson) != value.InputsHash || value.TerminalAt is not null && !Terminal(value.Status)
            || Terminal(value.Status) && value.TerminalAt is null)
            throw new ArgumentException("Invalid immutable workflow execution binding/projection.");
        if (value.Cancellation is { } cancellation && (!value.CancellationRequested || !WorkflowGraphRules.Id(cancellation.PrincipalId)
            || !WorkflowGraphRules.Hash(cancellation.Fingerprint) || cancellation.At < value.CreatedAt || cancellation.At.Offset != TimeSpan.Zero))
            throw new ArgumentException("Invalid qualified workflow cancellation receipt.");
        if (value.SourceClosure is { } closure && (value.Revision < 2 || closure.At < value.CreatedAt || closure.At.Offset != TimeSpan.Zero
            || closure.ReasonCode is not ("workflow_tenant_disabled" or "workflow_source_session_ended" or "workflow_source_identity_ended" or "workflow_source_automation_ended")))
            throw new ArgumentException("A technical source closure requires its exact lifecycle reason.");
        _ = WorkflowCatalogRules.RequestKey(value.RequestKey);
        var reserveSource = value.Source.Kind != WorkflowInvocationKind.HumanManual && graph.Nodes.Any(n => n.Kind == WorkflowNodeKind.AgentTask);
        if (reserveSource && (value.SourceBudget?.MaxSteps != graph.MaxTotalTaskSteps || value.SourceBudget?.DurationSeconds != graph.MaxTotalTaskSeconds)
            || !reserveSource && value.SourceBudget is not null) throw new ArgumentException("Non-human workflow work reserves its complete declared source budget once.");
        if (value.ContentRetiredAt is { } retired)
        {
            if (!Terminal(value.Status) || value.Source.SessionId is null || retired.Offset != TimeSpan.Zero || retired < value.TerminalAt || value.InputsJson != "{}")
                throw new ArgumentException("Content retirement preserves only a terminal source-bound technical receipt.");
            return;
        }
        using var input = JsonDocument.Parse(value.InputsJson);
        if (Inputs(graph, input.RootElement) != value.InputsJson) throw new ArgumentException("Workflow inputs must be canonical.");
    }
    public static string Inputs(WorkflowGraph graph, JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object || Encoding.UTF8.GetByteCount(input.GetRawText()) > 16 * 1024)
            throw new ArgumentException("Use a bounded typed workflow input object.");
        var fields = input.EnumerateObject().ToArray();
        if (fields.Length > WorkflowGraphRules.MaxInputs || fields.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != fields.Length
            || fields.Any(p => !graph.Inputs.Any(i => i.Key == p.Name))) throw new ArgumentException("Unknown or duplicate workflow input field.");
        var values = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var schema in graph.Inputs)
        {
            if (!input.TryGetProperty(schema.Key, out var field)) { if (schema.Required) throw new ArgumentException("Missing required input: " + schema.Key); continue; }
            object? value = schema.Kind switch {
                WorkflowInputKind.Text when field.ValueKind == JsonValueKind.String && field.GetString() is { } text && text.Length <= schema.MaxLength
                    && (!schema.Required || !string.IsNullOrWhiteSpace(text)) => text,
                WorkflowInputKind.Choice when field.ValueKind == JsonValueKind.String && schema.Choices!.Contains(field.GetString(), StringComparer.Ordinal) => field.GetString(),
                WorkflowInputKind.Boolean when field.ValueKind is JsonValueKind.True or JsonValueKind.False => field.GetBoolean(),
                WorkflowInputKind.Number when field.ValueKind == JsonValueKind.Number && field.TryGetDecimal(out var number)
                    && number is >= -1_000_000_000_000m and <= 1_000_000_000_000m => number,
                WorkflowInputKind.Date when field.ValueKind == JsonValueKind.String && DateOnly.TryParseExact(field.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                _ => throw new ArgumentException("Invalid typed input: " + schema.Key)
            };
            values.Add(schema.Key, value);
        }
        return JsonSerializer.Serialize(values);
    }
    public static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    public static void Operation(WorkflowExecution execution, WorkflowStepOperation value, WorkflowVersion version)
    {
        var node = version.Graph.Read().Nodes.SingleOrDefault(n => n.Id == value.NodeId);
        if (value.TenantId != execution.TenantId || value.ExecutionId != execution.ExecutionId || value.GraphHash != execution.GraphHash
            || node is null || node.Kind != value.Kind || value.Kind is WorkflowNodeKind.Start or WorkflowNodeKind.EndFailure
            || value.Attempt != 1 || !WorkflowGraphRules.Id(value.OperationId) || !WorkflowGraphRules.Text(value.BookmarkId, 128)
            || value.Revision < 1 || !Enum.IsDefined(value.Status) || value.CreatedAt.Offset != TimeSpan.Zero
            || value.CreatedAt < execution.CreatedAt || value.TaskId == Guid.Empty || value.WorkerRunId == Guid.Empty
            || value.EvaluationAttemptId == Guid.Empty || value.DecisionActorId == Guid.Empty
            || (value.Status >= WorkflowOperationStatus.Settled) != (value.SettledAt is not null)
            || value.Status >= WorkflowOperationStatus.Settled && !WorkflowGraphRules.Text(value.Outcome, 40))
            throw new ArgumentException("Workflow operation must match its immutable node/bookmark/source.");
    }
}
