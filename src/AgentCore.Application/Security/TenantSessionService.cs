using AgentCore.Application.Agents;
using AgentCore.Application.Models;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed record TenantSessionMetadata(TenantSessionRoot Owner, string Title, SessionMode Mode, SessionStatus Status,
    long Revision, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, SessionLifecycleStatus LifecycleStatus, DateTimeOffset? ArchivedAt);
public sealed record TenantSessionMetadataPage(IReadOnlyList<TenantSessionMetadata> Items, int Total, int Page, int PageSize);

public sealed partial class TenantSessionService(IIamStore security, ITenantAgentStore agents, ITenantSessionStore sessions, ITenantRunStore runs,
    ITenantDefinitionStore definitions, IBuiltInAgentDefinitionStore builtIns, IdentityService identity, IamAuthorizer authorizer,
    IModelCatalog models, VoiceAvailability voice, IIdGenerator ids, TimeProvider time)
{
    public async ValueTask<TenantSessionMetadata> CreateAsync(Guid tenant, Guid instance, TenantSessionInitiator initiator, SessionMode mode,
        long expectedSecurityRevision, long expectedInstanceRevision, string? modelKey = null, string? reasoningEffort = null, CancellationToken ct = default)
    {
        var state = await Human(tenant, initiator, ct).ConfigureAwait(false);
        foreach (var key in new[] { "sessions.create", "sessions.read" }) await Require(state, initiator.PrincipalId, key, AccessScope.ForTenant(tenant), ct).ConfigureAwait(false);
        var parent = await agents.FindTenantAgentAsync(tenant, instance, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        await Require(state, initiator.PrincipalId, "agents.chat", AccessScope.ForResource(tenant, "agent", instance.ToString("D")), ct).ConfigureAwait(false);
        if (parent.Instance.Lifecycle != AgentInstanceLifecycle.Active) throw new AccessDeniedException("agent_archived");
        TenantSessionRules.RequireExecutor(state, parent);
        if (state.Tenant.Revision != expectedSecurityRevision || parent.Instance.Revision != expectedInstanceRevision) throw new SecurityConflictException("stale_revision");
        if (mode is not (SessionMode.Text or SessionMode.Voice)) throw AgentCoreErrors.Validation("Unknown Session mode.");
        var baseline = Guid.TryParse(parent.Instance.DefinitionId, out var definitionId)
            ? (await definitions.FindDefinitionPublicationAsync(tenant, definitionId, parent.Instance.ActiveVersion, ct).ConfigureAwait(false))?.Payload
            : await builtIns.GetAsync(parent.Instance.DefinitionId, parent.Instance.ActiveVersion, ct).ConfigureAwait(false);
        if (baseline is null) throw new AccessDeniedException("definition_unavailable");
        var definition = InstanceSettingsResolver.Resolve(baseline, parent.Instance.SettingsOverrides) with { SelfManagement = ManagementPolicyResolver.Resolve(baseline, parent.Instance) };
        if (mode == SessionMode.Voice && !voice.IsAvailable(definition, definition.ConversationPolicy.Language)) throw AgentCoreErrors.VoiceUnavailable();
        var now = DateTimeOffset.FromUnixTimeMilliseconds(time.GetUtcNow().ToUnixTimeMilliseconds()); var id = ids.NewSessionId();
        var snapshot = new SessionSnapshot(1, id, 1, definition, mode, null, SessionStatus.Created, [], "", 0, null, null, now, now, instance,
            LastUserActivityAt: now, Purpose: SessionPurpose.OngoingDefault, CompletionPolicy: SessionCompletionPolicy.Default,
            LifecycleSource: LifecycleTransitionSource.System, LifecycleChangedAt: now,
            ModelSelection: SessionModelBinder.Bind(models, modelKey, reasoningEffort, ModelSelectionSource.Host, definition.ModelDefaults),
            PinnedPersona: parent.Instance.Persona, PinnedPersonaRevision: parent.Instance.PersonaRevision);
        var owner = new TenantSessionRoot(tenant, id, instance, parent.PrincipalId, initiator.PrincipalId, now);
        var next = state with { Tenant = state.Tenant with { Revision = checked(state.Tenant.Revision + 1) } };
        var audit = new AuthorizationAudit(ids.NewId(), tenant, initiator.PrincipalId, "sessions.create", id.ToString("D"), "allowed", "session_created", now);
        if (!await sessions.TryCreateTenantSessionAsync(next, state.Tenant.Revision, owner, snapshot, initiator, parent.Instance.Revision, audit, ct).ConfigureAwait(false)) throw new SecurityConflictException("stale_revision");
        return Metadata(new(owner, snapshot));
    }
    public async ValueTask<TenantSessionMetadata> ReadAsync(Guid actor, Guid tenant, Guid id, CancellationToken ct = default)
    {
        var item = await sessions.FindTenantSessionAsync(tenant, id, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        await authorizer.RequireAsync(actor, "sessions.read", TenantSessionRules.Scope(tenant, id), ct).ConfigureAwait(false); return Metadata(item);
    }
    public async ValueTask<TenantSessionMetadataPage> ListAsync(Guid actor, Guid tenant, int page = 0, int pageSize = 30, CancellationToken ct = default)
    {
        var state = await security.ReadTenantAsync(tenant, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("tenant_unavailable");
        var decision = await authorizer.CheckTenantAsync(state, actor, PermissionCatalog.Get("sessions.read"), AccessScope.ForTenant(tenant), ct).ConfigureAwait(false);
        IReadOnlyList<Guid>? allowed = null;
        if (!decision.Allowed)
        {
            if (decision.ReasonCode != "permission_denied") throw new AccessDeniedException(decision.ReasonCode);
            var ids = new List<Guid>();
            foreach (var assignment in state.Assignments.Where(a => a.PrincipalId == actor && TenantSessionRules.TryId(a.Scope, out _)))
                if ((await authorizer.CheckTenantAsync(state, actor, PermissionCatalog.Get("sessions.read"), assignment.Scope, ct).ConfigureAwait(false)).Allowed && TenantSessionRules.TryId(assignment.Scope, out var id)) ids.Add(id);
            allowed = ids.Distinct().ToArray();
        }
        var result = await sessions.ListTenantSessionsAsync(tenant, allowed, Math.Clamp(page, 0, 100_000), Math.Clamp(pageSize, 1, 100), ct).ConfigureAwait(false);
        return new(result.Items.Select(Metadata).ToArray(), result.Total, result.Page, result.PageSize);
    }
    private static TenantSessionMetadata Metadata(TenantSession item) => new(item.Owner, item.Snapshot.Title, item.Snapshot.Mode, item.Snapshot.Status,
        item.Snapshot.Revision, item.Snapshot.CreatedAt, item.Snapshot.UpdatedAt, item.Snapshot.LifecycleStatus, item.Snapshot.ArchivedAt);
    private ValueTask<TenantSecurityState> Human(Guid tenant, TenantSessionInitiator initiator, CancellationToken ct) =>
        TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, initiator, ct);
    private async ValueTask Require(TenantSecurityState state, Guid actor, string key, AccessScope scope, CancellationToken ct)
    { var decision = await authorizer.CheckTenantAsync(state, actor, PermissionCatalog.Get(key), scope, ct).ConfigureAwait(false); if (!decision.Allowed) throw new AccessDeniedException(decision.ReasonCode); }
}
