using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Application.Triggers;
using AgentCore.Domain.Security;
using AgentCore.Domain.Triggers;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Providers;

namespace AgentCore.Application.Tests;
public sealed class TenantAutomationMutationContractTests
{
    [Theory]
    [InlineData("{\"enabled\":false,\"executionTarget\":\"backgroundSession\",\"tenantId\":\"forged\"}")]
    [InlineData("{\"enabled\":false,\"enabled\":true,\"executionTarget\":\"backgroundSession\"}")]
    [InlineData("{\"enabled\":\"false\",\"executionTarget\":\"backgroundSession\"}")]
    [InlineData("{\"executionTarget\":\"backgroundSession\",\"triggers\":[{\"kind\":\"event\",\"source\":{\"kind\":\"builtin\",\"key\":\"run.completed\",\"tenantId\":\"forged\"}}]}")]
    [InlineData("{\"executionTarget\":\"backgroundSession\",\"coreEventKey\":\"run.completed\",\"relativeDelaySeconds\":60}")]
    public void Tenant_mutation_arguments_reject_forged_ambiguous_or_untyped_fields(string json) =>
        Assert.ThrowsAny<ArgumentException>(() => TenantAutomationMutationContract.Parse(ToolCatalog.AutomationCreate, json));
    [Fact]
    public void Relative_timing_uses_only_the_immutable_task_reference_and_consent_preserves_wall_clock_metadata()
    {
        var at = new DateTimeOffset(2026, 10, 22, 9, 0, 0, TimeSpan.Zero);
        var definition = SampleDefinitions.Examiner with { TriggerPolicy = new(true, true, true, true, true, true, 32, 365, 1, ["schedule"], true, 60) };
        var ids = new SystemIdGenerator(TimeProvider.System); var agent = new TenantAgent(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), new(Guid.NewGuid(), definition.Id, definition.Version, definition.Identity, AgentInstanceLifecycle.Active, at, at));
        var args = TenantAutomationMutationContract.Parse(ToolCatalog.AutomationCreate, """{"enabled":false,"name":"Exact task","instructions":"Do the accepted task.","relativeDelaySeconds":3600,"executionTarget":"backgroundSession","timeZone":"Asia/Ho_Chi_Minh"}""");
        var change = TriggerScheduleCommands.NormalizeTenant(definition, null, args, Guid.NewGuid(), at, ids);
        var proposed = TenantAutomationAuthoring.Build(agent, definition, null, change, new(TriggerAuthorizationOrigin.AgentTask, null, null, at.AddMinutes(2), at.AddMinutes(2)), at.AddMinutes(2), ModelCatalogFactory.Synthetic(), ids, null);
        var once = Assert.IsType<OneShotSchedule>(proposed.Schedule); Assert.Equal(at.AddHours(1), once.AtUtc); Assert.NotNull(once.LocalDate); Assert.NotNull(once.LocalTime);
        var value = new PreparedAutomationMutation(Guid.NewGuid(), Guid.NewGuid(), args.Tool, args.Json, null, proposed, new string('a',64), false);
        var saved = JsonSerializer.Deserialize<PreparedAutomationMutation>(JsonSerializer.Serialize(value))!;
        Assert.Equal(value, saved); Assert.Equal(value.GetHashCode(), saved.GetHashCode());
        Assert.True(TenantAutomationRules.Same(proposed, saved.Proposed)); Assert.Equal(once.LocalDate, ((OneShotSchedule)saved.Proposed.Schedule).LocalDate);
    }
    [Fact]
    public void Editing_the_same_event_source_preserves_omitted_filter_dispatch_enabled_and_child_identity()
    {
        var at = new DateTimeOffset(2026, 10, 22, 9, 0, 0, TimeSpan.Zero); var ids = new SystemIdGenerator(TimeProvider.System);
        var definition = SampleDefinitions.Examiner with { TriggerPolicy = new(true, true, true, true, true, true, 32, 365, 1, ["coreEvent"], true, 60) };
        var agent = new TenantAgent(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), new(Guid.NewGuid(), definition.Id, definition.Version, definition.Identity, AgentInstanceLifecycle.Active, at, at));
        var change = new TenantAutomationChange(false, "Events", "Retain the task", null,
            Events: [new(null, new CoreEventTrigger("run.completed", null, new EventDispatch(EventDispatchMode.CoalesceLatest, 90)), false)], RequiresTools: false);
        var current = TenantAutomationAuthoring.Build(agent, definition, null, change, new(TriggerAuthorizationOrigin.AgentTask, null, null, at, at), at, ModelCatalogFactory.Synthetic(), ids, null);
        var arguments = TenantAutomationMutationContract.Parse(ToolCatalog.AutomationUpdate, JsonSerializer.Serialize(new { automationId = current.AutomationId, expectedRevision = 1, coreEventKey = "run.completed", name = "Renamed" }));
        var normalized = TriggerScheduleCommands.NormalizeTenant(definition, current, arguments, Guid.NewGuid(), at, ids);
        var edited = TenantAutomationAuthoring.Build(agent, definition, current, normalized, current.Provenance.WithUpdated(at.AddMinutes(1)), at.AddMinutes(1), ModelCatalogFactory.Synthetic(), ids, null);
        var child = Assert.Single(edited.Triggers); Assert.Equal(current.Triggers[0].TriggerId, child.TriggerId); Assert.Equal(current.Triggers[0].Revision, child.Revision);
        Assert.False(child.Enabled); Assert.Equal(90, ((CoreEventTrigger)child.Configuration).Dispatch.WindowSeconds); Assert.Equal("Renamed", edited.Name);
    }
    [Fact]
    public void Receipt_evidence_rejects_unrelated_tools_and_discontinuous_revisions()
    {
        var proof = new TenantAutomationMutationReceipt(Guid.NewGuid(), ToolCatalog.AutomationUpdate, 4, 5, new string('a',64), new string('b',64));
        Assert.True(TenantAutomationMutationEvidence.Valid(proof)); Assert.False(TenantAutomationMutationEvidence.Valid(proof with { Revision = 6 }));
        Assert.False(TenantAutomationMutationEvidence.Valid(proof with { ToolName = "automation.run" }));
    }
}
