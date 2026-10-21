using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentCore.Application.Agents;
using AgentCore.Application.Memory;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Memory;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed record TenantMemoryReader(AgentRun Run, AgentRun Root, TenantSecurityState Security, TenantAgent Agent,
    AgentDefinition Current, TenantSession Session, TenantSession RootSession, TenantUser? User)
{
    public AgentDefinition Effective => StateIsolationAccess.RestrictDefinition(Run.Admission.Configuration!.Definition, Current);
}

public static class TenantMemoryRules
{
    public const int CandidateCount = MemoryProposalCodec.MaxProposalsPerTurn * 3;
    public static string Hash(MemoryKind kind, string subject, string content) => Convert.ToHexStringLower(SHA256.HashData(
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { kind, subject, content }))));
    public static string ScopeName(MemoryScope scope) => scope switch { MemoryScope.Session => "session", MemoryScope.IdentityUser => "identityUser", _ => "user" };
    public static IReadOnlyList<string> OwnerKeys(TenantMemoryReader reader) => reader.User is { } user
        ? [$"session:{reader.Run.SessionId:D}:{user.PrincipalId:D}", $"identityUser:{reader.Run.AgentInstanceId:D}:{user.PrincipalId:D}", $"user:{user.PrincipalId:D}"]
        : [$"session:{reader.Run.SessionId:D}:{reader.Agent.PrincipalId:D}"];
    public static bool ScopeEnabled(MemoryPolicy? policy, MemoryScope scope, bool write) => policy?.SessionMemory == true && scope switch {
        MemoryScope.Session => true,
        MemoryScope.IdentityUser => write ? policy.IdentityUserPromotion : policy.IdentityUserRetrieval,
        MemoryScope.User => write ? policy.UserPromotion : policy.UserRetrieval, _ => false };
    public static IReadOnlyList<TenantLearnedMemory> Project(IEnumerable<TenantLearnedMemory> values)
    {
        var unique = values.Where(i => i.Status == MemoryItemStatus.Active && i.ContentHash == Hash(i.Kind, i.Subject, i.Content))
            .OrderBy(i => i.Scope).ThenByDescending(i => i.UpdatedAt).ThenBy(i => i.MemoryId)
            .GroupBy(i => (i.Kind, i.SubjectKey)).Select(g => g.First()).ToArray();
        var cross = new List<TenantLearnedMemory>(); var characters = 0;
        foreach (var scope in new[] { MemoryScope.IdentityUser, MemoryScope.User })
            foreach (var item in unique.Where(i => i.Scope == scope).Take(3))
                if (characters + item.Subject.Length + item.Content.Length <= MemoryLimits.PromptMaxCharacters)
                { cross.Add(item); characters += item.Subject.Length + item.Content.Length; }
        var session = new List<TenantLearnedMemory>();
        foreach (var item in unique.Where(i => i.Scope == MemoryScope.Session))
            if (session.Count + cross.Count < MemoryLimits.PromptMaxItems && characters + item.Subject.Length + item.Content.Length <= MemoryLimits.PromptMaxCharacters)
            { session.Add(item); characters += item.Subject.Length + item.Content.Length; }
        return Array.AsReadOnly(session.Concat(cross).ToArray());
    }
    public static void Match(TenantMemoryPlan actual, TenantMemoryPlan proposed, SessionSnapshot snapshot)
    {
        static string Receipts(IReadOnlyList<MemoryReceipt>? receipts) => JsonSerializer.Serialize(receipts?.Select(r => new {
            r.Outcome, r.Operation, r.Source, r.Subject, r.Scopes, r.Presentation, r.Owner }));
        if (JsonSerializer.Serialize(actual) != JsonSerializer.Serialize(proposed)
            || Receipts(actual.Receipts) != Receipts(proposed.Receipts)
            || Receipts(snapshot.Entries.SingleOrDefault(e => e.Role == ConversationRole.Assistant && e.ResponseId == proposed.ResponseId)?.Envelope?.MemoryReceipts)
                != Receipts(proposed.Receipts)) throw new AccessDeniedException("memory_learning_changed");
    }
    public static bool Owns(TenantMemoryReader reader, TenantLearnedMemory item) => Owns(reader, item.Reference());
    public static bool Owns(TenantMemoryReader reader, TenantMemoryReference item) => item.TenantId == reader.Security.Tenant.TenantId
        && item.UserPrincipalId == reader.User?.PrincipalId && item.AccountId == reader.User?.AccountId && item.UserId == reader.User?.UserId && item.Scope switch {
            MemoryScope.Session => item.SessionId == reader.Run.SessionId && item.ExecutorPrincipalId == reader.Run.Owner.ExecutorPrincipalId,
            MemoryScope.IdentityUser => reader.User is not null && item.AgentInstanceId == reader.Run.AgentInstanceId,
            MemoryScope.User => reader.User is not null, _ => false };
    public static async ValueTask<bool> PermissionAsync(TenantMemoryReader reader, string key, AccessScope scope, IamAuthorizer iam, CancellationToken ct)
    {
        var permission = PermissionCatalog.Get(key); var state = reader.Security;
        if (!(await iam.CheckOwnedTenantAsync(state, reader.Agent.PrincipalId, permission, scope, ct).ConfigureAwait(false)).Allowed) return false;
        var policy = TenantOperationPolicyService.Effective(state, permission);
        return policy.Mode != OperationApprovalMode.Always && (policy.Mode != OperationApprovalMode.WhenRequesterLacksPermission
            || reader.Run.Admission.ExecutionContext?.OriginalInitiatorPrincipalId is { } user
                && (await iam.CheckOwnedTenantAsync(state, user, permission, scope, ct).ConfigureAwait(false)).Allowed);
    }
    public static async ValueTask<bool> SourceAllowedAsync(TenantMemoryReader reader, TenantLearnedMemory item, bool write, IamAuthorizer iam, CancellationToken ct, bool allowDeleted = false)
        => await SourceAllowedAsync(reader, item.Reference(), write, iam, ct, allowDeleted).ConfigureAwait(false);
    public static async ValueTask<bool> SourceAllowedAsync(TenantMemoryReader reader, TenantMemoryReference item, bool write, IamAuthorizer iam, CancellationToken ct, bool allowDeleted = false)
    {
        if (!Owns(reader, item) || item.Status == MemoryItemStatus.Deleted && !allowDeleted || !reader.Security.SystemIdentities.Any(i => i.TenantId == item.TenantId
            && i.AgentInstanceId == item.AgentInstanceId && i.PrincipalId == item.ExecutorPrincipalId && i.Kind == SystemIdentityKind.Agent)) return false;
        var tenant = item.TenantId;
        if (!await PermissionAsync(reader, write ? "memory.manage" : "memory.read", AccessScope.ForResource(tenant, "agent", item.AgentInstanceId.ToString("D")), iam, ct).ConfigureAwait(false)) return false;
        // Promotion creates an independently owned learned fact. Provenance does not grant
        // source-conversation access or make long-term memory disappear with that conversation.
        if (item.Scope != MemoryScope.Session) return true;
        if (!await PermissionAsync(reader, "sessions.read", TenantSessionRules.Scope(tenant, item.SessionId), iam, ct).ConfigureAwait(false)) return false;
        return reader.User is null || (await iam.CheckOwnedTenantAsync(reader.Security, reader.User.PrincipalId, PermissionCatalog.Get("sessions.read"),
            TenantSessionRules.Scope(tenant, item.SessionId), ct).ConfigureAwait(false)).Allowed;
    }
    public static async ValueTask<bool> AvailableAsync(TenantMemoryReader reader, bool write, IamAuthorizer iam, CancellationToken ct)
    {
        if (reader.Security.Tenant.Status != IdentityStatus.Active || reader.Agent.Instance.Lifecycle != AgentInstanceLifecycle.Active
            || reader.Run.Admission.Configuration is null || reader.Run.Owner.ExecutorPrincipalId != reader.Agent.PrincipalId
            || reader.Session.Owner.ExecutorPrincipalId != reader.Agent.PrincipalId || reader.Session.Snapshot.ProfileId is not null
            || reader.RootSession.Snapshot.ProfileId is not null || reader.Root.Admission.ExecutionContext?.Origin == ExecutionOrigin.UserMessage && reader.User is null) return false;
        TenantSessionRules.RequireExecutor(reader.Security, reader.Agent);
        foreach (var id in new[] { reader.Run.SessionId, reader.Root.SessionId }.Distinct())
            if (!await PermissionAsync(reader, "sessions.read", TenantSessionRules.Scope(reader.Security.Tenant.TenantId, id), iam, ct).ConfigureAwait(false)) return false;
        if (reader.User is { } user)
            foreach (var (permission, target) in new[] { ("sessions.read", TenantSessionRules.Scope(user.TenantId, reader.Root.SessionId)),
                ("agents.chat", AccessScope.ForResource(user.TenantId, "agent", reader.Run.AgentInstanceId.ToString("D"))) })
                if (!(await iam.CheckTenantAsync(reader.Security, user.PrincipalId, PermissionCatalog.Get(permission), target, ct).ConfigureAwait(false)).Allowed) return false;
        return await PermissionAsync(reader, write ? "memory.manage" : "memory.read", AccessScope.ForResource(reader.Security.Tenant.TenantId,
            "agent", reader.Run.AgentInstanceId.ToString("D")), iam, ct).ConfigureAwait(false);
    }
    public static TenantMemoryPlan Plan(TenantRuntimeFrame frame, TenantMemoryReader reader, IReadOnlyList<TenantLearnedMemory> existing,
        IReadOnlySet<Guid> writable, IReadOnlyList<MemoryProposal> proposals, IReadOnlyList<Guid> candidates, Guid audit, DateTimeOffset at, bool allowed,
        IReadOnlyList<ConversationEntry> entries)
    {
        if (proposals.Count > MemoryProposalCodec.MaxProposalsPerTurn || candidates.Count != CandidateCount || candidates.Any(id => id == Guid.Empty)
            || candidates.Distinct().Count() != candidates.Count || audit == Guid.Empty || at.Offset != TimeSpan.Zero
            || at != DateTimeOffset.FromUnixTimeMilliseconds(at.ToUnixTimeMilliseconds()) || at.ToUnixTimeMilliseconds() < reader.Run.CreatedAtUtc.ToUnixTimeMilliseconds())
            throw new ArgumentException("Bounded owned memory proposals and exact candidate identities are required.");
        var rows = existing.ToDictionary(i => i.MemoryId); var changes = new List<TenantMemoryChange>(); var results = new List<MemoryAdmissionResult>();
        var sourceEntry = reader.User is null ? (Guid?)null : reader.Root.Admission.Activation.SourceEntryIds.FirstOrDefault();
        if (sourceEntry == Guid.Empty) allowed = false;
        var policy = reader.Effective.MemoryPolicy;
        var admission = SessionMemoryPrompt.CreateAdmissionContext("learned", reader.Effective, null, entries);
        for (var index = 0; index < proposals.Count; index++)
        {
            var proposal = proposals[index]; var scopes = new List<string>();
            if (!MemoryProposalCodec.IsConversationalSource(proposal.Source) || reader.User is null && proposal.Source == MemoryProposalSource.UserExplicit || !Enum.IsDefined(proposal.Operation)
                || !Enum.IsDefined(proposal.Kind) || proposal.ScopeHint is { } hint && !Enum.IsDefined(hint))
            { results.Add(new(MemoryAdmissionStatus.Rejected, proposal)); continue; }
            if (!allowed || policy?.SessionMemory != true) { results.Add(new(MemoryAdmissionStatus.Unavailable, proposal)); continue; }
            if (proposal.Operation == MemoryProposalOperation.Delete) { results.Add(new(MemoryAdmissionStatus.ApprovalRequired, proposal)); continue; }
            var subject = StructuredMemoryItem.CollapseSubject(proposal.Subject); var key = StructuredMemoryItem.SubjectKeyFor(subject); var body = proposal.Content.Trim();
            try
            {
                if (proposal.Operation == MemoryProposalOperation.Upsert)
                    (subject, body, key, _) = StructuredMemoryService.ValidateContent(proposal.Kind, subject, body, sourceEntry is { } entry ? [entry] : [], admission);
                else if (proposal.Kind != MemoryKind.OpenLoop || subject.Length is 0 or > MemoryLimits.MaxSubjectCharacters) throw new ArgumentException("An exact open loop is required.");
            }
            catch (Exception error) when (error is AgentCoreException or ArgumentException)
            { results.Add(new(MemoryAdmissionStatus.Rejected, proposal)); continue; }
            var status = MemoryAdmissionStatus.NotFound; var changed = false;
            foreach (var scope in Enum.GetValues<MemoryScope>())
            {
                var selected = scope == MemoryScope.Session || reader.User is not null && (scope == MemoryScope.IdentityUser
                    ? proposal.ScopeHint != MemoryScopeHint.Session : proposal.ScopeHint == MemoryScopeHint.User);
                if (!selected || !ScopeEnabled(policy, scope, proposal.Operation != MemoryProposalOperation.Resolve)) continue;
                var draft = new TenantLearnedMemory(reader.Security.Tenant.TenantId, candidates[index * 3 + (int)scope], scope, reader.User?.PrincipalId,
                    reader.User?.AccountId, reader.User?.UserId, reader.Run.AgentInstanceId, reader.Agent.PrincipalId, reader.Run.SessionId, reader.Run.AgentRunId, reader.Root.AgentRunId,
                    sourceEntry, proposal.Kind, MemoryItemStatus.Active, subject, body, key, ScopeSource(proposal.Source), Hash(proposal.Kind, subject, body), 1, at, at);
                var old = rows.Values.SingleOrDefault(i => i.OwnerKey == draft.OwnerKey && i.Kind == proposal.Kind && i.SubjectKey == key
                    && i.Status == MemoryItemStatus.Active);
                if (proposal.Operation == MemoryProposalOperation.Resolve)
                    old ??= rows.Values.FirstOrDefault(i => i.OwnerKey == draft.OwnerKey && i.Kind == proposal.Kind && i.SubjectKey == key && i.Status == MemoryItemStatus.Resolved);
                if (old is not null && !writable.Contains(old.MemoryId) && old.RunId != frame.RunId) continue;
                if (old is not null && at < old.UpdatedAt) throw new AccessDeniedException("memory_learning_changed");
                if (proposal.Operation == MemoryProposalOperation.Resolve)
                {
                    if (old is null) continue;
                    if (old.Status == MemoryItemStatus.Resolved) { scopes.Add(ScopeName(scope)); continue; }
                    var resolved = old with { Status = MemoryItemStatus.Resolved, Revision = old.Revision + 1, UpdatedAt = at };
                    changes.Add(new(resolved, old.Revision)); rows[old.MemoryId] = resolved; scopes.Add(ScopeName(scope)); changed = true; continue;
                }
                if (old is null && rows.Values.Count(i => i.OwnerKey == draft.OwnerKey && i.Status == MemoryItemStatus.Active) >= MemoryLimits.MaxActiveItems)
                { if (scope == MemoryScope.Session) break; continue; }
                scopes.Add(ScopeName(scope));
                if (old?.Content == body) { if (scope == MemoryScope.Session) status = MemoryAdmissionStatus.AlreadyStored; continue; }
                if (old is not null)
                {
                    var superseded = old with { Status = MemoryItemStatus.Superseded, Revision = old.Revision + 1, UpdatedAt = at };
                    changes.Add(new(superseded, old.Revision)); rows[old.MemoryId] = superseded; draft = draft with { SupersedesMemoryId = old.MemoryId };
                }
                changes.Add(new(draft, 0)); rows.Add(draft.MemoryId, draft); changed = true;
                if (scope == MemoryScope.Session) status = old is null ? MemoryAdmissionStatus.Stored : MemoryAdmissionStatus.Updated;
            }
            if (proposal.Operation == MemoryProposalOperation.Resolve) status = changed ? MemoryAdmissionStatus.Resolved : scopes.Count > 0 ? MemoryAdmissionStatus.AlreadyResolved : MemoryAdmissionStatus.NotFound;
            else if (scopes.Count == 0) status = MemoryAdmissionStatus.Rejected;
            else if (scopes.Count == 1) status = status switch { MemoryAdmissionStatus.Stored => MemoryAdmissionStatus.StoredSessionOnly,
                MemoryAdmissionStatus.Updated => MemoryAdmissionStatus.UpdatedSessionOnly, _ => status };
            results.Add(new(status, proposal, scopes.ToArray()));
        }
        return new(frame, reader.Run.ResponseId ?? throw new AccessDeniedException("memory_learning_changed"), at, audit, Array.AsReadOnly(proposals.ToArray()), Array.AsReadOnly(candidates.ToArray()),
            Array.AsReadOnly(changes.ToArray()), Array.AsReadOnly(MemoryReceiptProjection.FromResults(results).Select(r => r with {
                Owner = new(reader.Security.Tenant.TenantId, reader.Run.AgentInstanceId, reader.User?.PrincipalId) }).ToArray()));
    }
    private static string ScopeSource(MemoryProposalSource source) => source == MemoryProposalSource.UserExplicit ? "userExplicit" : "agentInferred";
}
