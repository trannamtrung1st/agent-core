using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentCore.Application.Agents;
using AgentCore.Application.Memory;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Memory;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public static class TenantMemoryOperationRules
{
    public const string Completed = "memory_operation_completed";
    public static bool HumanTask(TenantMemoryReader reader) => reader.User is not null && reader.Root.Admission.ExecutionContext?.Origin == ExecutionOrigin.UserMessage;
    public static void Eligible(TenantMemoryReader reader, TenantMemoryArguments arguments)
    {
        var current = reader.Effective;
        if (!TenantTaskExecutionContract.Allows(reader.Run, reader.Current) || !TenantMemoryToolContract.Allows(reader.Run.Admission.Configuration!.Definition, arguments.Tool)
            || !TenantMemoryToolContract.Allows(reader.Current, arguments.Tool)) throw new AccessDeniedException("memory_capability_unavailable");
        if (arguments.ReadOnly) return;
        if (!HumanTask(reader) || current.StateIsolationPolicy?.CrossSessionContinuity == false
            || ManagementPolicyResolver.Resolve(reader.Current, reader.Agent.Instance).Frozen
            || reader.Run.Admission.Configuration.Definition.SelfManagement?.Frozen == true
            || arguments.Tool == ToolCatalog.MemoryForget && reader.Run.Admission.OutputContract != AgentRunOutputContract.ConversationResponse)
            throw new AccessDeniedException("memory_maintenance_unavailable");
    }
    public static ValueTask RequireRawAsync(TenantMemoryReader reader, IEnumerable<TenantLearnedMemory> items, IamAuthorizer iam,
        CancellationToken ct, Guid? human = null, bool manage = false, bool exceptional = false) => RequireRawReferencesAsync(reader, items.Select(i => i.Reference()), iam, ct, human, manage, exceptional);
    public static async ValueTask RequireRawReferencesAsync(TenantMemoryReader reader, IEnumerable<TenantMemoryReference> items, IamAuthorizer iam,
        CancellationToken ct, Guid? human = null, bool manage = false, bool exceptional = false)
    {
        if (human is { } viewer && reader.User is { } owner && owner.PrincipalId != viewer) throw new AccessDeniedException("approval_unavailable");
        var references = items.ToArray();
        if (references.Any(source => !reader.Security.SystemIdentities.Any(i => i.TenantId == source.TenantId && i.AgentInstanceId == source.AgentInstanceId
            && i.PrincipalId == source.ExecutorPrincipalId && i.Kind == SystemIdentityKind.Agent && i.Status == IdentityStatus.Active))) throw new AccessDeniedException("memory_source_unavailable");
        var actor = human ?? reader.Agent.PrincipalId;
        foreach (var (key, scope) in ReferenceTargets(reader, references, manage))
        {
            var primary = key == "memory.manage" && scope == AccessScope.ForResource(reader.Security.Tenant.TenantId, "agent", reader.Run.AgentInstanceId.ToString("D"));
            if (human is null && exceptional && primary) continue;
            if (!(await iam.CheckOwnedTenantAsync(reader.Security, actor, PermissionCatalog.Get(key), scope, ct).ConfigureAwait(false)).Allowed)
                throw new AccessDeniedException(human is null ? key.EndsWith(".read", StringComparison.Ordinal) ? "memory_read_required" : "memory_manage_required" : "approval_unavailable");
        }
    }
    public static IEnumerable<(string Permission, AccessScope Scope)> Targets(TenantMemoryReader reader, IEnumerable<TenantLearnedMemory> items, bool manage)
        => ReferenceTargets(reader, items.Select(i => i.Reference()), manage);
    public static IEnumerable<(string Permission, AccessScope Scope)> ReferenceTargets(TenantMemoryReader reader, IEnumerable<TenantMemoryReference> items, bool manage)
    {
        var tenant = reader.Security.Tenant.TenantId;
        foreach (var session in new[] { reader.Run.SessionId, reader.Root.SessionId }.Distinct()) yield return ("sessions.read", TenantSessionRules.Scope(tenant, session));
        foreach (var agent in items.Select(i => i.AgentInstanceId).Append(reader.Run.AgentInstanceId).Distinct())
        {
            yield return ("agents.read", AccessScope.ForResource(tenant, "agent", agent.ToString("D")));
            yield return ("memory.read", AccessScope.ForResource(tenant, "agent", agent.ToString("D")));
            if (manage) yield return ("memory.manage", AccessScope.ForResource(tenant, "agent", agent.ToString("D")));
        }
        foreach (var session in items.Where(i => i.Scope == MemoryScope.Session).Select(i => i.SessionId).Distinct().Except(new[] { reader.Run.SessionId, reader.Root.SessionId }))
            yield return ("sessions.read", TenantSessionRules.Scope(tenant, session));
    }
    public static async ValueTask<IReadOnlyList<Guid>> ReadableAgentsAsync(TenantMemoryReader reader, IamAuthorizer iam, Guid? human, CancellationToken ct)
    {
        if (human is { } viewer && reader.User is { } owner && owner.PrincipalId != viewer) throw new AccessDeniedException("approval_unavailable");
        await RequireRawAsync(reader, [], iam, ct, human).ConfigureAwait(false);
        var actor = human ?? reader.Agent.PrincipalId; var result = new List<Guid>();
        foreach (var source in reader.Security.SystemIdentities.Where(i => i.TenantId == reader.Security.Tenant.TenantId && i.Kind == SystemIdentityKind.Agent && i.Status == IdentityStatus.Active && i.AgentInstanceId is not null))
        {
            var id = source.AgentInstanceId!.Value; var scope = AccessScope.ForResource(reader.Security.Tenant.TenantId, "agent", id.ToString("D"));
            if ((await iam.CheckOwnedTenantAsync(reader.Security, actor, PermissionCatalog.Get("agents.read"), scope, ct).ConfigureAwait(false)).Allowed
                && (await iam.CheckOwnedTenantAsync(reader.Security, actor, PermissionCatalog.Get("memory.read"), scope, ct).ConfigureAwait(false)).Allowed) result.Add(id);
        }
        return result.AsReadOnly();
    }
    public static IReadOnlyList<string> Keys(TenantMemoryReader reader, bool inspection) => TenantMemoryRules.OwnerKeys(reader).Where(key => inspection
        || TenantMemoryRules.ScopeEnabled(reader.Effective.MemoryPolicy, key.StartsWith("session:", StringComparison.Ordinal) ? MemoryScope.Session
            : key.StartsWith("identityUser:", StringComparison.Ordinal) ? MemoryScope.IdentityUser : MemoryScope.User, false)).ToArray();
    public static TenantMemoryOperationObservation Observe(TenantMemoryReader reader, TenantMemoryArguments args, IReadOnlyList<TenantLearnedMemory> rows,
        PreparedMemoryOperation? expected = null, bool inspection = false)
    {
        if (!inspection) Eligible(reader, args);
        var owned = rows.Where(i => TenantMemoryRules.Owns(reader, i)).ToArray(); IReadOnlyList<TenantLearnedMemory> items; var more = false; TenantLearnedMemory? existingResult = null;
        if (inspection && expected is not null)
        {
            var sources = Sources(expected); items = sources.Select(s => owned.SingleOrDefault(i => i.MemoryId == s.MemoryId && i.ContentHash == s.ContentHash)
                ?? throw new AccessDeniedException("memory_source_changed")).ToArray();
        }
        else if (args.Tool == ToolCatalog.ContinuitySearch)
        {
            var words = (args.Query ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(w => w.ToLowerInvariant()).Distinct().ToArray();
            var ranked = owned.Where(i => i.Status == MemoryItemStatus.Active && TenantMemoryRules.ScopeEnabled(reader.Effective.MemoryPolicy, i.Scope, false))
                .Select(i => (Item: i, Score: words.Sum(w => (i.Subject.Contains(w, StringComparison.OrdinalIgnoreCase) ? 3 : 0) + (i.Content.Contains(w, StringComparison.OrdinalIgnoreCase) ? 1 : 0))))
                .Where(p => words.Length == 0 || p.Score > 0).OrderByDescending(p => p.Score).ThenBy(p => p.Item.Scope).ThenByDescending(p => p.Item.UpdatedAt).ThenBy(p => p.Item.MemoryId).Take(args.Limit + 1).Select(p => p.Item).ToArray();
            more = ranked.Length > args.Limit; items = ranked;
        }
        else
        {
            var ids = args.SourceIds ?? (args.MemoryId is { } id ? [id] : []);
            items = ids.Select(id => owned.SingleOrDefault(i => i.MemoryId == id && TenantMemoryRules.ScopeEnabled(reader.Effective.MemoryPolicy, i.Scope, false))
                ?? throw new AccessDeniedException("memory_source_unavailable")).ToArray();
            if (args.Tool == ToolCatalog.MemoryConsolidate && items.Any(i => i.Status != MemoryItemStatus.Active))
            {
                var priorResultId = ResultId(reader, args, items[0]);
                existingResult = owned.SingleOrDefault(i => i.MemoryId == priorResultId && i.OwnerKey == items[0].OwnerKey
                    && i.Kind == args.Kind && i.ContentHash == TenantMemoryRules.Hash(args.Kind!.Value, args.Subject!, args.Content!)
                    && i.SourceMemoryIdsJson == JsonSerializer.Serialize(args.SourceIds!.Order().ToArray()));
                if (existingResult is null) throw new SecurityConflictException("memory_source_changed");
            }
            if (args.Tool == ToolCatalog.MemoryForget && items.Any(i => i.Status == MemoryItemStatus.Deleted)) throw new SecurityConflictException("memory_source_changed");
        }
        if (!args.ReadOnly && !inspection && existingResult is null)
        {
            if (items.Count == 0 || items.Any(i => i.OwnerKey != items[0].OwnerKey || i.Scope != items[0].Scope || i.Kind != items[0].Kind)
                || args.Kind is { } kind && items[0].Kind != kind) throw new ArgumentException("Select exact same-owner, same-scope and same-kind sources.");
            if (args.Tool == ToolCatalog.MemoryConsolidate)
            {
                var context = SessionMemoryPrompt.CreateAdmissionContext("learned", reader.Effective, null, reader.Session.Snapshot.Entries);
                var (_, _, key, _) = StructuredMemoryService.ValidateContent(args.Kind!.Value, args.Subject!, args.Content!, [], context);
                if (owned.Any(i => i.OwnerKey == items[0].OwnerKey && i.Kind == items[0].Kind && i.SubjectKey == key && i.Status == MemoryItemStatus.Active && !items.Any(source => source.MemoryId == i.MemoryId)))
                    throw new SecurityConflictException("memory_subject_conflict");
            }
        }
        var sourceJson = JsonSerializer.Serialize(items.Select(i => new PreparedMemorySource(i.MemoryId, i.Revision, i.ContentHash)));
        Guid? resultId = args.Tool == ToolCatalog.MemoryConsolidate ? ResultId(reader, args, items[0]) : null;
        var value = new PreparedMemoryOperation(reader.Run.SessionId, args.Tool, args.CanonicalArguments, sourceJson, resultId,
            Hash(JsonSerializer.Serialize(new { args.CanonicalArguments, sourceJson, resultId })), reader.Root.SessionId, reader.User?.PrincipalId, JsonSerializer.Serialize(items.Select(i => new {
                memoryId = i.MemoryId, scope = TenantMemoryRules.ScopeName(i.Scope), kind = i.Kind.ToString(), subject = i.Subject })));
        if (expected is not null && !inspection && value != expected) throw new SecurityConflictException("memory_source_changed");
        return new(reader, args, Array.AsReadOnly(items.ToArray()), inspection ? expected! : value, more, existingResult);
    }
    public static Guid ResultId(TenantMemoryReader reader, TenantMemoryArguments args, TenantLearnedMemory source) => new(SHA256.HashData(Encoding.UTF8.GetBytes(
        $"tenant-memory-consolidation:v1:{reader.Security.Tenant.TenantId:D}:{source.OwnerKey}:" + args.CanonicalArguments)).AsSpan(0, 16));
    public static PreparedMemorySource[] Sources(PreparedMemoryOperation value)
    {
        if (Encoding.UTF8.GetByteCount(value.SourcesJson) > 8192) throw new ArgumentException("Use bounded exact Memory witnesses.");
        var sources = JsonSerializer.Deserialize<PreparedMemorySource[]>(value.SourcesJson) ?? throw new ArgumentException("Memory source witnesses are required.");
        if (sources.Length > 11 || sources.Select(s => s.MemoryId).Distinct().Count() != sources.Length || sources.Any(s => s.MemoryId == Guid.Empty || s.Revision < 1 || !ValidHash(s.ContentHash))
            || JsonSerializer.Serialize(sources) != value.SourcesJson) throw new ArgumentException("Use canonical immutable Memory source witnesses.");
        return sources;
    }
    public static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public static bool ValidHash(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}
