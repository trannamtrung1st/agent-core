namespace AgentCore.Domain.Triggers;

public enum AutomationExecutionTargetKind { BackgroundSession, ExistingSession }

/// <summary>Exact destination bound by Core at authoring; never resolved from an attached transport.</summary>
public sealed record AutomationExecutionTarget
{
    public AutomationExecutionTarget(AutomationExecutionTargetKind kind, Guid? sessionId = null)
    {
        if (!Enum.IsDefined(kind) || (kind == AutomationExecutionTargetKind.ExistingSession) != (sessionId is not null)
            || sessionId == Guid.Empty) throw new ArgumentException("Execution target requires exactly one valid destination.");
        Kind = kind;
        SessionId = sessionId;
    }
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
        if (target.Kind == AutomationExecutionTargetKind.ExistingSession && SessionId is not null)
            throw new ArgumentException("An existing conversation already receives its response; separate reporting is invalid.");
    }
}
