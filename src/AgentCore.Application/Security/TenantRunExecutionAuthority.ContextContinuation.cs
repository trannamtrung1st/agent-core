using AgentCore.Application.Execution;
using AgentCore.Application.Models;
using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using Microsoft.Extensions.Logging;

namespace AgentCore.Application.Security;

public sealed partial class TenantRunExecutionAuthority
{
    public async ValueTask<TenantContextContinuationAdmission?> PrepareContextContinuationAsync(TenantRuntimeFrame frame,
        SessionSnapshot snapshot, AgentRunCommand.Complete completion, IIdGenerator ids, CancellationToken ct)
    {
        TenantContextContinuationAdmission? Decline(string reason)
        {
            logger?.LogDebug(new EventId(4721, "ContextContinuationWithheld"), "Context successor for Run {RunId} withheld: {Reason}", frame.RunId, reason);
            return null;
        }
        if (configurationModels is null || store is not ITenantRunStore runs || store is not ITenantConversationStore conversations) return Decline("dependencies_unavailable");
        try
        {
            var current = await ResolveCurrentAsync(await runs.FindTenantRunAsync(frame.Owner.TenantId!.Value, frame.RunId, ct).ConfigureAwait(false)
                ?? throw new AccessDeniedException("run_unavailable"), ct).ConfigureAwait(false);
            if (current.Run.Owner != frame.Owner || current.Run.SessionId != frame.SessionId || current.Run.Claim?.Generation != frame.Generation) return Decline("frame_changed");
            var source = completion.Apply(current.Run, ids.NewId); var pending = TenantContextContinuationRules.Pending(source);
            if (pending is null) return Decline("no_quiescent_intent");
            var root = await TenantExecutionLineage.RootAsync(current.Run, runs, ct).ConfigureAwait(false);
            var siblings = new List<AgentRun>();
            foreach (var id in await runs.ListTenantRunIdsAsync(current.State.Tenant.TenantId, source.SessionId, ct).ConfigureAwait(false))
                if (await runs.FindTenantRunAsync(current.State.Tenant.TenantId, id, ct).ConfigureAwait(false) is { } run) siblings.Add(run);
            var history = await conversations.ReadTenantConversationAsync(current.State.Tenant.TenantId, source.SessionId, null, null, 100, ct).ConfigureAwait(false);
            if (history is null || !TenantContextContinuationRules.SafeSession(source, root, snapshot, siblings, history.History.Items)) return Decline("session_boundary_changed");
            var resolved = await configurations.ResolveInSpaceAsync(current.State.Tenant.TenantId, source.AgentInstanceId, snapshot.ContextSelection!, ct).ConfigureAwait(false);
            var definition = resolved.Configuration.Configuration.Definition;
            var models = await configurationModels.ReadAsync(current.State, ct).ConfigureAwait(false);
            var task = string.Join("\n", history.History.Items.Where(e => root.Admission.Activation.SourceEntryIds.Contains(e.EntryId)).OrderBy(e => e.Sequence).Select(e => e.Text));
            if (task.Length > 4000) task = task[..4000];
            var selected = routing is null ? SessionModelBinder.RefreshDefault(models, definition, snapshot.ModelSelection)
                : await routing.ResolveAsync(models, snapshot.ModelPreference ?? AgentCore.Domain.Models.ModelPreference.Inherit, definition.ModelDefaults, false, true,
                    "contextContinuation", RoutingTaskSignals.Project(task, "contextContinuation"), ct); if (selected is null) return Decline("model_unavailable");
            var next = AgentRunContextContinuationFactory.Create(ids.NewId(), ids.NewId(), ids.NewId(), snapshot with { ModelSelection = selected, PinnedPersona = resolved.Configuration.Persona },
                source, pending, task, time.GetUtcNow(), resolved.Configuration.Skills, ExecutionBudgetResolver.Resolve(definition, resolved.Configuration.InstanceBudgets, false), resolved.Configuration.Configuration);
            var after = await ResolveCurrentAsync(current.Run, ct).ConfigureAwait(false);
            if (after.State.Tenant.Revision != current.State.Tenant.Revision || after.Run.Revision != current.Run.Revision) return Decline("source_changed");
            return new(next, current.State.Tenant.Revision, pending.TransitionId);
        }
        catch (AccessDeniedException ex) { return Decline(ex.ReasonCode); }
        catch (SecurityConflictException ex) { return Decline(ex.ReasonCode); }
        catch (AgentCore.Application.Sessions.AgentCoreException ex) { return Decline(ex.Code); }
    }
}
