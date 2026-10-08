using System.Text.Json.Serialization;
using AgentCore.Application.Triggers;
using AgentCore.Contracts.Http;
using AgentCore.Domain.Triggers;

namespace AgentCore.Api.Tests;

// Test builders author the final wire shape directly.
internal sealed record IntervalAutomationDraft(long ExpectedRevision, bool Enabled, [property: JsonIgnore] int IntervalSeconds,
    string Instructions, string? ModelKey = null, string? ReasoningEffort = null)
{
    public AutomationExecutionTargetDto ExecutionTarget => new("backgroundSession");
    public AutomationCompletionDeliveryDto CompletionDelivery => new("none");
    public string Name => "Periodic review";
    public AutomationTriggerDto Trigger => new("schedule", new("fixedInterval", Interval: IntervalSeconds,
        AnchorAtUtc: DateTimeOffset.UtcNow.AddHours(1).ToString("O")));
}
internal sealed record ScheduleAutomationDraft(long ExpectedRevision, bool Enabled, string Instructions,
    [property: JsonIgnore] AutomationTiming Schedule, string? ModelKey = null, string? ReasoningEffort = null)
{
    public AutomationExecutionTargetDto ExecutionTarget => new("backgroundSession");
    public AutomationCompletionDeliveryDto CompletionDelivery => new("none");
    public string Name => "Scheduled work";
    public AutomationTriggerDto Trigger => new("schedule", Schedule);
}
internal static class AutomationFixtures
{
    public static ValueTask<Automation> SaveAsync(this AdminAutomationAuthoringService service, Guid instanceId, Guid? id,
        long revision, bool enabled, int interval, string instructions, string? model, string? effort, CancellationToken ct = default) =>
        service.SaveAsync(instanceId, id, revision, enabled, "Periodic review", instructions,
            new ScheduleTrigger(new FixedIntervalSchedule(interval, DateTimeOffset.UtcNow.AddHours(1))), model, effort, ct);
}
