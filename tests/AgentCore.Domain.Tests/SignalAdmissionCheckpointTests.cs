using System.Text.Json;
using AgentCore.Domain.Conversation;

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
}
