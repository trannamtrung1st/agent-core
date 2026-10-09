using System.Text.Json;
using AgentCore.Domain.Definitions;

namespace AgentCore.Domain.Tests;

public sealed class ExecutionBudgetPolicyTests
{
    [Theory]
    [InlineData(ExecutionBudgetPreset.Standard, 48, 300)]
    [InlineData(ExecutionBudgetPreset.Extended, 96, 600)]
    [InlineData(ExecutionBudgetPreset.DeepWorkflow, 144, 900)]
    public void Interactive_presets_are_bounded(ExecutionBudgetPreset preset, int steps, int seconds)
    {
        var profile = ExecutionBudgetProfile.For(ExecutionBudgetClass.InteractiveBrowser, preset);
        profile.Validate();
        Assert.Equal(steps, profile.MaxSteps); Assert.Equal(seconds, profile.DurationSeconds);
    }

    [Fact]
    public void Overrides_are_per_class_and_instance_and_roundtrip_immutable_pin()
    {
        var definition = new ExecutionBudgetPolicy(InteractiveBrowser: ExecutionBudgetProfile.For(ExecutionBudgetClass.InteractiveBrowser, ExecutionBudgetPreset.Extended));
        var instance = new ExecutionBudgetPolicy(Standard: new(40, 240));
        var inherited = ExecutionBudgetPolicy.Resolve(ExecutionBudgetClass.InteractiveBrowser, definition, instance);
        Assert.Equal("definition", inherited.Source); Assert.Equal(96, inherited.Profile.MaxSteps);
        var standard = ExecutionBudgetPolicy.Resolve(ExecutionBudgetClass.Standard, definition, instance);
        Assert.Equal("instance", standard.Source); Assert.Equal(40, standard.Profile.MaxSteps);
        Assert.Equal(24, ExecutionBudgetPolicy.Resolve(ExecutionBudgetClass.Standard, definition, null).Profile.MaxSteps);
        Assert.Equal(32, ExecutionBudgetPolicy.Resolve(ExecutionBudgetClass.UnattendedBoundBrowser, definition, instance).Profile.MaxSteps);
        definition = definition with { InteractiveBrowser = ExecutionBudgetProfile.For(ExecutionBudgetClass.InteractiveBrowser, ExecutionBudgetPreset.DeepWorkflow) };
        Assert.Equal(96, JsonSerializer.Deserialize<EffectiveExecutionBudget>(JsonSerializer.Serialize(inherited))!.Profile.MaxSteps);
        Assert.Equal(144, ExecutionBudgetPolicy.Resolve(ExecutionBudgetClass.InteractiveBrowser, definition, instance).Profile.MaxSteps);
    }

    [Fact]
    public void Definition_reset_changes_inheritance_without_mutating_other_classes_instances_or_admitted_runs()
    {
        var definition = new ExecutionBudgetPolicy(Standard: new(40, 240), InteractiveBrowser: ExecutionBudgetProfile.For(ExecutionBudgetClass.InteractiveBrowser, ExecutionBudgetPreset.Extended));
        var instance = new ExecutionBudgetPolicy(InteractiveBrowser: new(120, 720));
        var admitted = ExecutionBudgetPolicy.Resolve(ExecutionBudgetClass.InteractiveBrowser, definition, null);
        var nextVersion = definition with { InteractiveBrowser = null };
        Assert.Equal(definition.Standard, nextVersion.Standard);
        Assert.Equal(48, ExecutionBudgetPolicy.Resolve(ExecutionBudgetClass.InteractiveBrowser, nextVersion, null).Profile.MaxSteps);
        Assert.Equal("system", ExecutionBudgetPolicy.Resolve(ExecutionBudgetClass.InteractiveBrowser, nextVersion, null).Source);
        Assert.Equal(instance.InteractiveBrowser, ExecutionBudgetPolicy.Resolve(ExecutionBudgetClass.InteractiveBrowser, nextVersion, instance).Profile);
        Assert.Equal(96, admitted.Profile.MaxSteps); Assert.Equal("definition", admitted.Source);
        var intent = admitted with { RequestedCleanup = true, CleanupIntent = new(true, false) };
        Assert.Equal(intent, JsonSerializer.Deserialize<EffectiveExecutionBudget>(JsonSerializer.Serialize(intent)));
        var historical = JsonSerializer.Deserialize<EffectiveExecutionBudget>(JsonSerializer.Serialize(admitted));
        Assert.Null(historical!.CleanupIntent);
    }

    [Theory]
    [InlineData(145, 900, 30)] [InlineData(144, 901, 30)] [InlineData(144, 900, 31)]
    [InlineData(7, 300, 30)] [InlineData(48, 59, 30)] [InlineData(48, 300, 0)]
    public void Invalid_or_above_host_values_are_rejected(int steps, int seconds, int perTool) =>
        Assert.Throws<ArgumentException>(() => new ExecutionBudgetProfile(steps, seconds, perTool).Validate());

    [Fact]
    public void Tighter_host_ceilings_reject_without_silent_clamping() =>
        Assert.Throws<ArgumentException>(() => new ExecutionBudgetProfile(96, 600).Validate(new(48, 300, 30)));
}
