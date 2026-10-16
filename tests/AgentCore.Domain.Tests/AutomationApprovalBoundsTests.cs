using AgentCore.Domain.Conversation;

namespace AgentCore.Domain.Tests;

public sealed class AutomationApprovalBoundsTests
{
    private static AgentRunApproval Approval(string tool, string args, string preview)
    {
        var now = DateTimeOffset.UtcNow;
        return new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, tool, args, new string('a', 64), preview,
            now.AddMinutes(1), AgentRunApprovalDecision.Pending, null, false, 1, now);
    }

    [Theory]
    [InlineData("automation.create")]
    [InlineData("automation.update")]
    [InlineData("automation.delete")]
    [InlineData("automation.enable")]
    [InlineData("automation.disable")]
    [InlineData("automation.run")]
    public void Exact_automation_previews_have_separate_bounded_capacity(string tool)
    {
        var args = "{" + new string(' ', 9000) + "}";
        var preview = new string('x', AgentRunLimits.MaxAutomationPreviewCharacters);
        Assert.Equal(preview, Approval(tool, args, preview).Preview);
        Assert.Throws<ArgumentException>(() => Approval(tool, args, preview + "x"));
        Assert.Throws<ArgumentException>(() => Approval(tool, new string('x', AgentRunLimits.MaxAutomationPreparedActionBytes + 1), preview));
        Assert.Throws<ArgumentException>(() => Approval("demo.sensitive_action", args, preview));
    }
}
