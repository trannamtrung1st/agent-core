using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentCore.Domain.Conversation;

namespace AgentCore.Domain.Tasks;

public enum DelegatedTaskState { PendingDependencies, Queued, Running, Waiting, Blocked, Succeeded, Failed, Cancelled, NeedsAttention }
public enum TaskActorKind { Agent, Human, WorkflowContinuation }
public enum AgentMessageKind { Progress, Question, Answer, Note, HumanGuidance }
public enum AgentMessageDelivery { NotificationPending, DeliveredToAuthorizedInbox, Acknowledged, Undeliverable, ActivationAdmitted }

/// <summary>Acceptance policy is independent of tool capability, IAM and execution authority.</summary>
public sealed record AgentCooperationPolicy(bool AcceptTasks = false, bool AcceptDirectMessages = false,
    bool AllowDirectMessageWake = false, int MaxConcurrentTasks = 2)
{
    public static AgentCooperationPolicy Disabled { get; } = new();
    public void Validate()
    {
        if (MaxConcurrentTasks is < 1 or > 8 || AllowDirectMessageWake && !AcceptDirectMessages)
            throw new ArgumentException("Cooperation requires bounded concurrency and message acceptance before wake.");
    }
}

public static class TaskLimits
{
    public const int ObjectiveCharacters = 4_000, TitleCharacters = 80, MessageCharacters = 2_000, ResultCharacters = 2_400;
    public const int MaxDependencies = 8, MaxChildrenPerRun = 2, MaxLiveDescendants = 8, MaxDepth = 3;
    public const int MaxPage = 100, MaxMessagePage = 50;
    public static readonly TimeSpan MaxLifetime = TimeSpan.FromHours(24);
    public static void Id(Guid value, string name) { if (value == Guid.Empty) throw new ArgumentException($"{name} is required."); }
    public static string Text(string value, int characters, string name)
    {
        ArgumentNullException.ThrowIfNull(value);
        value = value.Trim();
        if (value.Length is 0 || value.Length > characters || Encoding.UTF8.GetByteCount(value) > characters * 4
            || value.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t')))
            throw new ArgumentException($"{name} must contain 1–{characters} characters of bounded text.");
        // Reject invalid UTF-16 rather than hash replacement bytes that conflate different inputs.
        try { _ = new UTF8Encoding(false, true).GetByteCount(value); }
        catch (EncoderFallbackException exception) { throw new ArgumentException($"{name} contains invalid Unicode.", exception); }
        return value;
    }
    public static string Preview(string value, int limit)
    {
        var end = Math.Min(value.Length, limit);
        if (end > 0 && end < value.Length && char.IsHighSurrogate(value[end - 1])) end--;
        return value[..end];
    }
    public static string Key(string value) => Text(value, 128, "Request key");
    public static string Hash<T>(T value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value))).ToLowerInvariant();
    public static bool Terminal(DelegatedTaskState state) => state is DelegatedTaskState.Blocked or DelegatedTaskState.Succeeded
        or DelegatedTaskState.Failed or DelegatedTaskState.Cancelled or DelegatedTaskState.NeedsAttention;
}

