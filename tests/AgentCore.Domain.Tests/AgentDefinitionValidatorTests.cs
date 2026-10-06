using AgentCore.Domain.Definitions;

namespace AgentCore.Domain.Tests;

public sealed class AgentDefinitionValidatorTests
{
    [Fact]
    public void Rejects_non_positive_version()
    {
        var definition = new AgentDefinition(
            1,
            "examiner",
            0,
            new AgentIdentity("Alex", "role", "desc", "tone"),
            ["goal"],
            "instructions",
            new BehaviorPolicy("acknowledgeThenContinue", true, true),
            new ConversationPolicy("concise", true, "en", 256),
            new InitiativePolicy(true, 8000, 30000, 1, ["longSilence"]),
            new VoiceConfiguration(true, "default", 1.0),
            new ProviderPreferences("primary-llm", "primary-stt", "primary-tts"),
            new Dictionary<string, string>());
        var error = Assert.Throws<ArgumentException>(() => AgentDefinitionValidator.Validate(definition));
        Assert.Contains("version", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Allows_repeated_silence_cap_and_defaults_omitted_consecutive_turns()
    {
        var policy = new InitiativePolicy(true, 8000, 30000, 3, ["longSilence"], MaxConsecutiveProactiveTurns: 3);
        Assert.Equal(3, policy.ConsecutiveCap);
        Assert.Equal(8, policy.SilentEvaluationCap);
        Assert.Equal(900_000, policy.InactivityLimitMs);
        var definition = new AgentDefinition(
            1,
            "examiner",
            1,
            new AgentIdentity("Alex", "role", "desc", "tone"),
            ["goal"],
            "instructions",
            new BehaviorPolicy("acknowledgeThenContinue", true, true),
            new ConversationPolicy("concise", true, "en", 256),
            policy,
            new VoiceConfiguration(true, "default", 1.0),
            new ProviderPreferences("primary-llm", "primary-stt", "primary-tts"),
            new Dictionary<string, string>());
        AgentDefinitionValidator.Validate(definition);
    }

    [Fact]
    public void Rejects_invalid_conversation_language()
    {
        var definition = new AgentDefinition(
            1,
            "broken",
            1,
            new AgentIdentity("X", "role", "desc", "tone"),
            ["goal"],
            "instructions",
            new BehaviorPolicy("acknowledgeThenContinue", true, true),
            new ConversationPolicy("concise", true, "potato!", 256),
            new InitiativePolicy(true, 8000, 30000, 1, ["longSilence"]),
            new VoiceConfiguration(true, "default", 1.0),
            new ProviderPreferences("primary-llm", "primary-stt", "primary-tts"),
            new Dictionary<string, string>());
        var error = Assert.Throws<ArgumentException>(() => AgentDefinitionValidator.Validate(definition));
        Assert.Contains("language", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rejects_invalid_model_default_catalog_key()
    {
        var definition = new AgentDefinition(
            1,
            "examiner",
            1,
            new AgentIdentity("Alex", "role", "desc", "tone"),
            ["goal"],
            "instructions",
            new BehaviorPolicy("acknowledgeThenContinue", true, true),
            new ConversationPolicy("concise", true, "en", 256),
            new InitiativePolicy(true, 8000, 30000, 1, ["longSilence"]),
            new VoiceConfiguration(true, "default", 1.0),
            new ProviderPreferences("primary-llm", "primary-stt", "primary-tts"),
            new Dictionary<string, string>(),
            ModelDefaults: new AgentModelDefaults("Not Valid", "medium"));
        var error = Assert.Throws<ArgumentException>(() => AgentDefinitionValidator.Validate(definition));
        Assert.Contains("modelDefaults.catalogKey", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Tool_authorization_has_no_arbitrary_count_limit()
    {
        AgentDefinitionValidator.Validate(WithTools(33));
        AgentDefinitionValidator.Validate(WithTools(100));
    }

    private static AgentDefinition WithTools(int count)
    {
        var tools = Enumerable.Range(0, count).Select(index => $"tool.n{index:D2}").ToArray();
        return new AgentDefinition(
            1,
            "examiner",
            1,
            new AgentIdentity("Alex", "role", "desc", "tone"),
            ["goal"],
            "instructions",
            new BehaviorPolicy("acknowledgeThenContinue", true, true),
            new ConversationPolicy("concise", true, "en", 256),
            new InitiativePolicy(true, 8000, 30000, 1, ["longSilence"]),
            new VoiceConfiguration(true, "default", 1.0),
            new ProviderPreferences("primary-llm", "primary-stt", "primary-tts"),
            new Dictionary<string, string>(),
            new RoleEnvironment(ToolAllowlist: tools));
    }

    [Fact]
    public void Accepts_optional_skills_and_treats_a_missing_list_as_empty()
    {
        AgentDefinitionValidator.Validate(Valid(null));
        var published = AgentDefinitionCandidate.FromDefinition(Valid(
            [
                new SkillSpec(
                    "refund.handle",
                    "Refund handling",
                    "",
                    "Confirm the order before any refund.",
                    ["refund"],
                    [SkillCapabilities.ChatRespond, "orders.read"],
                    [])
            ])).ToPublished(3);
        Assert.Equal(3, published.Version);
        Assert.Equal("refund.handle", Assert.Single(published.SkillList).Id);
        AgentDefinitionValidator.Validate(published);
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("missing")]
    [InlineData("budget")]
    [InlineData("activation")]
    [InlineData("resource")]
    public void Rejects_invalid_skills(string kind)
    {
        var skills = kind switch
        {
            "duplicate" => new[] { Skill("same"), Skill("same") },
            "missing" => new[] { Skill("refund.handle") with { Procedure = "" } },
            "budget" => Enumerable.Range(0, 17).Select(index => Skill($"skill.n{index:D2}")).ToArray(),
            "activation" => new[] { Skill("refund.handle") with { ActivationKeywords = ["", "refund"] } },
            _ => new[] { Skill("refund.handle") with { ResourcePaths = ["../secret.md"] } }
        };
        var error = Assert.Throws<ArgumentException>(() => AgentDefinitionValidator.Validate(Valid(skills)));
        Assert.Contains("skill", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rejects_aggregate_procedure_text_over_the_budget()
    {
        var skills = Enumerable.Range(0, 4)
            .Select(index => Skill($"skill.n{index}") with { Procedure = new string('a', 3001) })
            .ToArray();
        var error = Assert.Throws<ArgumentException>(() => AgentDefinitionValidator.Validate(Valid(skills)));
        Assert.Contains("12000", error.Message, StringComparison.Ordinal);
    }

    private static AgentDefinition Valid(IReadOnlyList<SkillSpec>? skills) =>
        new(
            1,
            "examiner",
            1,
            new AgentIdentity("Alex", "role", "desc", "tone"),
            ["goal"],
            "instructions",
            new BehaviorPolicy("acknowledgeThenContinue", true, true),
            new ConversationPolicy("concise", true, "en", 256),
            new InitiativePolicy(true, 8000, 30000, 1, ["longSilence"]),
            new VoiceConfiguration(true, "default", 1.0),
            new ProviderPreferences("primary-llm", "primary-stt", "primary-tts"),
            new Dictionary<string, string>(),
            Skills: skills);

    private static SkillSpec Skill(string id) =>
        new(id, "Name", "", "Do the task.", ["task"], [], []);
}
