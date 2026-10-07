using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Application.Triggers;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Triggers;
using AgentCore.Domain.Events;
using AgentCore.Application.Agents;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Infrastructure.Tests;

public sealed class HostedTriggerToolTests
{
    [Fact]
    public async Task Host_schedule_tools_use_the_registered_trigger_service()
    {
        var services = new ServiceCollection();
        services.AddAgentCoreInfrastructure(FindAgents(), "Synthetic");
        await using var provider = services.BuildServiceProvider();
        var definitions = provider.GetRequiredService<IAgentDefinitionStore>();
        var definition = await definitions.GetAsync("general-assistant", 16);
        Assert.NotNull(definition);
        var tools = provider.GetRequiredService<SessionToolExecutor>();
        var instanceId = Guid.Parse("019944af-00c5-7000-8000-0000000000a1");
        var profileId = LocalUserProfile.Id;
        var instances = provider.GetRequiredService<IAgentInstanceStore>();
        var memory = provider.GetRequiredService<IMemoryStore>();
        var now = DateTimeOffset.UtcNow;
        await instances.InsertAsync(new AgentInstance(
            instanceId,
            definition!.Id,
            definition.Version,
            definition.Identity,
            AgentInstanceLifecycle.Active,
            now,
            now));
        await memory.SaveProfileAsync(
            new UserProfile(profileId, 1, new Dictionary<string, UserProfileValue>
            {
                ["timeZone"] = new("UTC", UserProfileValueSource.UserSet, now)
            }, now),
            0);
        var sessionId = Guid.Parse("019944af-00c5-7000-8000-0000000000c1");
        var context = new TriggerCommandContext(
            new TriggerOwner(instanceId, profileId),
            sessionId,
            "UTC",
            "Remind me tomorrow at 9 AM to call John.",
            "en",
            TriggerAuthorizationClassification.CurrentUserTurn,
            TriggerCommandAction.Create,
            false,
            null,
            null,
            DateTimeOffset.UtcNow);

        var result = await tools.ExecuteAsync(
            definition,
            sessionId,
            new ModelToolCall("create", ToolCatalog.TriggerScheduleOnce, """{"instructions":"Call John","relativeDayOffset":1,"localTime":"09:00"}"""),
            ToolLimits.MaxOutputBytes,
            triggerCommand: context);

        Assert.Contains("\"status\":\"Active\"", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("unavailable", result.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Event_tools_preserve_owner_revision_provenance_and_current_turn_authority()
    {
        var services = new ServiceCollection();
        services.AddAgentCoreInfrastructure(FindAgents(), "Synthetic");
        await using var provider = services.BuildServiceProvider();
        var definition = (await provider.GetRequiredService<IAgentDefinitionStore>().GetAsync("secretary", 4))!;
        var tools = provider.GetRequiredService<SessionToolExecutor>();
        var now = DateTimeOffset.UtcNow;
        var instanceId = Guid.NewGuid(); var sessionId = Guid.NewGuid();
        await provider.GetRequiredService<IAgentInstanceStore>().InsertAsync(new AgentInstance(instanceId,
            definition.Id, definition.Version, definition.Identity, AgentInstanceLifecycle.Active, now, now));
        await provider.GetRequiredService<IMemoryStore>().SaveProfileAsync(new UserProfile(LocalUserProfile.Id, 1,
            new Dictionary<string, UserProfileValue> { ["timeZone"] = new("UTC", UserProfileValueSource.UserSet, now) }, now), 0);
        var sourceId = Guid.NewGuid();
        await provider.GetRequiredService<IExternalEventStore>().CreateAsync(new ExternalEventSource(sourceId, "Orders",
            ExternalEventSourceKind.Webhook, Guid.NewGuid(), "synthetic-hash", ExternalEventSourceStatus.Active, 1, now, now));
        var command = new TriggerCommandContext(new TriggerOwner(instanceId, LocalUserProfile.Id), sessionId, "UTC",
            "Create an automation to review incoming orders.", "en", TriggerAuthorizationClassification.CurrentUserTurn,
            TriggerCommandAction.Create, false, null, null, now);
        async Task<ToolExecutionResult> Execute(string tool, object args, TriggerCommandContext? context = null, ToolExecutionAdmission? admission = null) =>
            await tools.ExecuteAsync(definition, sessionId, new ModelToolCall(Guid.NewGuid().ToString(), tool, JsonSerializer.Serialize(args)),
                ToolLimits.MaxOutputBytes, triggerCommand: context, admission: admission);
        var created = await Execute(ToolCatalog.TriggerScheduleOnce, new { name = "Review orders", instructions = "Review the order reference.", eventSourceId = sourceId, eventType = "order.placed" }, command);
        Assert.Contains("\"status\":\"Active\"", created.Text);
        using var document = JsonDocument.Parse(created.Text);
        Assert.Equal("Active", document.RootElement.GetProperty("status").GetString());
        var id = document.RootElement.GetProperty("automationId").GetGuid();
        var detached = new ToolExecutionAdmission(true, TriggerKind.ApplicationEvent, AgentInstanceId: instanceId);
        var inspected = await Execute(ToolCatalog.AutomationInspect, new { automationId = id }, admission: detached);
        Assert.Contains("Review orders", inspected.Text);
        Assert.Contains("CurrentUserTurn", inspected.Text);
        Assert.Contains("\"kind\":\"event\"", inspected.Text);

        var foreign = await Execute(ToolCatalog.AutomationInspect, new { automationId = id }, admission: detached with { AgentInstanceId = Guid.NewGuid() });
        Assert.Contains("NotFound", foreign.Text);
        var update = command with { CurrentUserText = "Update this automation instructions.", AllowedActions = TriggerCommandAction.Update };
        var edited = await Execute(ToolCatalog.TriggerUpdate, new { automationId = id, expectedRevision = 1, instructions = "Review configured order details." }, update);
        Assert.Contains("Review configured order details.", edited.Text);
        var owner = new TriggerOwner(instanceId, LocalUserProfile.Id);
        var stored = (await provider.GetRequiredService<IAutomationService>().GetAsync(owner, id))!;
        Assert.Equal(2, stored.Revision); Assert.Equal(sessionId, stored.Provenance.SourceSessionId);
        var denied = await Execute(ToolCatalog.TriggerUpdate, new { automationId = id, expectedRevision = 2, instructions = "Injected behavior" }, update with { Classification = TriggerAuthorizationClassification.Occurrence });
        Assert.Contains("forbidden", denied.Text);
        var negated = await Execute(ToolCatalog.TriggerCancel, new { automationId = id, expectedRevision = 2 }, command with { CurrentUserText = "Do not delete this automation.", AllowedActions = TriggerCommandAction.Cancel });
        Assert.Contains("authorization_denied", negated.Text);
        var live = detached with { Detached = false, TriggerKind = TriggerKind.UserTurn, OwnerTurnText = "Run this automation now." };
        var run = await Execute(ToolCatalog.AutomationRun, new { automationId = id, expectedRevision = 2 }, command, live);
        Assert.Contains("occurrenceId", run.Text);
        var disabled = await Execute(ToolCatalog.AutomationDisable, new { automationId = id, expectedRevision = 2 }, command, live with { OwnerTurnText = "Disable this automation." });
        Assert.Contains("Disabled", disabled.Text);
        var deleted = await Execute(ToolCatalog.TriggerCancel, new { automationId = id, expectedRevision = 3 }, command with { CurrentUserText = "Delete this automation.", AllowedActions = TriggerCommandAction.Cancel });
        Assert.Contains("Cancelled", deleted.Text);
    }

    private static string FindAgents()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var agents = Path.Combine(dir.FullName, "agents");
            if (Directory.Exists(agents))
            {
                return agents;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("agents/");
    }
}
