using System.Text.Json.Serialization;
using AgentCore.Application.Triggers;
using AgentCore.Contracts.Http;
using AgentCore.Domain.Triggers;

namespace AgentCore.Api.Tests;

// Test builders author the final wire shape directly.
internal sealed record IntervalAutomationDraft(long ExpectedRevision, bool Enabled, [property: JsonIgnore] int IntervalSeconds,
    string Instructions, string? OfferingId = null, string? ReasoningEffort = null)
{
    public AutomationExecutionTargetDto ExecutionTarget => new("backgroundSession");
    public AutomationCompletionDeliveryDto CompletionDelivery => new("none");
    public string Name => "Periodic review";
    public IReadOnlyList<AutomationTriggerDto> Triggers => [new AutomationTriggerDto("schedule", new("fixedInterval", Interval: IntervalSeconds,
        AnchorAtUtc: DateTimeOffset.UtcNow.AddHours(1).ToString("O")), TriggerId: Guid.NewGuid().ToString())];
}
internal sealed record ScheduleAutomationDraft(long ExpectedRevision, bool Enabled, string Instructions,
    [property: JsonIgnore] AutomationTiming Schedule, string? OfferingId = null, string? ReasoningEffort = null)
{
    public AutomationExecutionTargetDto ExecutionTarget => new("backgroundSession");
    public AutomationCompletionDeliveryDto CompletionDelivery => new("none");
    public string Name => "Scheduled work";
    public IReadOnlyList<AutomationTriggerDto> Triggers => [new AutomationTriggerDto("schedule", Schedule, TriggerId: Guid.NewGuid().ToString())];
}
