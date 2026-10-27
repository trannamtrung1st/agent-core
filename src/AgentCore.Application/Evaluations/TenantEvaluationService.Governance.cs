using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Evaluations;
using AgentCore.Domain.Security;
namespace AgentCore.Application.Evaluations;
public sealed partial class TenantEvaluationService
{
    public async ValueTask<EvaluationGovernance> GovernanceAsync(Guid actor, Guid tenant, HumanSessionReference? human, CancellationToken ct = default)
    {
        var state = await State(actor, tenant, human, ct); await Require(state, actor, "tenant.governance.read", AccessScope.ForTenant(tenant), ct);
        var saved = await store.ReadEvaluationGovernanceAsync(tenant, ct);
        foreach (var binding in saved.Defaults) _ = await SuiteAsync(actor, tenant, binding.SuiteId, binding.Version, human, ct);
        await Current(state, human, ct); return saved;
    }
    public async ValueTask<EvaluationGovernance> SaveGovernanceAsync(Guid actor, Guid tenant, IReadOnlyList<EvaluationBinding> defaults, long expectedRevision, long expectedPolicyRevision, HumanSessionReference human, CancellationToken ct = default)
    {
        var state = await State(actor, tenant, human, ct); await Require(state, actor, "tenant.governance.manage", AccessScope.ForTenant(tenant), ct); Revision(state, expectedRevision);
        ArgumentNullException.ThrowIfNull(defaults);
        EvaluationRules.ValidateBindings(defaults);
        foreach (var binding in defaults)
        {
            var suite = await SuiteAsync(actor, tenant, binding.SuiteId, binding.Version, human, ct);
            if (suite.DraftId is not null || suite.Archived || (await store.FindSuiteAsync(tenant, suite.SuiteId, ct: ct))?.Archived != false) throw new ArgumentException("Use an available reusable Suite for organization defaults.");
            await EvaluationBindingValidation.RequireAsync(store, tenant, binding, suite, ct);
        }
        var value = new EvaluationGovernance(tenant, expectedPolicyRevision + 1, Array.AsReadOnly(defaults.ToArray()));
        if (!await store.TrySaveEvaluationGovernanceAsync(Advance(state), state.Tenant.Revision, value, expectedPolicyRevision, Audit(state, actor, "tenant.governance.manage", tenant), human, ct)) throw new SecurityConflictException("stale_revision");
        return value;
    }
}
