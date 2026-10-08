namespace AgentCore.Domain.Conversation;

public enum CompletionInboxStatus { Pending, Claimed, Handled, DeliveryQueued, Delivered, Skipped }

/// <summary>Accounting and immutable source references only. The child Run owns result content.</summary>
public sealed record CompletionInboxItem(Guid ChildAgentRunId, AgentRunOwner Owner, Guid ParentSessionId,
    Guid ChildSessionId, DateTimeOffset SourceFinishedAtUtc, long Revision = 1,
    CompletionInboxStatus Status = CompletionInboxStatus.Pending, Guid? ClaimRunId = null,
    Guid? ClaimGeneration = null, string? ClaimToolCallId = null, Guid? ClaimToken = null,
    DateTimeOffset? ClaimExpiresAtUtc = null, string? Acknowledgment = null,
    Guid? HandledByRunId = null, Guid? ReportActivationId = null, string? SkipReason = null)
{
    public bool IsAccounted => Status is CompletionInboxStatus.Handled or CompletionInboxStatus.DeliveryQueued
        or CompletionInboxStatus.Delivered or CompletionInboxStatus.Skipped;

    public CompletionInboxItem Release() => this with { Revision = Revision + 1,
        Status = CompletionInboxStatus.Pending, ClaimRunId = null, ClaimGeneration = null,
        ClaimToolCallId = null, ClaimToken = null, ClaimExpiresAtUtc = null, Acknowledgment = null };

    public CompletionInboxItem Take(AgentRun parent, long expectedRevision, string toolCallId,
        Guid token, DateTimeOffset now)
    {
        if (parent.Status != AgentRunStatus.Running || parent.Claim is null
            || parent.Claim.LeaseExpiresAtUtc <= now || parent.SessionId != ParentSessionId || parent.Owner != Owner)
            throw new AgentRunTransitionException(AgentRunTransitionFailure.StaleRevision, "Completion claim is stale.");
        if (Status == CompletionInboxStatus.Claimed && ClaimRunId == parent.AgentRunId
            && ClaimGeneration == parent.Claim.Generation && ClaimToolCallId == toolCallId && ClaimExpiresAtUtc > now)
            return this;
        if (Revision != expectedRevision) throw new AgentRunTransitionException(AgentRunTransitionFailure.StaleRevision, "Completion revision is stale.");
        if (Status != CompletionInboxStatus.Pending || string.IsNullOrWhiteSpace(toolCallId) || toolCallId.Length > 128 || token == Guid.Empty)
            throw new AgentRunTransitionException(AgentRunTransitionFailure.NotClaimable, "Completion is unavailable for take.");
        return this with { Revision = Revision + 1, Status = CompletionInboxStatus.Claimed,
            ClaimRunId = parent.AgentRunId, ClaimGeneration = parent.Claim.Generation,
            ClaimToolCallId = toolCallId, ClaimToken = token,
            ClaimExpiresAtUtc = parent.Claim.LeaseExpiresAtUtc, Acknowledgment = null };
    }

    public CompletionInboxItem Acknowledge(AgentRun parent, long expectedRevision, Guid token,
        string usage, DateTimeOffset now)
    {
        if (Status != CompletionInboxStatus.Claimed || ClaimToken != token
            || ClaimRunId != parent.AgentRunId || ClaimGeneration != parent.Claim?.Generation
            || ClaimExpiresAtUtc <= now || parent.Claim?.LeaseExpiresAtUtc <= now || parent.Status != AgentRunStatus.Running)
            throw new AgentRunTransitionException(AgentRunTransitionFailure.StaleGeneration, "Completion token is expired or fenced.");
        if (string.IsNullOrWhiteSpace(usage) || usage.Length > 1000)
            throw new ArgumentException("Completion acknowledgment requires a usage statement of 1–1000 characters.");
        if (Acknowledgment == usage.Trim()) return this;
        if (Acknowledgment is not null) throw new AgentRunTransitionException(AgentRunTransitionFailure.StaleRevision, "Acknowledgment intent is already staged.");
        if (Revision != expectedRevision) throw new AgentRunTransitionException(AgentRunTransitionFailure.StaleRevision, "Completion revision is stale.");
        return this with { Revision = Revision + 1, Acknowledgment = usage.Trim() };
    }

    public CompletionInboxItem Settle(AgentRun run, Guid? generation)
    {
        if (Status == CompletionInboxStatus.Claimed && ClaimRunId == run.AgentRunId)
        {
            if (run.Status == AgentRunStatus.Completed && run.Result?.OutcomeEntryId is not null && Acknowledgment is not null
                && ClaimExpiresAtUtc > run.UpdatedAtUtc && ClaimGeneration == generation)
                return Release() with { Status = CompletionInboxStatus.Handled, HandledByRunId = run.AgentRunId };
            if (run.Status == AgentRunStatus.Running && run.Claim is { } claim && claim.Generation != ClaimGeneration)
                return ClaimExpiresAtUtc > run.UpdatedAtUtc ? this with { Revision = Revision + 1, ClaimGeneration = claim.Generation } : Release();
            if (run.IsTerminal || run.Status == AgentRunStatus.WaitingToRetry) return Release();
        }
        if (Status == CompletionInboxStatus.DeliveryQueued && ReportActivationId == run.ActivationId
            && run.Status == AgentRunStatus.Completed && run.Result?.OutcomeEntryId is not null)
            return this with { Revision = Revision + 1, Status = CompletionInboxStatus.Delivered };
        return this;
    }
}
