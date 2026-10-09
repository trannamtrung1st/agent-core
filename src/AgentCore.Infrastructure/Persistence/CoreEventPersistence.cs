using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Events;
using AgentCore.Domain.Triggers;

namespace AgentCore.Infrastructure.Persistence;

public sealed class CoreEventRecord
{
    public string EventId { get; set; } = "";
    public string DedupeKey { get; set; } = "";
    public string AgentInstanceId { get; set; } = "";
    public string ProfileId { get; set; } = "";
    public long ReceivedAtUtc { get; set; }
    public string PayloadJson { get; set; } = "";
    public bool Snapshotted { get; set; }
}
public sealed class CoreEventDeliveryRecord
{
    public string EventId { get; set; } = "";
    public string AutomationId { get; set; } = "";
    public string AgentInstanceId { get; set; } = "";
    public string ProfileId { get; set; } = "";
    public string SnapshotJson { get; set; } = "";
    public int Status { get; set; }
    public string? DecisionJson { get; set; }
    public string? Code { get; set; }
}
public sealed class CoreEventBucketRecord
{
    public string CoverageJson { get; set; } = "[]";
    public string BucketId { get; set; } = "";
    public string AutomationId { get; set; } = "";
    public long TriggerRevision { get; set; }
    public long DueAtUtc { get; set; }
    public string PayloadJson { get; set; } = "";
    public bool Flushed { get; set; }
}
internal static class CoreEventPersistence
{
    internal static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    internal static Guid Id(string key) => new(SHA256.HashData(Encoding.UTF8.GetBytes(key)).AsSpan(0, 16));
    internal static CoreEventOccurrence? Run(AgentRun previous, AgentRun current)
    {
        if (previous.IsTerminal || current.Status is not (AgentRunStatus.Completed or AgentRunStatus.Failed)) return null;
        var key = current.Status == AgentRunStatus.Completed ? "run.completed" : "run.failed";
        var dedupe = $"{key}:{current.AgentRunId:D}";
        Guid? root = null; var depth = 0; Guid[] visited = [];
        if (Causation(current) is { } cause)
        { root = cause.Root; depth = cause.Depth; visited = cause.Visited; }
        return new(Id(dedupe), dedupe, new(current.AgentInstanceId, current.ProfileId), key, current.UpdatedAtUtc,
            JsonSerializer.Serialize(new { agentRunId = current.AgentRunId, sessionId = current.SessionId,
                activationKind = current.Admission.Activation.Kind.ToString(), outcomeKind = current.Result?.OutcomeKind.ToString(), failureCode = current.Failure?.Code }), root ?? current.AgentRunId, depth, visited);
    }
    internal static CoreEventOccurrence? Session(SessionSnapshot? previous, SessionSnapshot current, AgentRun? originRun = null)
    {
        if (current.AgentInstanceId == Guid.Empty || current.ProfileId is not { } profile
            || current.LifecycleStatus == previous?.LifecycleStatus || current.LifecycleStatus is not (SessionLifecycleStatus.Completed or SessionLifecycleStatus.Ended)) return null;
        var key = current.LifecycleStatus == SessionLifecycleStatus.Completed ? "session.completed" : "session.ended";
        var dedupe = $"{key}:{current.SessionId:D}";
        var signal = new CoreEventOccurrence(Id(dedupe), dedupe, new(current.AgentInstanceId, profile), key, current.UpdatedAt,
            JsonSerializer.Serialize(new { sessionId = current.SessionId, previousLifecycle = previous?.LifecycleStatus.ToString(), lifecycle = current.LifecycleStatus.ToString() }), current.Origin.OriginatingAgentRunId ?? current.Origin.InitialBackgroundAgentRunId, current.Origin.AutomationId is null ? 0 : 1, current.Origin.AutomationId is { } automation ? [automation] : []);
        if (originRun is not null && Causation(originRun) is { } cause)
            signal = signal with { RootAgentRunId = cause.Root ?? originRun.AgentRunId, TriggerDepth = cause.Depth, VisitedAutomationIds = cause.Visited };
        return signal;
    }
    // Only structured evidence on a durable Automation activation carries trusted causation.
    // Legacy/native signals may be plain text; projection must never break their terminal commit.
    private static (Guid? Root, int Depth, Guid[] Visited)? Causation(AgentRun run)
    {
        var activation = run.Admission.Activation;
        if (activation.Kind is not (ActivationKind.CoreEvent or ActivationKind.ScheduledWork or ActivationKind.ApplicationEvent or ActivationKind.ManualBackground)
            || activation.TriggerOccurrenceId is null || activation.EvidenceJson is not { } evidence) return null;
        try
        {
            using var outer = JsonDocument.Parse(evidence);
            if (outer.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (outer.RootElement.TryGetProperty("Text", out var text) && text.ValueKind == JsonValueKind.String)
                evidence = text.GetString()!;
            using var doc = JsonDocument.Parse(evidence);
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("triggerContext", out var context)
                || context.ValueKind != JsonValueKind.Object || !context.TryGetProperty("causation", out var cause)
                || cause.ValueKind != JsonValueKind.Object || !cause.TryGetProperty("triggerDepth", out var d)
                || !d.TryGetInt32(out var depth) || depth is < 0 or > 4) return null;
            Guid? root = cause.TryGetProperty("rootAgentRunId", out var r) && r.ValueKind == JsonValueKind.String && r.TryGetGuid(out var g) ? g : null;
            var visited = cause.TryGetProperty("visitedAutomationIds", out var v) && v.ValueKind == JsonValueKind.Array
                ? v.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.String && i.TryGetGuid(out _)).Select(i => i.GetGuid()).Distinct().ToArray() : [];
            return (root, depth, visited);
        }
        catch (JsonException) { return null; }
    }
    internal static IReadOnlyList<CoreEventOccurrence> Instance(AgentInstance previous, AgentInstance current)
    {
        var result = new List<CoreEventOccurrence>();
        if (previous.ActiveVersion != current.ActiveVersion)
        {
            var key = $"harness.definition_adopted:{current.InstanceId:D}:{current.Revision}";
            result.Add(new(Id(key), key, new(current.InstanceId, LocalUserProfile.Id), "harness.definition_adopted", current.UpdatedAt,
                JsonSerializer.Serialize(new { agentInstanceId = current.InstanceId, definitionId = current.DefinitionId, previousVersion = previous.ActiveVersion, activeVersion = current.ActiveVersion })));
        }
        var changed = new List<string>();
        if (previous.Persona != current.Persona) changed.Add("persona");
        if (previous.UnattendedModelCatalogKey != current.UnattendedModelCatalogKey || previous.UnattendedReasoningEffort != current.UnattendedReasoningEffort) changed.Add("unattendedModel");
        if (previous.ExecutionBudgets != current.ExecutionBudgets) changed.Add("executionBudgets");
        if (JsonSerializer.Serialize(previous.HarnessManagement?.Policy) != JsonSerializer.Serialize(current.HarnessManagement?.Policy)) changed.Add("harnessPolicy");
        if (changed.Count > 0)
        {
            var key = $"instance.config_changed:{current.InstanceId:D}:{current.Revision}";
            result.Add(new(Id(key), key, new(current.InstanceId, LocalUserProfile.Id), "instance.config_changed", current.UpdatedAt,
                JsonSerializer.Serialize(new { agentInstanceId = current.InstanceId, revision = current.Revision, changedSections = changed, actor = "guardedWrite" })));
        }
        return result;
    }
    internal static void Stage(AgentCoreDbContext db, CoreEventOccurrence? e)
    {
        if (e is null) return;
        db.CoreEvents.Add(new() { EventId = e.EventId.ToString("D"), DedupeKey = e.DedupeKey,
            AgentInstanceId = e.Owner.AgentInstanceId.ToString("D"), ProfileId = e.Owner.ProfileId.ToString("D"),
            ReceivedAtUtc = e.OccurredAtUtc.ToUnixTimeMilliseconds(), PayloadJson = JsonSerializer.Serialize(e, Json) });
    }
}
