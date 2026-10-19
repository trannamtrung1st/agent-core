using AgentCore.Application.Events;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed record TenantConversationHistory(IReadOnlyList<PublicHistoryEntry> Items, long NextAfter, bool HasMore,
    bool HasOlder, long? NextBefore, long SessionRevision, long SecurityRevision);
public sealed record TenantConversationRecipient(TenantSessionRoot Owner, long SessionRevision, long SecurityRevision,
    SessionMode Mode, SessionStatus Status, SessionLifecycleStatus LifecycleStatus, PublicAgentDescriptor Agent, SessionModelSelection? Model = null);
public sealed record TenantConversationView(TenantSessionRoot Owner, string Title, long Revision, long SecurityRevision,
    SessionMode Mode, SessionStatus Status, SessionLifecycleStatus LifecycleStatus, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    PublicAgentDescriptor Agent, SessionModelSelection? Model);

public sealed class TenantConversationService(IIamStore security, ITenantSessionStore sessions, ITenantConversationStore content,
    IdentityService identity, IamAuthorizer authorizer, TenantArtifactService? artifactAccess = null, TenantAttachmentService? attachmentAccess = null)
{
    public async ValueTask<bool> CanReadArtifactReferenceAsync(Guid tenant, Guid session, TenantSessionInitiator human, string? reference, CancellationToken ct = default)
    {
        if (artifactAccess is null || !Guid.TryParse(reference, out var id) || id == Guid.Empty) return false;
        try { await artifactAccess.ReadAsync(tenant, session, id, human, ct).ConfigureAwait(false); return true; }
        catch (AccessDeniedException) { return false; }
    }
    private async ValueTask<AttachmentRecord?> ReadAttachmentReferenceAsync(Guid tenant, Guid session, TenantSessionInitiator human, string? reference, CancellationToken ct)
    {
        if (attachmentAccess is null || !Guid.TryParse(reference, out var id) || id == Guid.Empty) return null;
        try { var source = await attachmentAccess.ReadAsync(tenant, session, id, human, ct).ConfigureAwait(false); return source.State == AttachmentState.Bound ? source : null; }
        catch (AccessDeniedException) { return null; }
    }
    public async ValueTask<TenantConversationRecipient> AuthorizeRecipientAsync(Guid tenant, Guid session, TenantSessionInitiator human, CancellationToken ct = default)
    {
        var state = await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, human, ct).ConfigureAwait(false);
        var saved = await RequireAsync(state, session, human, ct).ConfigureAwait(false);
        if (!saved.Snapshot.Surfaces.HasFlag(SessionSurface.ChatList)) throw new AccessDeniedException("resource_unavailable");
        var current = await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, human, ct).ConfigureAwait(false);
        if (current.Tenant.Revision != state.Tenant.Revision) throw new SecurityConflictException("stale_revision");
        return new(saved.Owner, saved.Snapshot.Revision, state.Tenant.Revision, saved.Snapshot.Mode, saved.Snapshot.Status,
            saved.Snapshot.LifecycleStatus, PublicHistory.FromSnapshot(saved.Snapshot, voiceAvailable: false), saved.Snapshot.ModelSelection);
    }
    public async ValueTask<TenantConversationView> ReadViewAsync(Guid tenant, Guid session, TenantSessionInitiator human, CancellationToken ct = default)
    {
        var state = await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, human, ct).ConfigureAwait(false);
        var saved = await RequireAsync(state, session, human, ct).ConfigureAwait(false);
        var current = await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, human, ct).ConfigureAwait(false);
        if (current.Tenant.Revision != state.Tenant.Revision) throw new SecurityConflictException("stale_revision");
        var s = saved.Snapshot;
        return new(saved.Owner, s.Title, s.Revision, state.Tenant.Revision, s.Mode, s.Status, s.LifecycleStatus, s.CreatedAt, s.UpdatedAt,
            PublicHistory.FromSnapshot(s, voiceAvailable: false), s.ModelSelection);
    }
    public async ValueTask<TenantConversationHistory> ReadAsync(Guid tenant, Guid session, TenantSessionInitiator human,
        long? after = null, long? before = null, int limit = 50, CancellationToken ct = default)
    {
        TenantConversationRules.RequirePaging(after, before, limit);
        var state = await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, human, ct).ConfigureAwait(false);
        var saved = await RequireAsync(state, session, human, ct).ConfigureAwait(false);
        var page = await content.ReadTenantConversationAsync(tenant, session, after, before, limit, ct).ConfigureAwait(false)
            ?? throw new AccessDeniedException("resource_unavailable");
        if (page.Owner != saved.Owner || page.SessionRevision != saved.Snapshot.Revision) throw new SecurityConflictException("stale_revision");
        var visible = new List<PublicHistoryEntry>(page.History.Items.Count);
        var cache = new Dictionary<(string Permission, string Type, string Id), bool>();
        var sources = new Dictionary<string, AttachmentRecord?>(StringComparer.Ordinal);
        async ValueTask<AttachmentRecord?> Attachment(string? id)
        {
            if (id is null) return null;
            if (!sources.TryGetValue(id, out var source)) { source = await ReadAttachmentReferenceAsync(tenant, session, human, id, ct).ConfigureAwait(false); sources.Add(id, source); }
            return source;
        }
        async ValueTask<bool> CanRead(string permission, string type, string? id)
        {
            if (string.IsNullOrWhiteSpace(id)) return false;
            var key = (permission, type, id);
            if (!cache.TryGetValue(key, out var allowed))
            {
                allowed = type == "artifact" ? await CanReadArtifactReferenceAsync(tenant, session, human, id, ct).ConfigureAwait(false)
                    : type == "attachment" ? await Attachment(id).ConfigureAwait(false) is not null
                    : (await authorizer.CheckTenantAsync(state, human.PrincipalId, PermissionCatalog.Get(permission),
                        AccessScope.ForResource(tenant, type, id), ct).ConfigureAwait(false)).Allowed;
                cache.Add(key, allowed);
            }
            return allowed;
        }
        foreach (var entry in page.History.Items)
        {
            var projected = PublicHistory.FromEntry(entry);
            var blocks = new List<PublicResponseBlock>();
            foreach (var block in projected.Blocks)
                if (block.Kind == "unknown" && block.AttachmentId is null && block.ArtifactId is null && block.Text == block.FallbackText
                        && block.FallbackText is "[Unavailable artifact]" or "[Unsupported content]"
                    || block.Kind == "markdown" && block.AttachmentId is null && block.ArtifactId is null
                    || block.Kind == "attachment" && block.ArtifactId is null && await CanRead("attachments.read", "attachment", block.AttachmentId).ConfigureAwait(false)
                    || block.Kind == "artifact" && block.AttachmentId is null && await CanRead("artifacts.read", "artifact", block.ArtifactId).ConfigureAwait(false))
                    blocks.Add(block.Kind == "artifact" ? block with { ArtifactId = Guid.Parse(block.ArtifactId!).ToString("D"), Text = "", FallbackText = "File" }
                        : block.Kind == "attachment" ? block with { AttachmentId = Guid.Parse(block.AttachmentId!).ToString("D"), Text = "", FallbackText = "File" } : block);
            var attachments = new List<PublicHistoryAttachment>();
            foreach (var attachment in projected.Attachments ?? [])
                if (await Attachment(attachment.AttachmentId.ToString("D")).ConfigureAwait(false) is { } attachmentSource)
                    attachments.Add(new(attachmentSource.AttachmentId, attachmentSource.DisplayName, attachmentSource.ContentType));
            // Structured resource parts remain unconverted. Do not expose legacy resource labels,
            // locators or untyped effect receipts as implicit authority for their underlying resources.
            var parts = projected.Parts?.Select(p => p.Kind == "text" ? p : new UserMessagePart("text", "[Resource unavailable]")).ToArray();
            visible.Add(projected with {
                SourceEventId = projected.SourceEventId is { } source && await CanRead("core-events.read", "event", source.ToString("D")).ConfigureAwait(false) ? source : null,
                Blocks = blocks.ToArray(), Attachments = attachments.Count == 0 ? null : attachments.ToArray(),
                Parts = parts, Text = parts is null ? projected.Text : UserMessageContent.DisplayText(parts),
                MemoryReceipts = await CanRead("memory.read", "agent", saved.Owner.AgentInstanceId.ToString("D")).ConfigureAwait(false) ? projected.MemoryReceipts : null,
                EffectReceipts = null
            });
        }
        // Storage and per-resource checks may await I/O. Recheck the actual human, current IAM and owning
        // Session before returning bytes; a cached projection must not outlive permission loss or deletion.
        var current = await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, human, ct).ConfigureAwait(false);
        var final = await RequireAsync(current, session, human, ct).ConfigureAwait(false);
        if (current.Tenant.Revision != state.Tenant.Revision || final.Owner != page.Owner || final.Snapshot.Revision != page.SessionRevision)
            throw new SecurityConflictException("stale_revision");
        if ((await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, human, ct).ConfigureAwait(false)).Tenant.Revision != current.Tenant.Revision)
            throw new SecurityConflictException("stale_revision");
        return new(visible.ToArray(), page.History.NextAfter, page.History.HasMore, page.History.HasOlder, page.History.NextBefore,
            page.SessionRevision, current.Tenant.Revision);
    }

    private async ValueTask<TenantSession> RequireAsync(TenantSecurityState state, Guid session, TenantSessionInitiator human, CancellationToken ct)
    {
        var saved = await sessions.FindTenantSessionAsync(state.Tenant.TenantId, session, ct).ConfigureAwait(false)
            ?? throw new AccessDeniedException("resource_unavailable");
        foreach (var (permission, scope) in new[] {
            ("sessions.read", TenantSessionRules.Scope(saved.Owner.TenantId, session)),
            ("agents.chat", AccessScope.ForResource(saved.Owner.TenantId, "agent", saved.Owner.AgentInstanceId.ToString("D"))) })
        {
            var decision = await authorizer.CheckTenantAsync(state, human.PrincipalId, PermissionCatalog.Get(permission), scope, ct).ConfigureAwait(false);
            if (!decision.Allowed) throw new AccessDeniedException(decision.ReasonCode);
        }
        return saved;
    }
}

public static class TenantConversationRules
{
    public static void RequirePaging(long? after, long? before, int limit)
    {
        if (after is not null && before is not null || after is < 0 || before is < 1 || limit is < 1 or > 100)
            throw AgentCoreErrors.Validation("History cursor is invalid.");
    }
}
