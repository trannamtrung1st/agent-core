using System.Text.Json;
using AgentCore.Application.Agents;
using AgentCore.Application.Identity;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Admin;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Application.Tests;

public sealed class InstanceSettingsResolverTests
{
    [Fact]
    public void Managed_instructions_append_tenant_supplement_while_an_ordinary_clone_keeps_replacement_semantics()
    {
        var definition = SampleDefinitions.Examiner with { Id = "general-assistant", AgentInstructions = "Canonical system instructions." };
        var now = DateTimeOffset.UnixEpoch;
        var instance = new AgentInstance(Guid.NewGuid(), definition.Id, definition.Version, definition.Identity, AgentInstanceLifecycle.Active, now, now,
            SettingsOverrides: new(AgentInstructions: new("Tenant supplement.")));
        var ordinary = new TenantAgent(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), instance);
        var managed = ordinary with { ManagedKind = AgentCore.Application.Security.ManagedGeneralAssistant.Kind };
        Assert.Equal("Tenant supplement.", InstanceSettingsResolver.ResolveForAgent(definition, ordinary).AgentInstructions);
        var resolved = InstanceSettingsResolver.ResolveForAgent(definition, managed).AgentInstructions;
        Assert.StartsWith(definition.AgentInstructions, resolved); Assert.Contains("subordinate", resolved); Assert.EndsWith("Tenant supplement.", resolved);
        Assert.Equal(definition.AgentInstructions, InstanceSettingsResolver.ResolveForAgent(definition, managed with { Instance = instance with { SettingsOverrides = null } }).AgentInstructions);
    }
    private static JsonElement Value<T>(T value) => JsonSerializer.SerializeToElement(value);
    [Fact]
    public void Canonical_model_inheritance_preserves_lower_choice_and_separate_reasoning_intent()
    {
        var definition = SampleDefinitions.Examiner with { ModelDefaults = AgentCore.Domain.Models.ModelPreference.Fixed("dca30000-0000-4000-8000-000000000001", "low") };
        var overrides = new InstanceSettingsOverrides(ModelPreference: new(new("inherit", Reasoning: new("fixed", "high"))));
        var effective = InstanceSettingsResolver.Resolve(definition, overrides);
        Assert.Equal(definition.ModelDefaults.OfferingId, effective.ModelDefaults!.OfferingId);
        Assert.Equal("high", effective.ModelDefaults.Reasoning!.Effort);
        effective = InstanceSettingsResolver.Resolve(definition, new(ModelPreference: new(AgentCore.Domain.Models.ModelPreference.Auto)));
        Assert.Equal("auto", effective.ModelDefaults!.Mode);
        effective = InstanceSettingsResolver.Resolve(definition, new(ModelPreference: new(null)));
        Assert.Null(effective.ModelDefaults); // An explicit sparse clear remains distinct from inherit.
    }
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
                if (field is "allowedProfileIds" or "bindings") continue; // Exact subset validation is covered by SandboxSetupResolverTests.
                var invalid = constraint.Choices is not null ? Value("not-authorized") : constraint.RequiredBoolean is { } required ? Value(!required)
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
        IAgentInstanceStore store = sqlite ? new SqliteAgentInstanceStore(db!.Factory, ids) : new InMemoryAgentInstanceStore { EventStore = new InMemoryAdminEventStore(new SystemIdGenerator(TimeProvider.System)) };
        var spec = new SkillSpec("review", "Review", "Review", "Review carefully", SkillProjection.OnDemand, true, [], []);
        var v1 = SkillActivationTests.Definition(1, spec); var v2 = SkillActivationTests.Definition(2, spec with { DefaultEnabled = false });
        var defs = new SkillActivationTests.Definitions(v1, v2); var owner = await NativeInstanceSeed.CreateAsync(store, defs, ids, clock, v1.Id, 1);
        Assert.Null(Assert.Single((await store.ReadSkillsAsync(owner.InstanceId)).DefinitionStates).EnabledOverride);
        await NativeInstanceSeed.UpgradeAsync(store, defs, clock, owner.InstanceId, 2, owner.Revision);
        Assert.Empty(await NativeSkillSeed.ResolveAsync(store, owner.InstanceId, v2));
        var disabled = await NativeSkillSeed.WriteAsync(store, defs, clock, owner.InstanceId, "set_enabled", "definition:review", 1, enabled: false, actor: SkillAuthor.Agent);
        owner = (await store.FindAsync(owner.InstanceId))!;
        await NativeInstanceSeed.UpgradeAsync(store, defs, clock, owner.InstanceId, 1, owner.Revision);
        Assert.Empty(await NativeSkillSeed.ResolveAsync(store, owner.InstanceId, v1));
        await NativeSkillSeed.WriteAsync(store, defs, clock, owner.InstanceId, "reset_enabled", disabled.Key, disabled.Revision, actor: SkillAuthor.Agent);
        Assert.Single(await NativeSkillSeed.ResolveAsync(store, owner.InstanceId, v1));
    }
}
