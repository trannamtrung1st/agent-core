using AgentCore.Domain.Definitions;
using AgentCore.Domain.Work;

namespace AgentCore.Domain.Conversation;

public enum ConversationTurnExecutionStatus
{
    Queued = 0,
    Running = 1,
    WaitingForApproval = 2,
    Completed = 3,
    Failed = 4,
    Cancelled = 5
}

public sealed class ConversationTurnExecution
{
    private ConversationTurnExecution(
        Guid executionId,
        Guid sessionId,
        Guid sourceUserEntryId,
        Guid sourceEventId,
        Guid responseId,
        Guid? agentInstanceId,
        Guid? profileId,
        string definitionId,
        int definitionVersion,
        AgentIdentity? pinnedPersona,
        WorkModelPin pinnedModel,
        ConversationTurnExecutionStatus status,
        long revision,
        WorkClaim? claim,
        Guid? assistantEntryId,
        bool cancellationRequested,
        DateTimeOffset? cancellationRequestedAtUtc,
        DateTimeOffset acceptedAtUtc,
        DateTimeOffset updatedAtUtc)
    {
        if (executionId == Guid.Empty)
        {
            throw new ArgumentException("Execution identifier is required.", nameof(executionId));
        }

        if (sessionId == Guid.Empty)
        {
            throw new ArgumentException("Session identifier is required.", nameof(sessionId));
        }

        if (sourceUserEntryId == Guid.Empty)
        {
            throw new ArgumentException("Source user entry is required.", nameof(sourceUserEntryId));
        }

        if (sourceEventId == Guid.Empty)
        {
            throw new ArgumentException("Source event is required.", nameof(sourceEventId));
        }

        if (responseId == Guid.Empty)
        {
            throw new ArgumentException("Response identifier is required.", nameof(responseId));
        }

        if (string.IsNullOrWhiteSpace(definitionId))
        {
            throw new ArgumentException("Definition id is required.", nameof(definitionId));
        }

        if (definitionVersion < 1)
        {
            throw new ArgumentException("Definition version must be positive.", nameof(definitionVersion));
        }

        if (pinnedModel is null)
        {
            throw new ArgumentNullException(nameof(pinnedModel));
        }
        if (revision < 1)
        {
            throw new ArgumentException("Revision starts at 1.");
        }

        WorkTime.RequireUtc(acceptedAtUtc, "Accepted");
        WorkTime.RequireUtc(updatedAtUtc, "Updated");
        WorkTime.RequireUtc(cancellationRequestedAtUtc, "Cancellation");

        ExecutionId = executionId;
        SessionId = sessionId;
        SourceUserEntryId = sourceUserEntryId;
        SourceEventId = sourceEventId;
        ResponseId = responseId;
        AgentInstanceId = agentInstanceId;
        ProfileId = profileId;
        DefinitionId = definitionId;
        DefinitionVersion = definitionVersion;
        PinnedPersona = pinnedPersona;
        PinnedModel = pinnedModel;
        Status = status;
        Revision = revision;
        Claim = claim;
        AssistantEntryId = assistantEntryId;
        CancellationRequested = cancellationRequested;
        CancellationRequestedAtUtc = cancellationRequestedAtUtc;
        AcceptedAtUtc = acceptedAtUtc;
        UpdatedAtUtc = updatedAtUtc;
    }

    public Guid ExecutionId { get; }

    public Guid SessionId { get; }

    public Guid SourceUserEntryId { get; }

    public Guid SourceEventId { get; }

    public Guid ResponseId { get; }

    public Guid? AgentInstanceId { get; }

    public Guid? ProfileId { get; }

    public string DefinitionId { get; }

    public int DefinitionVersion { get; }

    public AgentIdentity? PinnedPersona { get; }

    public WorkModelPin PinnedModel { get; }

    public ConversationTurnExecutionStatus Status { get; }

    public long Revision { get; }

    public WorkClaim? Claim { get; }

    public Guid? AssistantEntryId { get; }

    public bool CancellationRequested { get; }

    public DateTimeOffset? CancellationRequestedAtUtc { get; }

    public DateTimeOffset AcceptedAtUtc { get; }

    public DateTimeOffset UpdatedAtUtc { get; }

