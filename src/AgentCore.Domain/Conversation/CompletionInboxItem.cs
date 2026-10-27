namespace AgentCore.Domain.Conversation;

// Numeric slots 1 and 2 belonged to the retired child consumption contract.
public enum CompletionInboxStatus { Pending = 0, DeliveryQueued = 3, Delivered = 4, Skipped = 5 }

/// <summary>Independent destination-report accounting and immutable source references. The source Run owns content.</summary>
public sealed record CompletionInboxItem(Guid ChildAgentRunId, AgentRunOwner Owner, Guid ParentSessionId,
    Guid ChildSessionId, DateTimeOffset SourceFinishedAtUtc, long Revision = 1,
    CompletionInboxStatus Status = CompletionInboxStatus.Pending, Guid? ReportActivationId = null, string? SkipReason = null)
{
    public bool IsAccounted => Status is CompletionInboxStatus.DeliveryQueued or CompletionInboxStatus.Delivered or CompletionInboxStatus.Skipped;

    public CompletionInboxItem Settle(AgentRun run)
    {
        if (Status == CompletionInboxStatus.DeliveryQueued && ReportActivationId == run.ActivationId
            && run.Status == AgentRunStatus.Completed && run.Result?.OutcomeEntryId is not null)
            return this with { Revision = Revision + 1, Status = CompletionInboxStatus.Delivered };
        return this;
    }
}
