using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Definitions;

namespace AgentCore.Infrastructure.Tests;

public sealed class AgentDefinitionStoreTests
{
    [Fact]
    public async Task Demo_definitions_deserialize_and_unknown_version_is_missing()
    {
        var store = new FileAgentDefinitionStore(FindAgents(), SyntheticProviderAliases.Default);
        var examiner = await store.GetAsync("examiner", 1);
        var missing = await store.GetAsync("examiner", 99);
        Assert.NotNull(examiner);
        Assert.Equal("Alex", examiner!.Identity.Name);
        var support = await store.GetAsync("customer-support", 1);
        var compliance = await store.GetAsync("compliance", 1);
        var general = await store.GetAsync("general-assistant", 1);
        Assert.NotNull(support);
        Assert.NotNull(compliance);
        Assert.NotNull(general);
        Assert.Equal("Riley", general!.Identity.Name);
        var latest = await store.GetAsync("general-assistant");
        Assert.Equal(15, latest!.Version);
        Assert.True(WorkspaceSemantics.IsV2(latest));
        Assert.DoesNotContain(ToolCatalog.WorkspaceRetain, latest.Environment!.ToolList);
        Assert.DoesNotContain(ToolCatalog.WorkspaceCheckout, latest.Environment.ToolList);
        Assert.Equal(31, latest.Environment.ToolList.Count);
        Assert.NotNull(latest.TriggerPolicy);
        Assert.True(latest.TriggerPolicy!.Enabled);
        Assert.True(latest.TriggerPolicy.AllowIndefiniteRecurrence);
        Assert.True(latest.MemoryPolicy?.SessionMemory);
        Assert.True(latest.MemoryPolicy?.IdentityUserRetrieval);
        var schedulingOnly = await store.GetAsync("general-assistant", 8);
        Assert.NotNull(schedulingOnly);
        Assert.False(schedulingOnly!.MemoryPolicy?.SessionMemory ?? false);
        var pinned = await store.GetAsync("general-assistant", 7);
        Assert.Null(pinned!.TriggerPolicy);
        Assert.Contains("working directory", latest.SystemInstructions, StringComparison.OrdinalIgnoreCase);
        var environment = RoleEnvironments.Of(latest);
        Assert.Contains("trigger.schedule_once", environment.ToolList);
        Assert.Contains("knowledge.retrieve", environment.ToolList);
        Assert.Contains("workspace.search", environment.ToolList);
        Assert.Contains("http.request", environment.ToolList);
        Assert.Contains("sandbox.run", environment.ToolList);
        Assert.Contains("email.send", environment.ToolList);
        Assert.Contains(ToolCatalog.BrowserNavigate, environment.ToolList);
        Assert.Contains(ToolCatalog.BrowserObserve, environment.ToolList);
        Assert.Contains(ToolCatalog.BrowserAct, environment.ToolList);
        Assert.Contains(ToolCatalog.BrowserClose, environment.ToolList);
        Assert.Equal(31, environment.ToolList.Count);
        Assert.Contains(ToolCatalog.WorkspaceCwd, environment.ToolList);
        var legacy = (await store.GetAsync("general-assistant", 14))!;
        Assert.False(WorkspaceSemantics.IsV2(legacy));
        Assert.Contains(ToolCatalog.WorkspaceRetain, legacy.Environment!.ToolList);
        Assert.Contains(ToolCatalog.WorkspaceCheckout, legacy.Environment.ToolList);
        Assert.Equal("browser.record.lookup", Assert.Single(latest.SkillList).Id);
        Assert.Equal(["fixture-record-lookup"], latest.SkillList[0].ActivationKeywords);
        Assert.Equal([ToolCatalog.BrowserNavigate], latest.SkillList[0].RequiredCapabilities);
        var previous = (await store.GetAsync("general-assistant", 10))!;
        Assert.Equal(23, RoleEnvironments.Of(previous).ToolList.Count);
        Assert.DoesNotContain(ToolCatalog.BrowserNavigate, RoleEnvironments.Of(previous).ToolList);
        Assert.Empty(previous.SkillList);
        Assert.Equal(previous.TriggerPolicy!.Enabled, latest.TriggerPolicy!.Enabled);
        Assert.Equal(previous.TriggerPolicy.AllowUserScheduling, latest.TriggerPolicy.AllowUserScheduling);
        Assert.Equal(previous.TriggerPolicy.AllowOneShot, latest.TriggerPolicy.AllowOneShot);
        Assert.Equal(previous.TriggerPolicy.AllowDaily, latest.TriggerPolicy.AllowDaily);
        Assert.Equal(previous.TriggerPolicy.AllowWeekly, latest.TriggerPolicy.AllowWeekly);
        Assert.Equal(previous.TriggerPolicy.AllowIndefiniteRecurrence, latest.TriggerPolicy.AllowIndefiniteRecurrence);
        Assert.Equal(previous.TriggerPolicy.MaxActiveRegistrations, latest.TriggerPolicy.MaxActiveRegistrations);
        Assert.Equal(previous.TriggerPolicy.OneShotHorizonDays, latest.TriggerPolicy.OneShotHorizonDays);
        Assert.Equal(previous.TriggerPolicy.MinRecurrenceDays, latest.TriggerPolicy.MinRecurrenceDays);
        Assert.Equal(previous.TriggerPolicy.AllowFixedInterval, latest.TriggerPolicy.AllowFixedInterval);
        Assert.Equal(previous.TriggerPolicy.MinFixedIntervalSeconds, latest.TriggerPolicy.MinFixedIntervalSeconds);
        Assert.Equal(previous.TriggerPolicy.AllowedSourceKinds, latest.TriggerPolicy.AllowedSourceKinds);
        Assert.DoesNotContain("demo.sensitive_action", environment.ToolList);
        Assert.DoesNotContain("attachments.read", environment.ToolList);
        Assert.Contains(environment.KnowledgeList, source => source.Identity == "support-order-policy");
        Assert.Contains(environment.KnowledgeList, source => source.Identity == "compliance-retention");
        Assert.True(ConversationLanguagePolicy.IsAuto(general.ConversationPolicy.Language));
        Assert.False(general.InitiativePolicy.Enabled);
        Assert.Equal(4096, general.ConversationPolicy.MaxOutputTokens);
        Assert.DoesNotContain("[[speech:", general.SystemInstructions, StringComparison.Ordinal);
        Assert.DoesNotContain("TTS", general.SystemInstructions, StringComparison.Ordinal);
        Assert.All(general.Goals, goal => Assert.DoesNotContain("[[speech:", goal, StringComparison.Ordinal));
        Assert.Equal(1, examiner.InitiativePolicy.ConsecutiveCap);
        Assert.Equal(2, support!.InitiativePolicy.ConsecutiveCap);
        Assert.Equal(2, support.InitiativePolicy.MaxPerSilencePeriod);
        Assert.Equal(0, compliance!.InitiativePolicy.ConsecutiveCap);
        Assert.DoesNotContain("[[speech:", examiner.SystemInstructions, StringComparison.Ordinal);
        Assert.DoesNotContain("[[speech:", support!.SystemInstructions, StringComparison.Ordinal);
        Assert.DoesNotContain("[[speech:", compliance.SystemInstructions, StringComparison.Ordinal);
        Assert.Null(missing);
        Assert.Equal("en", examiner!.ConversationPolicy.Language);
        Assert.Equal("en", support!.ConversationPolicy.Language);
        Assert.Equal("en", compliance.ConversationPolicy.Language);
    }

