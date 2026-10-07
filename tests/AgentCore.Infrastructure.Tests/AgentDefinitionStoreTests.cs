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
        var support = await store.GetAsync("customer-support", 3);
        var compliance = await store.GetAsync("compliance", 2);
        var general = await store.GetAsync("general-assistant", 16);
        Assert.NotNull(support);
        Assert.NotNull(compliance);
        Assert.NotNull(general);
        Assert.Equal("Riley", general!.Identity.Name);
        var latest = await store.GetAsync("general-assistant");
        Assert.Equal(16, latest!.Version);
        Assert.DoesNotContain("workspace.retain", latest.Environment!.ToolList);
        Assert.DoesNotContain("workspace.checkout", latest.Environment.ToolList);
        Assert.Equal(40, latest.Environment.ToolList.Count);
        Assert.NotNull(latest.TriggerPolicy);
        Assert.True(latest.TriggerPolicy!.Enabled);
        Assert.True(latest.TriggerPolicy.AllowIndefiniteRecurrence);
        Assert.True(latest.MemoryPolicy?.SessionMemory);
        Assert.True(latest.MemoryPolicy?.IdentityUserRetrieval);
        Assert.Null(await store.GetAsync("general-assistant", 15));
        Assert.Null(await store.GetAsync("general-assistant", 14));
        Assert.Contains("working directory", latest.SystemInstructions, StringComparison.OrdinalIgnoreCase);
        var environment = RoleEnvironments.Of(latest);
        Assert.Contains("automation.create", environment.ToolList);
        Assert.Contains("knowledge.retrieve", environment.ToolList);
        Assert.Contains("workspace.search", environment.ToolList);
        Assert.Contains("http.request", environment.ToolList);
        Assert.Contains("sandbox.run", environment.ToolList);
        Assert.Contains("email.send", environment.ToolList);
        Assert.Contains(ToolCatalog.BrowserNavigate, environment.ToolList);
        Assert.Contains(ToolCatalog.BrowserObserve, environment.ToolList);
        Assert.Contains(ToolCatalog.BrowserAct, environment.ToolList);
        Assert.Contains(ToolCatalog.BrowserClose, environment.ToolList);
        Assert.Equal(40, environment.ToolList.Count);
        Assert.Contains(ToolCatalog.WorkspaceCwd, environment.ToolList);
        Assert.Equal("browser.record.lookup", Assert.Single(latest.SkillList).Id);
        Assert.Equal(SkillProjection.OnDemand, latest.SkillList[0].Projection);
        Assert.Equal([ToolCatalog.BrowserNavigate], latest.SkillList[0].RequiredCapabilities);
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
            var definition = await store.GetAsync(id);
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