    public bool IsOpen => Status is ConversationTurnExecutionStatus.Queued
        or ConversationTurnExecutionStatus.Running
        or ConversationTurnExecutionStatus.WaitingForApproval;

    public bool IsInitialQueued => Status == ConversationTurnExecutionStatus.Queued && Revision == 1 && Claim is null;

    public static ConversationTurnExecution Restore(
        Guid executionId,
        Guid sessionId,
        Guid sourceUserEntryId,
        Guid sourceEventId,
        Guid responseId,
        Guid? agentInstanceId,
        Guid? profileId,
        string definitionId,
        int definitionVersion,
        AgentIdentity? pinnedPersona,
        WorkModelPin pinnedModel,
        ConversationTurnExecutionStatus status,
        long revision,
        WorkClaim? claim,
        Guid? assistantEntryId,
        bool cancellationRequested,
        DateTimeOffset? cancellationRequestedAtUtc,
        DateTimeOffset acceptedAtUtc,
        DateTimeOffset updatedAtUtc) =>
        new(
            executionId,
            sessionId,
            sourceUserEntryId,
            sourceEventId,
            responseId,
            agentInstanceId,
            profileId,
            definitionId,
            definitionVersion,
            pinnedPersona,
            pinnedModel,
            status,
            revision,
            claim,
            assistantEntryId,
            cancellationRequested,
            cancellationRequestedAtUtc,
            acceptedAtUtc,
            updatedAtUtc);

    public static ConversationTurnExecution AcceptNew(
        Guid executionId,
        Guid sessionId,
        Guid sourceUserEntryId,
        Guid sourceEventId,
        Guid responseId,
        Guid? agentInstanceId,
        Guid? profileId,
        string definitionId,
        int definitionVersion,
        AgentIdentity? pinnedPersona,
        WorkModelPin pinnedModel,
        DateTimeOffset acceptedAtUtc) =>
        new(
            executionId,
            sessionId,
            sourceUserEntryId,
            sourceEventId,
            responseId,
            agentInstanceId,
            profileId,
            definitionId,
            definitionVersion,
            pinnedPersona,
            pinnedModel,
            ConversationTurnExecutionStatus.Queued,
            1,
            null,
            null,
            false,
            null,
            acceptedAtUtc,
            acceptedAtUtc);

    public ConversationTurnExecution WithClaim(WorkClaim claim, ConversationTurnExecutionStatus status, DateTimeOffset updatedAtUtc) =>
        new(
            ExecutionId,
            SessionId,
            SourceUserEntryId,
            SourceEventId,
            ResponseId,
            AgentInstanceId,
            ProfileId,
            DefinitionId,
            DefinitionVersion,
            PinnedPersona,
            PinnedModel,
            status,
            Revision + 1,
            claim,
            AssistantEntryId,
            CancellationRequested,
            CancellationRequestedAtUtc,
            AcceptedAtUtc,
            updatedAtUtc);

    public ConversationTurnExecution WithAssistant(Guid assistantEntryId, DateTimeOffset updatedAtUtc) =>
        new(
            ExecutionId,
            SessionId,
            SourceUserEntryId,
            SourceEventId,
            ResponseId,
            AgentInstanceId,
            ProfileId,
            DefinitionId,
            DefinitionVersion,
            PinnedPersona,
            PinnedModel,
            Status,
            Revision + 1,
            Claim,
            assistantEntryId,
            CancellationRequested,
            CancellationRequestedAtUtc,
            AcceptedAtUtc,
            updatedAtUtc);

    public ConversationTurnExecution WithStatus(
        ConversationTurnExecutionStatus status,
        DateTimeOffset updatedAtUtc,
        bool clearClaim = false) =>
        new(
            ExecutionId,
            SessionId,
            SourceUserEntryId,
            SourceEventId,
            ResponseId,
            AgentInstanceId,
            ProfileId,
            DefinitionId,
            DefinitionVersion,
            PinnedPersona,
            PinnedModel,
            status,
            Revision + 1,
            clearClaim ? null : Claim,
            AssistantEntryId,
            CancellationRequested,
            CancellationRequestedAtUtc,
            AcceptedAtUtc,
            updatedAtUtc);

