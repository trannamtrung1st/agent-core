namespace AgentCore.Domain.Triggers;

public enum AutomationExecutionTargetKind { BackgroundSession, ExistingSession, StartWorkflow }

/// <summary>Exact destination bound by Core at authoring; never resolved from an attached transport.</summary>
public sealed record AutomationExecutionTarget
{
    public AutomationExecutionTarget(AutomationExecutionTargetKind kind, Guid? sessionId = null, AutomationWorkflowStart? workflow = null)
    {
        if (!Enum.IsDefined(kind) || (kind == AutomationExecutionTargetKind.ExistingSession) != (sessionId is not null)
            || sessionId == Guid.Empty || (kind == AutomationExecutionTargetKind.StartWorkflow) != (workflow is not null)) throw new ArgumentException("Execution target requires exactly one valid destination.");
        Kind = kind;
        SessionId = sessionId; Workflow = workflow;
    }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public AutomationWorkflowStart? Workflow { get; }
    public AutomationExecutionTargetKind Kind { get; }
    public Guid? SessionId { get; }
    public static AutomationExecutionTarget Background { get; } = new(AutomationExecutionTargetKind.BackgroundSession);
    public static AutomationExecutionTarget Existing(Guid sessionId) => new(AutomationExecutionTargetKind.ExistingSession, sessionId);
}

public sealed record AutomationCompletionDelivery
{
    public AutomationCompletionDelivery(Guid? sessionId = null)
    {
        if (sessionId == Guid.Empty) throw new ArgumentException("Completion destination must be a valid Session.");
        SessionId = sessionId;
    }
    public Guid? SessionId { get; }
    public static AutomationCompletionDelivery None { get; } = new();
    public static AutomationCompletionDelivery ToSession(Guid sessionId) => new(sessionId);
    public void ValidateFor(AutomationExecutionTarget target)
    {
        if (target.Kind != AutomationExecutionTargetKind.BackgroundSession && SessionId is not null)
            throw new ArgumentException("An existing conversation already receives its response; separate reporting is invalid.");
    }
}

/// <summary>Saved immutable destination; inputs are validated against this exact version before every admission.</summary>
[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record AutomationWorkflowStart
{
    public AutomationWorkflowStart(Guid workflowId, int version, string graphHash, string inputsJson)
    {
        if (workflowId == Guid.Empty || version < 1 || !Workflows.WorkflowGraphRules.Hash(graphHash)
            || System.Text.Encoding.UTF8.GetByteCount(inputsJson) > 16384) throw new ArgumentException("Invalid Workflow destination.");
        using var input = System.Text.Json.JsonDocument.Parse(inputsJson);
        if (input.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) throw new ArgumentException("Use typed Workflow inputs.");
        WorkflowId = workflowId; Version = version; GraphHash = graphHash; InputsJson = inputsJson;
    }
    public Guid WorkflowId { get; }
    public int Version { get; }
    public string GraphHash { get; }
    public string InputsJson { get; }
}
