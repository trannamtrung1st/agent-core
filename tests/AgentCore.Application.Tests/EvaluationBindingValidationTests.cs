using AgentCore.Application.Identity;
using AgentCore.Application.Evaluations;
using AgentCore.Domain.Evaluations;
using AgentCore.Domain.Definitions;
using AgentCore.Application.Agents;
namespace AgentCore.Application.Tests;
public sealed class EvaluationBindingValidationTests
{
    private static readonly Guid Tenant = Guid.NewGuid(), Id = Guid.NewGuid();
    private static EvaluationSuiteVersion Suite(params EvaluationCheck[] checks) => EvaluationRules.Freeze(new EvaluationSuiteVersion(Tenant, Id, 1, "Preconditions", checks, "", Guid.NewGuid(), DateTimeOffset.UtcNow));
    [Theory]
    [InlineData(EvaluationPredicate.ToolOffered)] [InlineData(EvaluationPredicate.ToolNotOffered)] [InlineData(EvaluationPredicate.ResourceBound)]
    [InlineData(EvaluationPredicate.TriggerSchedulePermitted)] [InlineData(EvaluationPredicate.ExternalActionDenied)]
    [InlineData(EvaluationPredicate.RunCompleted)] [InlineData(EvaluationPredicate.OutputPresent)] [InlineData(EvaluationPredicate.OutcomeSucceeded)]
    public void Required_preconditions_reject_unavailable_subject_predicates(EvaluationPredicate predicate) =>
        Assert.Throws<ArgumentException>(() => EvaluationRules.ValidatePolicy(new(Id, 1, EvaluationHook.BeforeRun, EvaluationPolicy.RequirePassBeforeRun), Suite(new EvaluationCheck("check", "Check", EvaluatorKind.Script, true, predicate))));
    [Fact]
    public void Required_preconditions_reject_required_manual_but_accept_advisory_review()
    {
        var binding = new EvaluationBinding(Id, 1, EvaluationHook.BeforeRun, EvaluationPolicy.RequirePassBeforeRun);
        Assert.Throws<ArgumentException>(() => EvaluationRules.ValidatePolicy(binding, Suite(new EvaluationCheck("human", "Review", EvaluatorKind.Manual, true))));
        Assert.Throws<ArgumentException>(() => EvaluationRules.ValidatePolicy(binding, Suite()));
        EvaluationRules.ValidatePolicy(binding, Suite(new EvaluationCheck("input", "Input", EvaluatorKind.Script, true, EvaluationPredicate.InputPresent), new EvaluationCheck("human", "Optional review", EvaluatorKind.Manual, false)));
    }
    [Fact]
    public async Task Referenced_script_preconditions_validate_actual_version_and_hook()
    {
        var script = EvaluationRules.Freeze(new ScriptVersion(Tenant, Guid.NewGuid(), 1, "Input", ScriptKind.Assertion, EvaluationPredicate.InputPresent, null, null, [EvaluationHook.BeforeRun], "", Guid.NewGuid(), DateTimeOffset.UtcNow));
        var binding = new EvaluationBinding(Id, 1, EvaluationHook.BeforeRun, EvaluationPolicy.RequirePassBeforeRun);
        var suite = Suite(new EvaluationCheck("input", "Input", EvaluatorKind.Script, true, Script: new(script.ScriptId, 1, script.ContentHash)));
        await EvaluationBindingValidation.RequireAsync(binding, suite, _ => ValueTask.FromResult<ScriptVersion?>(script));
        EvaluationBindingValidation.Require(binding, suite, _ => script);
        foreach (var wrong in new[] { script with { Predicate = EvaluationPredicate.OutputPresent }, script with { Kind = ScriptKind.Sandbox, Body = "echo unsafe" }, script with { Hooks = [EvaluationHook.Manual] } })
        {
            await Assert.ThrowsAsync<ArgumentException>(() => EvaluationBindingValidation.RequireAsync(binding, suite, _ => ValueTask.FromResult<ScriptVersion?>(wrong)).AsTask());
            Assert.Throws<ArgumentException>(() => EvaluationBindingValidation.Require(binding, suite, _ => wrong));
        }
    }
    [Fact]
    public void Existing_configuration_tool_schema_preserves_binding_array_shape_and_bounds()
    {
        System.Text.Json.JsonElement Arguments(object bindings) => System.Text.Json.JsonSerializer.SerializeToElement(new { section = "evaluationPolicy", expectedInstanceRevision = 1, set = new { bindings }, clear = Array.Empty<string>() });
        AgentCore.Application.Tools.ConfigurationTools.Validate("settings.patch", Arguments(new[] { EvaluationRules.BindingKey(new(Id, 1, EvaluationHook.OnFailure)) }));
        Assert.Throws<ArgumentException>(() => AgentCore.Application.Tools.ConfigurationTools.Validate("settings.patch", Arguments(true)));
        Assert.Throws<ArgumentException>(() => AgentCore.Application.Tools.ConfigurationTools.Validate("settings.patch", Arguments(Enumerable.Range(0, 9).Select(n => n.ToString()).ToArray())));
    }
    [Fact]
    public void Sparse_instance_binding_selection_preserves_exact_required_policies_and_clear_inheritance()
    {
        var optional = new EvaluationBinding(Id, 1, EvaluationHook.OnFailure);
        var required = new EvaluationBinding(Guid.NewGuid(), 2, EvaluationHook.AfterRun, EvaluationPolicy.RequirePassForVerifiedOutcome);
        var baseline = SampleDefinitions.Examiner with { EvaluationBindings = [optional, required] };
        var selected = new InstanceSettingsOverrides(EvaluationBindings: new([EvaluationRules.BindingKey(required)]));
        var effective = InstanceSettingsResolver.Resolve(baseline, selected);
        Assert.Equal(required, Assert.Single(effective.EvaluationBindings!)); Assert.Equal(2, baseline.EvaluationBindings.Count);
        Assert.Throws<AgentCore.Application.Sessions.AgentCoreException>(() => InstanceSettingsResolver.Resolve(baseline, new(EvaluationBindings: new([]))));
        Assert.Throws<AgentCore.Application.Sessions.AgentCoreException>(() => InstanceSettingsResolver.Resolve(baseline, new(EvaluationBindings: new([EvaluationRules.BindingKey(required with { Version = 3 })]))));
        var restored = InstanceSettingsResolver.Patch(selected, "evaluationPolicy", new Dictionary<string, System.Text.Json.JsonElement>(), ["bindings"]);
        Assert.Equal(baseline.EvaluationBindings, InstanceSettingsResolver.Resolve(baseline, restored).EvaluationBindings);
    }
}
