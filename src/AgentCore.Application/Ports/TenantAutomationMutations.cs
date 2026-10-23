using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Domain.Triggers;

namespace AgentCore.Application.Ports;

public sealed record TenantAutomationMutationObservation(TenantMemoryReader Reader, Automation? Current,
    TenantSession? Conversation, TenantSession? ReportDestination, string IntentText, bool AdmittedTask)
{
    // Transient untrusted context from the owning conversation; never grant or prepared-plan data.
    public string? ConversationEvidenceJson { get; init; }
}
public sealed record TenantAutomationMutationCommit(Guid OperationId, Guid RunId, Guid Generation, long ExpectedRunRevision,
    string SourceCallId, string ParametersHash, PreparedAutomationMutation Prepared, long ExpectedInstanceRevision, string ActionHash,
    TenantAutomationAdmission? InvocationAdmission = null);

// Facet of the existing IAM/Automation/Run/receipt transaction, never a human facade.
public interface ITenantAutomationMutationStore : ITenantDirectOperationStore
{
    ValueTask<TenantAutomationMutationObservation> ObserveTenantAutomationMutationAsync(TenantRunReference reference, Guid? generation,
        string tool, string arguments, PreparedAutomationMutation? expected = null, Guid? inspectionHuman = null, CancellationToken ct = default);
    ValueTask<TenantOperationReceipt?> TryCommitTenantAutomationMutationAsync(TenantSecurityState next, long expectedSecurityRevision,
        TenantAutomationMutationCommit commit, AuthorizationAudit audit, CancellationToken ct = default);
}
