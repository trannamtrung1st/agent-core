using System.Text.Json;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;

namespace AgentCore.Domain.Tests;

public sealed class SignalAdmissionCheckpointTests
{
    [Fact]
    public void Historical_approval_admission_is_not_reclassified_as_signal_admission()
    {
        const string historical = """{"PayloadJson":"{}","StepCount":1,"OutputBytes":0,"RemainingOverallBudgetMs":1000,"ActiveExecutionMs":10,"AwaitingRuntimeAdmission":true}""";
        var checkpoint = JsonSerializer.Deserialize<AgentRunCheckpoint>(historical)!;
        Assert.True(checkpoint.AwaitingRuntimeAdmission); Assert.False(checkpoint.AwaitingSignalAdmission);
        var clocked = checkpoint.StampBudgetClock(DateTimeOffset.UnixEpoch).PauseBudgetClock(DateTimeOffset.UnixEpoch.AddMilliseconds(5));
        Assert.True(clocked.AwaitingRuntimeAdmission); Assert.False(clocked.AwaitingSignalAdmission);
        Assert.Equal(995, clocked.RemainingOverallBudgetMs);
    }

    [Fact]
    public void Signal_admission_roundtrips_paused_budget_and_clears_both_flags_at_runtime_acceptance()
    {
        var checkpoint = new AgentRunCheckpoint("{}", 1, 0, 1000, 10).WithSignalAdmissionPending();
        var saved = JsonSerializer.Deserialize<AgentRunCheckpoint>(JsonSerializer.Serialize(checkpoint))!;
        Assert.True(saved.AwaitingSignalAdmission); Assert.True(saved.AwaitingRuntimeAdmission);
        var paused = saved.PauseBudgetClock(DateTimeOffset.UnixEpoch.AddMinutes(10));
        Assert.Equal(1000, paused.RemainingOverallBudgetMs); Assert.True(paused.AwaitingSignalAdmission);
        var admitted = paused.WithRuntimeAdmissionPending(false).StampBudgetClock(DateTimeOffset.UnixEpoch);
        Assert.False(admitted.AwaitingSignalAdmission); Assert.False(admitted.AwaitingRuntimeAdmission);
        Assert.DoesNotContain("AwaitingSignalAdmission", JsonSerializer.Serialize(admitted));
    }

    [Fact]
    public void Signal_discriminator_cannot_exist_without_its_pending_admission_boundary() =>
        Assert.Throws<ArgumentException>(() => new AgentRunCheckpoint("{}", 1, 0, 1000, awaitingSignalAdmission: true));

    [Fact]
    public void Repeated_signal_dispatch_reclaims_preserve_pending_result_until_runtime_acceptance()
    {
        var now = DateTimeOffset.UnixEpoch;
        var tenant = Guid.NewGuid(); var executor = Guid.NewGuid(); var session = Guid.NewGuid();
        var id = Guid.NewGuid(); var activationId = Guid.NewGuid();
        var activation = Activation.Start(activationId, session, ActivationKind.UserTurn, [Guid.NewGuid()], null, null, null, null, "signal-reclaim", now);
        var context = new TrustedExecutionContext(tenant, executor, Guid.NewGuid(), session, id, activationId, activationId, null, null, ExecutionOrigin.UserMessage);
        var run = AgentRun.Create(id, AgentRunOwner.ForTenant(tenant, Guid.NewGuid(), executor),
            new(activation, "signal-fixture", 1, new AgentIdentity("Test", "Test", "Test", "Test"), Guid.NewGuid(),
                AgentRunOutputContract.ConversationResponse, executionContext: context),
            new("synthetic", "synthetic", "synthetic", null), 2, now);
        run = run.TakeClaim(Guid.NewGuid(), now, now.AddMinutes(5));
        var checkpoint = new AgentRunCheckpoint("{\"savedWaitResult\":true}", 3, 100, 10000, 10);
        run = run.SaveCheckpoint(run.Revision, run.Claim!.Generation, checkpoint, null, now);
        run = run.SuspendForSignal(run.Revision, run.Claim!.Generation, checkpoint,
            new("exact-wait", AgentRunWaitMode.Duration, AgentRunWaitUntil.All, now, now.AddSeconds(1)), now);
        now = now.AddSeconds(1);
        run = run.ResumeFromSignal(run.Revision, Guid.NewGuid(), checkpoint, now, now.AddMinutes(5));
        var payload = run.Checkpoint!.PayloadJson;
        for (var retry = 0; retry < 2; retry++)
        {
            var previous = run.Claim!.Generation;
            run = run.DeferUnacceptedSignalDispatch(run.Revision, previous, now, now.AddSeconds(1));
            now = now.AddSeconds(1);
            run = run.TakeClaim(Guid.NewGuid(), now, now.AddMinutes(5));
            Assert.Equal(1, run.AttemptCount); Assert.Equal(1, run.WaitCount); Assert.NotEqual(previous, run.Claim!.Generation);
            Assert.Equal(payload, run.Checkpoint!.PayloadJson); Assert.Equal(3, run.Checkpoint.StepCount);
            Assert.Equal(10000, run.Checkpoint.RemainingOverallBudgetMs);
            Assert.True(run.Checkpoint.AwaitingRuntimeAdmission); Assert.True(run.Checkpoint.AwaitingSignalAdmission);
        }
        run = run.SaveCheckpoint(run.Revision, run.Claim!.Generation, run.Checkpoint!.WithRuntimeAdmissionPending(false), null, now);
        Assert.False(run.Checkpoint!.AwaitingRuntimeAdmission); Assert.False(run.Checkpoint.AwaitingSignalAdmission);
        Assert.Throws<AgentRunTransitionException>(() => run.DeferUnacceptedSignalDispatch(run.Revision, run.Claim!.Generation, now, now.AddSeconds(1)));
        run = run.Fail(run.Revision, run.Claim!.Generation, "provider-unavailable", "Controlled provider failure", true, now, now.AddSeconds(1));
        now = now.AddSeconds(1); run = run.TakeClaim(Guid.NewGuid(), now, now.AddMinutes(5));
        Assert.Equal(2, run.AttemptCount); Assert.False(run.Checkpoint!.AwaitingRuntimeAdmission); Assert.False(run.Checkpoint.AwaitingSignalAdmission);
    }
}
