namespace AgentCore.Domain.Conversation;

public enum SessionOriginKind
{
    UserChat,
    ImmediateBackground,
    AutomationOccurrence,
    ManualBackground,
    SourceOccurrence
}

[Flags]
public enum SessionSurface
{
    None = 0,
    ChatList = 1,
    BackgroundWork = 2
}

/// <summary>Creation provenance is independent of lifecycle and mutable presentation.</summary>
public sealed record SessionOrigin
{
    public static SessionOrigin UserChat { get; } = new(SessionOriginKind.UserChat);

    public SessionOrigin(SessionOriginKind kind, Guid? originatingSessionId = null,
        Guid? originatingAgentRunId = null, Guid? initialBackgroundAgentRunId = null,
        Guid? automationId = null, Guid? triggerOccurrenceId = null, bool reportCompletionToOrigin = false, string? initialTitle = null)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentException("Session origin kind is invalid.");
        AgentRunText.RequireOptionalId(originatingSessionId, "Originating Session");
        AgentRunText.RequireOptionalId(originatingAgentRunId, "Originating run");
        AgentRunText.RequireOptionalId(initialBackgroundAgentRunId, "Initial background run");
        AgentRunText.RequireOptionalId(automationId, "Automation");
        AgentRunText.RequireOptionalId(triggerOccurrenceId, "Occurrence");
        if (kind == SessionOriginKind.UserChat
            && (originatingSessionId is not null || originatingAgentRunId is not null
                || initialBackgroundAgentRunId is not null || automationId is not null || triggerOccurrenceId is not null))
            throw new ArgumentException("User chat has no background provenance.");
        if (kind != SessionOriginKind.UserChat && initialBackgroundAgentRunId is null)
            throw new ArgumentException("Background origin requires its initial run identity.");
        if (kind == SessionOriginKind.ImmediateBackground
            && (originatingSessionId is null || originatingAgentRunId is null || automationId is not null || triggerOccurrenceId is not null))
            throw new ArgumentException("Immediate work requires parent identity and has no Automation.");
        if (kind == SessionOriginKind.AutomationOccurrence && (automationId is null || triggerOccurrenceId is null))
            throw new ArgumentException("An Automation origin requires both Automation and occurrence.");
        if (kind == SessionOriginKind.SourceOccurrence && (automationId is not null || triggerOccurrenceId is null))
            throw new ArgumentException("A source occurrence requires an occurrence without an Automation.");
        if (kind is not (SessionOriginKind.AutomationOccurrence or SessionOriginKind.SourceOccurrence)
            && (automationId is not null || triggerOccurrenceId is not null))
            throw new ArgumentException("Only occurrence origins carry occurrence provenance.");
        if (reportCompletionToOrigin && (kind is not (SessionOriginKind.ImmediateBackground or SessionOriginKind.AutomationOccurrence) || originatingSessionId is null))
            throw new ArgumentException("Only explicitly parent-linked work can report back.");
        Kind = kind;
        OriginatingSessionId = originatingSessionId;
        OriginatingAgentRunId = originatingAgentRunId;
        InitialBackgroundAgentRunId = initialBackgroundAgentRunId;
        AutomationId = automationId;
        TriggerOccurrenceId = triggerOccurrenceId;
        ReportCompletionToOrigin = reportCompletionToOrigin;
        InitialTitle = initialTitle is null ? null : AgentRunText.RequireUtf8(initialTitle.Trim(), 800, "Initial title");
    }

    public SessionOriginKind Kind { get; }
    public Guid? OriginatingSessionId { get; }
    public Guid? OriginatingAgentRunId { get; }
    public Guid? InitialBackgroundAgentRunId { get; }
    public Guid? AutomationId { get; }
    public Guid? TriggerOccurrenceId { get; }
    public bool ReportCompletionToOrigin { get; }
    public string? InitialTitle { get; }
    public SessionSurface InitialSurface => Kind == SessionOriginKind.UserChat ? SessionSurface.ChatList : SessionSurface.BackgroundWork;

    public bool MayReportCompletion(Guid agentRunId) => ReportCompletionToOrigin && InitialBackgroundAgentRunId == agentRunId;

    public static SessionSurface ContinueInChat(SessionSurface current)
    {
        if ((current & ~(SessionSurface.ChatList | SessionSurface.BackgroundWork)) != 0)
            throw new ArgumentException("Session surface flags are invalid.");
        return current | SessionSurface.ChatList;
    }
}
