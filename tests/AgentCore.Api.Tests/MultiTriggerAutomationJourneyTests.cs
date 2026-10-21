using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AgentCore.Application.Admin;
using AgentCore.Application.Events;
using AgentCore.Application.Ports;
using AgentCore.Contracts.Http;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Events;
using AgentCore.Domain.Triggers;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed class MultiTriggerAutomationJourneyTests
{

    private sealed class ExplicitUserAuthorizer : AgentCore.Application.Triggers.ITriggerCommandAuthorizer
    {
        public ValueTask<AgentCore.Application.Triggers.TriggerCommandAuthorizationVerdict> AuthorizeAsync(AgentCore.Application.Triggers.AutomationAuthorizationRequest proposed, CancellationToken ct = default)
            => ValueTask.FromResult(new AgentCore.Application.Triggers.TriggerCommandAuthorizationVerdict(AgentCore.Application.Triggers.TriggerCommandAuthorizationDecision.Allow));
    }

    [Fact(Timeout = 60000)]
    public async Task Chat_collection_authoring_retains_siblings_and_occurrence_context_cannot_write()
    {
        await using var host = new ExperienceHost(Path.Combine(Path.GetTempPath(), $"chat-children-{Guid.NewGuid():N}.db"));
        var s = host.Services;
        var instance = await s.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 21);
        await s.GetRequiredService<ILocalUserProfileService>().GetLocalProfileAsync();
        var owner = new TriggerOwner(instance.InstanceId, LocalUserProfile.Id);
        var definitions = s.GetRequiredService<IAgentDefinitionStore>();
        var definition = (await definitions.GetAsync("general-assistant", 21))!;
        var now = DateTimeOffset.UtcNow;
        var context = new AgentCore.Application.Triggers.TriggerCommandContext(owner, Guid.NewGuid(), "UTC", "Create this disabled automation", "en",
            AgentCore.Application.Triggers.TriggerAuthorizationClassification.CurrentUserTurn, Guid.NewGuid(), now);
        var first = Guid.NewGuid(); var second = Guid.NewGuid();
        async Task<AgentCore.Application.Tools.ToolExecutionResult> Execute(string tool, object args, AgentCore.Application.Triggers.TriggerCommandContext command) =>
            await AgentCore.Application.Triggers.TriggerScheduleCommands.ExecuteAsync(definition, s.GetRequiredService<IAutomationService>(), tool,
                JsonSerializer.SerializeToElement(args), command, default, new ExplicitUserAuthorizer(), s.GetRequiredService<IAgentInstanceStore>(), definitions,
                s.GetRequiredService<IMemoryStore>(), s.GetRequiredService<AgentCore.Application.Triggers.AdminAutomationAuthoringService>());
        var created = await Execute(AgentCore.Application.Tools.ToolCatalog.AutomationCreate, new { name = "Two independent signals", instructions = "Review each signal quietly.", executionTarget = "backgroundSession",
            triggers = new[] { new { triggerId = first, revision = 1, enabled = true, kind = "event", source = new { kind = "builtin", key = "run.failed" }, filterExpression = "true" },
                new { triggerId = second, revision = 1, enabled = true, kind = "event", source = new { kind = "builtin", key = "session.ended" }, filterExpression = "false" } } }, context);
        Assert.DoesNotContain("\"error\"", created.Text);
        var store = s.GetRequiredService<ITriggerStore>(); var saved = Assert.Single(await store.ListAsync(owner, null));
        Assert.Equal(AutomationStatus.Disabled, saved.Status); Assert.Equal(2, saved.Triggers.Count);
        var updated = await Execute(AgentCore.Application.Tools.ToolCatalog.AutomationUpdate, new { automationId = saved.AutomationId, expectedRevision = saved.Revision, name = "Renamed task" },
            context);
        Assert.DoesNotContain("\"error\"", updated.Text);
        saved = (await store.GetAsync(owner, saved.AutomationId))!;
        Assert.Equal(new[] { first, second }.Order(), saved.Triggers.Select(t => t.TriggerId).Order()); Assert.All(saved.Triggers, t => Assert.Equal(1, t.Revision));
        var blockedEnable = await Execute(AgentCore.Application.Tools.ToolCatalog.AutomationUpdate, new { automationId = saved.AutomationId, expectedRevision = saved.Revision, enabled = true }, context);
        Assert.Contains("error", blockedEnable.Text); Assert.Equal(AutomationStatus.Disabled, (await store.GetAsync(owner, saved.AutomationId))!.Status);
        var denied = await Execute(AgentCore.Application.Tools.ToolCatalog.AutomationUpdate, new { automationId = saved.AutomationId, expectedRevision = saved.Revision, name = "Unauthorized" },
            context with { Classification = AgentCore.Application.Triggers.TriggerAuthorizationClassification.Occurrence });
        Assert.Contains("error", denied.Text); Assert.Equal("Renamed task", (await store.GetAsync(owner, saved.AutomationId))!.Name);
    }

}
