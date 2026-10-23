using System.Text.Json;
using AgentCore.Application.Agents;
using AgentCore.Application.Execution;
using AgentCore.Application.Memory;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public static class TenantContinuityRules
{
    public const string Completed = "continuity_read_completed";
    public const int CandidateLimit = 100;
    public static void Eligible(TenantMemoryReader reader)
    {
        if (!TenantTaskExecutionContract.Allows(reader.Run, reader.Current) || !TenantContinuityToolContract.Allows(reader.Run.Admission.Configuration!.Definition)
            || !TenantContinuityToolContract.Allows(reader.Current)) throw new AccessDeniedException("continuity_unavailable");
    }
    public static IEnumerable<(string Permission, AccessScope Scope)> Targets(TenantMemoryReader reader, IEnumerable<Guid> sessions,
        IEnumerable<AgentCore.Domain.Memory.TenantMemoryReference>? memory = null, bool includeMemory = false)
    {
        var tenant = reader.Security.Tenant.TenantId;
        foreach (var session in sessions.Append(reader.Run.SessionId).Append(reader.Root.SessionId).Distinct()) yield return ("sessions.read", TenantSessionRules.Scope(tenant, session));
        var agent = AccessScope.ForResource(tenant, "agent", reader.Run.AgentInstanceId.ToString("D"));
        yield return ("agents.read", agent); yield return ("agents.chat", agent);
        if (includeMemory) foreach (var target in TenantMemoryOperationRules.ReferenceTargets(reader, memory ?? [], false)) yield return target;
    }
    public static async ValueTask RequireRawAsync(TenantMemoryReader reader, IEnumerable<Guid> sessions, IamAuthorizer iam, CancellationToken ct,
        Guid? human = null, IEnumerable<AgentCore.Domain.Memory.TenantMemoryReference>? memory = null, bool includeMemory = false)
    {
        var actor = human ?? reader.Agent.PrincipalId;
        if (human is { } person && !reader.Security.Users.Any(u => u.PrincipalId == person && u.Status == IdentityStatus.Active)) throw new AccessDeniedException("approval_unavailable");
        foreach (var (key, scope) in Targets(reader, sessions, memory, includeMemory).Distinct())
            if (!(await iam.CheckOwnedTenantAsync(reader.Security, actor, PermissionCatalog.Get(key), scope, ct).ConfigureAwait(false)).Allowed)
                throw new AccessDeniedException(human is null ? "continuity_read_required" : "approval_unavailable");
        if (includeMemory) await TenantMemoryOperationRules.RequireRawReferencesAsync(reader, memory ?? [], iam, ct, human).ConfigureAwait(false);
    }
    public static async ValueTask<IReadOnlyList<Guid>?> VisibleSessionIdsAsync(TenantMemoryReader reader, IamAuthorizer iam, CancellationToken ct, Guid? human = null)
    {
        await RequireRawAsync(reader, [], iam, ct, human).ConfigureAwait(false); var actor = human ?? reader.Agent.PrincipalId;
        var permission = PermissionCatalog.Get("sessions.read");
        if ((await iam.CheckOwnedTenantAsync(reader.Security, actor, permission, AccessScope.ForTenant(reader.Security.Tenant.TenantId), ct).ConfigureAwait(false)).Allowed) return null;
        var ids = new List<Guid>();
        foreach (var scope in reader.Security.Assignments.Where(a => a.PrincipalId == actor && a.Scope.ResourceType == "session"
            && reader.Security.Roles.Any(r => r.RoleId == a.RoleId && r.Permissions.Contains("sessions.read", StringComparer.Ordinal))).Select(a => a.Scope).Distinct())
            if (Guid.TryParseExact(scope.ResourceId, "D", out var id) && (await iam.CheckOwnedTenantAsync(reader.Security, actor, permission, scope, ct).ConfigureAwait(false)).Allowed) ids.Add(id);
        return ids.AsReadOnly();
    }
    public static async ValueTask<bool> IncludeMemoryAsync(TenantMemoryReader reader, TenantContinuityArguments args, IamAuthorizer iam, CancellationToken ct, Guid? human = null)
    {
        if (args.Kind is "Session" or "Experience") return false;
        var allowed = reader.Effective.MemoryPolicy?.SessionMemory == true && (await iam.CheckOwnedTenantAsync(reader.Security,
            human ?? reader.Agent.PrincipalId, PermissionCatalog.Get("memory.read"), AccessScope.ForResource(reader.Security.Tenant.TenantId, "agent", reader.Run.AgentInstanceId.ToString("D")), ct).ConfigureAwait(false)).Allowed;
        if (!allowed && args.Kind == "Memory") throw new AccessDeniedException("continuity_source_unavailable");
        return allowed;
    }
    public static async ValueTask<bool> IncludeExperienceAsync(TenantMemoryReader reader, TenantIsolationMetadata settings, TenantContinuityArguments args, IamAuthorizer iam, CancellationToken ct)
    {
        if (args.Kind is "Session" or "Memory") return false;
        var allowed = false;
        try { TenantExperienceRules.Eligible(reader, settings, TenantExperienceToolContract.Source);
            await TenantExperienceRules.RawAsync(reader, null, true, iam, ct).ConfigureAwait(false); allowed = true; }
        catch (AccessDeniedException) { }
        if (!allowed && args.Kind == "Experience") throw new AccessDeniedException("experience_unavailable");
        return allowed;
    }
    public static string ObservationHash(TenantContinuityArguments args, string sessions, PreparedMemoryOperation? memory, PreparedExperienceRead? experience) =>
        TenantMemoryOperationRules.Hash(experience is null ? JsonSerializer.Serialize(new { args.Tool, args.CanonicalArguments, sessions, memory })
            : JsonSerializer.Serialize(new { args.Tool, args.CanonicalArguments, sessions, memory, experience }));
    public static string Safe(string value, int limit, out bool truncated)
    {
        truncated = value.Length > limit;
        return MemoryContentRules.ContainsSensitive(value) ? "[sensitive historical content omitted]" : truncated ? value[..limit] : value;
    }
    public static TenantHistoricalSession Project(Guid session, long revision, long through, string title, string summary, DateTimeOffset updated,
        IReadOnlyList<ConversationEntry> entries, long after, int limit, bool hasMore)
    {
        updated = DateTimeOffset.FromUnixTimeMilliseconds(updated.ToUnixTimeMilliseconds());
        var projected = entries.Where(e => e.Status != EntryStatus.Streaming && e.Sequence <= through && e.Sequence > after).OrderBy(e => e.Sequence).Take(limit).Select(e => {
            var text = Safe(e.Role == ConversationRole.Assistant ? AssistantSemanticProjection.Text(e) : e.Text, 700, out var clipped);
            return new TenantHistoricalEntry(e.EntryId, e.Sequence, e.Role.ToString(), e.Status.ToString(), text, clipped);
        }).ToArray();
        var safeTitle = Safe(title, 160, out _); var safeSummary = Safe(summary, 700, out _);
        var next = projected.Length > 0 ? (long?)projected[^1].Sequence : null;
        var hash = TenantMemoryOperationRules.Hash(JsonSerializer.Serialize(new { session, revision, through, after, limit, safeTitle, safeSummary, updated,
            entries = projected, hasMore, next }));
        return new(session, revision, through, safeTitle, safeSummary, updated, Array.AsReadOnly(projected), hasMore, next, after, limit, hash);
    }
    public static PreparedHistoricalSession Witness(TenantHistoricalSession value) => new(value.SessionId, value.Revision, value.ThroughEntrySequence,
        value.AfterEntrySequence, value.Limit, value.ObservationHash);
    public static PreparedHistoricalSession[] Sessions(PreparedContinuityRead value)
    {
        var sources = JsonSerializer.Deserialize<PreparedHistoricalSession[]>(value.SessionsJson) ?? throw new ArgumentException("Use exact historical witnesses.");
        if (sources.Length > 11 || sources.Select(s => s.SessionId).Distinct().Count() != sources.Length
            || sources.Any(s => s.SessionId == Guid.Empty || s.SessionId == value.SessionId || s.Revision < 1 || s.ThroughEntrySequence < 0
                || s.AfterEntrySequence < 0 || s.Limit is < 1 or > 20 || !TenantMemoryOperationRules.ValidHash(s.ObservationHash))
            || JsonSerializer.Serialize(sources) != value.SessionsJson) throw new ArgumentException("Use bounded canonical historical witnesses.");
        return sources;
    }
    public static TenantContinuityObservation Observe(TenantMemoryReader reader, TenantContinuityArguments args, IReadOnlyList<TenantHistoricalSession> candidates,
        TenantMemoryOperationObservation? memory, PreparedContinuityRead? expected = null, bool inspection = false, bool candidateHasMore = false, TenantExperienceFactObservation? experience = null)
    {
        if (!inspection) Eligible(reader);
        var selected = args.Tool == ToolCatalog.ContinuityGet || inspection ? candidates.ToArray() : candidates
            .Select(s => (Item: s, Score: AgentCore.Application.Continuity.ContinuityRelevance.Score(s.Title + " " + s.Summary + " " + string.Join(' ', s.Entries.Select(e => e.Text)), args.Query)))
            .Where(s => string.IsNullOrWhiteSpace(args.Query) || s.Score > 0).OrderByDescending(s => s.Score).ThenByDescending(s => s.Item.UpdatedAt).ThenBy(s => s.Item.SessionId)
            .Take(args.Limit + 1).Select(s => s.Item).ToArray();
        if (args.Kind == "Session" && (selected.Length != 1 || selected[0].SessionId != args.Id)) throw new AccessDeniedException("continuity_source_unavailable");
        var json = JsonSerializer.Serialize(selected.Select(Witness));
        var labels = JsonSerializer.Serialize(selected.Select(s => new { sessionId = s.SessionId, title = s.Title, throughEntrySequence = s.ThroughEntrySequence, afterEntrySequence = s.AfterEntrySequence, limit = s.Limit }));
        var memoryValue = memory?.Value;
        var value = new PreparedContinuityRead(reader.Run.SessionId, reader.Root.SessionId, reader.User?.PrincipalId, args.Tool, args.CanonicalArguments,
            json, ObservationHash(args, json, memoryValue, experience?.Value), labels, memoryValue, experience?.Value);
        var changed = experience?.SourcesChanged == true || expected is not null && (value != expected || memory is not null && TenantMemoryOperationRules.Sources(memory.Value).Any(s => !memory.Items.Any(m => m.MemoryId == s.MemoryId && m.Revision == s.Revision && m.ContentHash == s.ContentHash)));
        if (changed && !inspection) throw new SecurityConflictException("continuity_source_changed");
        return new(reader, args, Array.AsReadOnly(selected), memory, inspection ? expected! : value,
            candidateHasMore || selected.Length > args.Limit || memory?.HasMore == true || experience?.HasMore == true, changed, experience);
    }
}
