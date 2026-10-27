using AgentCore.Domain.Tasks;

namespace AgentCore.Domain.Security;

/// <summary>Frozen assignment and reserved graph identities, never an admitted worker or a transferable grant.</summary>
public sealed record PreparedTaskDelegation(DelegatedTask Task, Guid? SourceGeneration,
    Guid? WorkerEntryId, Guid? WorkerActivationId, Guid? WorkerResponseId,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] Guid? ReservedWorkerRunId = null)
{
    // Approval decisions reload independently. Nested immutable request collections must
    // compare by value rather than their deserialized array identity.
    public bool Equals(PreparedTaskDelegation? other) => other is not null
        && SourceGeneration == other.SourceGeneration && WorkerEntryId == other.WorkerEntryId
        && WorkerActivationId == other.WorkerActivationId && WorkerResponseId == other.WorkerResponseId && ReservedWorkerRunId == other.ReservedWorkerRunId
        && System.Text.Json.JsonSerializer.Serialize(Task) == System.Text.Json.JsonSerializer.Serialize(other.Task);
    public override int GetHashCode() => HashCode.Combine(TaskLimits.Hash(Task), SourceGeneration, WorkerEntryId, WorkerActivationId, WorkerResponseId, ReservedWorkerRunId);
    // This identifies a future worker for a detached human approval. It is never a
    // source Run, an admitted Run, or proof that a worker exists before readiness.
    [System.Text.Json.Serialization.JsonIgnore]
    public Guid PlannedWorkerRunId => Task.InitialWorkerRunId ?? ReservedWorkerRunId ?? throw new ArgumentException("A detached human approval requires its reserved future worker identity.");
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Task);
        Task.Source.Validate(); Task.Assignment.Validate();
        if (Task.ContentRetiredAt is not null || Task.Request.RetiredFingerprint is not null || Task.Revision != 1 || Task.State is not (DelegatedTaskState.Queued or DelegatedTaskState.PendingDependencies)
            || Task.CreatedAt != Task.UpdatedAt || Task.TerminalRunId is not null || Task.Result is not null || Task.Reason is not null || Task.CancellationRequested
            || Task.Assignment.Model is null || Task.Assignment.Budget is null
            || Task.Source.ActorKind == TaskActorKind.Agent && (SourceGeneration is null || SourceGeneration == Guid.Empty)
            || Task.Source.ActorKind == TaskActorKind.Human && SourceGeneration is not null)
            throw new ArgumentException("Task consent must bind the real source and an uncommitted initial assignment.");
        var reserved = new[] { Task.WorkerSessionId, Task.InitialWorkerRunId, WorkerEntryId, WorkerActivationId, WorkerResponseId };
        if (Task.State == DelegatedTaskState.PendingDependencies ? reserved.Any(id => id is not null)
            : reserved.Any(id => id is null || id == Guid.Empty) || reserved.Distinct().Count() != reserved.Length
                || reserved.Any(id => id == Task.TaskId || id == Task.Source.SessionId || id == Task.Source.RunId || id == Task.Source.HumanCommandId)
                || Task.Source.ActorKind == TaskActorKind.Human && WorkerActivationId != Task.Source.RootActivationId
                || Task.Source.ActorKind == TaskActorKind.Agent && WorkerActivationId == Task.Source.RootActivationId)
            throw new ArgumentException("Task consent reserves exact worker identities without creating a speculative dependency Run.");
        if (Task.Source.ActorKind == TaskActorKind.Human && Task.State == DelegatedTaskState.PendingDependencies
            ? ReservedWorkerRunId is null || ReservedWorkerRunId == Guid.Empty || ReservedWorkerRunId == Task.TaskId || ReservedWorkerRunId == Task.Source.SessionId || ReservedWorkerRunId == Task.Source.RootActivationId || ReservedWorkerRunId == Task.Source.HumanCommandId
            : ReservedWorkerRunId is not null)
            throw new ArgumentException("Only a pending human assignment reserves a detached future worker Run.");
    }
}
