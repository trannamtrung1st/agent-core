using AgentCore.Domain.Events;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Ports;

public sealed record TenantWebhookPage(IReadOnlyList<WebhookEvent> Items, int Total, int Page, int PageSize);
public sealed record TenantWebhookReceiptPage(IReadOnlyList<ExternalEvent> Items, int Total, int Page, int PageSize);
public sealed record TenantWebhookWrite(WebhookEvent Source, bool CanManage, long SecurityRevision);
public sealed record TenantWebhookToken(TenantWebhookWrite View, string Token);
public enum TenantWebhookAdmissionKind { Accepted, Duplicate, Unauthorized, Conflict }
public sealed record TenantWebhookAdmission(TenantWebhookAdmissionKind Kind, Guid? EventId = null);

// Facet of the existing IAM and WebhookEvent/ExternalEvent owners. No second
// event queue or copied human authority is introduced by an incoming token.
public interface ITenantWebhookStore
{
    ValueTask<WebhookEvent?> FindTenantWebhookAsync(Guid tenant, Guid source, CancellationToken ct = default);
    ValueTask<TenantWebhookPage> ListTenantWebhooksAsync(Guid tenant, IReadOnlyList<Guid>? allowed, int page, int pageSize, CancellationToken ct = default);
    ValueTask<TenantWebhookReceiptPage> ListTenantWebhookReceiptsAsync(Guid tenant, Guid source, int page, int pageSize, CancellationToken ct = default);
    ValueTask<bool> TrySaveTenantWebhookAsync(Guid tenant, long expectedSecurityRevision, WebhookEvent source,
        long expectedSourceRevision, HumanSessionReference human, AuthorizationAudit audit, CancellationToken ct = default);
    ValueTask<TenantWebhookAdmission> TryAdmitTenantWebhookAsync(Guid tenant, Guid source, string token, ExternalEvent receipt, CancellationToken ct = default);
}
