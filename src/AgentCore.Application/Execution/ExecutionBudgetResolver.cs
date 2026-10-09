using AgentCore.Application.Agents;
using AgentCore.Application.Tools;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Execution;

public static class ExecutionBudgetResolver
{
    public static EffectiveExecutionBudget Resolve(AgentDefinition definition, ExecutionBudgetPolicy? instance, bool interactive)
    {
        var authorized = ToolRegistry.All.Any(tool => ToolCatalog.IsBrowserTool(tool.Name) && RolePermissions.AllowsTool(definition, tool.Name));
        var kind = !authorized ? ExecutionBudgetClass.Standard : interactive
            ? ExecutionBudgetClass.InteractiveBrowser : ExecutionBudgetClass.UnattendedBoundBrowser;
        return ExecutionBudgetPolicy.Resolve(kind, definition.ExecutionBudgets, instance);
    }
}
