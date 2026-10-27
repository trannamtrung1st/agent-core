using AgentCore.Application.Models;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Tasks;
using AgentCore.Domain.Triggers;

namespace AgentCore.Application.Tasks;

/// <summary>One bounded pass called by the incumbent tenant Run coordinator; no separate runner.</summary>
public sealed class TenantTaskCoordinationService(ITenantTaskStore tasks, IIamStore security, TenantRunConfigurationResolver configuration,
    ITenantModelCatalogReader models, IIdGenerator ids, TimeProvider time, IamAuthorizer iam, ITenantAgentMessageStore messages, ITenantSpaceStore spaces, AutoModelRouter? routing = null, TenantRunSourceAuthority? sources = null)
{
    public async ValueTask ReconcileAsync(int limit, CancellationToken ct)
    {
        await ReconcileMessageWakesAsync(limit, ct);
        var reconciliation = await tasks.ListTaskReconciliationAsync(limit, ct);
        AgentCore.Application.Observability.TaskTelemetry.Recovery("worker", reconciliation.Count, reconciliation.Select(t => (DateTimeOffset?)t.Task.CreatedAt).Min(), time.GetUtcNow());
        foreach (var candidate in reconciliation)
        {
            var task = candidate.Task; var tenant = task.Source.TenantId;
            if (task.State == DelegatedTaskState.PendingDependencies && (candidate.Source is null
                || SessionRetention.IsDue(candidate.Source.Snapshot, time.GetUtcNow())))
            {
                await tasks.ReconcileTaskAsync(tenant, task.TaskId, task.Revision, ids.NewId(), ct, "task_source_unavailable"); continue;
            }
            if (task.State != DelegatedTaskState.PendingDependencies || task.DeadlineAt <= time.GetUtcNow()
                || candidate.Dependencies.Any(t => TaskLimits.Terminal(t.State) && t.State != DelegatedTaskState.Succeeded))
            {
                await tasks.ReconcileTaskAsync(tenant, task.TaskId, task.Revision, ids.NewId(), ct); continue;
            }
            if (candidate.Dependencies.Count != task.Request.DependsOnTaskIds.Count || candidate.Dependencies.Any(t => t.State != DelegatedTaskState.Succeeded)) continue;
            try
            {
                var at = DateTimeOffset.FromUnixTimeMilliseconds(time.GetUtcNow().ToUnixTimeMilliseconds());
                var state = await security.ReadTenantAsync(tenant, ct) ?? throw new AccessDeniedException("tenant_unavailable");
                var resolved = await configuration.ResolveInSpaceAsync(tenant, task.Assignment.InstanceId,
                    new(tenant, task.Assignment.SpaceId, task.Assignment.EnvironmentId,
                        task.Assignment.EnvironmentSelectionEpoch, 1, at), ct);
                if (TaskLimits.Hash(resolved.Configuration.Configuration) != task.Assignment.ConfigurationHash || task.Assignment.Model is not { } pin)
                    throw new AccessDeniedException("task_assignment_changed");
                var catalog = await models.ReadAsync(state, ct);
                if (!ExecutionModelPolicy.Matches(catalog, new(pin.OfferingId, pin.ConnectionId, pin.ExternalModelId, pin.ReasoningEffort,
                    ExecutionModelSource.ConversationDefault, pin.Binding, pin.Decision), out _)) throw new AccessDeniedException("task_model_unavailable");
                var activation = task.Source.ActorKind == TaskActorKind.Human ? task.Source.RootActivationId : ids.NewId();
                var reserved = task.Source.ActorKind == TaskActorKind.Human && security is ITenantApprovalStore approvalOwner
                    ? await approvalOwner.FindTaskDelegationApprovalAsync(tenant, task.Source.OperationScope, task.Request.RequestKey, ct) : null;
                var session = ids.NewSessionId(); var run = reserved is not null && TenantTaskConsentRules.IsHuman(reserved) ? reserved.RunId : ids.NewId(); var ready = task.Advance(DelegatedTaskState.Queued, at, session, run);
                var graph = TaskAdmissionFactory.Worker(ready, resolved.Agent, resolved.Configuration, pin, session, ids.NewId(), activation, run, ids.NewId(), at);
                await tasks.AdmitReadyTaskAsync(new(tenant, task.TaskId, task.Revision, graph.Session, graph.Run, ids.NewId()), ct);
            }
            catch (SecurityConflictException) { } // A current CAS winner or refreshed pass owns the transition.
            catch (AccessDeniedException error)
            {
                AgentCore.Application.Observability.TaskTelemetry.Refusal(error.ReasonCode);
                await tasks.ReconcileTaskAsync(tenant, task.TaskId, task.Revision, ids.NewId(), ct, "task_assignment_unavailable");
            }
        }
        var reports = await tasks.ListTaskReportsAsync(limit, ct);
        AgentCore.Application.Observability.TaskTelemetry.Recovery("report", reports.Count, reports.Select(r => (DateTimeOffset?)r.Notification.CreatedAt).Min(), time.GetUtcNow());
        foreach (var source in reports)
        {
            try
            {
                var at = DateTimeOffset.FromUnixTimeMilliseconds(time.GetUtcNow().ToUnixTimeMilliseconds());
                TaskReportRules.Parent(source, at);
                var tenant = source.Task.Source.TenantId;
                var state = await security.ReadTenantAsync(tenant, ct) ?? throw new AccessDeniedException("tenant_unavailable");
                await TaskReportRules.AuthorizeAsync(source, state, iam, at, ct);
                if (TenantRunSourceAuthority.HasSources(source.Worker!))
                    await (sources ?? throw new AccessDeniedException("source_context_unavailable")).RequireAsync(source.Worker!, ct);
                var parent = source.Parent!;
                var resolved = await configuration.ResolveInSpaceAsync(tenant, parent.Owner.AgentInstanceId,
                    parent.Snapshot.ContextSelection ?? throw new AccessDeniedException("space_context_required"), ct);
                var catalog = await models.ReadAsync(state, ct);
                var preference = parent.Snapshot.ModelPreference ?? AgentCore.Domain.Models.ModelPreference.Inherit;
                var defaults = resolved.Configuration.Configuration.Definition.ModelDefaults;
                var selection = routing is null ? SessionModelBinder.BindPreference(catalog, preference, defaults)
                    : await routing.ResolveAsync(catalog, preference, defaults, false, false, "taskCompletion", RoutingTaskSignals.Project(null, "taskCompletion"), ct);
                var pin = new AgentRunModelPin(selection.OfferingId, selection.ConnectionId, selection.ExternalModelId, selection.ReasoningEffort, selection.Binding, selection.Decision);
                if (!ExecutionModelPolicy.Matches(catalog, new(pin.OfferingId, pin.ConnectionId, pin.ExternalModelId, pin.ReasoningEffort,
                    ExecutionModelSource.ConversationDefault, pin.Binding, pin.Decision), out _)) throw new AccessDeniedException("task_report_model_unavailable");
                var report = TaskReportRules.Create(source, resolved.Configuration, pin, ids.NewId(), ids.NewId(), ids.NewId(), at);
                await tasks.AdmitTaskReportAsync(new(tenant, source.Task.TaskId, source.Notification.Revision, parent.Snapshot.Revision, report, ids.NewId()), ct);
            }
            catch (SecurityConflictException) { } // Busy parent or a winning receipt; retry admission only on a fresh pass.
            catch (AccessDeniedException error)
            { await tasks.WithholdTaskReportAsync(source.Task.Source.TenantId, source.Task.TaskId, source.Notification.Revision, error.ReasonCode, ct); }
            catch (AgentCore.Application.Sessions.AgentCoreException error) when (error.Code is "ModelUnavailable" or "ModelRouteStale" or "ModelCapabilityUnsupported" or "ModelRoutingUnavailable" or "ValidationError")
            { await tasks.WithholdTaskReportAsync(source.Task.Source.TenantId, source.Task.TaskId, source.Notification.Revision, "task_report_model_unavailable", ct); }
        }
    }
    private async ValueTask ReconcileMessageWakesAsync(int limit, CancellationToken ct)
    {
        var wakes = await messages.ListAgentMessageWakesAsync(limit, ct);
        AgentCore.Application.Observability.TaskTelemetry.Recovery("message", wakes.Count, wakes.Select(w => (DateTimeOffset?)w.Message.CreatedAt).Min(), time.GetUtcNow());
        foreach (var source in wakes)
        {
            try
            {
                var tenant = source.Message.TenantId; var at = DateTimeOffset.FromUnixTimeMilliseconds(time.GetUtcNow().ToUnixTimeMilliseconds());
                var state = await security.ReadTenantAsync(tenant, ct) ?? throw new AccessDeniedException("tenant_unavailable");
                var (sender, senderCurrent) = await configuration.CurrentPolicyAsync(tenant, source.Message.SenderInstanceId!.Value, ct);
                var (recipient, _) = await configuration.CurrentPolicyAsync(tenant, source.Message.RecipientInstanceId, ct);
                await AgentMessageWakeRules.AuthorizeAsync(source, sender, recipient, state, senderCurrent, iam, ct);
                var space = await spaces.FindPrivateSpaceAsync(tenant, recipient.Instance.InstanceId, ct) ?? throw new AccessDeniedException("private_space_unavailable");
                var resolved = await configuration.ResolveInSpaceAsync(tenant, recipient.Instance.InstanceId, new(tenant, space.SpaceId, null, 0, 1, at), ct);
                var catalog = await models.ReadAsync(state, ct);
                var preference = recipient.Instance.UnattendedModel ?? AgentCore.Domain.Models.ModelPreference.Inherit;
                var defaults = resolved.Configuration.Configuration.Definition.ModelDefaults;
                var selection = routing is null ? SessionModelBinder.BindPreference(catalog, preference, defaults)
                    : await routing.ResolveAsync(catalog, preference, defaults, false, true, "agentMessage", RoutingTaskSignals.Project(null, "agentMessage"), ct);
                var pin = new AgentRunModelPin(selection.OfferingId, selection.ConnectionId, selection.ExternalModelId, selection.ReasoningEffort, selection.Binding, selection.Decision);
                if (!ExecutionModelPolicy.Matches(catalog, new(pin.OfferingId, pin.ConnectionId, pin.ExternalModelId, pin.ReasoningEffort,
                    ExecutionModelSource.ConversationDefault, pin.Binding, pin.Decision), out _)) throw new AccessDeniedException("message_wake_model_unavailable");
                var graph = AgentMessageWakeRules.Create(source, recipient, resolved.Configuration, pin, ids.NewSessionId(), ids.NewId(), ids.NewId(), ids.NewId(), ids.NewId(), at);
                await messages.AdmitAgentMessageWakeAsync(new(source, graph.Session, graph.Run, ids.NewId()), ct);
            }
            catch (SecurityConflictException) { } // One active/queued wake owns this thread; new messages stay pending.
            catch (AccessDeniedException error) { await messages.WithholdAgentMessageWakeAsync(source.Message.TenantId, source.Message.MessageId, source.Receipt.Revision, error.ReasonCode, ct); }
            catch (AgentCore.Application.Sessions.AgentCoreException error) when (error.Code is "ModelUnavailable" or "ModelRouteStale" or "ModelCapabilityUnsupported" or "ModelRoutingUnavailable" or "ValidationError")
            { await messages.WithholdAgentMessageWakeAsync(source.Message.TenantId, source.Message.MessageId, source.Receipt.Revision, "message_wake_model_unavailable", ct); }
        }
    }
}
