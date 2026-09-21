using System.Text;

namespace AgentCore.Application.Tools;

public static class ToolOutputBudget
{
    public static int TextByteCount(ToolExecutionResult result) =>
        Encoding.UTF8.GetByteCount(result.Text);
}
