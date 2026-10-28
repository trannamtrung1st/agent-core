using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Definitions;

namespace AgentCore.Infrastructure.Tests;

public sealed class AgentDefinitionStoreTests
{
    private static string RetiredPath(int version) => Path.Combine(FindAgents(), "..", "tests", "fixtures", "retired-builtins", $"general-assistant-v{version}.json");
    private static AgentDefinition RetiredDefinition(int version) => JsonSerializer.Deserialize<AgentDefinition>(File.ReadAllText(RetiredPath(version)),
        new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })!;
    [Fact]
    public void Retired_child_Definition_cannot_be_reinstalled_as_an_executable_catalog_file()
    {
        var directory = Directory.CreateTempSubdirectory("agent-core-retired-definition-");
        try
        {
            File.Copy(RetiredPath(34), Path.Combine(directory.FullName, "retired.json"));
            var error = Assert.Throws<AgentCoreException>(() => new FileAgentDefinitionStore(directory.FullName, SyntheticProviderAliases.Default));
            Assert.Contains("retired_background_contract", error.Message);
        }
        finally { directory.Delete(true); }
    }
    [Fact]
    public async Task General_assistant_v35_adds_canonical_cooperation_without_rewriting_frozen_predecessor_bytes()
    {
        var store = new FileAgentDefinitionStore(FindAgents(), SyntheticProviderAliases.Default);
        var old = RetiredDefinition(33);
        var latest = (await store.GetAsync("general-assistant", 35))!;
        Assert.Equal(35, latest.Version);
        Assert.All(await store.ListAsync(), definition => Assert.DoesNotContain(DefinitionExecutionPolicies.Of(definition).ToolList,
            name => name.StartsWith("background.", StringComparison.Ordinal)));
        Assert.Equal("634515c2225df4ee021dde76254fd7378aa5355de92eeab720e06f340ba9f0c6", Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(RetiredPath(33)))));
        var previous = RetiredDefinition(34);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(old), System.Text.Json.JsonSerializer.Serialize(previous with { Version = 33, AgentInstructions = old.AgentInstructions }));
        Assert.All(TenantTaskToolContract.Names, name => Assert.Contains(name, latest.ExecutionPolicy!.ToolList));
        Assert.DoesNotContain(latest.ExecutionPolicy!.ToolList, name => name.StartsWith("background.", StringComparison.Ordinal));
        Assert.Contains("captured Space", latest.AgentInstructions);
        Assert.DoesNotContain("durable Agent Workspace", latest.AgentInstructions);
    }

    [Fact]
    public async Task Production_default_is_current_demo_free_text_first_and_loads_without_optional_speech_aliases()
    {
        var directory = Directory.CreateTempSubdirectory("agent-core-production-default-");
        try
        {
            File.Copy(Path.Combine(FindAgents(), "general-assistant-v36.json"), Path.Combine(directory.FullName, "general-assistant-v36.json"));
            var standalone = new FileAgentDefinitionStore(directory.FullName, new ProviderAliasSet(new HashSet<string>(), new HashSet<string>()));
            var current = (await standalone.GetAsync("general-assistant"))!;
            Assert.Equal(36, current.Version); Assert.Equal("General Assistant", current.Identity.Name);
            Assert.Empty(current.SkillList); Assert.Empty(DefinitionExecutionPolicies.Of(current).KnowledgeList);
            Assert.False(current.Voice.Enabled); Assert.Null(current.SandboxPolicy); Assert.Null(current.ModelDefaults);
            Assert.DoesNotContain("Riley", current.AgentInstructions); Assert.DoesNotContain("fixture", current.Identity.Description);
            Assert.True(current.ExecutionPolicy!.Projection!.AlwaysCapabilities.Count <= 8);
            var catalog = new FileAgentDefinitionStore(FindAgents(), SyntheticProviderAliases.Default);
            var templates = AgentCore.Application.Security.ManagedGeneralAssistant.ProductTemplates(await catalog.ListAsync());
            Assert.Equal(36, Assert.Single(templates, d => d.Id == "general-assistant").Version);
            Assert.NotNull(await catalog.GetAsync("general-assistant", 35));
        }
        finally { directory.Delete(true); }
    }

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
        var general = await store.GetAsync("general-assistant", 35);
        Assert.NotNull(support);
        Assert.NotNull(compliance);
        Assert.NotNull(general);
        Assert.Equal("Riley", general!.Identity.Name);
        var latest = await store.GetAsync("general-assistant", 35);
        Assert.Equal(35, latest!.Version);
        for (var version = 21; version <= 34; version++) Assert.Null(await store.GetAsync("general-assistant", version));
        // Retired browser instructions are absent from the executable catalog.
        Assert.Null(await store.GetAsync("general-assistant", 20));
        Assert.Null(await store.GetAsync("secretary", 7));
        Assert.NotNull(await store.GetAsync("secretary", 8));
        Assert.Equal(10, (await store.GetAsync("secretary"))!.Version);
        Assert.DoesNotContain("workspace.retain", latest.ExecutionPolicy!.ToolList);
        Assert.DoesNotContain("workspace.checkout", latest.ExecutionPolicy.ToolList);
        Assert.True(latest.ExecutionPolicy.ToolList.Count > 40);
        Assert.NotNull(latest.ExecutionPolicy.Capabilities);
        Assert.DoesNotContain("browser.console_messages", latest.ExecutionPolicy.Projection!.AlwaysCapabilities);
        Assert.NotNull(latest.TriggerPolicy);
        Assert.True(latest.TriggerPolicy!.Enabled);
        Assert.True(latest.TriggerPolicy.AllowIndefiniteRecurrence);
        Assert.True(latest.MemoryPolicy?.SessionMemory);
        Assert.True(latest.MemoryPolicy?.IdentityUserRetrieval);
        Assert.Null(await store.GetAsync("general-assistant", 15));
        Assert.Null(await store.GetAsync("general-assistant", 14));
        Assert.Contains("working directory", latest.AgentInstructions, StringComparison.OrdinalIgnoreCase);
        var environment = DefinitionExecutionPolicies.Of(latest);
        Assert.Contains("automation.create", environment.ToolList);
        Assert.Contains("knowledge.retrieve", environment.ToolList);
        Assert.Contains("workspace.search", environment.ToolList);
        Assert.Contains("http.request", environment.ToolList);
        Assert.Contains(ToolCatalog.SandboxExec, environment.ToolList);
        Assert.Contains(ToolCatalog.SandboxReset, environment.ToolList);
        Assert.DoesNotContain("sandbox.run", environment.ToolList);
        Assert.Contains("email.send", environment.ToolList);
        Assert.Contains(ToolCatalog.BrowserNavigate, environment.ToolList);
        Assert.Contains(ToolCatalog.BrowserSnapshot, environment.ToolList);
        Assert.Contains(ToolCatalog.BrowserClick, environment.ToolList);
        Assert.Contains(ToolCatalog.BrowserClose, environment.ToolList);
        var predecessor = DefinitionExecutionPolicies.Of(RetiredDefinition(32));
        Assert.Equal(predecessor.ToolList.Where(name => !name.StartsWith("background.", StringComparison.Ordinal)).Order(StringComparer.Ordinal), environment.ToolList.Except(TenantContextToolContract.Names.Concat(TenantTaskToolContract.Names), StringComparer.Ordinal).Order(StringComparer.Ordinal));
        Assert.Equal(TenantContextToolContract.Names.Concat(TenantTaskToolContract.Names).Order(StringComparer.Ordinal), environment.ToolList.Except(predecessor.ToolList, StringComparer.Ordinal).Order(StringComparer.Ordinal));
        Assert.Contains(ToolCatalog.SandboxProfiles, environment.ToolList);
        Assert.Contains(ToolCatalog.WebDownload, environment.ToolList);
        Assert.Contains(ToolCatalog.OfficeDocumentCreate, environment.ToolList);
        Assert.Contains(ToolCatalog.DriveUpload, environment.ToolList);
        var frozen = RetiredDefinition(25);
        Assert.Equal(92, frozen.ExecutionPolicy!.ToolList.Count);
        Assert.DoesNotContain(ToolCatalog.OfficeDocumentCreate, frozen.ExecutionPolicy.ToolList);
        Assert.DoesNotContain(ToolCatalog.DriveUpload, frozen.ExecutionPolicy.ToolList);
        Assert.Contains("automation.preferences.inspect", environment.ToolList);
        Assert.Contains("automation.preferences.patch", environment.ToolList);
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
        Assert.DoesNotContain("[[speech:", general.AgentInstructions, StringComparison.Ordinal);
        Assert.DoesNotContain("TTS", general.AgentInstructions, StringComparison.Ordinal);
        Assert.All(general.Goals, goal => Assert.DoesNotContain("[[speech:", goal, StringComparison.Ordinal));
        Assert.Equal(1, examiner.InitiativePolicy.ConsecutiveCap);
        Assert.Equal(2, support!.InitiativePolicy.ConsecutiveCap);
        Assert.Equal(2, support.InitiativePolicy.MaxPerSilencePeriod);
        Assert.Equal(0, compliance!.InitiativePolicy.ConsecutiveCap);
        Assert.DoesNotContain("[[speech:", examiner.AgentInstructions, StringComparison.Ordinal);
        Assert.DoesNotContain("[[speech:", support!.AgentInstructions, StringComparison.Ordinal);
        Assert.DoesNotContain("[[speech:", compliance.AgentInstructions, StringComparison.Ordinal);
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
            Assert.DoesNotContain("Respond in", definition!.AgentInstructions, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("current language", definition.AgentInstructions, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(
                ConversationLanguagePolicy.PromptInstruction(definition.ConversationPolicy.Language),
                definition.AgentInstructions,
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Unknown_speech_alias_is_rejected()
    {
        var directory = Directory.CreateTempSubdirectory("agent-core-defs");
        try
        {
            File.WriteAllText(
                Path.Combine(directory.FullName, "broken.json"),
                """
                {
                  "schemaVersion": 2,
                  "id": "broken",
                  "version": 1,
                  "identity": {"name": "X", "role": "R", "description": "D", "tone": "T"},
                  "goals": ["g"],
                  "agentInstructions": "s",
                  "behaviorPolicy": {"interruptionStyle": "answerNewTurn", "acknowledgeInterruption": true, "avoidUnsupportedClaims": true},
                  "conversationPolicy": {"responseLength": "concise", "askOneQuestionAtATime": true, "language": "en", "maxOutputTokens": 256},
                  "initiativePolicy": {"enabled": false, "silenceThresholdMs": 8000, "cooldownMs": 30000, "maxPerSilencePeriod": 1, "triggers": ["longSilence"]},
                  "voice": {"enabled": true, "voiceId": "default", "speakingRate": 1.0},
                  "providerPreferences": {"speechRecognizer": "missing-stt", "speechSynthesizer": "primary-tts", "interruptionClassifier": "heuristic"},
                  "metadata": {}
                }
                """);
            var error = Assert.Throws<AgentCoreException>(() =>
                new FileAgentDefinitionStore(directory.FullName, SyntheticProviderAliases.Default));
            Assert.Equal("ValidationError", error.Code);
            Assert.Contains("missing-stt", error.Message, StringComparison.Ordinal);
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
