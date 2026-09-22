using System.Text.RegularExpressions;
using AgentCore.Application.Observability;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Memory;

namespace AgentCore.Application.Memory;

public sealed class StructuredMemoryService(IStructuredMemoryStore store, IIdGenerator ids, TimeProvider time)
    : IStructuredMemoryService
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

    public async ValueTask<StructuredMemoryItem> WriteAsync(
        TrustedMemoryOwner owner,
        MemoryWriteProposal proposal,
        MemoryAdmissionContext admission,
        CancellationToken cancellationToken = default)
    {
        var drafted = Draft(owner, proposal.Kind, proposal.Subject, proposal.Content, proposal.SourceEntryIds, admission, supersedes: null);
        var existing = await store.FindActiveBySubjectAsync(owner.SessionId, drafted.Kind, drafted.SubjectKey, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            Reject("memory_rejected");
            throw new AgentCoreException(
                "Conflict",
                "An active memory already uses this subject. Update that item.",
                409);
        }

        if (await store.CountActiveAsync(owner.SessionId, cancellationToken).ConfigureAwait(false) >= MemoryLimits.MaxActiveItems)
        {
            Reject("memory_rejected");
            throw new AgentCoreException("MemoryCapacity", "Active session memory is full.", 409);
        }

        await store.InsertAsync(drafted, cancellationToken).ConfigureAwait(false);
        return drafted;
    }

    public async ValueTask<StructuredMemoryItem> UpdateAsync(
        TrustedMemoryOwner owner,
        MemoryUpdateProposal proposal,
        MemoryAdmissionContext admission,
        CancellationToken cancellationToken = default)
    {
        var current = await RequireActiveAsync(owner, proposal.MemoryId, cancellationToken).ConfigureAwait(false);
        var created = Draft(
            owner,
            current.Kind,
            proposal.Subject,
            proposal.Content,
            proposal.SourceEntryIds,
            admission,
            current.MemoryId);
        var other = await store.FindActiveBySubjectAsync(owner.SessionId, created.Kind, created.SubjectKey, cancellationToken)
            .ConfigureAwait(false);
        if (other is not null && other.MemoryId != current.MemoryId)
        {
            Reject("memory_rejected");
            throw new AgentCoreException(
                "Conflict",
                "An active memory already uses this subject. Update that item.",
                409);
        }

        var superseded = current with
        {
            Status = MemoryItemStatus.Superseded,
            UpdatedAt = created.UpdatedAt
        };
        await store.SupersedeAsync(superseded, created, cancellationToken).ConfigureAwait(false);
        return created;
    }

    public async ValueTask<StructuredMemoryItem> DeleteAsync(
        TrustedMemoryOwner owner,
        Guid memoryId,
        CancellationToken cancellationToken = default)
    {
        var current = await RequireActiveAsync(owner, memoryId, cancellationToken).ConfigureAwait(false);
        var now = time.GetUtcNow();
        var tombstone = current with
        {
            Status = MemoryItemStatus.Deleted,
            Subject = string.Empty,
            Content = string.Empty,
            SubjectKey = string.Empty,
            UpdatedAt = now
        };
        await store.TombstoneAsync(tombstone, cancellationToken).ConfigureAwait(false);
        return tombstone;
    }

    public async ValueTask<StructuredMemoryItem?> GetAsync(
        TrustedMemoryOwner owner,
        Guid memoryId,
        MemoryAdmissionContext admission,
        CancellationToken cancellationToken = default)
    {
        var item = await store.FindAsync(owner.SessionId, memoryId, cancellationToken).ConfigureAwait(false);
        if (item is null || item.SessionId != owner.SessionId)
        {
            return null;
        }

        if (item.Status == MemoryItemStatus.Active && Occupied(admission).Contains(item.SubjectKey))
        {
            return null;
        }

        return item;
    }

    public async ValueTask<IReadOnlyList<StructuredMemoryItem>> SearchAsync(
        TrustedMemoryOwner owner,
        MemorySearchQuery query,
        MemoryAdmissionContext admission,
        CancellationToken cancellationToken = default)
    {
        var occupied = Occupied(admission);
        var tokens = Tokens(query.Text);
        var ranked = new List<(StructuredMemoryItem Item, int Score)>();
        foreach (var item in await store.ListActiveAsync(owner.SessionId, cancellationToken).ConfigureAwait(false))
        {
            if (item.SessionId != owner.SessionId || occupied.Contains(item.SubjectKey))
            {
                continue;
            }

            if (query.Kind is not null && item.Kind != query.Kind)
            {
                continue;
            }

            var score = tokens.Count == 0 ? 1 : Score(item, tokens);
            if (score > 0)
            {
                ranked.Add((item, score));
            }
        }

        ranked.Sort(static (left, right) =>
        {
            var byScore = right.Score.CompareTo(left.Score);
            if (byScore != 0)
            {
                return byScore;
            }

            var byTime = right.Item.UpdatedAt.CompareTo(left.Item.UpdatedAt);
            return byTime != 0 ? byTime : left.Item.MemoryId.CompareTo(right.Item.MemoryId);
        });

        var results = new List<StructuredMemoryItem>();
        var characters = 0;
        foreach (var (item, _) in ranked)
        {
            var weight = item.Subject.Length + item.Content.Length;
            if (results.Count >= MemoryLimits.SearchMaxItems
                || (results.Count > 0 && characters + weight > MemoryLimits.SearchMaxCharacters))
            {
                break;
            }

            results.Add(item);
            characters += weight;
        }

        return results;
    }

    private async ValueTask<StructuredMemoryItem> RequireActiveAsync(
        TrustedMemoryOwner owner,
        Guid memoryId,
        CancellationToken cancellationToken)
    {
        var current = await store.FindAsync(owner.SessionId, memoryId, cancellationToken).ConfigureAwait(false);
        if (current is null || current.SessionId != owner.SessionId || current.Status != MemoryItemStatus.Active)
        {
            throw AgentCoreErrors.NotFound("Memory was not found.");
        }

        return current;
    }

    private StructuredMemoryItem Draft(
        TrustedMemoryOwner owner,
        MemoryKind kind,
        string subject,
        string content,
        IReadOnlyList<Guid> sourceEntryIds,
        MemoryAdmissionContext admission,
        Guid? supersedes)
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

        var now = time.GetUtcNow();
        var entries = new List<Guid>(sourceEntryIds.Count);
        foreach (var entryId in sourceEntryIds)
        {
            if (entryId != Guid.Empty && !entries.Contains(entryId))
            {
                entries.Add(entryId);
            }
        }

        return new StructuredMemoryItem(
            ids.NewId(),
            owner.SessionId,
            kind,
            MemoryItemStatus.Active,
            collapsed,
            body,
            subjectKey,
            new MemoryProvenance(source, entries, supersedes, now),
            now,
            now);
    }

    private static bool ContainsSensitive(string value) =>
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

    private static List<string> Tokens(string? text)
    {
        var tokens = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return tokens;
        }

        foreach (var token in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            tokens.Add(token.ToLowerInvariant());
        }

        return tokens;
    }

    private static int Score(StructuredMemoryItem item, IReadOnlyList<string> tokens)
    {
        var haystack = item.SubjectKey + " " + item.Content.ToLowerInvariant();
        var score = 0;
        foreach (var token in tokens)
        {
            if (haystack.Contains(token, StringComparison.Ordinal))
            {
                score++;
            }
        }

        return score;
    }

    private static void Reject(string kind) => RuntimeTelemetry.RecordDropped(kind);
}
