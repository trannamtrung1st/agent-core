using System.Text.Json;
using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Tests;

public sealed class StateVisibilityCeilingTests
{
    [Theory]
    [InlineData("Off")]
    [InlineData("CurrentSession")]
    [InlineData("InstanceSummary")]
    public void Instance_can_override_a_default_within_its_independent_ceiling(string visibility)
    {
        var baseline = SampleDefinitions.Examiner with { StateIsolationPolicy = new(MaxAgentStateVisibility: AgentStateVisibility.InstanceSummary) };
        var patch = InstanceSettingsResolver.Patch(new(), "stateIsolationPolicy",
            new Dictionary<string, JsonElement> { ["agentStateVisibility"] = JsonSerializer.SerializeToElement(visibility) }, []);
        Assert.Equal(visibility, InstanceSettingsResolver.Resolve(baseline, patch).StateIsolationPolicy!.AgentStateVisibility.ToString());
        Assert.Equal(AgentStateVisibility.Off, baseline.StateIsolationPolicy.AgentStateVisibility);
        Assert.Equal(new[] { "Off", "CurrentSession", "InstanceSummary" }, InstanceSettingsResolver.Constraints(baseline, "stateIsolationPolicy")["agentStateVisibility"].Choices);
        Assert.Throws<AgentCore.Application.Sessions.AgentCoreException>(() => InstanceSettingsResolver.Patch(new(), "stateIsolationPolicy",
            new Dictionary<string, JsonElement> { ["maxAgentStateVisibility"] = JsonSerializer.SerializeToElement("InstanceSummary") }, []));
    }

    [Fact]
    public void Legacy_json_keeps_its_original_ceiling_and_new_defaults_must_fit_the_maximum()
    {
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var legacy = JsonSerializer.Deserialize<StateIsolationPolicy>("""{"agentStateVisibility":"Off"}""", json)!;
        Assert.Equal(AgentStateVisibility.Off, legacy.VisibilityCeiling);
        Assert.DoesNotContain("maxAgentStateVisibility", JsonSerializer.Serialize(legacy, json));
        Assert.DoesNotContain("visibilityCeiling", JsonSerializer.Serialize(legacy, json));
        Assert.Throws<ArgumentException>(() => new StateIsolationPolicy(AgentStateVisibility: AgentStateVisibility.InstanceSummary,
            MaxAgentStateVisibility: AgentStateVisibility.CurrentSession).Validate());
        Assert.Throws<ArgumentException>(() => new StateIsolationPolicy(MaxAgentStateVisibility: (AgentStateVisibility)999).Validate());
        var explicitMaximum = legacy with { MaxAgentStateVisibility = AgentStateVisibility.InstanceSummary };
        explicitMaximum.Validate();
        Assert.Equal(explicitMaximum, JsonSerializer.Deserialize<StateIsolationPolicy>(JsonSerializer.Serialize(explicitMaximum, json), json));
    }

    [Fact]
    public void A_maximum_never_enables_inspection_or_broadens_an_admitted_run()
    {
        var definition = SampleDefinitions.Examiner with { StateIsolationPolicy = new(MaxAgentStateVisibility: AgentStateVisibility.InstanceSummary),
            Environment = new RoleEnvironment(Capabilities: new("Selected", [ToolCatalog.AgentStateInspect])) };
        var context = new AgentContext(definition, [], "", null, SessionMode.Text, null, false, null,
            new(Guid.NewGuid(), TriggerKind.UserTurn, "Hello"), AgentInstanceId: Guid.NewGuid(), OwnedSessionId: Guid.NewGuid());
        Assert.False(ToolPolicy.IsOffered(definition, context, ToolCatalog.AgentStateInspect, ToolConfigurationGates.AllowAll));
        var enabled = definition with { StateIsolationPolicy = definition.StateIsolationPolicy with { AgentStateVisibility = AgentStateVisibility.InstanceSummary } };
        Assert.True(ToolPolicy.IsOffered(enabled, context with { Definition = enabled }, ToolCatalog.AgentStateInspect, ToolConfigurationGates.AllowAll));
        Assert.Equal(AgentStateVisibility.Off, StateIsolationAccess.RestrictDefinition(definition, enabled).StateIsolationPolicy!.AgentStateVisibility);
        enabled = enabled with { Environment = new RoleEnvironment(Capabilities: new("Selected", [])) };
        Assert.False(ToolPolicy.IsOffered(enabled, context with { Definition = enabled }, ToolCatalog.AgentStateInspect, ToolConfigurationGates.AllowAll));
    }
}
