using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Evaluations;
using AgentCore.Domain.Security;
namespace AgentCore.Application.Evaluations;
public sealed record EvaluationSubjectView(Guid SubjectId, EvaluationSubjectKind SubjectKind, long Revision, string SourceFingerprint, string Label, bool CanEvaluate);
public sealed record EvaluationBindingView(EvaluationBinding Binding, string SuiteName, string Source);
public sealed partial class TenantEvaluationService
{
    public async ValueTask<EvaluationSubjectView> SubjectAsync(Guid actor, Guid tenant, EvaluationSubjectKind kind, Guid id, HumanSessionReference? human, CancellationToken ct = default)
    {
        var state = await State(actor, tenant, human, ct); var source = await Subject(state, actor, kind, id, false, ct);
        await Current(state, human, ct);
        return new(id, kind, source.Revision, source.Fingerprint, source.Draft?.Draft.Candidate.Identity.Name ?? (source.Run is not null ? "Agent Run · " + source.Run.Status : "Published Definition evidence"), source.Draft is not null || source.Run?.IsTerminal == true);
    }
    public async ValueTask<IReadOnlyList<EvaluationBindingView>> InstanceBindingsAsync(Guid actor, Guid tenant, Guid instance, HumanSessionReference human, TenantRunConfigurationResolver resolver, CancellationToken ct = default)
    {
        var state = await State(actor, tenant, human, ct); await Require(state, actor, "agents.read", AccessScope.ForResource(tenant, "agent", instance.ToString("D")), ct);
        var (agent, definition) = await resolver.CurrentPolicyAsync(tenant, instance, ct); var defaults = await store.ReadEvaluationGovernanceAsync(tenant, ct);
        var result = new List<EvaluationBindingView>();
        foreach (var (binding, source) in defaults.Defaults.Select(b => (b, "Organization default")).Concat((definition.EvaluationBindings ?? []).Select(b => (b, agent.Instance.SettingsOverrides?.EvaluationBindings is null || b.Hook == EvaluationHook.Publication ? "Definition v" + definition.Version : "Instance selection from Definition v" + definition.Version))))
        {
            var suite = await SuiteAsync(actor, tenant, binding.SuiteId, binding.Version, human, ct);
            result.Add(new(binding, suite.Name, source));
        }
        await Current(state, human, ct); return result;
    }
}