    public ConversationTurnExecution WithCancellationRequested(DateTimeOffset requestedAtUtc) =>
        new(
            ExecutionId,
            SessionId,
            SourceUserEntryId,
            SourceEventId,
            ResponseId,
            AgentInstanceId,
            ProfileId,
            DefinitionId,
            DefinitionVersion,
            PinnedPersona,
            PinnedModel,
            Status,
            Revision + 1,
            Claim,
            AssistantEntryId,
            true,
            requestedAtUtc,
            AcceptedAtUtc,
            requestedAtUtc);

    public ConversationTurnExecution TakeClaim(Guid generation, DateTimeOffset claimedAtUtc, DateTimeOffset leaseExpiresAtUtc)
    {
        if (Status is not ConversationTurnExecutionStatus.Queued || Claim is not null)
        {
            throw new WorkItemTransitionException(WorkTransitionFailure.NotClaimable, "Turn execution cannot be claimed.");
        }

        var claim = new WorkClaim(generation, claimedAtUtc, leaseExpiresAtUtc);
        return WithClaim(claim, ConversationTurnExecutionStatus.Running, claimedAtUtc);
    }

    public ConversationTurnExecution RequireOperational(long expectedRevision, Guid generation)
    {
        if (Revision != expectedRevision)
        {
            throw new WorkItemTransitionException(WorkTransitionFailure.StaleRevision, "Turn execution revision is stale.");
        }

        if (Claim is null || Claim.Generation != generation)
        {
            throw new WorkItemTransitionException(WorkTransitionFailure.StaleGeneration, "Turn execution generation is stale.");
        }

        if (Status is ConversationTurnExecutionStatus.Completed
            or ConversationTurnExecutionStatus.Failed
            or ConversationTurnExecutionStatus.Cancelled)
        {
            throw new WorkItemTransitionException(WorkTransitionFailure.Terminal, "Turn execution is terminal.");
        }

        return this;
    }

    public ConversationTurnExecution RenewClaim(
        long expectedRevision,
        Guid generation,
        DateTimeOffset leaseExpiresAtUtc,
        DateTimeOffset renewedAtUtc)
    {
        var current = RequireOperational(expectedRevision, generation);
        if (leaseExpiresAtUtc <= renewedAtUtc)
        {
            throw new ArgumentException("Claim lease must be later than the renewal time.");
        }

        var claim = new WorkClaim(generation, current.Claim!.ClaimedAtUtc, leaseExpiresAtUtc);
        return current.WithClaim(claim, current.Status, renewedAtUtc);
    }

    public ConversationTurnExecution CompleteTerminal(long expectedRevision, Guid generation, DateTimeOffset completedAtUtc)
    {
        RequireOperational(expectedRevision, generation);
        if (AssistantEntryId is null)
        {
            throw new WorkItemTransitionException(WorkTransitionFailure.Illegal, "Assistant entry must be attached before completion.");
        }

        return WithStatus(ConversationTurnExecutionStatus.Completed, completedAtUtc, clearClaim: true);
    }

    public ConversationTurnExecution CommitCancellation(long expectedRevision, Guid generation, DateTimeOffset cancelledAtUtc)
    {
        RequireOperational(expectedRevision, generation);
        return WithStatus(ConversationTurnExecutionStatus.Cancelled, cancelledAtUtc, clearClaim: true);
    }

    public ConversationTurnExecution FailTerminal(long expectedRevision, Guid generation, DateTimeOffset failedAtUtc)
    {
        RequireOperational(expectedRevision, generation);
        return WithStatus(ConversationTurnExecutionStatus.Failed, failedAtUtc, clearClaim: true);
    }

    public ConversationTurnExecution RequeueExpiredClaim(DateTimeOffset updatedAtUtc)
    {
        if (Status != ConversationTurnExecutionStatus.Running || Claim is null)
        {
            throw new WorkItemTransitionException(WorkTransitionFailure.Illegal, "Only an expired running claim can be requeued.");
        }

        return new ConversationTurnExecution(
            ExecutionId,
            SessionId,
            SourceUserEntryId,
            SourceEventId,
            ResponseId,
            AgentInstanceId,
            ProfileId,
            DefinitionId,
            DefinitionVersion,
            PinnedPersona,
            PinnedModel,
            ConversationTurnExecutionStatus.Queued,
            Revision + 1,
            null,
            AssistantEntryId,
            CancellationRequested,
            CancellationRequestedAtUtc,
            AcceptedAtUtc,
            updatedAtUtc);
    }
}