    [Fact]
    public async Task Demo_definitions_do_not_duplicate_language_policy_in_system_instructions()
    {
        var store = new FileAgentDefinitionStore(FindAgents(), SyntheticProviderAliases.Default);
        foreach (var id in new[] { "examiner", "customer-support", "compliance", "general-assistant" })
        {
            var definition = await store.GetAsync(id, 1);
            Assert.NotNull(definition);
            Assert.DoesNotContain("Respond in", definition!.SystemInstructions, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("current language", definition.SystemInstructions, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(
                ConversationLanguagePolicy.PromptInstruction(definition.ConversationPolicy.Language),
                definition.SystemInstructions,
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Missing_language_model_alias_is_rejected()
    {
        var directory = Directory.CreateTempSubdirectory("agent-core-defs");
        try
        {
            File.WriteAllText(
                Path.Combine(directory.FullName, "broken.json"),
                """
                {
                  "schemaVersion": 1,
                  "id": "broken",
                  "version": 1,
                  "identity": {"name": "X", "role": "R", "description": "D", "tone": "T"},
                  "goals": ["g"],
                  "systemInstructions": "s",
                  "behaviorPolicy": {"interruptionStyle": "answerNewTurn", "acknowledgeInterruption": true, "avoidUnsupportedClaims": true},
                  "conversationPolicy": {"responseLength": "concise", "askOneQuestionAtATime": true, "language": "en", "maxOutputTokens": 256},
                  "initiativePolicy": {"enabled": false, "silenceThresholdMs": 8000, "cooldownMs": 30000, "maxPerSilencePeriod": 1, "triggers": ["longSilence"]},
                  "voice": {"enabled": false, "voiceId": "default", "speakingRate": 1.0},
                  "providerPreferences": {"languageModel": "missing-llm", "interruptionClassifier": "heuristic"},
                  "metadata": {}
                }
                """);
            var error = Assert.Throws<AgentCoreException>(() =>
                new FileAgentDefinitionStore(directory.FullName, SyntheticProviderAliases.Default));
            Assert.Equal("ValidationError", error.Code);
            Assert.Contains("missing-llm", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            directory.Delete(true);
        }
    }

    private static string FindAgents()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var agents = Path.Combine(dir.FullName, "agents");
            if (Directory.Exists(agents) && File.Exists(Path.Combine(agents, "examiner.json")))
            {
                return agents;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("agents/");
    }
}
