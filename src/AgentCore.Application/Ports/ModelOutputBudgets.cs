namespace AgentCore.Application.Ports;

/// <summary>
/// Bounded generation ceilings. Compact evaluators keep their own small requests.
/// A tool-offered conversational turn uses <see cref="ToolCapable"/> so function
/// arguments are not cut off at the conversational default.
/// </summary>
public static class ModelOutputBudgets
{
    public const int ConversationalDefault = 512;
    public const int ToolCapable = 8192;

    public static int ForTurn(int requested, bool toolsOffered) =>
        toolsOffered ? ToolCapable : requested;
}
