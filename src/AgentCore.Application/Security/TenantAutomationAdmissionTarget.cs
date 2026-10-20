using AgentCore.Application.Execution;
using AgentCore.Application.Models;
using AgentCore.Application.Ports;
using AgentCore.Domain.Security;
using AgentCore.Domain.Triggers;

namespace AgentCore.Application.Security;

public sealed record TenantAutomationAdmissionState(TenantSecurityState State, TenantAgent Agent, ResolvedAgentRunConfiguration Configuration,
    Automation Automation, TriggerOccurrence Occurrence);

public sealed class TenantAutomationAdmissionTarget(IIamStore security, ITenantAutomationStore store, TenantRunConfigurationResolver configuration,
    IamAuthorizer iam, IModelCatalog models)
{
    public async ValueTask<TenantAutomationAdmissionState> RequireAsync(TenantApprovalRequest request, CancellationToken ct)
    {
        var p = request.Operation.AutomationAdmission ?? throw new ArgumentException("Automation admission source required.");
        var state = await security.ReadTenantAsync(request.TenantId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("tenant_unavailable");
        var source = await store.FindTenantAutomationAsync(request.TenantId, p.Current.AutomationId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("automation_unavailable");
        var occurrence = await store.FindTenantOccurrenceAsync(request.TenantId, p.Occurrence.OccurrenceId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("occurrence_unavailable");
        if (!TenantAutomationRules.Same(source, p.Next) || !TenantAutomationAdmissionApprovalRules.SameOccurrence(occurrence, TenantAutomationAdmissionApprovalRules.Pending(p)))
            throw new SecurityConflictException("operation_witness_changed");
        var current = await configuration.ResolveAsync(request.TenantId, request.Operation.InstanceId, ct).ConfigureAwait(false);
        TenantAutomationAdmissionApprovalRules.Shape(request, current.Agent);
        try { TenantAutomationRules.AdmissionSource(state, p.Current, current.Agent, current.Configuration.Configuration.Definition, p.ManualInitiatorPrincipalId); }
        catch (ArgumentException) { throw new AccessDeniedException("operation_configuration_changed"); }
        if (TenantAutomationRules.Hash(current.Configuration) != p.ConfigurationHash) throw new SecurityConflictException("operation_witness_changed");
        var model = ExecutionModelPolicy.Resolve(models, current.Configuration.Configuration.Definition, current.Agent.Instance, p.Current);
        if (!model.Accepted || model.Pin is null || model.Pin.CatalogKey != p.Model.CatalogKey || model.Pin.ProviderAlias != p.Model.ProviderAlias
            || model.Pin.ModelId != p.Model.ModelId || model.Pin.ReasoningEffort != p.Model.ReasoningEffort) throw new AccessDeniedException("automation_model_unavailable");
        await TenantAutomationAdmissionApprovalRules.ExecutorAsync(state, request, iam, ct).ConfigureAwait(false);
        return new(state, current.Agent, current.Configuration, source, occurrence);
    }
}
