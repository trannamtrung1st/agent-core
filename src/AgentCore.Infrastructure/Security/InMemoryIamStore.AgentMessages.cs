using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tasks;
using AgentCore.Domain.Security;
using AgentCore.Domain.Tasks;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore : ITenantAgentMessageStore
{
    private readonly Dictionary<(Guid Tenant, Guid Thread), AgentThread> agentThreads = [];
    private readonly HashSet<(Guid Tenant, Guid Thread)> retiredAgentThreads = [];
    private readonly Dictionary<(Guid Tenant, Guid Message), (AgentMessage Message, AgentMessageReceipt Receipt, string Scope)> agentMessages = [];
    public ValueTask<AgentMessageResult> AppendAgentMessageAsync(AgentMessageCommand command, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate) lock (sessionBacking.AdmissionGate) lock (instances.CredentialGate) lock (definitions.TenantGate)
        {
            command.Actor.Validate(); var tenant = command.TenantId; var state = _tenants.GetValueOrDefault(tenant) ?? throw new AccessDeniedException("tenant_unavailable");
            var task = command.TaskId is { } taskId ? delegatedTasks.GetValueOrDefault((tenant, taskId)) ?? throw new AccessDeniedException("resource_unavailable") : null;
            var sourceSession = command.Actor.Agent?.SessionId ?? task?.Source.SessionId ?? throw new AccessDeniedException("agent_sender_required");
            var (source, run, sender) = RequireTaskSource(tenant, sourceSession, command.Actor, "agent-messages.send", ct, task is not null ? TaskAuthority.Scope(tenant, task.TaskId) : command.ThreadId is { } selectedThread
                ? AgentCore.Domain.Security.AccessScope.ForResource(tenant, "agent-thread", selectedThread.ToString("D"))
                : AgentCore.Domain.Security.AccessScope.ForResource(tenant, "agent", command.Actor.Agent!.Owner.AgentInstanceId.ToString("D")));
            if (task is not null && !CanReadTask(state, command.Actor, task, ct)) throw new AccessDeniedException("resource_unavailable");
            AgentThread? thread;
            if (task is not null)
            {
                if (task.WorkerSessionId is null || command.RecipientInstanceId is not null) throw new AccessDeniedException("task_message_unavailable");
                thread = agentThreads.Values.SingleOrDefault(t => t.TenantId == tenant && t.TaskId == task.TaskId)
                    ?? new(tenant, command.NewThreadId, task.Source.InstanceId, task.Assignment.InstanceId, task.TaskId, task.Source.SessionId, task.WorkerSessionId, TaskNow);
            }
            else
            {
                if (command.Actor.Human is not null || command.Actor.Tool != "agent-messages.send" || command.RecipientInstanceId is not { } recipientId) throw new AccessDeniedException("agent_sender_required");
                var recipient = FindTenantAgentAsync(tenant, recipientId, ct).Result ?? throw new AccessDeniedException("agent_message_unavailable");
                AgentMessageRules.DirectPolicyAsync(state, sender, recipient, new(this), ct).AsTask().GetAwaiter().GetResult();
                thread = agentThreads.Values.SingleOrDefault(t => t.TenantId == tenant && t.TaskId is null && !retiredAgentThreads.Contains((tenant, t.ThreadId)) && t.Includes(sender.Instance.InstanceId) && t.Includes(recipientId))
                    ?? new(tenant, command.NewThreadId, sender.Instance.InstanceId, recipientId, null, null, null, TaskNow);
                if (agentThreads.ContainsKey((tenant, thread.ThreadId)) && !agentMessages.Values.Any(m => m.Message.TenantId == tenant
                    && m.Message.ThreadId == thread.ThreadId && m.Message.SourceSessionId is { } id && sessionBacking.ReadTenantSession(tenant, id) is not null))
                {
                    thread = new(tenant, command.NewThreadId, sender.Instance.InstanceId, recipientId, null, null, null, TaskNow);
                }
            }
            if (command.ThreadId is { } selected && selected != thread.ThreadId) throw new AccessDeniedException("resource_unavailable");
            var scope = AgentMessageRules.SourceScope(command); var hash = AgentMessageRules.Fingerprint(command); var key = TaskLimits.Key(command.RequestKey);
            var previous = agentMessages.Values.SingleOrDefault(m => m.Message.TenantId == tenant && m.Message.SenderPrincipalId == command.Actor.PrincipalId && m.Scope == scope && m.Message.RequestKey == key);
            if (previous.Message is not null)
            {
                if (previous.Message.RequestHash != hash || previous.Message.ThreadId != thread.ThreadId) throw new SecurityConflictException("message_request_conflict");
                AgentCore.Application.Observability.TaskTelemetry.Message(AgentCore.Application.Observability.AgentMessageEvent.Replayed, previous.Message, previous.Receipt);
                return ValueTask.FromResult(new AgentMessageResult(thread, previous.Message, previous.Receipt, state.Tenant.Revision, true));
            }
            if (task is not null && TaskLimits.Terminal(task.State)) throw new AccessDeniedException("task_message_unavailable");
            if (state.Tenant.Revision != command.ExpectedSecurityRevision) throw new SecurityConflictException("stale_revision");
            var sequence = agentMessages.Values.Where(m => m.Message.TenantId == tenant && m.Message.ThreadId == thread.ThreadId).Select(m => m.Message.Sequence).DefaultIfEmpty().Max() + 1;
            var message = AgentMessageRules.Create(command, thread, source, run, sequence, TaskNow);
            var receiver = FindTenantAgentAsync(tenant, message.RecipientInstanceId, ct).Result ?? throw new AccessDeniedException("message_recipient_unavailable");
            TaskAuthority.RequireAsync(state, receiver.PrincipalId, "agent-messages.read", task is null
                ? AgentCore.Domain.Security.AccessScope.ForResource(tenant, "agent", receiver.Instance.InstanceId.ToString("D")) : TaskAuthority.Scope(tenant, task.TaskId), new(this), ct).AsTask().GetAwaiter().GetResult();
            var wakeEnabled = task is null && FindTenantAgentAsync(tenant, message.RecipientInstanceId, ct).Result?.Instance.Cooperation?.AllowDirectMessageWake == true;
        var receipt = new AgentMessageReceipt(tenant, message.MessageId, message.RecipientInstanceId,
            wakeEnabled ? AgentMessageDelivery.NotificationPending : AgentMessageDelivery.DeliveredToAuthorizedInbox, 1, TaskNow); receipt.Validate();
            if (agentMessages.ContainsKey((tenant, message.MessageId)) || _audit.Any(a => a.AuditId == command.AuditId)) throw new SecurityConflictException("identity_conflict");
            if (thread.TaskId is null)
                foreach (var former in agentThreads.Values.Where(t => t.TenantId == tenant && t.TaskId is null && t.ThreadId != thread.ThreadId
                    && t.FirstInstanceId == thread.FirstInstanceId && t.SecondInstanceId == thread.SecondInstanceId))
                    retiredAgentThreads.Add((tenant, former.ThreadId));
            agentThreads.TryAdd((tenant, thread.ThreadId), thread); agentMessages.Add((tenant, message.MessageId), (message, receipt, scope)); BumpTaskSecurity(state);
            _audit.Add(new(command.AuditId, tenant, command.Actor.PrincipalId, "agent-messages.send", message.MessageId.ToString("D"), "allowed", "message_recorded", TaskNow));
            AgentCore.Application.Observability.TaskTelemetry.Message(AgentCore.Application.Observability.AgentMessageEvent.Appended, message, receipt);
            return ValueTask.FromResult(new AgentMessageResult(thread, message, receipt, state.Tenant.Revision + 1));
        }
    }
    private void RequireMessageReader(Guid tenant, TaskActor actor, CancellationToken ct)
    {
        actor.Validate(); if (actor.Human is { } human) RequireHumanSession(tenant, human);
        else
        {
            var frame = actor.Agent!;
            var source = sessionBacking.ReadTenantSession(tenant, frame.SessionId) ?? throw new AccessDeniedException("resource_unavailable");
            var agent = FindTenantAgentAsync(tenant, source.Owner.AgentInstanceId, ct).Result ?? throw new AccessDeniedException("resource_unavailable");
            var run = sessionBacking.ReadTenantRun(tenant, frame.RunId);
            TaskAuthority.SourceAsync(_tenants[tenant], actor, source, agent, run,
                AgentCore.Application.Agents.InstanceSettingsResolver.Resolve(TenantAgentDefinitionBaseline(agent), agent.Instance.SettingsOverrides), new(this), TaskNow, "sessions.read", false, ct).AsTask().GetAwaiter().GetResult();
        }
    }
    private bool CanReadAgentThread(TenantSecurityState state, TaskActor actor, AgentThread thread, CancellationToken ct)
    {
        if (!AgentMessageRules.CanReadThreadAsync(state, actor, thread, new(this), ct, actor.Agent is { } frame
            ? sessionBacking.ReadRetainedTenantOperationRun(state.Tenant.TenantId, frame.RunId) : null).Result) return false;
        if (thread.TaskId is not { } taskId) return true;
        return delegatedTasks.GetValueOrDefault((thread.TenantId, taskId)) is { } task
            && sessionBacking.ReadTenantSession(thread.TenantId, task.Source.SessionId) is { } source && !AgentCore.Domain.Conversation.SessionRetention.IsDue(source.Snapshot, TaskNow)
            && CanReadTask(state, actor, task, ct);
    }
    public ValueTask<TaskPage<AgentThread>> ReadAgentThreadsAsync(TaskQuery query, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); if (query.Limit is < 1 or > 100) throw new ArgumentException("Invalid thread page.");
        lock (_gate) lock (sessionBacking.AdmissionGate) lock (instances.CredentialGate) lock (definitions.TenantGate)
        {
            RequireMessageReader(query.TenantId, query.Actor, ct); var state = _tenants[query.TenantId];
            var items = agentThreads.Values.Where(t => t.TenantId == query.TenantId && !retiredAgentThreads.Contains((query.TenantId, t.ThreadId)) && (query.Scope != "direct" || t.TaskId is null) && (query.InstanceId is null || t.Includes(query.InstanceId.Value))
                && (query.Cursor is null || t.ThreadId.CompareTo(query.Cursor.Value) > 0)).OrderBy(t => t.ThreadId)
                .Where(t => CanReadAgentThread(state, query.Actor, t, ct)
                    && agentMessages.Values.Any(m => m.Message.TenantId == query.TenantId && m.Message.ThreadId == t.ThreadId
                        && m.Message.SourceSessionId is { } source && sessionBacking.ReadTenantSession(query.TenantId, source) is not null)).Take(query.Limit + 1).ToArray();
            return ValueTask.FromResult(new TaskPage<AgentThread>(items.Take(query.Limit).ToArray(), items.Length > query.Limit ? items[query.Limit - 1].ThreadId : null, items.Length > query.Limit, state.Tenant.Revision));
        }
    }
    public ValueTask<TaskPage<AgentMessage>> ReadAgentMessagesAsync(Guid tenant, TaskActor actor, Guid threadId, long afterSequence, int limit, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); if (afterSequence < 0 || limit is < 1 or > TaskLimits.MaxMessagePage) throw new ArgumentException("Invalid message page.");
        lock (_gate) lock (sessionBacking.AdmissionGate) lock (instances.CredentialGate) lock (definitions.TenantGate)
        {
            RequireMessageReader(tenant, actor, ct); var state = _tenants[tenant];
            var thread = agentThreads.GetValueOrDefault((tenant, threadId)) ?? throw new AccessDeniedException("resource_unavailable");
            if (retiredAgentThreads.Contains((tenant, threadId)) || !CanReadAgentThread(state, actor, thread, ct)) throw new AccessDeniedException("resource_unavailable");
            if (thread.TaskId is null && !agentMessages.Values.Any(m => m.Message.TenantId == tenant && m.Message.ThreadId == threadId
                && m.Message.SourceSessionId is { } sourceId && sessionBacking.ReadTenantSession(tenant, sourceId) is not null))
                throw new AccessDeniedException("resource_unavailable");
            var items = agentMessages.Values.Select(m => m.Message).Where(m => m.TenantId == tenant && m.ThreadId == threadId && m.Sequence > afterSequence
                && m.SourceSessionId is { } sourceId && sessionBacking.ReadTenantSession(tenant, sourceId) is { } source && !AgentCore.Domain.Conversation.SessionRetention.IsDue(source.Snapshot, TaskNow)).OrderBy(m => m.Sequence).Take(limit + 1).ToArray();
            return ValueTask.FromResult(new TaskPage<AgentMessage>(items.Take(limit).ToArray(), items.Length > limit ? items[limit - 1].MessageId : null, items.Length > limit, state.Tenant.Revision));
        }
    }
}
