namespace AgentCore.Application.Tools;

public static class ToolLimits
{
    public const int MaxSteps = 24;
    public static readonly TimeSpan PerTool = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan Overall = TimeSpan.FromSeconds(180);
    public const int MaxOutputBytes = 8 * 1024 * 1024;
}

public enum ToolExecutionClass
{
    Standard,
    InteractiveBrowser,
    UnattendedBoundBrowser
}

public readonly record struct ToolBudgetSignal(bool InteractiveBrowser, bool PersistentBrowserLease);

public readonly record struct ToolExecutionBudget(
    ToolExecutionClass Class,
    int MaxSteps,
    TimeSpan Overall,
    TimeSpan PerTool)
{
    // The final reply shares the existing hard deadline; it does not extend the Run.
    public TimeSpan CleanupReserve => Class == ToolExecutionClass.InteractiveBrowser ? TimeSpan.FromSeconds(60) : TimeSpan.Zero;
    public TimeSpan FinalizationReserve => Class == ToolExecutionClass.InteractiveBrowser ? TimeSpan.FromSeconds(30) : TimeSpan.Zero;
    public static ToolExecutionBudget Standard { get; } = new(
        ToolExecutionClass.Standard,
        ToolLimits.MaxSteps,
        ToolLimits.Overall,
        ToolLimits.PerTool);

    public static ToolExecutionBudget InteractiveBrowser { get; } = new(
        ToolExecutionClass.InteractiveBrowser,
        48,
        TimeSpan.FromSeconds(300),
        ToolLimits.PerTool);

    public static ToolExecutionBudget UnattendedBoundBrowser { get; } = new(
        ToolExecutionClass.UnattendedBoundBrowser,
        32,
        TimeSpan.FromSeconds(240),
        ToolLimits.PerTool);

    public static ToolExecutionBudget Resolve(ToolBudgetSignal signal)
    {
        if (signal.PersistentBrowserLease)
        {
            return UnattendedBoundBrowser;
        }

        if (signal.InteractiveBrowser)
        {
            return InteractiveBrowser;
        }

        return Standard;
    }
}