/// <summary>Closed immutable intent. Authoritative identities and lineage are never accepted here.</summary>
public sealed record TaskRequest
{
    public TaskRequest(string objective, string? title, IReadOnlyList<Guid> candidateInstanceIds, bool eligible,
        Guid? spaceId, Guid? environmentId, IReadOnlyList<Guid> dependsOnTaskIds, bool reportOnCompletion,
        DateTimeOffset? deadlineAt, string requestKey, Guid? retryOfTaskId = null, string? retiredFingerprint = null)
    {
        if (retiredFingerprint is not null && (retiredFingerprint.Length != 64 || !retiredFingerprint.All(Uri.IsHexDigit)
            || objective != "[retired]" || title != "[retired]")) throw new ArgumentException("A content tombstone preserves only its original request hash.");
        RetiredFingerprint = retiredFingerprint;
        Objective = TaskLimits.Text(objective, TaskLimits.ObjectiveCharacters, "Objective");
        Title = title is null ? TaskLimits.Preview(Objective, TaskLimits.TitleCharacters) : TaskLimits.Text(title, TaskLimits.TitleCharacters, "Title");
        if (candidateInstanceIds.Count is < 1 or > 100 || !eligible && candidateInstanceIds.Count != 1
            || candidateInstanceIds.Any(id => id == Guid.Empty) || candidateInstanceIds.Distinct().Count() != candidateInstanceIds.Count)
            throw new ArgumentException("Select one explicit target or a bounded distinct eligible candidate set.");
        if (dependsOnTaskIds.Count > TaskLimits.MaxDependencies || dependsOnTaskIds.Any(id => id == Guid.Empty)
            || dependsOnTaskIds.Distinct().Count() != dependsOnTaskIds.Count) throw new ArgumentException("Invalid task dependencies.");
        if (spaceId == Guid.Empty || environmentId == Guid.Empty || environmentId is not null && spaceId is null || retryOfTaskId == Guid.Empty)
            throw new ArgumentException("Invalid task context selection.");
        if (deadlineAt is { Offset: var offset } && offset != TimeSpan.Zero) throw new ArgumentException("Deadline must use UTC.");
        CandidateInstanceIds = Array.AsReadOnly(candidateInstanceIds.Order().ToArray()); Eligible = eligible;
        SpaceId = spaceId; EnvironmentId = environmentId; DependsOnTaskIds = Array.AsReadOnly(dependsOnTaskIds.Order().ToArray());
        ReportOnCompletion = reportOnCompletion; DeadlineAt = deadlineAt; RequestKey = TaskLimits.Key(requestKey); RetryOfTaskId = retryOfTaskId;
    }
    public string Objective { get; }
    public string Title { get; }
    public IReadOnlyList<Guid> CandidateInstanceIds { get; }
    public bool Eligible { get; }
    public Guid? SpaceId { get; }
    public Guid? EnvironmentId { get; }
    public IReadOnlyList<Guid> DependsOnTaskIds { get; }
    public bool ReportOnCompletion { get; }
    public DateTimeOffset? DeadlineAt { get; }
    public string RequestKey { get; }
    public Guid? RetryOfTaskId { get; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? RetiredFingerprint { get; }
    public TaskRequest RetireContent() => RetiredFingerprint is not null ? this : new("[retired]", "[retired]", CandidateInstanceIds, Eligible,
        SpaceId, EnvironmentId, DependsOnTaskIds, ReportOnCompletion, DeadlineAt, RequestKey, RetryOfTaskId, Fingerprint());
    public string Fingerprint() => RetiredFingerprint ?? TaskLimits.Hash(new { Objective, Title, CandidateInstanceIds, Eligible, SpaceId, EnvironmentId,
        DependsOnTaskIds, ReportOnCompletion, DeadlineAt, RetryOfTaskId });
}

/// <summary>Saved trusted provenance. A human command has no invented source Run.</summary>
public sealed record TaskSource(Guid TenantId, Guid? SessionId, Guid InstanceId, Guid PrincipalId, TaskActorKind ActorKind,
    Guid? RunId, Guid? ActivationId, Guid? HumanCommandId, Guid? OriginalInitiatorPrincipalId,
    Guid RootActivationId, Guid? ParentTaskId, int Depth,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] WorkflowTaskContinuation? Workflow = null)
{
    public void Validate()
    {
        foreach (var id in new[] { TenantId, InstanceId, PrincipalId, RootActivationId }) TaskLimits.Id(id, "Task source identity");
        if (!Enum.IsDefined(ActorKind) || Depth is < 1 or > TaskLimits.MaxDepth || ParentTaskId == Guid.Empty
            || OriginalInitiatorPrincipalId == Guid.Empty || (Depth > 1) != (ParentTaskId is not null)
            || ActorKind != TaskActorKind.WorkflowContinuation && (SessionId is null || SessionId == Guid.Empty || Workflow is not null)
            || ActorKind == TaskActorKind.WorkflowContinuation && (SessionId is not null || RunId is not null || ActivationId is not null
                || HumanCommandId is not null || ParentTaskId is not null || Depth != 1 || Workflow is null)
            || ActorKind == TaskActorKind.Agent && (RunId is null || RunId == Guid.Empty || ActivationId is null || ActivationId == Guid.Empty || HumanCommandId is not null)
            || ActorKind == TaskActorKind.Human && (RunId is not null || ActivationId is not null || HumanCommandId is null || HumanCommandId == Guid.Empty
                || OriginalInitiatorPrincipalId != PrincipalId || ParentTaskId is not null))
            throw new ArgumentException("Task source requires real causal identity and bounded delegation depth.");
        Workflow?.Validate();
    }
    public Guid RequireSessionId() => SessionId ?? throw new InvalidOperationException("Workflow continuation has no source Session.");
    public string OperationScope => ActorKind switch {
        TaskActorKind.Agent => $"run:{RunId:D}", TaskActorKind.Human => $"human:{PrincipalId:D}:session:{SessionId:D}",
        TaskActorKind.WorkflowContinuation when Workflow is { } workflow => $"workflow:{workflow.ExecutionId:D}:{workflow.OperationId:D}",
        _ => throw new ArgumentException("A valid Task source is required.")
    };
}

