using System.Text.Json;
using AgentCore.Application.Agents;
using AgentCore.Application.Sessions;
using AgentCore.Application.Tools;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Tests;

public sealed class StateIsolationPolicyTests
{
    [Fact]
    public void Legacy_defaults_remain_absent_and_false_overrides_clear_to_the_new_definition()
    {
        var baseline = SampleDefinitions.Examiner;
        Assert.Null(InstanceSettingsResolver.Resolve(baseline, new()).StateIsolationPolicy);
        var patch = InstanceSettingsResolver.Patch(new(), "stateIsolationPolicy",
            new Dictionary<string, JsonElement> { ["profileEvidence"] = JsonSerializer.SerializeToElement(false) }, []);
        Assert.False(InstanceSettingsResolver.Resolve(baseline, patch).StateIsolationPolicy!.ProfileEvidence);
        Assert.Equal(false, InstanceSettingsResolver.Overrides(patch, "stateIsolationPolicy")["profileEvidence"]);
        patch = InstanceSettingsResolver.Patch(patch, "stateIsolationPolicy", new Dictionary<string, JsonElement>(), ["profileEvidence"]);
        Assert.Empty(InstanceSettingsResolver.Overrides(patch, "stateIsolationPolicy"));
        Assert.Null(InstanceSettingsResolver.Resolve(baseline, patch).StateIsolationPolicy);
    }

    [Theory]
    [InlineData("homeAccess", "ReadWrite")]
    [InlineData("browserIsolation", "PersistentAgent")]
    [InlineData("agentStateVisibility", "InstanceSummary")]
    [InlineData("conversationContextScope", "Session")]
    [InlineData("profileEvidence", true)]
    [InlineData("experienceRecord", true)]
    [InlineData("experienceRecall", true)]
    [InlineData("crossSessionContinuity", true)]
    public void Every_definition_ceiling_rejects_instance_broadening(string field, object value)
    {
        var baseline = SampleDefinitions.Examiner with { Voice = SampleDefinitions.Examiner.Voice with { Enabled = false },
            StateIsolationPolicy = new(ConversationContextScope.CurrentRequest, HomeAccess.Disabled,
                BrowserIsolation: BrowserIsolation.EphemeralSession, AgentStateVisibility: AgentStateVisibility.CurrentSession,
                ExperienceRecord: false, ExperienceRecall: false, ProfileEvidence: false, CrossSessionContinuity: false) };
        var patch = InstanceSettingsResolver.Patch(new(), "stateIsolationPolicy",
            new Dictionary<string, JsonElement> { [field] = JsonSerializer.SerializeToElement(value) }, []);
        Assert.Throws<AgentCoreException>(() => InstanceSettingsResolver.Resolve(baseline, patch));
    }

    [Fact]
    public void Live_restrictions_intersect_the_pin_and_loosened_settings_do_not_restore_old_run_access()
    {
        var admitted = SampleDefinitions.Examiner with { StateIsolationPolicy = new(HomeAccess: HomeAccess.ReadOnly, ProfileEvidence: false),
            MemoryPolicy = new(true, false, false, false, false) };
        var tightened = StateIsolationAccess.RestrictDefinition(admitted, admitted with { StateIsolationPolicy = new(HomeAccess: HomeAccess.Disabled) });
        Assert.Equal(HomeAccess.Disabled, tightened.StateIsolationPolicy!.HomeAccess);
        Assert.False(tightened.StateIsolationPolicy.ProfileEvidence);
        var loosened = StateIsolationAccess.RestrictDefinition(admitted, admitted with { StateIsolationPolicy = StateIsolationPolicy.Default,
            MemoryPolicy = new(true, true, true, true, true) });
        Assert.Equal(HomeAccess.ReadOnly, loosened.StateIsolationPolicy!.HomeAccess);
        Assert.False(loosened.StateIsolationPolicy.ProfileEvidence);
        Assert.False(loosened.MemoryPolicy!.UserRetrieval);
    }

    [Theory]
    [InlineData(ToolCatalog.ContinuitySearch)]
    [InlineData(ToolCatalog.ContinuityGet)]
    [InlineData("experience.source")]
    [InlineData("experience.record")]
    [InlineData(ToolCatalog.MemoryConsolidate)]
    public void Continuity_and_learning_tools_cannot_bypass_pinned_isolation(string tool)
    {
        var definition = SampleDefinitions.Examiner with { StateIsolationPolicy = new(ExperienceRecall: false,
            ExperienceRecord: false, CrossSessionContinuity: false) };
        Assert.Equal(ToolPolicyDecision.Deny, ToolPolicy.EvaluateExecution(definition, tool, ToolConfigurationGates.AllowAll));
    }
}
