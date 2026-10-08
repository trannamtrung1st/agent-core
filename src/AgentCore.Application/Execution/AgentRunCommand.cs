using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Execution;

/// <summary>Shared persisted transition vocabulary for attached and detached execution.</summary>
public abstract record AgentRunCommand(long ExpectedRevision, DateTimeOffset AtUtc)
{
    public sealed record DeferDispatch(long Revision, DateTimeOffset At, Guid Generation, DateTimeOffset RetryAt)
        : AgentRunCommand(Revision, At);

    public sealed record Claim(long Revision, DateTimeOffset At, Guid Generation, DateTimeOffset LeaseExpiresAt)
        : AgentRunCommand(Revision, At);
    public sealed record Renew(long Revision, DateTimeOffset At, Guid Generation, DateTimeOffset LeaseExpiresAt)
        : AgentRunCommand(Revision, At);
    public sealed record Checkpoint(long Revision, DateTimeOffset At, Guid Generation, AgentRunCheckpoint Value, string? Progress)
        : AgentRunCommand(Revision, At);
    public sealed record Complete(long Revision, DateTimeOffset At, Guid Generation, string Summary,
        AgentRunOutcomeKind OutcomeKind, Guid? OutcomeEntryId)
        : AgentRunCommand(Revision, At);
    public sealed record Fail(long Revision, DateTimeOffset At, Guid Generation, string Code, string Summary,
        bool ReplaySafe, DateTimeOffset? RetryAt)
        : AgentRunCommand(Revision, At);
    public sealed record RequestCancellation(long Revision, DateTimeOffset At, string? KnownEffect)
        : AgentRunCommand(Revision, At);
    public sealed record CommitCancellation(long Revision, DateTimeOffset At, Guid Generation, string? KnownEffect)
        : AgentRunCommand(Revision, At);
    public sealed record Recover(long Revision, DateTimeOffset At) : AgentRunCommand(Revision, At);
    public sealed record BeginApproval(long Revision, DateTimeOffset At, Guid Generation, Guid ApprovalId,
        string ToolName, string PreparedActionJson, string ActionHash, string Preview, DateTimeOffset ExpiresAt)
        : AgentRunCommand(Revision, At);
    public sealed record DecideApproval(long Revision, DateTimeOffset At, Guid ApprovalId, long ApprovalRevision,
        string ActionHash, AgentRunApprovalDecision Decision) : AgentRunCommand(Revision, At);
    public sealed record ExpireApproval(long Revision, DateTimeOffset At) : AgentRunCommand(Revision, At);
    public sealed record MarkSideEffect(long Revision, DateTimeOffset At, Guid Generation,
        AgentRunSideEffectDisposition Disposition, string ToolCallId, string ActionHash) : AgentRunCommand(Revision, At);
    public sealed record ClearSideEffect(long Revision, DateTimeOffset At, Guid Generation, bool RecordExternalEffect = true)
        : AgentRunCommand(Revision, At);
    public sealed record AcceptBrowserSnapshot(long Revision, DateTimeOffset At, Guid Generation)
        : AgentRunCommand(Revision, At);
    public sealed record LoadSkills(long Revision, DateTimeOffset At, Guid Generation, IReadOnlyList<string> Keys)
        : AgentRunCommand(Revision, At);
    public sealed record LoadCapabilities(long Revision, DateTimeOffset At, Guid Generation, IReadOnlyList<string> Names)
        : AgentRunCommand(Revision, At);

    public AgentRun Apply(AgentRun run, Func<Guid> allocateDiagnosticId)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(allocateDiagnosticId);
        // Store-level CAS also fences idempotent domain transitions under a stale worker.
        if (run.Revision != ExpectedRevision)
            throw new AgentRunTransitionException(AgentRunTransitionFailure.StaleRevision, "AgentRun revision is stale.");
        if (AtUtc.Offset != TimeSpan.Zero || AtUtc < run.UpdatedAtUtc)
            throw new ArgumentException("AgentRun command requires monotonic UTC time.");
        if (run.Claim is { } claim && claim.LeaseExpiresAtUtc <= AtUtc
            && this is not (Recover or RequestCancellation))
            throw new AgentRunTransitionException(AgentRunTransitionFailure.StaleGeneration, "AgentRun lease has expired.");
        return this switch
        {
            DeferDispatch c => run.DeferUnstartedDispatch(c.ExpectedRevision, c.Generation, c.AtUtc, c.RetryAt),
            Claim c => run.TakeClaim(c.Generation, c.AtUtc, c.LeaseExpiresAt),
            Renew c => run.RenewClaim(c.ExpectedRevision, c.Generation, c.LeaseExpiresAt, c.AtUtc),
            Checkpoint c => run.SaveCheckpoint(c.ExpectedRevision, c.Generation, c.Value, c.Progress, c.AtUtc),
            Complete c => run.Complete(c.ExpectedRevision, c.Generation, c.Summary, c.AtUtc,
                c.OutcomeKind == AgentRunOutcomeKind.NeedsAttention, c.OutcomeKind, c.OutcomeEntryId),
            Fail c => run.Fail(c.ExpectedRevision, c.Generation, c.Code, c.Summary, c.ReplaySafe, c.AtUtc, c.RetryAt, allocateDiagnosticId),
            RequestCancellation c => run.RequestCancellation(c.ExpectedRevision, c.KnownEffect, c.AtUtc),
            CommitCancellation c => run.CommitCancellation(c.ExpectedRevision, c.Generation, c.KnownEffect, c.AtUtc),
            Recover c => run.RecoverExpiredClaim(c.AtUtc, allocateDiagnosticId),
            BeginApproval c => run.BeginApproval(c.ExpectedRevision, c.Generation, c.ApprovalId, c.ToolName,
                c.PreparedActionJson, c.ActionHash, c.Preview, c.ExpiresAt, c.AtUtc),
            DecideApproval c => run.DecideApproval(c.ApprovalId, c.ExpectedRevision, c.ApprovalRevision,
                c.ActionHash, c.Decision, c.AtUtc),
            ExpireApproval c => run.ExpireApproval(c.ExpectedRevision, c.AtUtc),
            MarkSideEffect c => run.MarkSideEffect(c.ExpectedRevision, c.Generation, c.Disposition, c.ToolCallId, c.ActionHash, c.AtUtc),
            ClearSideEffect c => run.ClearSideEffect(c.ExpectedRevision, c.Generation, c.AtUtc, c.RecordExternalEffect),
            AcceptBrowserSnapshot c => run.AcceptBrowserSnapshot(c.ExpectedRevision, c.Generation, c.AtUtc),
            LoadSkills c => run.AdmitActiveSkills(c.ExpectedRevision, c.Generation, c.Keys, c.AtUtc),
            LoadCapabilities c => run.AdmitCapabilities(c.ExpectedRevision, c.Generation, c.Names, c.AtUtc),
            _ => throw new ArgumentException("AgentRun transition is unsupported.")
        };
    }
}
