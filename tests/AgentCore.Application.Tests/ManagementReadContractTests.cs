using System.Text.Json;
using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;
namespace AgentCore.Application.Tests;

public sealed class ManagementReadContractTests
{
    private static AgentContext Context()
    {
        var policy = new HarnessManagementPolicy(HarnessManagementMode.Managed, [HarnessManagementScope.Settings, HarnessManagementScope.AutomationPreferences], [], []);
        var definition = SampleDefinitions.Examiner with { SelfManagement = policy, Environment = new(Capabilities: new("Selected", TenantManagementReadContract.Tools), Projection: new([])) };
        var session = Guid.NewGuid(); var activation = Guid.NewGuid();
        return new(definition, [], "", null, SessionMode.Text, null, false, null, new(Guid.NewGuid(), TriggerKind.UserTurn, "Inspect settings"),
            AgentInstanceId: Guid.NewGuid(), OwnedSessionId: session, Harness: new(policy, 1, 1))
            { ExecutionContext = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), session, Guid.NewGuid(), activation, activation, null, null, ExecutionOrigin.UserMessage) };
    }
    [Theory]
    [InlineData("management.inspect", "{}")] [InlineData("settings.list", "{}")] [InlineData("settings.inspect", "{\"section\":\"conversationPolicy\"}")]
    [InlineData("automation.preferences.inspect", "{}")]
    public void Discovery_loads_only_the_selected_read_without_granting_legacy_mutations(string tool, string arguments)
    {
        var context = Context(); Assert.DoesNotContain(ToolCatalog.For(context.Definition, context, ToolConfigurationGates.AllowAll), t => t.Name == tool);
        using var query = JsonDocument.Parse(JsonSerializer.Serialize(new { query = tool, limit = 1 }));
        var result = CapabilityDiscoveryMatcher.Load(context.Definition, context, ToolConfigurationGates.AllowAll, query.RootElement, 0); Assert.Equal([tool], result.Loaded);
        var offered = ToolCatalog.For(context.Definition, context with { LoadedCapabilityIds = result.Loaded }, ToolConfigurationGates.AllowAll);
        Assert.Contains(offered, t => t.Name == tool); Assert.DoesNotContain(offered, t => t.Name is "settings.patch" or "management.freeze" or "configuration.inspect" or "harness.inspect");
        var intent = TenantApprovalToolContract.Parse(new("exact", ToolCatalog.RequestAuthorization, JsonSerializer.Serialize(new { operation = "inspect_management", read = new { tool, arguments = JsonSerializer.Deserialize<JsonElement>(arguments) } })));
        var value = Observation(tool, TenantManagementReadContract.Parse(tool, arguments).Json);
        var operation = TenantApprovalRules.PrepareManagementRead(Guid.NewGuid(), Guid.NewGuid(), value, 1, 1);
        Assert.True(intent.Matches(operation)); Assert.Equal("agents.read", operation.Permission); Assert.Equal("agent", operation.ResourceType); Assert.False(operation.IsLocalConfiguration);
        Assert.Throws<ArgumentException>(() => (operation with { Persona = SampleDefinitions.Examiner.Identity }).Kind);
        Assert.Throws<ArgumentException>(() => TenantApprovalRules.PrepareManagementRead(Guid.NewGuid(), operation.InstanceId, value, 2, 1));
    }
    private static PreparedManagementRead Observation(string tool = "settings.list", string args = "{}")
    {
        var hash = new string('a',64); var stable = new string('b',64);
        return new(Guid.NewGuid(), Guid.NewGuid(), tool, args, 1, hash, stable, "{}", TenantManagementReadRules.Hash(tool, args, 1, hash, stable, "{}"));
    }
    [Theory]
    [InlineData("settings.inspect", "{\"section\":\"conversationPolicy\",\"instanceId\":\"foreign\"}")]
    [InlineData("settings.inspect", "{\"section\":\"conversationPolicy\",\"section\":\"instructions\"}")]
    [InlineData("settings.inspect", "{\"section\":\"unowned\"}")]
    [InlineData("management.inspect", "{\"permission\":\"agents.update\"}")]
    [InlineData("settings.patch", "{}")]
    public void Read_contract_rejects_ambiguous_owner_authority_and_mutation_arguments(string tool, string args)
        => Assert.Throws<ArgumentException>(() => TenantManagementReadContract.Parse(tool,args));
    [Theory]
    [InlineData("frozen")] [InlineData("off")] [InlineData("scope")] [InlineData("capability")] [InlineData("model")] [InlineData("foreign_session")] [InlineData("background")]
    public void Offered_read_family_requires_delegation_capability_and_current_execution_shape(string boundary)
    {
        var context = Context();
        context = boundary switch {
            "frozen" => context with { Harness = context.Harness! with { Policy = context.Harness.Policy with { Frozen = true } } },
            "off" => context with { Definition = context.Definition with { SelfManagement = HarnessManagementPolicy.Disabled } },
            "scope" => context with { Harness = context.Harness! with { Policy = context.Harness.Policy with { Scopes = [] } }, Definition = context.Definition with { SelfManagement = context.Definition.SelfManagement! with { Scopes = [] } } },
            "capability" => context with { Definition = context.Definition with { Environment = new(Capabilities: new("Selected", [])) } },
            "model" => context with { ModelSupportsTools = false },
            "foreign_session" => context with { OwnedSessionId = Guid.NewGuid() },
            "background" => context with { OutputContract = AgentRunOutputContract.BackgroundOutcome }, _ => throw new ArgumentException()
        };
        var reads = TenantManagementReadContract.Allowed(context.Definition, context);
        // Procedure activation is independent of self-management delegation.
        if (boundary == "scope") Assert.Equal(["harness.inspect", "configuration.inspect", "management.inspect", "skills.load"], reads);
        else if (boundary == "capability") Assert.Equal(["harness.inspect", "skills.load"], reads);
        else if (boundary is "frozen" or "off") Assert.Equal(["skills.load"], reads);
        else Assert.Empty(reads);
    }
    [Fact]
    public void Source_codec_ignores_user_claims_deduplicates_and_bounds_retained_references()
    {
        var value = Observation(); var reference = TenantManagementReadService.Reference(value, Guid.NewGuid());
        var json = JsonSerializer.Serialize(new { tenantManagementRead = true, managementReadSource = reference });
        Assert.Empty(TenantManagementReadReferences.Read([new(ModelRole.User, json, Name: "settings.list")]));
        Assert.Equal(reference, Assert.Single(TenantManagementReadReferences.Read([new(ModelRole.Tool,json,Name: "settings.list"),new(ModelRole.Tool,json,Name: "settings.list")])));
        var tooMany = Enumerable.Range(0,65).Select(i => reference with { ObservationHash = TenantMemoryOperationRules.Hash(i.ToString()) });
        Assert.Throws<AccessDeniedException>(() => TenantManagementReadReferences.Merge([],tooMany));
    }
    [Fact]
    public void Stable_configuration_witness_excludes_only_existing_preference_fields_and_write_bookkeeping()
    {
        var definition = SampleDefinitions.Examiner; var now = DateTimeOffset.Parse("2026-10-22T00:00:00Z");
        var owner = new AgentInstance(Guid.NewGuid(),definition.Id,1,definition.Identity,AgentInstanceLifecycle.Active,now,now);
        var preferences = owner with { Revision = 2, UpdatedAt = now.AddSeconds(1), SettingsOverrides = new(ResponseLength: new("concise"), Language: new("vi"), AskOneQuestionAtATime: new(false)) };
        Assert.NotEqual(TenantManagementReadRules.ConfigurationHash(owner),TenantManagementReadRules.ConfigurationHash(preferences));
        Assert.Equal(TenantManagementReadRules.StableConfigurationHash(owner),TenantManagementReadRules.StableConfigurationHash(preferences));
        Assert.NotEqual(TenantManagementReadRules.StableConfigurationHash(owner),TenantManagementReadRules.StableConfigurationHash(preferences with { SettingsOverrides = preferences.SettingsOverrides with { AgentInstructions = new("Changed instructions") } }));
        Assert.NotEqual(TenantManagementReadRules.StableConfigurationHash(owner),TenantManagementReadRules.StableConfigurationHash(owner with { ActiveVersion = 2 }));
        Assert.NotEqual(TenantManagementReadRules.StableConfigurationHash(owner),TenantManagementReadRules.StableConfigurationHash(owner with { Persona = definition.Identity with { Name = "Changed" } }));
        Assert.NotEqual(TenantManagementReadRules.StableConfigurationHash(owner),TenantManagementReadRules.StableConfigurationHash(owner with { HarnessManagement = new(HarnessManagementPolicy.Disabled with { Frozen = true }) }));
    }
}
