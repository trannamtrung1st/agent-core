using AgentCore.Domain.Security;

namespace AgentCore.Application.Ports;

public sealed record TenantSandboxApprovalVisibility(IReadOnlyList<Guid>? Instances, IReadOnlyList<string>? Profiles, IReadOnlyList<Guid>? Sessions, IReadOnlyList<Guid>? ControlInstances = null, IReadOnlyList<Guid>? ReadInstances = null);
public sealed record TenantBrowserApprovalVisibility(IReadOnlyList<Guid>? ReadInstances, IReadOnlyList<Guid>? UseInstances, IReadOnlyList<Guid>? Sessions);
public sealed record TenantWorkspaceMutationApprovalVisibility(IReadOnlyList<string>? Reads, IReadOnlyList<string>? Writes, IReadOnlyList<string>? Deletes, IReadOnlyList<Guid>? HomeAgents, IReadOnlyList<Guid>? Sessions);
public sealed record TenantWorkspaceReadApprovalVisibility(IReadOnlyList<string>? Workspaces, IReadOnlyList<Guid>? HomeAgents, IReadOnlyList<Guid>? Sessions, IReadOnlyList<string>? Resources = null);
public sealed record TenantAttachmentReadApprovalVisibility(IReadOnlyList<Guid>? Attachments, IReadOnlyList<Guid>? Sessions);
public sealed record TenantArtifactReadApprovalVisibility(IReadOnlyList<Guid>? Artifacts, IReadOnlyList<Guid>? Sessions);
public sealed record TenantCredentialReadApprovalVisibility(IReadOnlyList<Guid>? Credentials, IReadOnlyList<Guid>? Sessions, IReadOnlyList<Guid>? EmailCredentials = null, IReadOnlyList<Guid>? DriveCredentials = null, IReadOnlyList<Guid>? EmailDraftCredentials = null, IReadOnlyList<Guid>? EmailSendCredentials = null, IReadOnlyList<Guid>? DriveWriteCredentials = null, IReadOnlyList<Guid>? DriveDeleteCredentials = null);
public sealed record TenantBackgroundReportApprovalVisibility(bool CanCreate, IReadOnlyList<Guid>? Sessions, IReadOnlyList<Guid>? Runs);
public sealed record TenantAutomationApprovalVisibility(bool CanCreate, IReadOnlyList<Guid>? Agents, IReadOnlyList<Guid>? ReadAutomations, IReadOnlyList<Guid>? RunAutomations, IReadOnlyList<Guid>? WebhookSources = null, IReadOnlyList<Guid>? CoreEvents = null, IReadOnlyList<Guid>? EmailAccounts = null, bool CanCreateRuns = true, IReadOnlyList<Guid>? ConversationSessions = null, IReadOnlyList<Guid>? ChatAgents = null, bool IncludeOccurrenceReservations = true);
public sealed record TenantContinuityApprovalVisibility(Guid HumanPrincipalId, IReadOnlyList<Guid>? Agents, IReadOnlyList<Guid>? ChatAgents, IReadOnlyList<Guid>? Sessions, IReadOnlyList<Guid>? MemoryAgents, IReadOnlyList<Guid>? Runs = null);
public sealed record TenantManagementChildVisibility(IReadOnlyList<Guid>? Agents, IReadOnlyList<string>? Children);
public sealed record TenantManagementApprovalVisibility(IReadOnlyList<Guid>? Agents, TenantManagementChildVisibility? Skills = null,
    TenantManagementChildVisibility? Resources = null, IReadOnlyList<Guid>? Credentials = null);
public sealed record TenantMemoryApprovalVisibility(Guid HumanPrincipalId, IReadOnlyList<Guid>? Agents, IReadOnlyList<Guid>? Sessions);
public sealed record TenantApprovalContinuationIds(Guid ActivationId, Guid RunId);
public sealed record TenantApprovalQuery(int Page = 0, int PageSize = 20, TenantApprovalStatus? Status = null, ApprovalKind? Kind = null, Guid? SessionId = null)
{
    public void Validate()
    {
        if (Page is < 0 or > 100000 || PageSize is < 1 or > 100 || Status is { } status && !Enum.IsDefined(status)
            || Kind is { } kind && !Enum.IsDefined(kind) || SessionId == Guid.Empty) throw new ArgumentException("Invalid approval inventory query.");
    }
}
public sealed record TenantApprovalPage(IReadOnlyList<TenantApprovalRequest> Items, int Total, int Page, int PageSize);

public interface ITenantApprovalStore
{
    ValueTask<TenantApprovalRequest?> FindApprovalAsync(Guid tenantId, Guid requestId, CancellationToken ct = default);
    ValueTask<TenantApprovalRequest?> FindRootApprovalAsync(Guid tenantId, Guid rootId, Guid executorId, PreparedTenantOperation operation, CancellationToken ct = default);
    ValueTask<TenantApprovalPage> ListApprovalsAsync(Guid tenantId, IReadOnlyList<Guid>? allowedRequests,
        IReadOnlyList<Guid>? allowedInstances, TenantApprovalQuery query, CancellationToken ct = default, IReadOnlyList<Guid>? allowedCredentials = null, TenantSandboxApprovalVisibility? sandboxVisibility = null, TenantBrowserApprovalVisibility? browserVisibility = null, TenantCredentialReadApprovalVisibility? credentialReadVisibility = null, IReadOnlyList<Guid>? artifactCreateSessions = null, TenantArtifactReadApprovalVisibility? artifactReadVisibility = null, TenantWorkspaceReadApprovalVisibility? workspaceReadVisibility = null, TenantWorkspaceMutationApprovalVisibility? workspaceMutationVisibility = null, TenantAttachmentReadApprovalVisibility? attachmentReadVisibility = null, bool backgroundCreationReadable = true, TenantBackgroundReportApprovalVisibility? reportVisibility = null, TenantAutomationApprovalVisibility? automationVisibility = null, TenantMemoryApprovalVisibility? memoryVisibility = null, TenantContinuityApprovalVisibility? continuityVisibility = null, TenantManagementApprovalVisibility? managementVisibility = null);
    ValueTask<TenantApprovalRequest?> TryCreateApprovalAsync(TenantSecurityState next, long expectedSecurityRevision,
        TenantApprovalRequest request, long expectedRunRevision, Guid? generation, AuthorizationAudit audit, CancellationToken ct = default);
    ValueTask<bool> TryDecideApprovalAsync(TenantSecurityState next, long expectedSecurityRevision, TenantApprovalRequest request,
        long expectedRequestRevision, TenantApprovalDecision decision, TenantOperationGrant? grant, ApprovalProcessedSignal signal,
        TenantSessionInitiator human, AuthorizationAudit audit, CancellationToken ct = default);
    ValueTask<IReadOnlyList<ApprovalProcessedSignal>> PendingApprovalSignalsAsync(int limit, CancellationToken ct = default);
    ValueTask<IReadOnlyList<TenantApprovalRequest>> DueApprovalsAsync(DateTimeOffset at, int limit, CancellationToken ct = default);
    ValueTask<bool> TryExpireApprovalAsync(Guid tenantId, Guid requestId, long expectedRevision, ApprovalProcessedSignal signal, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<bool> TryResumeApprovalAsync(ApprovalProcessedSignal signal, DateTimeOffset at, CancellationToken ct = default, TenantApprovalContinuationIds? continuation = null);
    ValueTask<TenantOperationGrant?> FindOperationGrantAsync(Guid tenantId, Guid requestId, CancellationToken ct = default);
}
