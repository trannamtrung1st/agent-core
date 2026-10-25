using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Spaces;

namespace AgentCore.Application.Security;

public sealed record ContextPendingTask(Guid SourceRunId, Guid TransitionId, long SelectionRevision, Guid SpaceId, Guid? EnvironmentId);

// The source's completed checkpoint and the canonical saved selection jointly prove intent.
// A tool result alone cannot select a context or admit a successor.
public static class TenantContextContinuationRules
{
    public static long? InputBoundary(AgentRun run) => run.Admission.Activation.Kind is ActivationKind.ContextContinuation or ActivationKind.ApprovalContinuation
        && run.Admission.Activation.EvidenceJson is { } evidence && HasContextBoundary(evidence) ? JsonSerializer.Deserialize<AgentRunAdmissionFactory.SignalInput>(evidence)?.InputBoundarySequence : null;
    private static bool HasContextBoundary(string evidence) { using var json = JsonDocument.Parse(evidence); return json.RootElement.TryGetProperty("EnvironmentKind", out var kind) && kind.GetString() == "context-transition" && json.RootElement.TryGetProperty("ContextTransition", out _); }
    public static ContextPendingTask? Pending(AgentRun source)
    {
        if (source.Status != AgentRunStatus.Completed || source.CancellationRequested || source.Result?.AttentionRequired != false
            || source.Admission.OutputContract != AgentRunOutputContract.ConversationResponse
            || source.Admission.ExecutionContext is not { OriginalInitiatorPrincipalId: not null, Origin: ExecutionOrigin.UserMessage or ExecutionOrigin.ApprovalContinuation }
            || TenantConfigurationContinuationRules.Target(source) is not null
            || source.SideEffect.Disposition is AgentRunSideEffectDisposition.InFlight or AgentRunSideEffectDisposition.Indeterminate
            || !AgentRunToolCallCheckpoint.TryReadState(source.Checkpoint, out var messages, out var uncertain, out var blocked)
            || uncertain || blocked is not null || messages is null || AgentRunToolCallCheckpoint.PendingCalls(messages).Count != 0) return null;
        ContextPendingTask? found = null; IReadOnlyList<ModelToolCall> calls = [];
        foreach (var message in messages)
        {
            if (message.Role == ModelRole.Assistant) calls = message.ToolCalls ?? [];
            if (message.Role != ModelRole.Tool) continue;
            var call = calls.SingleOrDefault(c => c.Id == message.ToolCallId && c.Name == message.Name);
            if (call is null || !ToolRegistry.TryGet(call.Name, out var descriptor)) return null;
            if (found is not null && descriptor.Effect != ToolEffect.ReadOnly) return null;
            if (!TenantContextToolContract.IsSwitch(call.Name)) continue;
            using var args = JsonDocument.Parse(call.ArgumentsJson); using var json = JsonDocument.Parse(message.Text);
            if (!args.RootElement.TryGetProperty("continueTask", out var wants) || wants.ValueKind != JsonValueKind.True
                || !json.RootElement.TryGetProperty("contextTransition", out var marker) || marker.GetProperty("continueTask").ValueKind != JsonValueKind.True) continue;
            var transition = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes($"AgentCore.ContextSwitch\0{source.AgentRunId:D}\0{call.Id}\0{call.Name}\0{call.ArgumentsJson}")).AsSpan(0, 16));
            if (found is not null || marker.GetProperty("sourceRunId").GetGuid() != source.AgentRunId || marker.GetProperty("transitionId").GetGuid() != transition) return null;
            found = new(source.AgentRunId, transition, marker.GetProperty("selectionRevision").GetInt64(), marker.GetProperty("spaceId").GetGuid(),
                marker.GetProperty("environmentId").ValueKind == JsonValueKind.Null ? null : marker.GetProperty("environmentId").GetGuid());
        }
        return found;
    }
    public static bool SafeSession(AgentRun source, AgentRun root, SessionSnapshot snapshot, IEnumerable<AgentRun> runs, IEnumerable<ConversationEntry> entries)
    {
        var pending = Pending(source); var selection = snapshot.ContextSelection;
        if (pending is null || selection is null || selection.TransitionId != pending.TransitionId || selection.EffectiveAfterRunId != source.AgentRunId
            || selection.Revision != pending.SelectionRevision || selection.SpaceId != pending.SpaceId || selection.EnvironmentId != pending.EnvironmentId
            || source.Admission.Configuration?.SpaceContext is not { } original || selection.Revision != original.SelectionRevision + 1
            || snapshot.SessionId != source.SessionId || snapshot.AgentInstanceId != source.AgentInstanceId || snapshot.ProfileId is not null
            || snapshot.ArchivedAt is not null || snapshot.DurablyDeletedAt is not null || snapshot.LifecycleStatus != SessionLifecycleStatus.Active
            || snapshot.Status is not (SessionStatus.Created or SessionStatus.Attached) || !snapshot.Surfaces.HasFlag(SessionSurface.ChatList)
            || root.Admission.ExecutionContext?.Origin != ExecutionOrigin.UserMessage || root.SessionId != source.SessionId) return false;
        var history = entries.ToArray(); var inputs = history.Where(e => root.Admission.Activation.SourceEntryIds.Contains(e.EntryId) && e.Role == ConversationRole.User).ToArray();
        return inputs.Length > 0 && inputs.Length == root.Admission.Activation.SourceEntryIds.Count
            && !history.Any(e => e.Role == ConversationRole.User && e.Sequence > inputs.Max(i => i.Sequence))
            && !runs.Any(r => r.AgentRunId != source.AgentRunId && (!r.IsTerminal || r.CreatedAtUtc > source.CreatedAtUtc
                || r.Admission.Activation.Kind == ActivationKind.ContextContinuation && r.Admission.Activation.RootActivationId == source.Admission.Activation.RootActivationId));
    }
    public static void RequireAdmission(AgentRun source, AgentRun root, SessionSnapshot snapshot, IEnumerable<AgentRun> runs,
        TenantContextContinuationAdmission admission, TenantAgent current, long securityRevision, IEnumerable<ConversationEntry> entries)
    {
        var pending = Pending(source) ?? throw new SecurityConflictException("context_continuation_unavailable");
        var next = admission.Run; var activation = next.Admission.Activation; var context = next.Admission.ExecutionContext;
        if (!SafeSession(source, root, snapshot, runs, entries) || admission.TransitionId != pending.TransitionId || securityRevision != admission.ExpectedSecurityRevision
            || current.Instance.Lifecycle != AgentCore.Domain.Definitions.AgentInstanceLifecycle.Active || current.PrincipalId != source.Owner.ExecutorPrincipalId
            || next.Owner != source.Owner || next.SessionId != source.SessionId || next.Status != AgentRunStatus.Queued || next.MaxAttempts != 1
            || next.Admission.Configuration?.InstanceRevision != current.Instance.Revision || next.DefinitionId != current.Instance.DefinitionId || next.DefinitionVersion != current.Instance.ActiveVersion
            || next.PinnedPersona != current.Instance.Persona || next.Admission.Configuration.PersonaRevision != current.Instance.PersonaRevision
            || next.Admission.Configuration.SpaceContext is not { } captured || !captured.MatchesSelection(snapshot.ContextSelection)
            || activation.Kind != ActivationKind.ContextContinuation || activation.SourceAgentRunId != source.AgentRunId || activation.SourceSessionId != source.SessionId
            || activation.SourceEventId != source.ActivationId || activation.DedupeKey != "context-continuation:" + source.Admission.Activation.RootActivationId.ToString("D") || activation.SourceEntryIds.Count != 0
            || InputBoundary(next) != snapshot.DurableLastEntrySequence || context is null || context.Origin != ExecutionOrigin.ContextContinuation
            || context.ParentRunId != source.AgentRunId || context.ParentActivationId != source.ActivationId || context.RootActivationId != source.Admission.Activation.RootActivationId
            || context.OriginalInitiatorPrincipalId != source.Admission.ExecutionContext!.OriginalInitiatorPrincipalId)
            throw new SecurityConflictException("context_continuation_unavailable");
        using var evidence = JsonDocument.Parse(activation.EvidenceJson!);
        if (evidence.RootElement.GetProperty("ContextTransition").Deserialize<ContextPendingTask>() != pending) throw new SecurityConflictException("context_continuation_unavailable");
    }
}
