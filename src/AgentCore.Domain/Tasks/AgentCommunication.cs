namespace AgentCore.Domain.Tasks;

/// <summary>Direct threads have exactly two distinct Agents. Task roles may share an Agent but never a Session.</summary>
public sealed record AgentThread
{
    public AgentThread(Guid tenantId, Guid threadId, Guid firstInstanceId, Guid secondInstanceId, Guid? taskId,
        Guid? sourceSessionId, Guid? workerSessionId, DateTimeOffset createdAt)
    {
        foreach (var id in new[] { tenantId, threadId, firstInstanceId, secondInstanceId }) TaskLimits.Id(id, "Thread identity");
        if (createdAt.Offset != TimeSpan.Zero || taskId == Guid.Empty || sourceSessionId == Guid.Empty || workerSessionId == Guid.Empty
            || taskId is null && (firstInstanceId == secondInstanceId || sourceSessionId is not null || workerSessionId is not null)
            || taskId is not null && (sourceSessionId is null || workerSessionId is null || sourceSessionId == workerSessionId))
            throw new ArgumentException("A thread requires two real Agent roles and exact task Session witnesses.");
        TenantId = tenantId; ThreadId = threadId; TaskId = taskId; SourceSessionId = sourceSessionId; WorkerSessionId = workerSessionId;
        // Task roles preserve source/worker order. Direct pairs have a stable unique order.
        FirstInstanceId = taskId is null && firstInstanceId.CompareTo(secondInstanceId) > 0 ? secondInstanceId : firstInstanceId;
        SecondInstanceId = taskId is null && firstInstanceId.CompareTo(secondInstanceId) > 0 ? firstInstanceId : secondInstanceId;
        CreatedAt = createdAt;
    }
    public Guid TenantId { get; }
    public Guid ThreadId { get; }
    public Guid FirstInstanceId { get; }
    public Guid SecondInstanceId { get; }
    public Guid? TaskId { get; }
    public Guid? SourceSessionId { get; }
    public Guid? WorkerSessionId { get; }
    public DateTimeOffset CreatedAt { get; }
    public bool Includes(Guid instance) => instance == FirstInstanceId || instance == SecondInstanceId;
    public Guid Recipient(Guid instance, Guid session)
    {
        if (!Includes(instance)) throw new InvalidOperationException("Sender is not a participant.");
        if (TaskId is null) return instance == FirstInstanceId ? SecondInstanceId : FirstInstanceId;
        if (session == SourceSessionId && instance == FirstInstanceId) return SecondInstanceId;
        if (session == WorkerSessionId && instance == SecondInstanceId) return FirstInstanceId;
        throw new InvalidOperationException("Task sender requires its actual source or worker Session.");
    }
}

/// <summary>Immutable inert evidence; sender identity is derived from an authenticated server witness.</summary>
public sealed record AgentMessage
{
    public AgentMessage(Guid tenantId, Guid threadId, Guid messageId, long sequence, TaskActorKind senderKind,
        Guid senderPrincipalId, Guid? senderInstanceId, Guid? sourceSessionId, Guid? sourceRunId, Guid recipientInstanceId,
        AgentMessageKind kind, string text, string requestKey, string requestHash, DateTimeOffset createdAt, DateTimeOffset? contentRetiredAt = null)
    {
        foreach (var id in new[] { tenantId, threadId, messageId, senderPrincipalId, recipientInstanceId }) TaskLimits.Id(id, "Message identity");
        if (sequence < 1 || !Enum.IsDefined(senderKind) || !Enum.IsDefined(kind) || createdAt.Offset != TimeSpan.Zero
            || senderInstanceId == Guid.Empty || sourceSessionId == Guid.Empty || sourceRunId == Guid.Empty
            || requestHash.Length != 64 || !requestHash.All(Uri.IsHexDigit)
            || senderKind == TaskActorKind.Agent && (senderInstanceId is null || sourceSessionId is null || sourceRunId is null || kind == AgentMessageKind.HumanGuidance)
            || senderKind == TaskActorKind.Human && (senderInstanceId is not null || sourceRunId is not null || sourceSessionId is null || kind != AgentMessageKind.HumanGuidance))
            throw new ArgumentException("Message attribution requires its real sender witness.");
        if (contentRetiredAt is { } retired && (retired.Offset != TimeSpan.Zero || retired < createdAt || text != "[retired]"))
            throw new ArgumentException("Retired message content is a factual tombstone.");
        ContentRetiredAt = contentRetiredAt;
        TenantId = tenantId; ThreadId = threadId; MessageId = messageId; Sequence = sequence; SenderKind = senderKind;
        SenderPrincipalId = senderPrincipalId; SenderInstanceId = senderInstanceId; SourceSessionId = sourceSessionId; SourceRunId = sourceRunId;
        RecipientInstanceId = recipientInstanceId; Kind = kind; Text = TaskLimits.Text(text, TaskLimits.MessageCharacters, "Message");
        RequestKey = TaskLimits.Key(requestKey); RequestHash = requestHash; CreatedAt = createdAt;
    }
    public Guid TenantId { get; }
    public Guid ThreadId { get; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? ContentRetiredAt { get; }
    public AgentMessage RetireContent(DateTimeOffset at) => ContentRetiredAt is not null ? this : new(TenantId, ThreadId, MessageId, Sequence,
        SenderKind, SenderPrincipalId, SenderInstanceId, SourceSessionId, SourceRunId, RecipientInstanceId, Kind, "[retired]", RequestKey, RequestHash, CreatedAt, at);
    public Guid MessageId { get; }
    public long Sequence { get; }
    public TaskActorKind SenderKind { get; }
    public Guid SenderPrincipalId { get; }
    public Guid? SenderInstanceId { get; }
    public Guid? SourceSessionId { get; }
    public Guid? SourceRunId { get; }
    public Guid RecipientInstanceId { get; }
    public AgentMessageKind Kind { get; }
    public string Text { get; }
    public string RequestKey { get; }
    public string RequestHash { get; }
    public DateTimeOffset CreatedAt { get; }
}

public sealed record AgentMessageReceipt(Guid TenantId, Guid MessageId, Guid RecipientInstanceId,
    AgentMessageDelivery State, long Revision, DateTimeOffset UpdatedAt, string? Reason = null,
    Guid? WakeSessionId = null, Guid? WakeRunId = null, Guid? WakeRootActivationId = null, int WakeHops = 0)
{
    public void Validate()
    {
        foreach (var id in new[] { TenantId, MessageId, RecipientInstanceId }) TaskLimits.Id(id, "Delivery identity");
        if (!Enum.IsDefined(State) || Revision < 1 || UpdatedAt.Offset != TimeSpan.Zero || WakeHops is < 0 or > 2
            || (WakeSessionId is null) != (WakeRunId is null) || (WakeRunId is null) != (WakeRootActivationId is null)
            || WakeSessionId == Guid.Empty || WakeRunId == Guid.Empty || WakeRootActivationId == Guid.Empty
            || State == AgentMessageDelivery.Undeliverable && string.IsNullOrWhiteSpace(Reason))
            throw new ArgumentException("Invalid message delivery receipt.");
    }
}

public sealed record TaskNotification(Guid TenantId, Guid NotificationId, Guid TaskId, Guid? TerminalRunId,
    Guid RecipientSessionId, long Revision, DateTimeOffset CreatedAt, Guid? HandlingRunId = null,
    DateTimeOffset? AcknowledgedAt = null, Guid? AcknowledgedBy = null, string? WithheldReason = null);
