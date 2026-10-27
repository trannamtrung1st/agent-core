using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Domain.Tasks;

namespace AgentCore.Application.Tasks;

public static class AgentMessageRules
{
    public static string Fingerprint(AgentMessageCommand command) => TaskLimits.Hash(new { command.TaskId, command.ThreadId,
        command.RecipientInstanceId, command.Kind, Text = TaskLimits.Text(command.Text, TaskLimits.MessageCharacters, "Message") });
    public static string SourceScope(AgentMessageCommand command) => command.Actor.Agent is { } frame ? $"run:{frame.RunId:D}"
        : command.TaskId is { } task ? $"human:{command.Actor.PrincipalId:D}:task:{task:D}" : throw new AccessDeniedException("agent_sender_required");
    public static async ValueTask DirectPolicyAsync(TenantSecurityState state, TenantAgent sender, TenantAgent recipient, IamAuthorizer iam, CancellationToken ct)
    {
        if (sender.TenantId != state.Tenant.TenantId || recipient.TenantId != state.Tenant.TenantId || sender.Instance.InstanceId == recipient.Instance.InstanceId
            || sender.Instance.Lifecycle != AgentCore.Domain.Definitions.AgentInstanceLifecycle.Active || recipient.Instance.Lifecycle != AgentCore.Domain.Definitions.AgentInstanceLifecycle.Active
            || sender.Instance.Cooperation?.AcceptDirectMessages != true || recipient.Instance.Cooperation?.AcceptDirectMessages != true)
            throw new AccessDeniedException("agent_message_unavailable");
        TenantSessionRules.RequireExecutor(state, sender); TenantSessionRules.RequireExecutor(state, recipient);
        await TaskAuthority.RequireAsync(state, sender.PrincipalId, "agents.read", AccessScope.ForResource(state.Tenant.TenantId, "agent", recipient.Instance.InstanceId.ToString("D")), iam, ct);
        await TaskAuthority.RequireAsync(state, recipient.PrincipalId, "agent-messages.read", AccessScope.ForResource(state.Tenant.TenantId, "agent", recipient.Instance.InstanceId.ToString("D")), iam, ct);
    }
    public static async ValueTask<bool> CanReadThreadAsync(TenantSecurityState state, TaskActor actor, AgentThread thread, IamAuthorizer iam, CancellationToken ct, AgentRun? reader = null)
    {
        actor.Validate();
        if (thread.TenantId != state.Tenant.TenantId || actor.Agent is { } frame && (frame.Owner.TenantId != thread.TenantId || !thread.Includes(frame.Owner.AgentInstanceId))) return false;
        var scope = thread.TaskId is { } task ? TaskAuthority.Scope(thread.TenantId, task)
            : AccessScope.ForResource(thread.TenantId, "agent-thread", thread.ThreadId.ToString("D"));
        if (!(await iam.CheckOwnedTenantAsync(state, actor.PrincipalId, PermissionCatalog.Get("agent-messages.read"), scope, ct)).Allowed) return false;
        if (actor.Agent is { } actual)
        {
            if (reader?.AgentRunId != actual.RunId || reader.Owner != actual.Owner || reader.SessionId != actual.SessionId) return false;
            if (await TenantSandboxReadOperationRules.NeedsReviewAsync(state, reader.Admission.ExecutionContext?.OriginalInitiatorPrincipalId,
                "agent-messages.read", scope, iam, ct)) return false;
        }
        foreach (var id in new[] { thread.FirstInstanceId, thread.SecondInstanceId }.Distinct())
        {
            if (!state.SystemIdentities.Any(i => i.AgentInstanceId == id && i.TenantId == thread.TenantId && i.Status == IdentityStatus.Active)) return false;
            if (!(await iam.CheckOwnedTenantAsync(state, actor.PrincipalId, PermissionCatalog.Get("agents.read"), AccessScope.ForResource(thread.TenantId, "agent", id.ToString("D")), ct)).Allowed) return false;
        }
        return true;
    }
    public static AgentMessage Create(AgentMessageCommand command, AgentThread thread, TenantSession source, AgentRun? run, long sequence, DateTimeOffset at)
    {
        var human = command.Actor.Human is not null;
        if (command.TenantId != thread.TenantId || human && (thread.TaskId is null || command.Kind != AgentMessageKind.HumanGuidance)
            || !human && (run is null || run.SessionId != source.Owner.SessionId || run.Owner.ExecutorPrincipalId != command.Actor.PrincipalId
                || command.Kind == AgentMessageKind.HumanGuidance)) throw new AccessDeniedException("agent_sender_required");
        Guid recipient;
        try { recipient = human ? thread.SecondInstanceId : thread.Recipient(source.Owner.AgentInstanceId, source.Owner.SessionId); }
        catch (InvalidOperationException) { throw new AccessDeniedException("agent_sender_required"); }
        return new(thread.TenantId, thread.ThreadId, command.MessageId, sequence, human ? TaskActorKind.Human : TaskActorKind.Agent,
            command.Actor.PrincipalId, human ? null : source.Owner.AgentInstanceId, source.Owner.SessionId, run?.AgentRunId, recipient,
            command.Kind, command.Text, command.RequestKey, Fingerprint(command), at);
    }
}
