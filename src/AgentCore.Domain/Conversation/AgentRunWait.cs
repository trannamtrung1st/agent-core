namespace AgentCore.Domain.Conversation;

public enum AgentRunWaitMode { Duration, Background }
public enum AgentRunWaitUntil { All, Any }

public sealed record AgentRunWait(string ToolCallId, AgentRunWaitMode Mode, IReadOnlyList<Guid> BackgroundSessionIds,
    AgentRunWaitUntil Until, DateTimeOffset StartedAtUtc, DateTimeOffset DeadlineUtc, Guid? SuspendedGeneration = null)
{
    public void Validate()
    {
        AgentRunTime.RequireUtc(StartedAtUtc, "Wait start");
        AgentRunTime.RequireUtc(DeadlineUtc, "Wait deadline");
        if (string.IsNullOrWhiteSpace(ToolCallId) || ToolCallId.Length > 128 || !Enum.IsDefined(Mode) || !Enum.IsDefined(Until)
            || DeadlineUtc <= StartedAtUtc || (DeadlineUtc - StartedAtUtc).TotalSeconds > AgentRunLimits.MaxWaitSeconds
            || BackgroundSessionIds is null || BackgroundSessionIds.Count > AgentRunLimits.MaxWaitTargets
            || BackgroundSessionIds.Any(id => id == Guid.Empty) || BackgroundSessionIds.Distinct().Count() != BackgroundSessionIds.Count
            || Mode == AgentRunWaitMode.Duration && BackgroundSessionIds.Count != 0
            || Mode == AgentRunWaitMode.Background && BackgroundSessionIds.Count == 0)
            throw new ArgumentException("Wait requires a bounded typed condition and deadline.");
    }
}
