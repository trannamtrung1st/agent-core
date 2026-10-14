using AgentCore.Application.Tools;

namespace AgentCore.Application.Tests;

public sealed class ApplicationMessageToolPolicyTests
{
    [Theory]
    [InlineData(ToolCatalog.WorkspaceList, true)]
    [InlineData(ToolCatalog.WebSearch, true)]
    [InlineData("browser.navigate", true)]
    [InlineData("sandbox.run", false)]
    [InlineData(ToolCatalog.SandboxExec, true)]
    [InlineData(ToolCatalog.SkillsLoad, false)]
    [InlineData(ToolCatalog.AppMessageSend, false)]
    public void Substantive_work_tools_unlock_intermediate_messaging(string toolName, bool unlocks) =>
        Assert.Equal(unlocks, ApplicationMessageToolPolicy.UnlocksIntermediateMessaging(toolName));
}
