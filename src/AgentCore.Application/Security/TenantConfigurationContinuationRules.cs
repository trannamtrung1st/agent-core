using System.Text.Json;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

// The existing bounded configuration-continuation discriminant, with actual tenant receipt and causal proof.
public static class TenantConfigurationContinuationRules
{
    public static long? InputBoundary(AgentRun run) => Target(run) is not null && run.Admission.Activation.EvidenceJson is { } evidence
        ? JsonSerializer.Deserialize<AgentRunAdmissionFactory.SignalInput>(evidence)?.InputBoundarySequence : null;
    public static void RequireDirectWrite(AgentRun run)
    {
        if (Target(run) is not null) throw new AccessDeniedException("configuration_task_review_required");
    }
    public static bool HasRemedyAttempt(AgentRun run) => run.Owner.TenantId is not null
        && AgentRunToolCallCheckpoint.TryRead(run.Checkpoint, out var messages)
        && messages!.Any(m => m.Role == ModelRole.Tool && m.Name == "configuration.propose");
    public static string? Target(AgentRun run)
    {
        if (run.Admission.Activation.Kind is not (ActivationKind.ConfigurationContinuation or ActivationKind.ApprovalContinuation)
            || run.Admission.Activation.EvidenceJson is not { } evidence) return null;
        using var body = JsonDocument.Parse(evidence);
        return body.RootElement.TryGetProperty("EnvironmentKind", out var kind) && kind.GetString() == "configuration"
            && body.RootElement.TryGetProperty("ContinuationTarget", out var target) ? target.GetString() : null;
    }
    public static IReadOnlyList<Guid> RequestIds(AgentRun source)
    {
        if (!AgentRunToolCallCheckpoint.TryRead(source.Checkpoint, out var messages)) return [];
        var result = new HashSet<Guid>();
        foreach (var message in messages!.Where(m => m.Role == ModelRole.Tool && m.Name == ToolCatalog.RequestAuthorization))
        {
            using var body = JsonDocument.Parse(message.Text);
            if (body.RootElement.ValueKind == JsonValueKind.Object && body.RootElement.TryGetProperty("requestId", out var value)
                && value.ValueKind == JsonValueKind.String && value.TryGetGuid(out var id)) result.Add(id);
        }
        return result.ToArray();
    }
    public static (ConfigurationPendingTask Task, TenantApprovalRequest Request)? Pending(AgentRun source,
        Func<Guid, TenantApprovalRequest?> request, Func<Guid, TenantOperationReceipt?> receipt)
    {
        if (source.Status != AgentRunStatus.Completed || source.CancellationRequested || source.Owner.TenantId is null
            || source.Admission.OutputContract != AgentRunOutputContract.ConversationResponse
            || source.Admission.ExecutionContext is not { OriginalInitiatorPrincipalId: not null, Origin: ExecutionOrigin.UserMessage or ExecutionOrigin.ApprovalContinuation }
            || Target(source) is not null || source.SideEffect.Disposition is AgentRunSideEffectDisposition.InFlight or AgentRunSideEffectDisposition.Indeterminate
            || !AgentRunToolCallCheckpoint.TryReadState(source.Checkpoint, out var messages, out var uncertain, out var blocked)
            || uncertain || blocked is not null || messages is null || AgentRunToolCallCheckpoint.PendingCalls(messages).Count != 0) return null;
        (ConfigurationPendingTask, TenantApprovalRequest)? candidate = null;
        IReadOnlyList<ModelToolCall> batch = [];
        foreach (var message in messages)
        {
            if (message.Role == ModelRole.Assistant) batch = message.ToolCalls ?? [];
            if (message.Role != ModelRole.Tool) continue;
            var call = batch.SingleOrDefault(c => c.Id == message.ToolCallId && c.Name == message.Name);
            if (call is null || !ToolRegistry.TryGet(call.Name, out var descriptor)) return null;
            if (call.Name == "configuration.propose" || descriptor.Effect == ToolEffect.ReadOnly && call.Name != ToolCatalog.RequestAuthorization) continue;
            if (call.Name != ToolCatalog.RequestAuthorization) return null;
            using var body = JsonDocument.Parse(message.Text); var data = body.RootElement;
            if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("requestId", out var requested)
                || requested.ValueKind != JsonValueKind.String || !requested.TryGetGuid(out var id)
                || request(id) is not { Status: TenantApprovalStatus.Approved } approved || approved.TenantId != source.Owner.TenantId
                || approved.SessionId != source.SessionId || approved.ExecutorPrincipalId != source.Owner.ExecutorPrincipalId
                || approved.Operation.InstanceId != source.AgentInstanceId || approved.OriginalInitiatorPrincipalId != source.Admission.ExecutionContext.OriginalInitiatorPrincipalId
                || approved.RootActivationId != source.Admission.Activation.RootActivationId || !TenantApprovalToolContract.Parse(call).Matches(approved.Operation)
                || receipt(approved.Operation.OperationId) is not { Outcome: TenantOperationOutcome.Completed } effect || effect.RequestId != id
                || effect.ActionHash != approved.Operation.ActionHash || effect.TenantId != approved.TenantId || effect.SessionId != source.SessionId
                || effect.RootActivationId != approved.RootActivationId || effect.ExecutorPrincipalId != approved.ExecutorPrincipalId || effect.InstanceId != source.AgentInstanceId) return null;
            if (approved.Operation.ManagementMutation?.Remedy is { } remedy)
            {
                if (candidate is not null || effect.RunId != source.AgentRunId || effect.ReasonCode != TenantManagementMutationRules.Completed
                    || effect.InstanceRevision <= source.Admission.Configuration!.InstanceRevision) return null;
                var args = TenantManagementMutationContract.Parse("configuration.propose", remedy.ArgumentsJson).Value;
                var target = args.GetProperty("target").GetString()!;
                if (!ToolRegistry.TryGet(target, out var task) || !task.DefinitionAuthorizable || ManagementTools.IsManagement(target)) return null;
                candidate = (new(source.AgentRunId, target, args.GetProperty("goal").GetString()!, effect.InstanceRevision), approved);
            }
            else if (approved.Operation.Kind is not (TenantOperationKind.ManagementRead or TenantOperationKind.WorkspaceRead or TenantOperationKind.AttachmentRead
                or TenantOperationKind.CredentialRead or TenantOperationKind.ArtifactVerification or TenantOperationKind.SandboxRead or TenantOperationKind.AutomationRead
                or TenantOperationKind.ContinuityRead or TenantOperationKind.ReportSourceRead)
                && approved.Operation.BrowserCommand?.ReadOnly != true) return null;
        }
        return candidate;
    }
    public static bool SafeSession(AgentRun source, AgentRun root, SessionSnapshot snapshot, IEnumerable<AgentRun> runs, IEnumerable<ConversationEntry>? authoritativeEntries = null)
    {
        if (snapshot.SessionId != source.SessionId || snapshot.AgentInstanceId != source.AgentInstanceId || snapshot.ProfileId is not null
            || source.Admission.Configuration?.SpaceContext is not { } sourceContext || !sourceContext.MatchesSelection(snapshot.ContextSelection)
            || snapshot.ArchivedAt is not null || snapshot.DurablyDeletedAt is not null || snapshot.LifecycleStatus != SessionLifecycleStatus.Active
            || snapshot.Status is not (SessionStatus.Created or SessionStatus.Attached) || !snapshot.Surfaces.HasFlag(SessionSurface.ChatList)
            || root.Admission.ExecutionContext?.Origin != ExecutionOrigin.UserMessage || root.SessionId != source.SessionId) return false;
        var history = (authoritativeEntries ?? snapshot.Entries).ToArray();
        var entries = history.Where(e => root.Admission.Activation.SourceEntryIds.Contains(e.EntryId) && e.Role == ConversationRole.User).ToArray();
        return entries.Length == root.Admission.Activation.SourceEntryIds.Count && entries.Length > 0
            && !history.Any(e => e.Role == ConversationRole.User && e.Sequence > entries.Max(i => i.Sequence))
            && !runs.Any(r => r.AgentRunId != source.AgentRunId && (!r.IsTerminal || r.CreatedAtUtc > source.CreatedAtUtc
                || r.Admission.Activation.Kind == ActivationKind.ConfigurationContinuation && r.Admission.Activation.RootActivationId == source.Admission.Activation.RootActivationId));
    }
    public static void RequireAdmission(AgentRun source, AgentRun root, SessionSnapshot snapshot, IEnumerable<AgentRun> runs,
        TenantConfigurationContinuationAdmission admission, TenantAgent current, long securityRevision,
        Func<Guid, TenantApprovalRequest?> requests, Func<Guid, TenantOperationReceipt?> receipts, IEnumerable<ConversationEntry>? authoritativeEntries = null)
    {
        var pending = Pending(source, requests, receipts) ?? throw new SecurityConflictException("configuration_continuation_unavailable");
        var next = admission.Run; var context = next.Admission.ExecutionContext;
        if (!SafeSession(source, root, snapshot, runs, authoritativeEntries) || securityRevision != admission.ExpectedSecurityRevision
            || current.Instance.Revision != pending.Task.InstanceRevision || current.Instance.Lifecycle != AgentCore.Domain.Definitions.AgentInstanceLifecycle.Active
            || pending.Request.RequestId != admission.RequestId || pending.Request.Operation.OperationId != admission.OperationId
            || next.Owner != source.Owner || next.SessionId != source.SessionId || next.Status != AgentRunStatus.Queued || next.MaxAttempts != 1
            || next.Admission.Configuration?.InstanceRevision != pending.Task.InstanceRevision
            || next.Admission.Configuration.SpaceContext is not { } nextContext || !nextContext.SameLocation(source.Admission.Configuration?.SpaceContext)
            || !nextContext.MatchesSelection(snapshot.ContextSelection)
            || next.DefinitionId != current.Instance.DefinitionId || next.DefinitionVersion != current.Instance.ActiveVersion
            || next.PinnedPersona != current.Instance.Persona || next.Admission.Configuration.PersonaRevision != current.Instance.PersonaRevision || Target(next) != pending.Task.Target
            || InputBoundary(next) != snapshot.DurableLastEntrySequence
            || next.Admission.Activation.Kind != ActivationKind.ConfigurationContinuation || next.Admission.Activation.SourceAgentRunId != source.AgentRunId
            || next.Admission.Activation.SourceSessionId != source.SessionId || next.Admission.Activation.SourceEventId != source.ActivationId
            || next.Admission.Activation.DedupeKey != "configuration-continuation:" + source.AgentRunId.ToString("D") || next.Admission.Activation.SourceEntryIds.Count != 0
            || context is null || context.Origin != ExecutionOrigin.ConfigurationContinuation || context.ParentRunId != source.AgentRunId
            || context.ParentActivationId != source.ActivationId || context.RootActivationId != source.Admission.Activation.RootActivationId
            || context.OriginalInitiatorPrincipalId != source.Admission.ExecutionContext!.OriginalInitiatorPrincipalId)
            throw new SecurityConflictException("configuration_continuation_unavailable");
    }
}