/// <summary>Accepted process/step witness; it is neither a human command nor an invented claimed Run.</summary>
public sealed record WorkflowTaskContinuation(Guid WorkflowId, Guid ExecutionId, int Version, string GraphHash,
    string NodeId, int Attempt, Guid OperationId,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] Guid? OriginalSessionId = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] AgentCore.Domain.Workflows.WorkflowEventCausation? Causation = null)
{
    public void Validate()
    {
        foreach (var id in new[] { WorkflowId, ExecutionId, OperationId }) TaskLimits.Id(id, "Workflow Task source");
        if (Version < 1 || Attempt != 1 || OriginalSessionId == Guid.Empty || GraphHash is not { Length: 64 } || !GraphHash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f')
            || NodeId is not { Length: > 0 and <= 64 } || !NodeId.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_'))
            throw new ArgumentException("Invalid immutable workflow Task source.");
        Causation?.Validate();
    }
}

public sealed record TaskBudgetReservation(int MaxSteps, int DurationSeconds, int RootMaxSteps, int RootDurationSeconds,
    int ReportMaxSteps = 0, int ReportDurationSeconds = 0)
{
    public void Validate()
    {
        if (MaxSteps is < 8 or > 144 || DurationSeconds is < 60 or > 900 || ReportMaxSteps is not (0 or 8)
            || ReportDurationSeconds != (ReportMaxSteps == 0 ? 0 : 60) || RootMaxSteps < MaxSteps + ReportMaxSteps || RootMaxSteps > 144
            || RootDurationSeconds < DurationSeconds + ReportDurationSeconds || RootDurationSeconds > 900)
            throw new ArgumentException("Task and report reservations must stay within the frozen root allowance.");
    }
}
public sealed record TaskAssignment(Guid InstanceId, Guid PrincipalId, Guid SpaceId, Guid? EnvironmentId,
    long InstanceRevision, string ConfigurationHash, string RouteReason, AgentRunModelPin? Model = null, long EnvironmentSelectionEpoch = 0, string? DisplayName = null,
    TaskBudgetReservation? Budget = null)
{
    public void Validate()
    {
        Budget?.Validate();
        foreach (var id in new[] { InstanceId, PrincipalId, SpaceId }) TaskLimits.Id(id, "Worker identity");
        if (EnvironmentId == Guid.Empty || EnvironmentSelectionEpoch < 0 || EnvironmentId is null && EnvironmentSelectionEpoch != 0 || InstanceRevision < 1 || ConfigurationHash.Length != 64 || !ConfigurationHash.All(Uri.IsHexDigit)
            || RouteReason is not ("explicit" or "eligible-fit-capacity-id-v1")) throw new ArgumentException("Invalid immutable worker assignment.");
    }
}

/// <summary>Coordination facts only; actual execution remains with the ordinary AgentRun.</summary>
public sealed record DelegatedTask
{
    public DelegatedTask(Guid taskId, TaskSource source, TaskRequest request, TaskAssignment assignment, DelegatedTaskState state,
        long revision, DateTimeOffset createdAt, DateTimeOffset updatedAt, DateTimeOffset deadlineAt,
        Guid? workerSessionId = null, Guid? initialWorkerRunId = null, Guid? terminalRunId = null,
        string? result = null, string? reason = null, bool cancellationRequested = false, DateTimeOffset? contentRetiredAt = null)
    {
        TaskLimits.Id(taskId, "Task"); source.Validate(); assignment.Validate();
        if (!request.CandidateInstanceIds.Contains(assignment.InstanceId) || request.SpaceId is { } space && space != assignment.SpaceId
            || request.EnvironmentId != assignment.EnvironmentId || !Enum.IsDefined(state) || revision < 1
            || createdAt.Offset != TimeSpan.Zero || updatedAt.Offset != TimeSpan.Zero || deadlineAt.Offset != TimeSpan.Zero
            || updatedAt < createdAt || deadlineAt <= createdAt || deadlineAt - createdAt > TaskLimits.MaxLifetime
            || request.DeadlineAt is { } requested && requested != deadlineAt
            || workerSessionId == Guid.Empty || initialWorkerRunId == Guid.Empty || terminalRunId == Guid.Empty
            || (workerSessionId is null) != (initialWorkerRunId is null)
            || state is not (DelegatedTaskState.PendingDependencies or DelegatedTaskState.Blocked) && workerSessionId is null
            || state == DelegatedTaskState.PendingDependencies && (workerSessionId is not null || request.DependsOnTaskIds.Count == 0)
            || state == DelegatedTaskState.Succeeded && (terminalRunId is null || result is null)
            || !TaskLimits.Terminal(state) && (terminalRunId is not null || result is not null))
            throw new ArgumentException("Task coordination facts are inconsistent.");
        if ((request.RetiredFingerprint is not null) != (contentRetiredAt is not null) || contentRetiredAt is { } retired
            && (retired.Offset != TimeSpan.Zero || retired < updatedAt || result is not null && result != "[retired]"))
            throw new ArgumentException("Retired Task content must be a factual tombstone.");
        ContentRetiredAt = contentRetiredAt;
        TaskId = taskId; Source = source; Request = request; Assignment = assignment; State = state; Revision = revision;
        CreatedAt = createdAt; UpdatedAt = updatedAt; DeadlineAt = deadlineAt; WorkerSessionId = workerSessionId;
        InitialWorkerRunId = initialWorkerRunId; TerminalRunId = terminalRunId;
        Result = result is null ? null : TaskLimits.Text(result, TaskLimits.ResultCharacters, "Task result");
        Reason = reason is null ? null : TaskLimits.Text(reason, 160, "Task reason"); CancellationRequested = cancellationRequested;
    }
    public Guid TaskId { get; }
    public TaskSource Source { get; }
    public TaskRequest Request { get; }
    public TaskAssignment Assignment { get; }
    public DelegatedTaskState State { get; }
    public long Revision { get; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset UpdatedAt { get; }
    public DateTimeOffset DeadlineAt { get; }
    public Guid? WorkerSessionId { get; }
    public Guid? InitialWorkerRunId { get; }
    public Guid? TerminalRunId { get; }
    public string? Result { get; }
    public string? Reason { get; }
    public bool CancellationRequested { get; }

    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? ContentRetiredAt { get; }
    public DelegatedTask RetireContent(DateTimeOffset at) => ContentRetiredAt is not null ? this : new(TaskId, Source, Request.RetireContent(), Assignment,
        State, checked(Revision + 1), CreatedAt, UpdatedAt, DeadlineAt, WorkerSessionId, InitialWorkerRunId, TerminalRunId,
        Result is null ? null : "[retired]", Reason, CancellationRequested, at);

    public DelegatedTask Advance(DelegatedTaskState state, DateTimeOffset at, Guid? workerSessionId = null, Guid? workerRunId = null,
        Guid? terminalRunId = null, string? result = null, string? reason = null, bool? cancellationRequested = null)
    {
        if (ContentRetiredAt is not null || TaskLimits.Terminal(State)) throw new InvalidOperationException("A settled task cannot be resurrected; create a new Task.");
        if (State == DelegatedTaskState.PendingDependencies && state is not (DelegatedTaskState.Queued or DelegatedTaskState.Blocked)
            || State != DelegatedTaskState.PendingDependencies && state is DelegatedTaskState.PendingDependencies or DelegatedTaskState.Blocked
            || workerSessionId is not null && WorkerSessionId is not null && workerSessionId != WorkerSessionId
            || workerRunId is not null && InitialWorkerRunId is not null && workerRunId != InitialWorkerRunId
            || CancellationRequested && cancellationRequested == false) throw new InvalidOperationException("Invalid task transition.");
        return new(TaskId, Source, Request, Assignment, state, checked(Revision + 1), CreatedAt, at, DeadlineAt,
            workerSessionId ?? WorkerSessionId, workerRunId ?? InitialWorkerRunId, terminalRunId, result, reason, cancellationRequested ?? CancellationRequested);
    }
}
