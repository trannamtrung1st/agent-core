using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
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
        Assert.NotNull(support);
        Assert.NotNull(compliance);
        Assert.Equal(1, examiner.InitiativePolicy.ConsecutiveCap);
        Assert.Equal(2, support!.InitiativePolicy.ConsecutiveCap);
        Assert.Equal(0, compliance!.InitiativePolicy.ConsecutiveCap);
        Assert.Null(missing);
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
