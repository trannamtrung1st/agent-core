using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Domain.Tasks;

namespace AgentCore.Application.Ports;

// This is the Task admission facet of the existing approval/decision/signal owner.
// A human command has no execution claim or local-effect source Run to fabricate.
public sealed record TenantHumanTaskAdmission(DelegatedTask Task, SessionSnapshot? Session, AgentRun? Run,
    long ExpectedSecurityRevision, Guid AuditId);
public interface ITenantHumanTaskApprovalStore
{
    ValueTask<TenantApprovalRequest?> TryPrepareHumanTaskApprovalAsync(TaskAdmission preparation, TenantApprovalRequest request,
        CancellationToken ct = default);
    ValueTask<HumanTaskApprovalOutcome?> FindHumanTaskApprovalOutcomeAsync(Guid tenant, Guid operation, CancellationToken ct = default);
    ValueTask<bool> TrySettleHumanTaskApprovalAsync(ApprovalProcessedSignal signal, TenantHumanTaskAdmission? admission,
        string? refusalReason, long expectedSecurityRevision, CancellationToken ct = default);
}
