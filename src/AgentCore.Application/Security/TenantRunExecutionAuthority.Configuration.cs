using AgentCore.Application.Agents;
using AgentCore.Application.Execution;
using AgentCore.Application.Models;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;
public sealed partial class TenantRunExecutionAuthority
{
    public async ValueTask<SessionModelSelection> BindNewRunModelAsync(AgentRun source, AgentDefinition definition,
        SessionModelSelection? selection, CancellationToken ct)
    {
        var current = await ResolveCurrentAsync(source, ct).ConfigureAwait(false);
        var catalog = await (configurationModels ?? throw new AccessDeniedException("model_context_required"))
            .ReadAsync(current.State, ct).ConfigureAwait(false);
        return SessionModelBinder.RefreshDefault(catalog, definition, selection);
    }
    // Prepared for the existing atomic response-outcome transaction, not a new worker or admission endpoint.
    public async ValueTask<TenantConfigurationContinuationAdmission?> PrepareConfigurationContinuationAsync(TenantRuntimeFrame frame,
        SessionSnapshot snapshot, AgentRunCommand.Complete completion, IIdGenerator ids, CancellationToken ct)
    {
        if (configurationModels is null || configurationTools is null || store is not ITenantRunStore runs
            || store is not ITenantApprovalStore approvals || store is not ITenantApprovedOperationStore effects) return null;
        try
        {
            var current = await ResolveCurrentAsync(await runs.FindTenantRunAsync(frame.Owner.TenantId!.Value, frame.RunId, ct).ConfigureAwait(false)
                ?? throw new AccessDeniedException("run_unavailable"), ct).ConfigureAwait(false);
            if (current.Run.Owner != frame.Owner || current.Run.SessionId != frame.SessionId || current.Run.Claim?.Generation != frame.Generation) return null;
            var source = completion.Apply(current.Run, ids.NewId);
            var requests = new Dictionary<Guid, TenantApprovalRequest>(); var receipts = new Dictionary<Guid, TenantOperationReceipt>();
            foreach (var id in TenantConfigurationContinuationRules.RequestIds(source))
            {
                if (await approvals.FindApprovalAsync(current.State.Tenant.TenantId, id, ct).ConfigureAwait(false) is not { } request) continue;
                requests.Add(id, request);
                if (await effects.FindOperationReceiptAsync(current.State.Tenant.TenantId, request.Operation.OperationId, ct).ConfigureAwait(false) is { } receipt) receipts.Add(receipt.OperationId, receipt);
            }
            var pending = TenantConfigurationContinuationRules.Pending(source, id => requests.GetValueOrDefault(id), id => receipts.GetValueOrDefault(id));
            if (pending is null) return null;
            var root = await TenantExecutionLineage.RootAsync(current.Run, runs, ct).ConfigureAwait(false);
            var sessionRuns = new List<AgentRun>();
            foreach (var id in await runs.ListTenantRunIdsAsync(current.State.Tenant.TenantId, source.SessionId, ct).ConfigureAwait(false))
                if (await runs.FindTenantRunAsync(current.State.Tenant.TenantId, id, ct).ConfigureAwait(false) is { } run) sessionRuns.Add(run);
            var history = store is ITenantConversationStore conversations ? await conversations.ReadTenantConversationAsync(current.State.Tenant.TenantId, source.SessionId, null, null, 100, ct).ConfigureAwait(false) : null;
            if (history is null || !TenantConfigurationContinuationRules.SafeSession(source, root, snapshot, sessionRuns, history.History.Items)) return null;
            var resolved = await configurations.ResolveAsync(current.State.Tenant.TenantId, source.AgentInstanceId, ct).ConfigureAwait(false);
            var definition = resolved.Configuration.Configuration.Definition;
            if (resolved.Agent.Instance.Revision != pending.Value.Task.InstanceRevision || definition.SelfManagement is not { } policy || !ManagementTools.Enabled(policy)
                || !RolePermissions.AllowsTool(definition, pending.Value.Task.Target) || !configurationTools.IsConfigured(pending.Value.Task.Target)) return null;
            var models = await configurationModels.ReadAsync(current.State, ct).ConfigureAwait(false);
            var selected = SessionModelBinder.RefreshDefault(models, definition, snapshot.ModelSelection);
            if (selected is null) return null;
            var next = AgentRunAdmissionFactory.ForConfigurationContinuation(ids.NewId(), ids.NewId(), ids.NewId(), snapshot with {
                ModelSelection = selected, PinnedPersona = resolved.Configuration.Persona }, source, pending.Value.Task, time.GetUtcNow(), resolved.Configuration.Skills,
                ExecutionBudgetResolver.Resolve(definition, resolved.Configuration.InstanceBudgets, false), resolved.Configuration.Configuration);
            var after = await ResolveCurrentAsync(current.Run, ct).ConfigureAwait(false);
            if (after.State.Tenant.Revision != current.State.Tenant.Revision || after.Run.Revision != current.Run.Revision) return null;
            return new(next, current.State.Tenant.Revision, pending.Value.Request.RequestId, pending.Value.Request.Operation.OperationId);
        }
        catch (AccessDeniedException) { return null; }
        catch (SecurityConflictException) { return null; }
        catch (Sessions.AgentCoreException error) when (error.Code is "ModelUnavailable" or "ModelRouteStale" or "ModelCapabilityUnsupported" or "ValidationError")
        { return null; }
    }
}
