using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tasks;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Tasks;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore
{
    public ValueTask RequireAgentMessageExecutionSourcesAsync(AgentRun wake, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate) lock (sessionBacking.AdmissionGate) lock (instances.CredentialGate) lock (definitions.TenantGate)
        {
            var work = wake.Admission.AgentMessage ?? throw new AccessDeniedException("message_wake_unavailable"); var tenant = wake.Owner.TenantId!.Value;
            var saved = FindAgentMessageSourceAsync(tenant, work.MessageId, ct).Result ?? throw new AccessDeniedException("message_source_unavailable");
            if (saved.Receipt.WakeSessionId != wake.SessionId || saved.Receipt.WakeRootActivationId != work.WakeRootActivationId || saved.Receipt.WakeHops != work.WakeHops)
                throw new AccessDeniedException("message_wake_unavailable");
            var sourceRun = sessionBacking.ReadTenantRun(tenant, saved.Message.SourceRunId!.Value) ?? throw new AccessDeniedException("message_source_unavailable");
            var root = TenantExecutionLineage.RootAsync(sourceRun, this, ct).AsTask().GetAwaiter().GetResult();
            var sender = FindTenantAgentAsync(tenant, saved.Message.SenderInstanceId!.Value, ct).Result ?? throw new AccessDeniedException("message_source_unavailable");
            var recipient = FindTenantAgentAsync(tenant, saved.Message.RecipientInstanceId, ct).Result ?? throw new AccessDeniedException("message_recipient_unavailable");
            AgentMessageWakeRules.AuthorizeAsync(new(saved.Thread, saved.Message, saved.Receipt, sourceRun, root.Admission.AgentMessage), sender, recipient, _tenants[tenant],
                AgentCore.Application.Agents.InstanceSettingsResolver.Resolve(TenantAgentDefinitionBaseline(sender), sender.Instance.SettingsOverrides), new(this), ct, pending: false).AsTask().GetAwaiter().GetResult();
            return ValueTask.CompletedTask;
        }
    }
    public ValueTask<AgentMessageResult?> FindAgentMessageSourceAsync(Guid tenant, Guid message, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate) lock (sessionBacking.AdmissionGate)
        {
            if (!agentMessages.TryGetValue((tenant, message), out var row) || row.Message.SourceSessionId is not { } source
                || sessionBacking.ReadTenantSession(tenant, source) is not { } session || SessionRetention.IsDue(session.Snapshot, TaskNow)) return ValueTask.FromResult<AgentMessageResult?>(null);
            return ValueTask.FromResult<AgentMessageResult?>(new(agentThreads[(tenant, row.Message.ThreadId)], row.Message, row.Receipt, _tenants[tenant].Tenant.Revision));
        }
    }
    private int messageWakeOffset;
    public ValueTask<IReadOnlyList<AgentMessageWakeSource>> ListAgentMessageWakesAsync(int limit, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate) lock (sessionBacking.AdmissionGate)
        {
            var candidates = agentMessages.Values.Where(r => r.Receipt.State == AgentMessageDelivery.NotificationPending)
                .OrderBy(r => r.Message.CreatedAt).ThenBy(r => r.Message.MessageId).ToArray();
            var take = Math.Clamp(limit, 1, 32); var offset = candidates.Length == 0 ? 0 : messageWakeOffset % candidates.Length;
            messageWakeOffset = checked(offset + take); var result = new List<AgentMessageWakeSource>();
            foreach (var row in candidates.Skip(offset).Concat(candidates.Take(offset)).Take(take))
            {
                var run = row.Message.SourceRunId is { } id ? sessionBacking.ReadTenantRun(row.Message.TenantId, id) : null;
                if (run is null || row.Message.SourceSessionId is not { } sessionId
                    || sessionBacking.ReadTenantSession(row.Message.TenantId, sessionId) is not { } session || SessionRetention.IsDue(session.Snapshot, TaskNow))
                {
                    WithholdAgentMessageWakeAsync(row.Message.TenantId, row.Message.MessageId, row.Receipt.Revision, "message_source_unavailable", ct).GetAwaiter().GetResult();
                    continue;
                }
                try
                {
                    var root = TenantExecutionLineage.RootAsync(run, this, ct).AsTask().GetAwaiter().GetResult();
                    result.Add(new(agentThreads[(row.Message.TenantId, row.Message.ThreadId)], row.Message, row.Receipt, run, root.Admission.AgentMessage));
                }
                catch (AccessDeniedException)
                { WithholdAgentMessageWakeAsync(row.Message.TenantId, row.Message.MessageId, row.Receipt.Revision, "message_source_unavailable", ct).GetAwaiter().GetResult(); }
            }
            return ValueTask.FromResult<IReadOnlyList<AgentMessageWakeSource>>(result);
        }
    }
    public ValueTask WithholdAgentMessageWakeAsync(Guid tenant, Guid message, long revision, string reason, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate)
        {
            if (agentMessages.TryGetValue((tenant, message), out var row) && row.Receipt.State == AgentMessageDelivery.NotificationPending && row.Receipt.Revision == revision)
            {
                var withheld = row.Receipt with { State = AgentMessageDelivery.Undeliverable, Reason = TaskLimits.Text(reason, 160, "Wake reason"), Revision = revision + 1, UpdatedAt = TaskNow };
                agentMessages[(tenant, message)] = (row.Message, withheld, row.Scope);
                AgentCore.Application.Observability.TaskTelemetry.Message(AgentCore.Application.Observability.AgentMessageEvent.WakeWithheld, row.Message, withheld);
            }
            return ValueTask.CompletedTask;
        }
    }
    public ValueTask<bool> AdmitAgentMessageWakeAsync(AgentMessageWakeAdmission admission, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate) lock (sessionBacking.AdmissionGate) lock (instances.CredentialGate) lock (definitions.TenantGate)
        {
            var message = admission.Source.Message; var tenant = message.TenantId; var state = _tenants[tenant];
            var saved = FindAgentMessageSourceAsync(tenant, message.MessageId, ct).Result ?? throw new AccessDeniedException("message_source_unavailable");
            if (saved.Receipt.WakeRunId is not null) return ValueTask.FromResult(false);
            if (saved.Receipt.Revision != admission.Source.Receipt.Revision || saved.Message != message || saved.Thread != admission.Source.Thread) throw new SecurityConflictException("message_wake_changed");
            var sourceRun = sessionBacking.ReadTenantRun(tenant, message.SourceRunId!.Value) ?? throw new AccessDeniedException("message_source_unavailable");
            var root = TenantExecutionLineage.RootAsync(sourceRun, this, ct).AsTask().GetAwaiter().GetResult();
            if (admission.Source.WakeAncestor != root.Admission.AgentMessage) throw new SecurityConflictException("message_wake_changed");
            var source = new AgentMessageWakeSource(saved.Thread, saved.Message, saved.Receipt, sourceRun, root.Admission.AgentMessage);
            var sender = FindTenantAgentAsync(tenant, message.SenderInstanceId!.Value, ct).Result ?? throw new AccessDeniedException("message_source_unavailable");
            var recipient = FindTenantAgentAsync(tenant, message.RecipientInstanceId, ct).Result ?? throw new AccessDeniedException("message_recipient_unavailable");
            AgentMessageWakeRules.AuthorizeAsync(source, sender, recipient, state, AgentCore.Application.Agents.InstanceSettingsResolver.Resolve(TenantAgentDefinitionBaseline(sender), sender.Instance.SettingsOverrides), new(this), ct).AsTask().GetAwaiter().GetResult();
            var pair = saved.Thread;
            var pairThreads = agentThreads.Values.Where(t => t.TenantId == tenant && t.TaskId is null
                && t.FirstInstanceId == pair.FirstInstanceId && t.SecondInstanceId == pair.SecondInstanceId).Select(t => t.ThreadId).ToHashSet();
            var prior = agentMessages.Values.Where(r => r.Message.TenantId == tenant && pairThreads.Contains(r.Message.ThreadId) && r.Receipt.WakeRunId is not null)
                .Select(r => sessionBacking.AgentRuns.Runs.GetValueOrDefault(r.Receipt.WakeRunId!.Value)).OfType<AgentRun>().ToArray();
            if (prior.Any(r => !r.IsTerminal)) throw new SecurityConflictException("message_thread_busy");
            if (prior.Count(r => r.CreatedAtUtc > TaskNow.AddHours(-1)) >= 8) throw new AccessDeniedException("message_pair_rate_limit");
            AgentMessageWakeRules.Shape(admission, recipient, TaskNow); var run = admission.Run;
            RequireSpaceExecutionSource(run, state, new(this), ct, admission: true).AsTask().GetAwaiter().GetResult();
            AgentCore.Application.Models.ExecutionModelPolicy.RequireCurrentDecision(run.PinnedModel, modelSettings.GetValueOrDefault(tenant)?.Revision ?? 0);
            StageTaskGraph(new(tenant, run.SessionId, recipient.Instance.InstanceId, recipient.PrincipalId, sourceRun.Admission.ExecutionContext!.OriginalInitiatorPrincipalId ?? recipient.PrincipalId, run.CreatedAtUtc), admission.Session, run);
            var receipt = saved.Receipt with { Revision = saved.Receipt.Revision + 1, State = AgentMessageDelivery.ActivationAdmitted, UpdatedAt = TaskNow,
                WakeRunId = run.AgentRunId, WakeSessionId = run.SessionId, WakeRootActivationId = run.Admission.AgentMessage!.WakeRootActivationId, WakeHops = run.Admission.AgentMessage.WakeHops }; receipt.Validate();
            var row = agentMessages[(tenant, message.MessageId)]; agentMessages[(tenant, message.MessageId)] = (row.Message, receipt, row.Scope); BumpTaskSecurity(state);
            _audit.Add(new(admission.AuditId, tenant, recipient.PrincipalId, "agent-messages.wake", message.MessageId.ToString("D"), "allowed", "recipient_own_wake_admitted", TaskNow));
            AgentCore.Application.Observability.TaskTelemetry.Message(AgentCore.Application.Observability.AgentMessageEvent.WakeAdmitted, message, receipt);
            AgentCore.Application.Observability.TaskTelemetry.DeliveryLag("message", message.CreatedAt, run.CreatedAtUtc);
            return ValueTask.FromResult(true);
        }
    }
}
