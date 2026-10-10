using AgentCore.Application.Execution;

namespace AgentCore.Application.Tests;

public sealed class BrowserCleanupIntentTests
{
    [Theory]
    [InlineData("Log out and close the browser.", true, true)]
    [InlineData("Close the browser but don't log out.", false, true)]
    [InlineData("Log out but leave the browser open.", true, false)]
    [InlineData("Don't close the browser yet.", false, false)]
    [InlineData("Sign off when finished.", true, false)]
    [InlineData("Inspect Pump 002 only.", false, false)]
    [InlineData("Please logout and close browser window", true, true)]
    [InlineData("Please sign out", true, false)]
    [InlineData("Do not log out and close the browser", false, false)]
    [InlineData("Do not close the browser", false, false)]
    [InlineData("Keep me signed in", false, false)]
    [InlineData("Leave the browser open", false, false)]
    [InlineData("Maybe log out later", false, false)]
    [InlineData("Should I close the browser?", false, false)]
    [InlineData("If you're done, log out", false, false)]
    [InlineData("Log out. Don't log out yet.", false, false)]
    [InlineData("Log out and do not close the browser", true, false)]
    [InlineData("Don’t sign off", false, false)]
    [InlineData("Sign me out and close my browser window", true, true)]
    [InlineData("Log-out when finished", true, false)]
    [InlineData("Explain how to log out", false, false)]
    [InlineData("Log out and close the browser even if logout fails", true, true)]
    [InlineData("Close the browser even if logging out fails", false, true)]
    [InlineData("Log out, then only close the browser after verified logout", true, false)]
    [InlineData("Keep me signed in and close the browser", false, true)]
    public void Resource_intent_is_independent_and_conservative(string text, bool logout, bool close)
    {
        var intent = BrowserCleanupIntentRecognition.From(text);
        Assert.Equal(logout, intent.LogoutRequested); Assert.Equal(close, intent.ClosureRequested);
        Assert.Equal(logout || close, AgentRunAdmissionFactory.RequestsCleanup(text));
    }

    [Fact]
    public void Oversized_ambiguous_input_uses_normal_capacity() =>
        Assert.False(AgentRunAdmissionFactory.RequestsCleanup(new string('x', 32769) + " log out"));
}
