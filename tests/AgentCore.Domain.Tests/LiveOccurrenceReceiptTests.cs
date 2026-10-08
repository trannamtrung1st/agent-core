using AgentCore.Domain.Triggers;

namespace AgentCore.Domain.Tests;

public sealed class LiveOccurrenceReceiptTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Quiet_evaluation_settles_native_receipt_without_a_run_and_cannot_be_admitted_again()
    {
        var sessionId = Guid.NewGuid();
        var prepared = Prepared().WithLiveSession(sessionId, 1, Now);
        var accepted = prepared.WithRouting(OccurrenceRoutingDisposition.AcceptedLive, null, 3, Now, null, null);
        var quiet = accepted.WithLiveEvaluation(sessionId, null, Now);
        Assert.Equal(sessionId, quiet.LiveSessionId);
        Assert.Equal(Now, quiet.LiveEvaluationCompletedAtUtc);
        Assert.Null(quiet.AcceptedAgentRunId);
        Assert.Null(quiet.BackgroundSessionId);
        Assert.Throws<ArgumentException>(() => quiet.WithLiveEvaluation(sessionId, Guid.NewGuid(), Now));
    }

    [Fact]
    public void Speaking_evaluation_links_exact_run_and_session_and_rejects_another_session()
    {
        var sessionId = Guid.NewGuid();
        var prepared = Prepared().WithLiveSession(sessionId, 1, Now);
        Assert.Throws<ArgumentException>(() => prepared.WithLiveEvaluation(Guid.NewGuid(), Guid.NewGuid(), Now));
        var runId = Guid.NewGuid();
        var spoken = prepared.WithLiveEvaluation(sessionId, runId, Now);
        Assert.Equal(OccurrenceRoutingDisposition.AcceptedLive, spoken.Disposition);
        Assert.Equal(runId, spoken.AcceptedAgentRunId);
        Assert.Null(spoken.ClaimId);
        Assert.Null(spoken.ClaimLeaseExpiresAtUtc);
        Assert.Throws<ArgumentException>(() => spoken.WithLiveEvaluation(sessionId, runId, Now));
    }

    [Fact]
    public void Preparation_rejects_stale_revision_target_switch_and_authored_automation()
    {
        var prepared = Prepared();
        Assert.Throws<ArgumentException>(() => prepared.WithLiveSession(Guid.NewGuid(), 0, Now));
        var bound = prepared.WithLiveSession(Guid.NewGuid(), 1, Now);
        Assert.Throws<ArgumentException>(() => bound.WithLiveSession(Guid.NewGuid(), 2, Now));
        Assert.Throws<ArgumentException>(() => Prepared(Guid.NewGuid()).WithLiveSession(Guid.NewGuid(), 1, Now));
    }

    private static TriggerOccurrence Prepared(Guid? automationId = null) => new(Guid.NewGuid(), "native-event:one",
        automationId, new(Guid.NewGuid(), Guid.NewGuid()), TriggerSourceKind.ApplicationEvent,
        null, Now, Now, "{}", Guid.NewGuid(), null, OccurrenceRoutingDisposition.LivePrepared, null,
        1, Now, Guid.NewGuid(), Now.AddSeconds(30));
}
