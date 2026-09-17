using AgentCore.Application.Sessions;

namespace AgentCore.Application.Tests;

public sealed class SessionPauseSemanticsTests
{
    [Theory]
    [InlineData("manual", true)]
    [InlineData("inactivity", true)]
    [InlineData("silentEvaluation", true)]
    [InlineData("initiative", true)]
    [InlineData("persistence", true)]
    [InlineData("disconnected", false)]
    [InlineData("recovered", false)]
    [InlineData(null, true)]
    public void Requires_explicit_resume_only_for_semantic_pauses(string? pauseReason, bool expected) =>
        Assert.Equal(expected, SessionPauseSemantics.RequiresExplicitResume(pauseReason));
}
