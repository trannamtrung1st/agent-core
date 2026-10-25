using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Application.Triggers;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Triggers;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Providers;

namespace AgentCore.Application.Tests;

public sealed class AutomationActivationDefaultTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 23, 9, 0, 0, TimeSpan.Zero);
    private static AgentDefinition Definition => SampleDefinitions.Examiner with {
        TriggerPolicy = new(true, true, true, true, true, true, 32, 365, 1, ["schedule", "coreEvent", "applicationEvent"], true, 60) };
    public static IEnumerable<object[]> CreationDefaults => new[] { "core", "webhook", "events", "schedule", "schedule-row", "preset-event", "preset-schedule" }
        .SelectMany(shape => new[] { "omitted", "enabled", "disabled" }.Select(choice => new object[] { shape, choice }));

    [Theory] [MemberData(nameof(CreationDefaults))]
    public void Creation_default_depends_on_source_and_preserves_explicit_activation(string shape, string choice)
    {
        var json = new JsonObject { ["name"] = "Exact task", ["instructions"] = "Retain the accepted task.", ["executionTarget"] = "backgroundSession", ["requiresTools"] = false };
        switch (shape)
        {
            case "core": json["coreEventKey"] = "run.completed"; break;
            case "webhook": json["eventId"] = Guid.NewGuid().ToString("D"); break;
            case "events": json["triggers"] = JsonNode.Parse("""[{"kind":"event","enabled":true,"source":{"kind":"builtin","key":"run.completed"}},{"kind":"event","enabled":false,"source":{"kind":"webhook","eventId":"b6557114-6074-4f1a-af98-21e74d15ca63"}}]"""); break;
            case "schedule": json["relativeDelaySeconds"] = 60; break;
            case "schedule-row": json["triggers"] = JsonNode.Parse("""[{"kind":"schedule","schedule":{"kind":"oneShot","atUtc":"2026-10-24T09:00:00Z","timeZone":"UTC"}}]"""); break;
            case "preset-event": json["presetId"] = "review-recent-work"; json["presetVersion"] = 1; break;
            case "preset-schedule": json["presetId"] = "consolidate-continuity"; json["presetVersion"] = 1; break;
        }
        if (choice != "omitted") json["enabled"] = choice == "enabled";
        var args = TenantAutomationMutationContract.Parse(ToolCatalog.AutomationCreate, json.ToJsonString());
        var change = TriggerScheduleCommands.NormalizeTenant(Definition, null, args, Guid.NewGuid(), At, new SystemIdGenerator(TimeProvider.System));
        Assert.Equal(choice == "enabled" || choice == "omitted" && shape is "schedule" or "schedule-row", change.Enabled);
        if (shape == "events") { Assert.True(change.Events![0].Enabled); Assert.False(change.Events[1].Enabled); }
    }

    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public void Omitted_update_preserves_parent_activation_and_explicit_controls_still_work(bool active, bool schedule)
    {
        var ids = new SystemIdGenerator(TimeProvider.System); var definition = Definition;
        var agent = new TenantAgent(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), new(Guid.NewGuid(), definition.Id, definition.Version,
            definition.Identity, AgentInstanceLifecycle.Active, At, At));
        var seed = schedule ? new TenantAutomationChange(active, "Timing", "Retain the task.", new OneShotSchedule(At.AddHours(1), "UTC"), RequiresTools: false)
            : new TenantAutomationChange(active, "Events", "Retain the task.", null, Events: [new(null, new CoreEventTrigger("run.completed"), true)], RequiresTools: false);
        var current = TenantAutomationAuthoring.Build(agent, definition, null, seed, new(TriggerAuthorizationOrigin.AgentTask, null, null, At, At), At, AgentCore.Infrastructure.Providers.Synthetic.SyntheticModelCatalog.Create(), ids, null);
        var json = JsonSerializer.Serialize(new { automationId = current.AutomationId, expectedRevision = current.Revision, name = "Renamed" });
        var update = TriggerScheduleCommands.NormalizeTenant(definition, current, TenantAutomationMutationContract.Parse(ToolCatalog.AutomationUpdate, json), Guid.NewGuid(), At, ids);
        Assert.Equal(active, update.Enabled);
        foreach (var tool in new[] { ToolCatalog.AutomationEnable, ToolCatalog.AutomationDisable })
        {
            var control = JsonSerializer.Serialize(new { automationId = current.AutomationId, expectedRevision = current.Revision });
            Assert.Equal(tool == ToolCatalog.AutomationEnable,
                TriggerScheduleCommands.NormalizeTenant(definition, current, TenantAutomationMutationContract.Parse(tool, control), Guid.NewGuid(), At, ids).Enabled);
        }
    }
}
