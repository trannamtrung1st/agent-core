using AgentCore.Application.Ports;
using AgentCore.Domain.Evaluations;
namespace AgentCore.Application.Evaluations;
public static class EvaluationBindingValidation
{
    private static void RequireScript(ScriptReference reference, ScriptVersion? script)
    {
        if (script is null || script.ContentHash != reference.ContentHash || script.Kind != ScriptKind.Assertion
            || !script.Hooks.Contains(EvaluationHook.BeforeRun) || script.Predicate != EvaluationPredicate.InputPresent)
            throw new ArgumentException("Required Before Run Scripts must be available deterministic input assertions with Before Run eligibility. Native Sandbox and completed-output checks cannot satisfy this gate.");
    }
    public static void Require(EvaluationBinding binding, EvaluationSuiteVersion suite, Func<ScriptReference, ScriptVersion?> resolve)
    {
        EvaluationRules.ValidatePolicy(binding, suite);
        if (binding.Policy != EvaluationPolicy.RequirePassBeforeRun) return;
        foreach (var reference in suite.Checks.Where(c => c.Required).Select(c => c.Script).OfType<ScriptReference>())
            RequireScript(reference, resolve(reference));
    }
    public static async ValueTask RequireAsync(EvaluationBinding binding, EvaluationSuiteVersion suite, Func<ScriptReference, ValueTask<ScriptVersion?>> resolve)
    {
        EvaluationRules.ValidatePolicy(binding, suite);
        if (binding.Policy != EvaluationPolicy.RequirePassBeforeRun) return;
        foreach (var check in suite.Checks.Where(c => c.Required && c.Script is not null))
        {
            var script = await resolve(check.Script!);
            RequireScript(check.Script!, script);
        }
    }
    public static ValueTask RequireAsync(ITenantEvaluationStore store, Guid tenant, EvaluationBinding binding, EvaluationSuiteVersion suite, CancellationToken ct) =>
        RequireAsync(binding, suite, reference => store.FindScriptAsync(tenant, reference.ScriptId, reference.Version, ct));
}
