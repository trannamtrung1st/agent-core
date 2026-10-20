using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Domain.Triggers;

namespace AgentCore.Application.Ports;

public sealed record TenantAutomationPage(IReadOnlyList<Automation> Items, int Total, int Page, int PageSize);
public sealed record TenantAutomationAdmission(Automation Current, Automation Next, TriggerOccurrence Occurrence,
    TenantSessionRoot SessionOwner, SessionSnapshot Session, AgentRun Run, long ExpectedInstanceRevision, string ConfigurationHash, PreparedEventAdmissionSource? EventSource = null);
public sealed record TenantAutomationAdmissionResult(bool Created, TriggerOccurrence Occurrence, Guid? SessionId, Guid? RunId, Guid? ApprovalRequestId = null);

// Facet of the existing IAM, Automation/occurrence and Session/Run transaction.
// No caller may turn the legacy global-profile API into a tenant authority path.
public interface ITenantAutomationStore
{
    ValueTask<Automation?> FindTenantAutomationAsync(Guid tenant, Guid automation, CancellationToken ct = default);
    ValueTask<TenantAutomationPage> ListTenantAutomationsAsync(Guid tenant, Guid instance, IReadOnlyList<Guid>? allowed, int page, int pageSize, CancellationToken ct = default);
    ValueTask<IReadOnlyList<Automation>> ListDueTenantAutomationsAsync(DateTimeOffset at, int limit, CancellationToken ct = default, string? afterId = null);
    ValueTask<IReadOnlyList<Automation>> ListFutureTenantAutomationsAsync(Guid tenant, Guid instance, int limit, CancellationToken ct = default);
    ValueTask<bool> TrySaveTenantAutomationAsync(TenantSecurityState next, long expectedSecurityRevision, Automation proposed, long expectedAutomationRevision,
        long expectedInstanceRevision, HumanSessionReference human, string permission, AuthorizationAudit audit, CancellationToken ct = default);
    ValueTask<TenantAutomationAdmissionResult?> TryAdmitTenantAutomationAsync(TenantSecurityState next, long expectedSecurityRevision,
        TenantAutomationAdmission admission, HumanSessionReference? human, AuthorizationAudit audit, CancellationToken ct = default);
    ValueTask<bool> TryAdvanceTenantAutomationAsync(TenantSecurityState next, long expectedSecurityRevision, Automation current, Automation advanced, AuthorizationAudit audit, CancellationToken ct = default);
    ValueTask<TenantAutomationAdmissionResult?> TryPrepareTenantAutomationApprovalAsync(TenantSecurityState next, long expectedSecurityRevision,
        TenantAutomationAdmission admission, TenantApprovalRequest request, HumanSessionReference? human, AuthorizationAudit audit, CancellationToken ct = default);
    ValueTask<bool> TrySettleTenantAutomationApprovalAsync(long expectedSecurityRevision,
        ApprovalProcessedSignal signal, TenantAutomationAdmission? admission, string? refusal, Guid auditId, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<bool> IsTenantAutomationBusyAsync(Guid tenant, Guid automation, CancellationToken ct = default);
    ValueTask<TriggerOccurrence?> FindTenantOccurrenceAsync(Guid tenant, Guid occurrence, CancellationToken ct = default);
}
