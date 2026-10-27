using System.Text.Json;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;
using AgentCore.Domain.Tasks;

namespace AgentCore.Application.Tasks;

public static class AgentMessageWakeRules
{
    public static async ValueTask AuthorizeAsync(AgentMessageWakeSource source, TenantAgent sender, TenantAgent recipient,
        TenantSecurityState state, AgentDefinition senderCurrent, IamAuthorizer iam, CancellationToken ct, bool pending = true)
    {
        var message = source.Message;
        if (!AgentCore.Application.Agents.RolePermissions.AllowsTool(source.SourceRun.Admission.Configuration!.Definition, "agent-messages.send")
            || !AgentCore.Application.Agents.RolePermissions.AllowsTool(senderCurrent, "agent-messages.send"))
            throw new AccessDeniedException("message_source_capability_unavailable");
        if (source.Thread.TaskId is not null || message.ThreadId != source.Thread.ThreadId || source.Receipt.MessageId != message.MessageId
            || source.Receipt.State != (pending ? AgentMessageDelivery.NotificationPending : AgentMessageDelivery.ActivationAdmitted) || source.SourceRun.AgentRunId != message.SourceRunId
            || source.SourceRun.SessionId != message.SourceSessionId || source.SourceRun.Owner.ExecutorPrincipalId != message.SenderPrincipalId
            || sender.Instance.InstanceId != message.SenderInstanceId || recipient.Instance.InstanceId != message.RecipientInstanceId
            || recipient.Instance.Cooperation?.AllowDirectMessageWake != true) throw new AccessDeniedException("message_wake_unavailable");
        await AgentMessageRules.DirectPolicyAsync(state, sender, recipient, iam, ct);
        foreach (var key in new[] { "sessions.read", "agent-messages.send" })
            await TaskAuthority.RequireAsync(state, sender.PrincipalId, key, TenantSessionRules.Scope(message.TenantId, source.SourceRun.SessionId), iam, ct);
        foreach (var key in new[] { "sessions.create", "agent-runs.create" })
        {
            await TaskAuthority.RequireAsync(state, recipient.PrincipalId, key, AccessScope.ForTenant(message.TenantId), iam, ct);
            if (await TenantSandboxReadOperationRules.NeedsReviewAsync(state, source.SourceRun.Admission.ExecutionContext!.OriginalInitiatorPrincipalId,
                key, AccessScope.ForTenant(message.TenantId), iam, ct)) throw new AccessDeniedException("message_wake_review_required");
        }
        var hops = (source.WakeAncestor?.WakeHops ?? source.SourceRun.Admission.AgentMessage?.WakeHops ?? 0) + 1;
        if (pending && hops > 2) throw new AccessDeniedException("message_wake_hop_limit");
    }
    public static (SessionSnapshot Session, AgentRun Run) Create(AgentMessageWakeSource source, TenantAgent recipient,
        ResolvedAgentRunConfiguration resolved, AgentRunModelPin model, Guid sessionId, Guid entryId, Guid activationId, Guid runId, Guid responseId, DateTimeOffset at)
    {
        var message = source.Message; var definition = resolved.Configuration.Definition;
        if (recipient.Instance.InstanceId != message.RecipientInstanceId || recipient.TenantId != message.TenantId
            || resolved.Configuration.InstanceRevision != recipient.Instance.Revision || resolved.Configuration.SpaceContext is not { } space)
            throw new AccessDeniedException("message_wake_assignment_changed");
        var work = new AgentMessageWork(message.MessageId, message.ThreadId, source.WakeAncestor?.WakeRootActivationId ?? source.SourceRun.Admission.AgentMessage?.WakeRootActivationId ?? activationId,
            (source.WakeAncestor?.WakeHops ?? source.SourceRun.Admission.AgentMessage?.WakeHops ?? 0) + 1);
        var entry = new ConversationEntry(entryId, 1, message.MessageId, ConversationRole.User, message.Text, null, EntryStatus.Completed, SessionMode.Text, 0, message.Text.Length, at);
        var activation = new Activation(activationId, sessionId, ActivationKind.AgentMessage, [entryId], message.MessageId, null, null, null,
            $"agent-message:{message.MessageId:D}", at, activationId, null,
            JsonSerializer.Serialize(new { messageId = message.MessageId, threadId = message.ThreadId }));
        var context = new TrustedExecutionContext(message.TenantId, recipient.PrincipalId, source.SourceRun.Admission.ExecutionContext!.OriginalInitiatorPrincipalId,
            sessionId, runId, activationId, activationId, null, null, ExecutionOrigin.AgentMessage);
        var run = AgentRun.Create(runId, AgentRunOwner.ForTenant(message.TenantId, recipient.Instance.InstanceId, recipient.PrincipalId),
            new(activation, definition.Id, definition.Version, resolved.Persona, responseId, AgentRunOutputContract.BackgroundOutcome,
                ExecutionBudgetResolver.Resolve(definition, resolved.InstanceBudgets, false), resolved.Configuration,
                taskIntent: new(message.Text, AgentRunTaskSource.AgentMessage, message.MessageId.ToString("D")), executionContext: context, agentMessage: work), model,
            AgentRunLimits.DefaultMaxAttempts, at, resolved.Skills, resolved.Skills.Where(s => s.Projection == SkillProjection.Always).Select(s => s.Key).ToArray());
        var snapshot = new SessionSnapshot(1, sessionId, 1, definition, SessionMode.Text, null, SessionStatus.Created, [entry], "", 0, null, null,
            at, at, recipient.Instance.InstanceId, Title: "Agent message", LastEntrySequence: 1, PinnedPersona: resolved.Persona,
            PinnedPersonaRevision: resolved.Configuration.PersonaRevision, ModelSelection: new(model.OfferingId, model.ConnectionId, model.ExternalModelId,
                ModelSelectionSource.Host, model.ReasoningEffort, model.Binding, model.Decision),
            Origin: new(SessionOriginKind.DirectAgentMessage, originatingSessionId: message.SourceSessionId, initialBackgroundAgentRunId: runId, initialTitle: "Agent message"),
            Surfaces: SessionSurface.BackgroundWork, ContextSelection: AgentCore.Domain.Spaces.SessionContextSelection.FromCapture(space, at), ModelPreference: recipient.Instance.UnattendedModel);
        return (snapshot, run);
    }
    public static void Shape(AgentMessageWakeAdmission admission, TenantAgent recipient, DateTimeOffset at)
    {
        var run = admission.Run;
        if (!run.IsInitialQueued || run.CreatedAtUtc > at || run.CreatedAtUtc != admission.Session.CreatedAt || run.Admission.Configuration is null)
            throw new AccessDeniedException("message_wake_mismatch");
        var expected = Create(admission.Source, recipient, new(run.Admission.Configuration, recipient.Instance.Persona, run.PinnedSkillCatalog, recipient.Instance.ExecutionBudgets),
            run.PinnedModel, run.SessionId, admission.Session.Entries.Single().EntryId, run.ActivationId, run.AgentRunId, run.ResponseId!.Value, run.CreatedAtUtc);
        if (JsonSerializer.Serialize(expected.Run.Admission) != JsonSerializer.Serialize(run.Admission) || run.Owner != expected.Run.Owner
            || JsonSerializer.Serialize(expected.Session) != JsonSerializer.Serialize(admission.Session)) throw new AccessDeniedException("message_wake_mismatch");
    }
}
