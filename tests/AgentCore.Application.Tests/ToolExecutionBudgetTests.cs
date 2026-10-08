using AgentCore.Application.Ports;
using AgentCore.Application.Tools;

namespace AgentCore.Application.Tests;

public sealed class ToolExecutionBudgetTests
{
    [Fact]
    public void Ordinary_execution_keeps_the_standard_profile()
    {
        var budget = ToolExecutionBudget.Resolve(new ToolBudgetSignal(false, false));

        Assert.Equal(ToolExecutionClass.Standard, budget.Class);
        Assert.Equal(24, budget.MaxSteps);
        Assert.Equal(TimeSpan.FromSeconds(180), budget.Overall);
        Assert.Equal(TimeSpan.FromSeconds(30), budget.PerTool);
        Assert.Equal(8 * 1024 * 1024, ToolLimits.MaxOutputBytes);
    }

    [Fact]
    public void Interactive_browser_execution_uses_the_larger_profile()
    {
        var budget = ToolExecutionBudget.Resolve(new ToolBudgetSignal(InteractiveBrowser: true, PersistentBrowserLease: false));

        Assert.Equal(ToolExecutionClass.InteractiveBrowser, budget.Class);
        Assert.Equal(48, budget.MaxSteps);
        Assert.Equal(TimeSpan.FromSeconds(300), budget.Overall);
        Assert.Equal(ToolLimits.PerTool, budget.PerTool);
    }

    [Fact]
    public void Bound_application_browser_wins_over_the_interactive_profile()
    {
        var budget = ToolExecutionBudget.Resolve(new ToolBudgetSignal(InteractiveBrowser: true, PersistentBrowserLease: true));

        Assert.Equal(ToolExecutionClass.UnattendedBoundBrowser, budget.Class);
        Assert.Equal(32, budget.MaxSteps);
        Assert.Equal(TimeSpan.FromSeconds(240), budget.Overall);
        Assert.Equal(ToolLimits.PerTool, budget.PerTool);
    }

    [Fact]
    public void Browser_authorization_is_a_host_tool_fact()
    {
        Assert.False(ToolCatalog.AuthorizesBrowser(null));
        Assert.False(ToolCatalog.AuthorizesBrowser([new ModelToolDefinition(ToolCatalog.WorkspaceWrite, "write", "{}")]));
        Assert.True(ToolCatalog.AuthorizesBrowser([new ModelToolDefinition(ToolCatalog.BrowserSnapshot, "look", "{}")]));
    }
}
