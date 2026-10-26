using AgentCore.Application.Execution;
using AgentCore.Application.Models;
using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed class TenantBackgroundReportTarget(ITenantRuntimeStore runtime, ITenantRunStore runs, IIamStore security,
    TenantRunConfigurationResolver configurations, IamAuthorizer iam, ITenantModelCatalogReader modelCatalogs, TenantReportSourceReadService? sourceReads = null, AutoModelRouter? routing = null)
{
    public async ValueTask<(TenantBackgroundCompletion Source, TenantSecurityState State, TenantAgent Agent, ResolvedAgentRunConfiguration Configuration, SessionModelSelection Model)> ResolveAsync(AgentRunOwner owner, Guid child, CancellationToken ct, PreparedBackgroundModel? captured = null)
    {
        var source = await runtime.ReadTenantBackgroundCompletionAsync(owner, child, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("completion_unavailable");
        TenantBackgroundCompletionFactory.RequireParent(source);
        var current = await configurations.ResolveInSpaceAsync(owner.TenantId!.Value, owner.AgentInstanceId,
            source.Parent!.Snapshot.ContextSelection ?? throw new AccessDeniedException("space_context_required"), ct).ConfigureAwait(false);
        var state = await security.ReadTenantAsync(owner.TenantId.Value, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("tenant_unavailable");
        TenantSessionRules.RequireExecutor(state, current.Agent);
        if (sourceReads is not null) await sourceReads.RequireAuthorizedAsync(source, state, ct).ConfigureAwait(false);
        else await TenantReportReadRules.RequireAsync(state, source, iam, ct).ConfigureAwait(false);
        await TenantExecutionLineage.RequireRequesterAsync(source.Child, state, iam, runs, ct).ConfigureAwait(false);
        var models = await modelCatalogs.ReadAsync(state, ct).ConfigureAwait(false);
        var definition = current.Configuration.Configuration.Definition;
        SessionModelSelection model;
        if (captured is not null)
        {
            if (!ExecutionModelPolicy.Matches(models, new(captured.OfferingId, captured.ConnectionId, captured.ExternalModelId,
                captured.ReasoningEffort, AgentCore.Domain.Triggers.ExecutionModelSource.ConversationDefault, captured.Binding, captured.Decision), out _))
                throw new AccessDeniedException("target_model_unavailable");
            model = new(captured.OfferingId, captured.ConnectionId, captured.ExternalModelId, ModelSelectionSource.Host,
                captured.ReasoningEffort, Binding: captured.Binding, Decision: captured.Decision);
        }
        else model = routing is null ? SessionModelBinder.BindPreference(models, source.Parent!.Snapshot.ModelPreference ?? AgentCore.Domain.Models.ModelPreference.Inherit, definition.ModelDefaults)
            : await routing.ResolveAsync(models, source.Parent!.Snapshot.ModelPreference ?? AgentCore.Domain.Models.ModelPreference.Inherit,
                definition.ModelDefaults, false, true, "completionReport", new Dictionary<string, string> { ["inputMode"] = "completionReport" }, ct).ConfigureAwait(false);
        _ = await modelCatalogs.ReadAsync(state, ct).ConfigureAwait(false);
        return (source, state, current.Agent, current.Configuration, model);
    }
    public async ValueTask<(TenantBackgroundCompletion Source, TenantSecurityState State, TenantAgent Agent, ResolvedAgentRunConfiguration Configuration)> RequireAsync(TenantApprovalRequest request, CancellationToken ct)
    {
        var current = await ResolveAsync(AgentRunOwner.ForTenant(request.TenantId, request.Operation.InstanceId, request.ExecutorPrincipalId), request.RunId, ct, request.Operation.BackgroundReport!.Model).ConfigureAwait(false);
        TenantApprovalRules.Validate(request, current.Source.Child, current.Agent);
        TenantBackgroundReportRules.Require(request, current.Source, current.Agent, current.Configuration.Configuration.Definition);
        var p = request.Operation.BackgroundReport!; var model = current.Model;
        if (current.Configuration.Configuration.SpaceContext?.MatchesSelection(p.DestinationContext) != true
            || p.Model != new PreparedBackgroundModel(model.OfferingId, model.ConnectionId, model.ExternalModelId, model.ReasoningEffort, model.Binding, model.Decision)
            || p.ConfigurationHash != TenantBackgroundCreationRules.Hash(current.Configuration)) throw new AccessDeniedException("operation_witness_changed");
        var root = await TenantExecutionLineage.RootAsync(current.Source.Child, runs, ct).ConfigureAwait(false);
        await TenantBackgroundReportRules.ExecutorAsync(current.State, request, root, current.Configuration.Configuration.Definition, iam, ct, current.Source).ConfigureAwait(false);
        return (current.Source, current.State, current.Agent, current.Configuration);
    }
}
