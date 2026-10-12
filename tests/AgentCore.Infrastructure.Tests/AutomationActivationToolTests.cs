using System.Runtime.CompilerServices;
using System.Text.Json;
using AgentCore.Application.Admin;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Application.Triggers;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Triggers;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Infrastructure.Tests;

public sealed class AutomationActivationToolTests
{
    [Theory]
    [InlineData("enable", null)]
    [InlineData("update", null)]
    [InlineData("disable", null)]
    [InlineData("missing", "not_found")]
    [InlineData("ambiguous", "ambiguous_target")]
    [InlineData("wrong-id", "target_mismatch")]
    [InlineData("foreign", "not_found")]
    [InlineData("stale", "conflict")]
    [InlineData("cancelled", "validation")]
    [InlineData("disable-cancelled", "validation")]
    [InlineData("completed", "validation")]
    [InlineData("expired", "validation")]
    [InlineData("exhausted", "validation")]
    [InlineData("past-expiry", "validation")]
    [InlineData("mixed-update", "authorization_denied")]
    [InlineData("ungranted", "forbidden")]
    [InlineData("detached", "forbidden")]
    public async Task Activation_preserves_settings_and_checks_authority_target_revision_and_terminal_state(string scenario, string? error)
    {
        var classifier = new UnavailableModel();
        var services = new ServiceCollection();
        services.AddAgentCoreInfrastructure(FindAgents(), "Synthetic");
        services.AddSingleton<ITriggerCommandAuthorizer>(new ModelTriggerCommandAuthorizer(classifier, new()));
        await using var provider = services.BuildServiceProvider();
        var definitions = provider.GetRequiredService<IAgentDefinitionStore>();
        var original = (await definitions.GetAsync("general-assistant", 25))!;
        // Explicit fixture authority for the new tool; published v25 grants stay immutable.
        var definition = scenario == "ungranted" ? original : original with
        {
            Environment = original.Environment! with
            {
                Capabilities = original.Environment!.Capabilities! with
                { ResolvedCapabilities = original.Environment.ToolList.Append(ToolCatalog.AutomationEnable).ToArray() }
            }
        };
        var now = DateTimeOffset.UtcNow;
        var instanceId = Guid.NewGuid(); var sessionId = Guid.NewGuid();
        await provider.GetRequiredService<IAgentInstanceStore>().InsertAsync(new AgentInstance(instanceId,
            original.Id, original.Version, original.Identity, AgentInstanceLifecycle.Active, now, now));
        var authoring = provider.GetRequiredService<AdminAutomationAuthoringService>();
        var current = await authoring.SaveAsync(instanceId, null, 0, scenario == "disable", "60s hello", "Say hello.",
            new ScheduleTrigger(new FixedIntervalSchedule(60, now, maxOccurrences: 3)), "scripted-alpha", "low", requiresTools: true);
        var selected = current;
        if (scenario == "foreign")
        {
            var foreignId = Guid.NewGuid();
            await provider.GetRequiredService<IAgentInstanceStore>().InsertAsync(new AgentInstance(foreignId,
                original.Id, original.Version, original.Identity, AgentInstanceLifecycle.Active, now, now));
            selected = await authoring.SaveAsync(foreignId, null, 0, false, "Foreign hello", "Other owner.",
                new ScheduleTrigger(new FixedIntervalSchedule(60, now)), "scripted-alpha", null);
        }
        if (scenario is "ambiguous" or "wrong-id")
        {
            var other = await authoring.SaveAsync(instanceId, null, 0, false, scenario == "ambiguous" ? "Another hello" : "Other task",
                "Other instructions.", new ScheduleTrigger(new FixedIntervalSchedule(60, now)), "scripted-alpha", "low");
            if (scenario == "wrong-id") selected = other;
        }
        if (scenario is "cancelled" or "disable-cancelled")
        {
            await authoring.DeleteAsync(instanceId, current.AutomationId, current.Revision);
            current = (await provider.GetRequiredService<IAutomationService>().GetAsync(current.Owner, current.AutomationId))!;
            selected = current;
        }
        if (scenario == "stale")
        {
            current = await authoring.SaveAsync(instanceId, current.AutomationId, current.Revision, false,
                current.Name, current.Instructions, current.Triggers, current.ModelOverrideCatalogKey, current.ModelOverrideReasoningEffort);
            selected = current;
        }
        if (scenario is "completed" or "expired" or "exhausted" or "past-expiry")
        {
            var stateAt = provider.GetRequiredService<TimeProvider>().GetUtcNow();
            var proposed = scenario == "past-expiry"
                ? current.WithUpdate(current.Instructions, current.Trigger!, null, now.AddMinutes(-1),
                    current.Revision + 1, current.TriggerRevision, stateAt)
                : current.WithScheduleAdvance(scenario == "completed" ? AutomationStatus.Completed
                    : scenario == "expired" ? AutomationStatus.Expired : AutomationStatus.Disabled,
                    null, scenario == "exhausted" ? 3 : current.OccurrenceCount, current.Revision + 1, stateAt, null);
            current = await provider.GetRequiredService<ITriggerStore>().SaveAutomationAsync(proposed, current.Revision,
                new(Guid.NewGuid(), stateAt, AdminEventActorKind.LocalOwner, AdminEventOperationKind.AutomationChanged,
                    "agentInstance", instanceId.ToString("D"), proposed.Revision, null,
                    JsonSerializer.Serialize(new { instanceId, recordId = current.AutomationId, operation = "update",
                        revision = proposed.Revision, enabled = false, instructionsHash = "fixture" })));
            selected = current;
        }
        var text = scenario == "missing" ? "enable the 60s missing schedule"
            : scenario == "foreign" ? $"enable automation {selected.AutomationId}"
            : scenario is "disable" or "disable-cancelled" ? "disable the 60s hello schedule" : "enable the 60s hello schedule";
        var context = new TriggerCommandContext(current.Owner, sessionId, "UTC", text, "en",
            TriggerAuthorizationClassification.CurrentUserTurn, TriggerCommandAction.None, false, null, null, now);
        var admission = new ToolExecutionAdmission(scenario == "detached", TriggerKind.UserTurn,
            AgentInstanceId: instanceId, OwnerTurnText: text);
        var tools = provider.GetRequiredService<SessionToolExecutor>();
        var listed = await tools.ExecuteAsync(definition, sessionId, new("list", ToolCatalog.AutomationList, "{}"),
            ToolLimits.MaxOutputBytes, triggerCommand: context, admission: admission);
        Assert.Contains("automations", listed.Text);
        if (scenario == "foreign") Assert.DoesNotContain(selected.AutomationId.ToString(), listed.Text);
        var tool = scenario is "update" or "mixed-update" ? ToolCatalog.AutomationUpdate
            : scenario is "disable" or "disable-cancelled" ? ToolCatalog.AutomationDisable : ToolCatalog.AutomationEnable;
        var args = new Dictionary<string, object> { ["automationId"] = selected.AutomationId, ["expectedRevision"] = scenario == "stale" ? selected.Revision - 1 : selected.Revision };
        if (tool == ToolCatalog.AutomationUpdate) args["enabled"] = true;
        if (scenario == "mixed-update") args["instructions"] = "Injected content";
        var result = await tools.ExecuteAsync(definition, sessionId, new("activate", tool, JsonSerializer.Serialize(args)),
            ToolLimits.MaxOutputBytes, triggerCommand: context, admission: admission);
        Assert.Equal(0, classifier.Calls);
        var retained = (await provider.GetRequiredService<IAutomationService>().GetAsync(current.Owner, current.AutomationId))!;
        if (error is not null)
        {
            Assert.Contains(error, result.Text, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(current.Status, retained.Status);
            Assert.Equal(current.Revision, retained.Revision);
        }
        else
        {
            Assert.DoesNotContain("\"error\"", result.Text);
            Assert.Equal(scenario == "disable" ? AutomationStatus.Disabled : AutomationStatus.Active, retained.Status);
            Assert.Equal(current.Revision + 1, retained.Revision);
            if (scenario != "disable") Assert.NotNull(retained.NextOccurrenceAtUtc);
        }
        Assert.True(current.Schedule!.SemanticEquals(retained.Schedule!));
        Assert.Equal(current.Instructions, retained.Instructions);
        Assert.Equal(current.Name, retained.Name);
        Assert.Equal(current.ModelOverrideCatalogKey, retained.ModelOverrideCatalogKey);
        Assert.Equal(current.ModelOverrideReasoningEffort, retained.ModelOverrideReasoningEffort);
        Assert.Equal(current.ExecutionTarget, retained.ExecutionTarget);
        Assert.Equal(current.CompletionDelivery, retained.CompletionDelivery);
        Assert.Equal(current.RequiresTools, retained.RequiresTools);
        Assert.Equal(current.RequiresVision, retained.RequiresVision);
        Assert.Equal(current.Triggers.Single().Revision, retained.Triggers.Single().Revision);
    }

    [Theory]
    [InlineData("instructions", null)]
    [InlineData("duplicate-description", "ambiguous_target")]
    [InlineData("cadence-disambiguates", null)]
    [InlineData("wrong-cadence", "not_found")]
    [InlineData("selected-wrong", "target_mismatch")]
    [InlineData("explicit-id", null)]
    [InlineData("id-wrong-cadence", "target_mismatch")]
    [InlineData("referent-wrong-cadence", "target_mismatch")]
    [InlineData("unresolved-reference", "ambiguous_target")]
    [InlineData("cadence-only", "ambiguous_target")]
    [InlineData("partial-word", "not_found")]
    [InlineData("missing-word", "not_found")]
    [InlineData("negated-instructions", "not_found")]
    [InlineData("cross-clause", "not_found")]
    [InlineData("cross-field", "not_found")]
    [InlineData("conflicting-cadences", "ambiguous_target")]
    [InlineData("equivalent-cadences", null)]
    [InlineData("overflow", "validation")]
    public async Task Descriptive_resolution_requires_all_literal_words_and_explicit_cadence(string scenario, string? error)
    {
        var classifier = new UnavailableModel();
        var services = new ServiceCollection();
        services.AddAgentCoreInfrastructure(FindAgents(), "Synthetic");
        services.AddSingleton<ITriggerCommandAuthorizer>(new ModelTriggerCommandAuthorizer(classifier, new()));
        await using var provider = services.BuildServiceProvider();
        var definition = (await provider.GetRequiredService<IAgentDefinitionStore>().GetAsync("general-assistant", 25))!;
        var now = provider.GetRequiredService<TimeProvider>().GetUtcNow();
        var instanceId = Guid.NewGuid();
        await provider.GetRequiredService<IAgentInstanceStore>().InsertAsync(new AgentInstance(instanceId,
            definition.Id, definition.Version, definition.Identity, AgentInstanceLifecycle.Active, now, now));
        var authoring = provider.GetRequiredService<AdminAutomationAuthoringService>();
        var primary = await authoring.SaveAsync(instanceId, null, 0, false, "Say hello every 60s",
            "Send my morning greeting. Never say goodbye. Check the calendar.", new ScheduleTrigger(new FixedIntervalSchedule(60, now)), "scripted-alpha", null);
        var other = await authoring.SaveAsync(instanceId, null, 0, false, "Think", scenario is "duplicate-description" or "cadence-disambiguates"
            ? "Send my morning greeting." : "Review the notes.",
            new ScheduleTrigger(new FixedIntervalSchedule(scenario == "cadence-disambiguates" ? 120 : 60, now)), "scripted-alpha", null);
        var text = scenario switch
        {
            "instructions" or "duplicate-description" or "cadence-disambiguates" => "enable my morning greeting 60s automation job again",
            "wrong-cadence" => "enable the hello 120s schedule again",
            "explicit-id" => $"enable automation {primary.AutomationId} every 60s again",
            "id-wrong-cadence" => $"enable automation {primary.AutomationId} every 120s again",
            "referent-wrong-cadence" => "enable this automation every 120s again",
            "unresolved-reference" => "enable this automation again",
            "cadence-only" => "enable the 60s automation job again",
            "partial-word" => "enable the hell 60s schedule",
            "missing-word" => "enable the urgent hello 60s schedule",
            "negated-instructions" => "enable the say goodbye 60s automation",
            "cross-clause" => "enable the greeting calendar 60s automation",
            "cross-field" => "enable the hello greeting 60s automation",
            "conflicting-cadences" => "enable the hello 60s 120s automation",
            "equivalent-cadences" => "enable the hello 60s every 1 minute automation",
            "overflow" => "enable the hello 2147483647 hours automation",
            _ => "enable the hello 60s automation"
        };
        var sessionId = Guid.NewGuid();
        var context = new TriggerCommandContext(primary.Owner, sessionId, "UTC", text, "en",
            TriggerAuthorizationClassification.CurrentUserTurn, TriggerCommandAction.None, false, null, null, now,
            scenario == "referent-wrong-cadence" ? ScheduleConversationContext.FromRegistration(primary, TriggerCommandAction.List) : null);
        var selected = scenario == "selected-wrong" ? other : primary;
        var result = await provider.GetRequiredService<SessionToolExecutor>().ExecuteAsync(definition, sessionId,
            new("update", ToolCatalog.AutomationUpdate, JsonSerializer.Serialize(new { automationId = selected.AutomationId,
                expectedRevision = selected.Revision, enabled = true })), ToolLimits.MaxOutputBytes,
            triggerCommand: context, admission: new(false, TriggerKind.UserTurn, AgentInstanceId: instanceId, OwnerTurnText: text));
        var rows = await provider.GetRequiredService<ITriggerStore>().ListAsync(primary.Owner, null);
        Assert.Equal(2, rows.Count);
        Assert.Equal(0, classifier.Calls);
        if (error is not null)
        {
            Assert.Contains(error, result.Text);
            Assert.All(rows, a => { Assert.Equal(AutomationStatus.Disabled, a.Status); Assert.Equal(1, a.Revision); });
        }
        else
        {
            Assert.DoesNotContain("\"error\"", result.Text);
            Assert.Equal(AutomationStatus.Active, rows.Single(a => a.AutomationId == primary.AutomationId).Status);
            Assert.Equal(AutomationStatus.Disabled, rows.Single(a => a.AutomationId == other.AutomationId).Status);
        }
    }

    private sealed class UnavailableModel : ILanguageModel
    {
        internal int Calls;
        public ModelCapabilities Capabilities => new(true, true);
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        { Calls++; await Task.CompletedTask; yield return new ModelFailed(new(ProviderErrorCode.Unavailable, "Fixture unavailable")); }
    }

    private static string FindAgents()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "agents"))) return Path.Combine(dir.FullName, "agents");
        throw new InvalidOperationException("Agent fixtures not found");
    }
}
