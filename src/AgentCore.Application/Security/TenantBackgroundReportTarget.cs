using AgentCore.Application.Execution;
using AgentCore.Application.Models;
using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed class TenantBackgroundReportTarget(ITenantRuntimeStore runtime, ITenantRunStore runs, IIamStore security,
    TenantRunConfigurationResolver configurations, IamAuthorizer iam, IModelCatalog models, TenantReportSourceReadService? sourceReads = null)
{
    public async ValueTask<(TenantBackgroundCompletion Source, TenantSecurityState State, TenantAgent Agent, ResolvedAgentRunConfiguration Configuration, SessionModelSelection Model)> ResolveAsync(AgentRunOwner owner, Guid child, CancellationToken ct)
    {
        var source = await runtime.ReadTenantBackgroundCompletionAsync(owner, child, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("completion_unavailable");
        TenantBackgroundCompletionFactory.RequireParent(source);
        var current = await configurations.ResolveAsync(owner.TenantId!.Value, owner.AgentInstanceId, ct).ConfigureAwait(false);
        var state = await security.ReadTenantAsync(owner.TenantId.Value, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("tenant_unavailable");
        TenantSessionRules.RequireExecutor(state, current.Agent);
        if (sourceReads is not null) await sourceReads.RequireAuthorizedAsync(source, state, ct).ConfigureAwait(false);
        else await TenantReportReadRules.RequireAsync(state, source, iam, ct).ConfigureAwait(false);
        await TenantExecutionLineage.RequireRequesterAsync(source.Child, state, iam, runs, ct).ConfigureAwait(false);
        var model = SessionModelBinder.RefreshDefault(models, current.Configuration.Configuration.Definition, source.Parent!.Snapshot.ModelSelection)
            ?? throw new AccessDeniedException("target_model_unavailable");
        return (source, state, current.Agent, current.Configuration, model);
    }
    public async ValueTask<(TenantBackgroundCompletion Source, TenantSecurityState State, TenantAgent Agent, ResolvedAgentRunConfiguration Configuration)> RequireAsync(TenantApprovalRequest request, CancellationToken ct)
    {
        var current = await ResolveAsync(AgentRunOwner.ForTenant(request.TenantId, request.Operation.InstanceId, request.ExecutorPrincipalId), request.RunId, ct).ConfigureAwait(false);
        TenantApprovalRules.Validate(request, current.Source.Child, current.Agent);
        TenantBackgroundReportRules.Require(request, current.Source, current.Agent, current.Configuration.Configuration.Definition);
        var p = request.Operation.BackgroundReport!; var model = current.Model;
        if (p.Model != new PreparedBackgroundModel(model.CatalogKey, model.ProviderAlias, model.ModelId, model.ReasoningEffort)
            || p.ConfigurationHash != TenantBackgroundCreationRules.Hash(current.Configuration)) throw new AccessDeniedException("operation_witness_changed");
        var root = await TenantExecutionLineage.RootAsync(current.Source.Child, runs, ct).ConfigureAwait(false);
        await TenantBackgroundReportRules.ExecutorAsync(current.State, request, root, current.Configuration.Configuration.Definition, iam, ct, current.Source).ConfigureAwait(false);
        return (current.Source, current.State, current.Agent, current.Configuration);
    }
}
