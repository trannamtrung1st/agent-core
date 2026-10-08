using System.Security.Cryptography;
using System.Text;

namespace AgentCore.Domain.Conversation;

public enum ActivationKind
{
    UserTurn,
    Initiative,
    ImmediateBackground,
    ScheduledWork,
    ApplicationEvent,
    ManualBackground,
    BackgroundCompleted
}

/// <summary>Immutable admission of one effective turn, including its ordered input batch.</summary>
public sealed class Activation
{
    public const int MaxSourceEntries = 100;

    public Activation(
        Guid activationId,
        Guid sessionId,
        ActivationKind kind,
        IReadOnlyList<Guid> sourceEntryIds,
        Guid? sourceEventId,
        Guid? triggerOccurrenceId,
        Guid? sourceSessionId,
        Guid? sourceAgentRunId,
        string dedupeKey,
        DateTimeOffset admittedAtUtc,
        string? evidenceJson = null)
    {
        if (activationId == Guid.Empty || sessionId == Guid.Empty || !Enum.IsDefined(kind))
            throw new ArgumentException("Activation, Session and valid kind are required.");
        ArgumentNullException.ThrowIfNull(sourceEntryIds);
        var entries = sourceEntryIds.ToArray();
        if (entries.Length > MaxSourceEntries || entries.Any(id => id == Guid.Empty)
            || entries.Distinct().Count() != entries.Length)
            throw new ArgumentException("Activation sources must be a bounded ordered set of distinct entries.");
        if (kind == ActivationKind.UserTurn && entries.Length == 0)
            throw new ArgumentException("A user turn requires at least one accepted input entry.");
        AgentRunText.RequireOptionalId(sourceEventId, "Source event");
        AgentRunText.RequireOptionalId(triggerOccurrenceId, "Trigger occurrence");
        AgentRunText.RequireOptionalId(sourceSessionId, "Source Session");
        AgentRunText.RequireOptionalId(sourceAgentRunId, "Source AgentRun");
        if (kind == ActivationKind.ScheduledWork && triggerOccurrenceId is null)
            throw new ArgumentException("Scheduled work requires its admitted occurrence.");
        if (kind is ActivationKind.ImmediateBackground or ActivationKind.BackgroundCompleted
            && (sourceSessionId is null || sourceAgentRunId is null || sourceSessionId == sessionId))
            throw new ArgumentException("Background admission requires another Session and its source run.");
        AgentRunTime.RequireUtc(admittedAtUtc, "Admission");
        EvidenceJson = evidenceJson is null ? null : AgentRunText.RequireUtf8(evidenceJson, AgentRunLimits.MaxEvidenceBytes, "Activation evidence");
        if (EvidenceJson is not null)
        {
            using var evidence = System.Text.Json.JsonDocument.Parse(EvidenceJson);
            if (evidence.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
                throw new ArgumentException("Activation evidence must be an object.");
        }
        ActivationId = activationId;
        SessionId = sessionId;
        Kind = kind;
        SourceEntryIds = Array.AsReadOnly(entries);
        SourceEventId = sourceEventId;
        TriggerOccurrenceId = triggerOccurrenceId;
        SourceSessionId = sourceSessionId;
        SourceAgentRunId = sourceAgentRunId;
        DedupeKey = AgentRunText.RequireDedupeKey(dedupeKey);
        AdmittedAtUtc = admittedAtUtc;
        var fingerprint = $"{sessionId:D}\n{kind}\n{string.Join(',', entries.Select(id => id.ToString("D")))}\n{sourceEventId:D}\n{triggerOccurrenceId:D}\n{sourceSessionId:D}\n{sourceAgentRunId:D}\n{EvidenceJson}";
        SourceFingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprint))).ToLowerInvariant();
    }

    public string? EvidenceJson { get; }

    public Guid ActivationId { get; }
    public Guid SessionId { get; }
    public ActivationKind Kind { get; }
    public IReadOnlyList<Guid> SourceEntryIds { get; }
    public Guid? SourceEventId { get; }
    public Guid? TriggerOccurrenceId { get; }
    public Guid? SourceSessionId { get; }
    public Guid? SourceAgentRunId { get; }
    public string DedupeKey { get; }
    public string SourceFingerprint { get; }
    public DateTimeOffset AdmittedAtUtc { get; }
}
