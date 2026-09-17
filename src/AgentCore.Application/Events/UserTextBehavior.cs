namespace AgentCore.Application.Events;

public enum UserTextBehavior
{
    Interrupt = 0,
    Queue = 1
}

public enum ResponseCancelResult
{
    Cancelled = 0,
    Idempotent = 1,
    Stale = 2,
    Unknown = 3
}

public static class UserTextBehaviors
{
    public static bool TryParse(string? raw, out UserTextBehavior behavior)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            behavior = UserTextBehavior.Interrupt;
            return true;
        }

        if (string.Equals(raw, "interrupt", StringComparison.OrdinalIgnoreCase))
        {
            behavior = UserTextBehavior.Interrupt;
            return true;
        }

        if (string.Equals(raw, "queue", StringComparison.OrdinalIgnoreCase))
        {
            behavior = UserTextBehavior.Queue;
            return true;
        }

        behavior = default;
        return false;
    }

    public static string WireName(UserTextBehavior behavior) =>
        behavior == UserTextBehavior.Queue ? "queue" : "interrupt";
}
