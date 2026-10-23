using System.Text.RegularExpressions;
using AgentCore.Application.Observability;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Memory;

namespace AgentCore.Application.Memory;

public static class MemoryContentRules
{
    private static readonly Regex SecretPattern = new(
        "(?i)(sk-[a-z0-9]{16,}|BEGIN [A-Z ]*PRIVATE KEY|AKIA[0-9A-Z]{16})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex Base64Blob = new(
        "[A-Za-z0-9+/]{80,}={0,2}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> ReservedSubjectKeys = new(StringComparer.Ordinal)
    {
        "language",
        "preferredname",
        "locale",
        "timezone",
        "name",
        "role",
        "description",
        "tone"
    };

    public static (string Subject, string Content, string SubjectKey, string Source) ValidateContent(
        MemoryKind kind, string subject, string content, IReadOnlyList<Guid> sourceEntryIds, MemoryAdmissionContext admission)
    {
        if (!Enum.IsDefined(kind))
        {
            throw AgentCoreErrors.Validation("Memory kind is not supported.");
        }

        var collapsed = StructuredMemoryItem.CollapseSubject(subject);
        var body = content.Trim();
        if (collapsed.Length is 0 or > MemoryLimits.MaxSubjectCharacters
            || body.Length is 0 or > MemoryLimits.MaxContentCharacters)
        {
            throw AgentCoreErrors.Validation("Memory subject and content must stay within the session memory bounds.");
        }

        var source = admission.Source.Trim();
        if (source.Length is 0 or > MemoryLimits.MaxSourceLabelCharacters)
        {
            throw AgentCoreErrors.Validation("Memory source must stay within the provenance bound.");
        }

        if (sourceEntryIds.Count > MemoryLimits.MaxSourceEntryIds)
        {
            throw AgentCoreErrors.Validation("Memory provenance accepts at most eight source entries.");
        }

        var subjectKey = StructuredMemoryItem.SubjectKeyFor(collapsed);
        if (ReservedSubjectKeys.Contains(subjectKey) || ContainsSensitive(collapsed) || ContainsSensitive(body) || ContainsForbidden(collapsed, body, admission))
        {
            Reject("memory_rejected");
            throw new AgentCoreException("MemoryRejected", "Memory content was rejected.", 400);
        }

        if (Occupied(admission).Contains(subjectKey))
            throw new AgentCoreException("MemoryRejected", "Memory cannot replace trusted profile or identity state.", 400);
        return (collapsed, body, subjectKey, source);
    }

    public static bool ContainsSensitive(string value) =>
        SecretPattern.IsMatch(value) || Base64Blob.IsMatch(value);

    private static bool ContainsForbidden(string subject, string content, MemoryAdmissionContext admission)
    {
        foreach (var fragment in admission.ForbiddenFragments)
        {
            if (string.IsNullOrWhiteSpace(fragment))
            {
                continue;
            }

            if (subject.Contains(fragment, StringComparison.Ordinal)
                || content.Contains(fragment, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static HashSet<string> Occupied(MemoryAdmissionContext admission)
    {
        var occupied = new HashSet<string>(StringComparer.Ordinal);
        foreach (var subject in admission.OccupiedTrustedSubjects)
        {
            var key = StructuredMemoryItem.SubjectKeyFor(StructuredMemoryItem.CollapseSubject(subject));
            if (key.Length > 0)
            {
                occupied.Add(key);
            }
        }

        return occupied;
    }

    private static void Reject(string kind) => RuntimeTelemetry.RecordDropped(kind);
}
