namespace AgentCore.Domain.Conversation;

public enum ExecutionOrigin { UserMessage, Reserved1, ConfigurationContinuation, CompletionReport, ScheduledOccurrence, ApplicationEvent, CoreEvent, ManualInvocation, Initiative, ApprovalContinuation, ContextContinuation, GoalContinuation, DelegatedTask, TaskCompletion, AgentMessage }

/// <summary>Immutable trusted references. Attribution never carries permission snapshots or login credentials.</summary>
public sealed record TrustedExecutionContext
{
    public TrustedExecutionContext(Guid tenantId, Guid agentPrincipalId, Guid? originalInitiatorPrincipalId,
        Guid sessionId, Guid runId, Guid activationId, Guid rootActivationId, Guid? parentRunId,
        Guid? parentActivationId, ExecutionOrigin origin)
    {
        if (new[] { tenantId, agentPrincipalId, sessionId, runId, activationId, rootActivationId }.Any(id => id == Guid.Empty)
            || !Enum.IsDefined(origin) || origin == ExecutionOrigin.Reserved1) throw new ArgumentException("Execution context requires concrete trusted identity.");
        AgentRunText.RequireOptionalId(originalInitiatorPrincipalId, "Original initiator");
        AgentRunText.RequireOptionalId(parentRunId, "Parent Run");
        AgentRunText.RequireOptionalId(parentActivationId, "Parent Activation");
        var continuation = origin == ExecutionOrigin.DelegatedTask && parentRunId is not null || origin is ExecutionOrigin.ConfigurationContinuation or ExecutionOrigin.CompletionReport or ExecutionOrigin.TaskCompletion or ExecutionOrigin.ApprovalContinuation or ExecutionOrigin.ContextContinuation or ExecutionOrigin.GoalContinuation;
        if (continuation != (parentRunId is not null && parentActivationId is not null)
            || !continuation && (parentRunId is not null || parentActivationId is not null || rootActivationId != activationId)
            || continuation && (rootActivationId == activationId || parentActivationId == activationId || parentRunId == runId)
            || origin == ExecutionOrigin.UserMessage && originalInitiatorPrincipalId is null
            || origin is ExecutionOrigin.ScheduledOccurrence or ExecutionOrigin.ApplicationEvent or ExecutionOrigin.CoreEvent or ExecutionOrigin.Initiative && originalInitiatorPrincipalId is not null)
            throw new ArgumentException("Execution origin and causal identity disagree.");
        TenantId = tenantId; AgentPrincipalId = agentPrincipalId; OriginalInitiatorPrincipalId = originalInitiatorPrincipalId;
        SessionId = sessionId; RunId = runId; ActivationId = activationId; RootActivationId = rootActivationId;
        ParentRunId = parentRunId; ParentActivationId = parentActivationId; Origin = origin;
    }
    public Guid TenantId { get; }
    public Guid AgentPrincipalId { get; }
    public Guid? OriginalInitiatorPrincipalId { get; }
    public Guid SessionId { get; }
    public Guid RunId { get; }
    public Guid ActivationId { get; }
    public Guid RootActivationId { get; }
    public Guid? ParentRunId { get; }
    public Guid? ParentActivationId { get; }
    public ExecutionOrigin Origin { get; }
}
