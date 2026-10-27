namespace AgentCore.Domain.Conversation;

public enum AgentRunWaitMode { Duration, Reserved1, Tasks }
public enum AgentRunWaitUntil { All, Any }
public sealed record TaskWaitWitness(Guid TaskId, long Revision, long MessageSequence);

[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record AgentRunWait(string ToolCallId, AgentRunWaitMode Mode,
    AgentRunWaitUntil Until, DateTimeOffset StartedAtUtc, DateTimeOffset DeadlineUtc, Guid? SuspendedGeneration = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<TaskWaitWitness>? Tasks = null)
{
    public void Validate()
    {
        AgentRunTime.RequireUtc(StartedAtUtc, "Wait start");
        AgentRunTime.RequireUtc(DeadlineUtc, "Wait deadline");
        if (string.IsNullOrWhiteSpace(ToolCallId) || ToolCallId.Length > 128 || !Enum.IsDefined(Mode) || Mode == AgentRunWaitMode.Reserved1 || !Enum.IsDefined(Until)
            || DeadlineUtc <= StartedAtUtc || (DeadlineUtc - StartedAtUtc).TotalSeconds > AgentRunLimits.MaxWaitSeconds
            || Mode != AgentRunWaitMode.Tasks && Tasks is not null
            || Mode == AgentRunWaitMode.Tasks && (Tasks is null || Tasks.Count is < 1 or > AgentRunLimits.MaxWaitTargets
                || Tasks.Any(t => t.TaskId == Guid.Empty || t.Revision < 0 || t.MessageSequence < 0) || Tasks.Select(t => t.TaskId).Distinct().Count() != Tasks.Count))
            throw new ArgumentException("Wait requires a bounded typed condition and deadline.");
    }
}
