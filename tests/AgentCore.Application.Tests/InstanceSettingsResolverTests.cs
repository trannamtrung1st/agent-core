using System.Text.Json;
using AgentCore.Application.Agents;
using AgentCore.Application.Identity;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Application.Tests;

public sealed class InstanceSettingsResolverTests
{
    private static JsonElement Value<T>(T value) => JsonSerializer.SerializeToElement(value);
    [Fact]
    public void Sparse_fields_follow_new_defaults_and_clear_restores_inheritance()
    {
        var v1 = SampleDefinitions.Examiner;
        var patch = InstanceSettingsResolver.Patch(new(), "conversationPolicy", new Dictionary<string, JsonElement> { ["maxOutputTokens"] = Value(2048) }, []);
        var v2 = v1 with { ConversationPolicy = v1.ConversationPolicy with { Language = "fr-FR", MaxOutputTokens = 1024 } };
        var effective = InstanceSettingsResolver.Resolve(v2, patch);
        Assert.Equal("fr-FR", effective.ConversationPolicy.Language);
        Assert.Equal(2048, effective.ConversationPolicy.MaxOutputTokens);
        var cleared = InstanceSettingsResolver.Patch(patch, "conversationPolicy", new Dictionary<string, JsonElement>(), ["maxOutputTokens"]);
        Assert.Equal(1024, InstanceSettingsResolver.Resolve(v2, cleared).ConversationPolicy.MaxOutputTokens);
        Assert.Same(v1.ExecutionBudgets, effective.ExecutionBudgets);
    }
    [Fact]
    public void Presence_keeps_false_zero_and_explicit_null_separate_from_inherit()
    {
        var baseline = SampleDefinitions.Examiner with { InitiativePolicy = SampleDefinitions.Examiner.InitiativePolicy with { MaxConsecutiveProactiveTurns = 4 } };
        var o = InstanceSettingsResolver.Patch(new(), "initiativePolicy", new Dictionary<string, JsonElement> {
            ["enabled"] = Value(false), ["maxConsecutiveProactiveTurns"] = Value(0) }, []);
        var d = InstanceSettingsResolver.Resolve(baseline, o);
        Assert.False(d.InitiativePolicy.Enabled); Assert.Equal(0, d.InitiativePolicy.MaxConsecutiveProactiveTurns);
        o = InstanceSettingsResolver.Patch(o, "initiativePolicy", new Dictionary<string, JsonElement> { ["maxConsecutiveProactiveTurns"] = Value<int?>(null) }, []);
        Assert.Null(InstanceSettingsResolver.Resolve(baseline, o).InitiativePolicy.MaxConsecutiveProactiveTurns);
        Assert.True(InstanceSettingsResolver.Overrides(o, "initiativePolicy").ContainsKey("maxConsecutiveProactiveTurns"));
        o = InstanceSettingsResolver.Patch(o, "initiativePolicy", new Dictionary<string, JsonElement>(), ["maxConsecutiveProactiveTurns"]);
        Assert.Equal(4, InstanceSettingsResolver.Resolve(baseline, o).InitiativePolicy.MaxConsecutiveProactiveTurns);
        Assert.False(InstanceSettingsResolver.Resolve(baseline, o).InitiativePolicy.Enabled);
    }
    [Fact]
    public void Unknown_fields_wrong_types_duplicates_and_authority_expansion_are_rejected()
    {
        Assert.Throws<AgentCoreException>(() => InstanceSettingsResolver.Patch(new(), "conversationPolicy", new Dictionary<string, JsonElement> { ["temperature"] = Value(1) }, []));
        Assert.Throws<AgentCoreException>(() => InstanceSettingsResolver.Patch(new(), "conversationPolicy", new Dictionary<string, JsonElement> { ["maxOutputTokens"] = Value("2048") }, []));
        Assert.Throws<AgentCoreException>(() => InstanceSettingsResolver.Patch(new(), "conversationPolicy", new Dictionary<string, JsonElement> { ["language"] = Value("fr") }, ["language"]));
        var selected = new InstanceSettingsOverrides(SelectedCapabilities: new(["shell"]));
        Assert.Throws<AgentCoreException>(() => InstanceSettingsResolver.Resolve(SampleDefinitions.Examiner, selected));
        Assert.Throws<AgentCoreException>(() => InstanceSettingsResolver.Resolve(SampleDefinitions.Examiner with { MemoryPolicy = null }, new(MemoryUserPromotion: new(true))));
    }
    [Fact]
    public void Published_constraints_reject_each_disallowed_value_with_the_disclosed_reason()
    {
        var baseline = SampleDefinitions.Examiner with {
            InitiativePolicy = SampleDefinitions.Examiner.InitiativePolicy with { Enabled = false },
            Voice = SampleDefinitions.Examiner.Voice with { Enabled = false },
            ProviderPreferences = SampleDefinitions.Examiner.ProviderPreferences with { SpeechRecognizer = null, SpeechSynthesizer = null },
            MemoryPolicy = MemoryPolicy.Disabled
        };
        foreach (var section in InstanceSettingsResolver.Sections)
            foreach (var (field, constraint) in InstanceSettingsResolver.Constraints(baseline, section))
            {
                var invalid = constraint.RequiredBoolean is { } required ? Value(!required)
                    : constraint.Minimum is { } min ? Value(min - 1) : Value(constraint.Maximum!.Value + 1);
                var patch = InstanceSettingsResolver.Patch(new(), section, new Dictionary<string, JsonElement> { [field] = invalid }, []);
                var rejected = Assert.Throws<AgentCoreException>(() => InstanceSettingsResolver.Resolve(baseline, patch));
                Assert.Equal(constraint.Reason, rejected.Message);
            }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Inherited_skill_defaults_change_but_explicit_disabled_survives_and_reset_restores(bool sqlite)
    {
        await using var db = sqlite ? await SqliteTestHarness.CreateMigratedAsync() : null;
        var ids = new SystemIdGenerator(TimeProvider.System); var clock = new FakeTimeProvider();
        IAgentInstanceStore store = sqlite ? new SqliteAgentInstanceStore(db!.Factory, ids) : new InMemoryAgentInstanceStore();
        var spec = new SkillSpec("review", "Review", "Review", "Review carefully", SkillProjection.OnDemand, true, [], []);
        var v1 = SkillActivationTests.Definition(1, spec); var v2 = SkillActivationTests.Definition(2, spec with { DefaultEnabled = false });
        var defs = new SkillActivationTests.Definitions(v1, v2); var service = new AgentInstanceService(store, defs, ids, clock);
        var owner = await service.CreateAsync(v1.Id, 1);
        Assert.Null(Assert.Single((await store.ReadSkillsAsync(owner.InstanceId)).DefinitionStates).EnabledOverride);
        await service.UpgradeAsync(owner.InstanceId, 2, owner.Revision);
        Assert.Empty(await new EffectiveSkillCatalogResolver(store).ResolveAsync(owner.InstanceId, v2));
        var skills = new AgentCore.Application.Admin.AgentInstanceSkillService(store, defs, ids, clock);
        var disabled = await skills.WriteAsync(owner.InstanceId, "set_enabled", "definition:review", 1, enabled: false, actor: SkillAuthor.Agent);
        owner = (await store.FindAsync(owner.InstanceId))!;
        await service.UpgradeAsync(owner.InstanceId, 1, owner.Revision);
        Assert.Empty(await new EffectiveSkillCatalogResolver(store).ResolveAsync(owner.InstanceId, v1));
        await skills.WriteAsync(owner.InstanceId, "reset_enabled", disabled.Key, disabled.Revision, actor: SkillAuthor.Agent);
        Assert.Single(await new EffectiveSkillCatalogResolver(store).ResolveAsync(owner.InstanceId, v1));
    }
}
