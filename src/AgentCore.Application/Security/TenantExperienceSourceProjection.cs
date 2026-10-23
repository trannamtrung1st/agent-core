using System.Text;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Agents;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Experience;

namespace AgentCore.Application.Security;

public static class TenantExperienceSourceProjection
{
    public static long StableSessionCheckpoint(IReadOnlyList<ConversationEntry> entries)
    {
        var firstStreaming = entries.FirstOrDefault(e => e.Status == EntryStatus.Streaming)?.Sequence ?? long.MaxValue;
        return entries.Where(e => e.Role == ConversationRole.Assistant && e.Status != EntryStatus.Streaming && e.Sequence < firstStreaming
            && (!string.IsNullOrWhiteSpace(AssistantSemanticProjection.Text(e)) || EffectReceipts.ModelSafe(e.Envelope?.EffectReceipts).Count > 0))
            .Select(e => e.Sequence).DefaultIfEmpty(0).Max();
    }

    public const int EvidenceBytes = 3000;
    public static TenantExperienceSourceObservation Session(TenantMemoryReader reader, TenantIsolationMetadata settings,
        SessionSnapshot snapshot, IReadOnlyList<ConversationEntry> rows, long stable, TenantExperienceSourceWitness? expected = null)
    {
        if (snapshot.SessionId == reader.Run.SessionId) throw new AccessDeniedException("experience_source_changed");
        var projected = SessionEvidence(snapshot, rows, stable, expected);
        return new(reader, settings, projected.Witness, projected.EvidenceJson, projected.Title);
    }
    public static (TenantExperienceSourceWitness Witness, string EvidenceJson, string Title) SessionEvidence(
        SessionSnapshot snapshot, IReadOnlyList<ConversationEntry> rows, long stable, TenantExperienceSourceWitness? expected = null)
    {
        if (stable < 1) throw new AccessDeniedException("experience_source_unavailable");
        if (expected is not null && (expected.Kind != ExperienceSourceKind.Session
            || expected.SourceId != snapshot.SessionId || expected.SessionRevision != snapshot.Revision || expected.ThroughCursor != stable))
            throw new AccessDeniedException("experience_source_changed");
        var selected = rows.Where(e => e.Sequence <= stable && e.Status != EntryStatus.Streaming && (expected is null || e.Sequence > expected.AfterEntrySequence))
            .OrderBy(e => e.Sequence).TakeLast(expected?.Limit ?? 20).Select(e => new { sequence = e.Sequence, role = e.Role.ToString(), status = e.Status.ToString(),
                text = TenantContinuityRules.Safe(e.Role == ConversationRole.Assistant ? AssistantSemanticProjection.Text(e) : e.Text, 300, out var _), excerpt = e.Text.Length > 300 }).ToList();
        if (selected.Count == 0 || selected[^1].sequence != stable) throw new AccessDeniedException("experience_source_unavailable");
        var summary = snapshot.SummarizedThroughEntrySequence > 0 && snapshot.SummarizedThroughEntrySequence <= stable ? TenantContinuityRules.Safe(snapshot.Summary, 80, out _) : "";
        string Serialize() => JsonSerializer.Serialize(new { sourceKind = "Session", sourceId = snapshot.SessionId, throughCursor = stable, summary, entries = selected,
            boundedObservation = true, untrustedHistory = true });
        var evidence = Serialize();
        while (Encoding.UTF8.GetByteCount(evidence) > EvidenceBytes && selected.Count > 1) { selected.RemoveAt(0); evidence = Serialize(); }
        if (Encoding.UTF8.GetByteCount(evidence) > EvidenceBytes) throw new AccessDeniedException("experience_source_budget");
        var title = TenantContinuityRules.Safe(snapshot.Title, 160, out _);
        var source = new TenantExperienceSourceWitness(ExperienceSourceKind.Session, snapshot.SessionId, snapshot.SessionId, snapshot.Revision,
            stable, selected[0].sequence - 1, selected.Count, null, null, TenantMemoryOperationRules.Hash(evidence));
        if (expected is not null && source != expected) throw new AccessDeniedException("experience_source_changed");
        return (source, evidence, title);
    }
    public static TenantExperienceSourceObservation Run(TenantMemoryReader reader, TenantIsolationMetadata settings, AgentRun source, Guid? privateUser, TenantExperienceSourceWitness? expected = null)
    {
        if (source.AgentRunId == reader.Run.AgentRunId) throw new AccessDeniedException("experience_source_unavailable");
        var projected = RunEvidence(source, privateUser, expected);
        return new(reader, settings, projected.Witness, projected.EvidenceJson, projected.Title);
    }
    public static (TenantExperienceSourceWitness Witness, string EvidenceJson, string Title) RunEvidence(
        AgentRun source, Guid? privateUser, TenantExperienceSourceWitness? expected = null)
    {
        if (!TenantExperienceRules.Substantive(source)) throw new AccessDeniedException("experience_source_unavailable");
        var evidence = JsonSerializer.Serialize(new { sourceKind = "AgentRun", sourceId = source.AgentRunId, throughCursor = source.Revision,
            status = source.Status.ToString(), outcome = source.Result?.OutcomeKind.ToString(),
            summary = TenantContinuityRules.Safe(source.Result?.Text ?? source.Failure?.Summary ?? "No completed result", 300, out _),
            source.UpdatedAtUtc, boundedObservation = true, untrustedHistory = true });
        if (Encoding.UTF8.GetByteCount(evidence) > EvidenceBytes) throw new AccessDeniedException("experience_source_budget");
        var witness = new TenantExperienceSourceWitness(ExperienceSourceKind.AgentRun, source.AgentRunId, source.SessionId, 0, source.Revision,
            0, 0, source.AgentRunId, source.Revision, TenantMemoryOperationRules.Hash(evidence), privateUser);
        if (expected is not null && witness != expected) throw new AccessDeniedException("experience_source_changed");
        return (witness, evidence, TenantContinuityRules.Safe(source.Admission.TaskIntent?.Text ?? "Completed AgentRun", 160, out _));
    }
}
