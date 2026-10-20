using System.Text.Json;
using AgentCore.Application.Events;
using AgentCore.Application.Ports;
using AgentCore.Domain.Events;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed record TenantWebhookList(IReadOnlyList<TenantWebhookWrite> Items, int Total, int Page, int PageSize, long SecurityRevision, bool CanCreate);
public sealed record TenantBuiltinEvent(string Key, string Description);
public sealed record TenantWebhookReceipts(TenantWebhookWrite Source, TenantWebhookReceiptPage Receipts);

public sealed class TenantWebhookService(IIamStore security, ITenantWebhookStore store, IdentityService identity,
    IamAuthorizer iam, IIdGenerator ids, TimeProvider time)
{
    public async ValueTask<IReadOnlyList<TenantBuiltinEvent>> BuiltinsAsync(Guid tenant, HumanSessionReference human, CancellationToken ct = default)
    {
        var state = await Human(tenant, human, ct); await Require(state, human.PrincipalId, "core-events.read", AccessScope.ForTenant(tenant), ct);
        var rows = CoreEventCatalog.Keys.Select(key => new TenantBuiltinEvent(key, BuiltInEventDefinitions.Description(key))).ToArray();
        await Fence(state, human, ct); return rows;
    }
    public async ValueTask<TenantWebhookList> ListAsync(Guid tenant, HumanSessionReference human, int page = 0, int pageSize = 20, CancellationToken ct = default)
    {
        TenantWebhookRules.Page(page, pageSize); var state = await Human(tenant, human, ct);
        IReadOnlyList<Guid>? allowed = null;
        var inherited = await iam.CheckTenantAsync(state, human.PrincipalId, PermissionCatalog.Get("webhooks.read"), AccessScope.ForTenant(tenant), ct);
        if (!inherited.Allowed)
        {
            if (inherited.ReasonCode != "permission_denied") throw new AccessDeniedException(inherited.ReasonCode);
            var readable = new List<Guid>();
            foreach (var scope in state.Assignments.Where(a => a.PrincipalId == human.PrincipalId && a.Scope.ResourceType == "webhook").Select(a => a.Scope).Distinct())
                if (TenantWebhookRules.TryId(scope, out var id) && (await iam.CheckTenantAsync(state, human.PrincipalId, PermissionCatalog.Get("webhooks.read"), scope, ct)).Allowed) readable.Add(id);
            allowed = readable;
        }
        var values = await store.ListTenantWebhooksAsync(tenant, allowed, page, pageSize, ct); var items = new List<TenantWebhookWrite>();
        foreach (var source in values.Items) items.Add(new(source, await CanManage(state, human.PrincipalId, TenantWebhookRules.Scope(tenant, source.ResourceId), ct), state.Tenant.Revision));
        var create = await CanManage(state, human.PrincipalId, AccessScope.ForTenant(tenant), ct); await Fence(state, human, ct);
        return new(items, values.Total, values.Page, values.PageSize, state.Tenant.Revision, create);
    }

    public async ValueTask<TenantWebhookWrite> ReadAsync(Guid tenant, Guid source, HumanSessionReference human, CancellationToken ct = default)
    {
        var state = await Human(tenant, human, ct); var saved = await Read(state, source, human.PrincipalId, ct);
        var result = new TenantWebhookWrite(saved, await CanManage(state, human.PrincipalId, TenantWebhookRules.Scope(tenant, source), ct), state.Tenant.Revision);
        await Fence(state, human, ct); return result;
    }

    public async ValueTask<TenantWebhookReceipts> ReceiptsAsync(Guid tenant, Guid source, HumanSessionReference human, int page = 0, int pageSize = 20, CancellationToken ct = default)
    {
        TenantWebhookRules.Page(page, pageSize); var state = await Human(tenant, human, ct); var saved = await Read(state, source, human.PrincipalId, ct);
        var rows = await store.ListTenantWebhookReceiptsAsync(tenant, source, page, pageSize, ct);
        var view = new TenantWebhookWrite(saved, await CanManage(state, human.PrincipalId, TenantWebhookRules.Scope(tenant, source), ct), state.Tenant.Revision);
        await Fence(state, human, ct); return new(view, rows);
    }

    public async ValueTask<TenantWebhookToken> CreateAsync(Guid tenant, HumanSessionReference human, string name, string key, long expectedRevision, CancellationToken ct = default)
    {
        var state = await Human(tenant, human, ct); Revision(state, expectedRevision); await Require(state, human.PrincipalId, "webhooks.manage", AccessScope.ForTenant(tenant), ct);
        var token = WebhookTokens.Create(); var at = AgentCore.Application.Triggers.TriggerScheduleCalculator.Truncate(time.GetUtcNow());
        var source = new WebhookEvent(ids.NewId(), name, WebhookEventKind.Webhook, key, WebhookTokens.Hash(token), WebhookEventStatus.Active, 1, at, at, tenant, human.PrincipalId);
        await Save(state, source, 0, human, "webhook_created", ct); return new(new(source, true, state.Tenant.Revision + 1), token);
    }

    public async ValueTask<TenantWebhookWrite> RenameAsync(Guid tenant, Guid id, HumanSessionReference human, string name, long expectedRevision, long expectedSourceRevision, CancellationToken ct = default)
    {
        var (state, source) = await Change(tenant, id, human, expectedRevision, expectedSourceRevision, ct);
        var next = Copy(source, name, source.CredentialHash, source.Status);
        await Save(state, next, source.Revision, human, "webhook_renamed", ct); return new(next, true, state.Tenant.Revision + 1);
    }

    public async ValueTask<TenantWebhookToken> RotateAsync(Guid tenant, Guid id, HumanSessionReference human, long expectedRevision, long expectedSourceRevision, CancellationToken ct = default)
    {
        var (state, source) = await Change(tenant, id, human, expectedRevision, expectedSourceRevision, ct);
        var token = WebhookTokens.Create(); var next = Copy(source, source.DisplayName, WebhookTokens.Hash(token), WebhookEventStatus.Active);
        await Save(state, next, source.Revision, human, "webhook_token_rotated", ct); return new(new(next, true, state.Tenant.Revision + 1), token);
    }

    public async ValueTask<TenantWebhookWrite> RevokeAsync(Guid tenant, Guid id, HumanSessionReference human, long expectedRevision, long expectedSourceRevision, CancellationToken ct = default)
    {
        var (state, source) = await Change(tenant, id, human, expectedRevision, expectedSourceRevision, ct);
        var next = Copy(source, source.DisplayName, null, WebhookEventStatus.Revoked);
        await Save(state, next, source.Revision, human, "webhook_revoked", ct); return new(next, true, state.Tenant.Revision + 1);
    }

    public async ValueTask<bool> CredentialsMatchAsync(Guid tenant, Guid source, string token, CancellationToken ct = default)
    {
        var state = await security.ReadTenantAsync(tenant, ct); var saved = await store.FindTenantWebhookAsync(tenant, source, ct);
        return state?.Tenant.Status == IdentityStatus.Active && saved?.Status == WebhookEventStatus.Active && WebhookTokens.Matches(saved.CredentialHash, token);
    }

    public async ValueTask<ExternalEventIngressResult> AdmitAsync(Guid tenant, Guid source, string token, ReadOnlyMemory<byte> body, CancellationToken ct = default)
    {
        if (!await CredentialsMatchAsync(tenant, source, token, ct)) return new(ExternalEventIngressKind.Unauthorized, null, "unauthorized");
        if (!ExternalEventEnvelope.TryNormalize(body.Span, out var evidence, out var sourceEventId, out var occurredAt, out var error)) return new(ExternalEventIngressKind.Invalid, null, error);
        if (TenantWebhookRules.ContainsCredential(evidence, token)) return new(ExternalEventIngressKind.Invalid, null, "source_credential_in_payload");
        using var json = JsonDocument.Parse(evidence);
        if (json.RootElement.GetProperty("rootAgentRunId").ValueKind != JsonValueKind.Null || json.RootElement.GetProperty("triggerDepth").GetInt32() != 0)
            return new(ExternalEventIngressKind.Invalid, null, "untrusted_correlation");
        var at = AgentCore.Application.Triggers.TriggerScheduleCalculator.Truncate(time.GetUtcNow());
        var current = await store.FindTenantWebhookAsync(tenant, source, ct);
        if (current is null) return new(ExternalEventIngressKind.Unauthorized, null, "unauthorized");
        var receipt = new ExternalEvent(ids.NewId(), source, sourceEventId, occurredAt == DateTimeOffset.UnixEpoch ? at : AgentCore.Application.Triggers.TriggerScheduleCalculator.Truncate(occurredAt), at, evidence, tenant, current.Revision);
        var result = await store.TryAdmitTenantWebhookAsync(tenant, source, token, receipt, ct);
        return result.Kind switch {
            TenantWebhookAdmissionKind.Accepted => new(ExternalEventIngressKind.Admitted, result.EventId, null),
            TenantWebhookAdmissionKind.Duplicate => new(ExternalEventIngressKind.Duplicate, result.EventId, null),
            TenantWebhookAdmissionKind.Conflict => new(ExternalEventIngressKind.Invalid, null, "source_event_conflict"),
            _ => new(ExternalEventIngressKind.Unauthorized, null, "unauthorized") };
    }

    private WebhookEvent Copy(WebhookEvent source, string name, string? hash, WebhookEventStatus status) => new(source.ResourceId, name, source.Kind, source.EventKey, hash, status,
        source.Revision + 1, source.CreatedAtUtc, AgentCore.Application.Triggers.TriggerScheduleCalculator.Truncate(time.GetUtcNow()), source.TenantId, source.CreatedByPrincipalId);
    private ValueTask<TenantSecurityState> Human(Guid tenant, HumanSessionReference human, CancellationToken ct) => TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, human, ct);
    private async ValueTask Fence(TenantSecurityState state, HumanSessionReference human, CancellationToken ct)
    { if ((await Human(state.Tenant.TenantId, human, ct)).Tenant.Revision != state.Tenant.Revision) throw new SecurityConflictException("stale_revision"); }
    private static void Revision(TenantSecurityState state, long expected) { if (state.Tenant.Revision != expected) throw new SecurityConflictException("stale_revision"); }
    private async ValueTask Require(TenantSecurityState state, Guid human, string key, AccessScope scope, CancellationToken ct)
    { var access = await iam.CheckTenantAsync(state, human, PermissionCatalog.Get(key), scope, ct); if (!access.Allowed) throw new AccessDeniedException(access.ReasonCode); }
    private async ValueTask<bool> CanManage(TenantSecurityState state, Guid human, AccessScope scope, CancellationToken ct) => (await iam.CheckTenantAsync(state, human, PermissionCatalog.Get("webhooks.manage"), scope, ct)).Allowed;
    private async ValueTask<WebhookEvent> Read(TenantSecurityState state, Guid source, Guid human, CancellationToken ct)
    {
        var saved = await store.FindTenantWebhookAsync(state.Tenant.TenantId, source, ct) ?? throw new AccessDeniedException("resource_unavailable");
        await Require(state, human, "webhooks.read", TenantWebhookRules.Scope(state.Tenant.TenantId, source), ct); return saved;
    }
    private async ValueTask<(TenantSecurityState State, WebhookEvent Source)> Change(Guid tenant, Guid source, HumanSessionReference human, long expected, long sourceRevision, CancellationToken ct)
    {
        var state = await Human(tenant, human, ct); Revision(state, expected); var saved = await Read(state, source, human.PrincipalId, ct);
        if (saved.Revision != sourceRevision) throw new SecurityConflictException("stale_revision");
        await Require(state, human.PrincipalId, "webhooks.manage", TenantWebhookRules.Scope(tenant, source), ct); return (state, saved);
    }
    private async ValueTask Save(TenantSecurityState state, WebhookEvent source, long expectedSourceRevision, HumanSessionReference human, string reason, CancellationToken ct)
    {
        var audit = new AuthorizationAudit(ids.NewId(), state.Tenant.TenantId, human.PrincipalId, "webhooks.manage", source.ResourceId.ToString("D"), "allowed", reason, source.UpdatedAtUtc);
        if (!await store.TrySaveTenantWebhookAsync(state.Tenant.TenantId, state.Tenant.Revision, source, expectedSourceRevision, human, audit, ct)) throw new SecurityConflictException("stale_revision");
    }
}
