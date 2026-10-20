using AgentCore.Application.Execution;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Ports;

// Trusted server-held ownership references. Never part of model arguments or a human cookie.
public sealed record TenantRuntimeFrame(AgentRunOwner Owner, Guid SessionId, Guid RunId, Guid Generation);

public sealed record TenantCompletionView(BackgroundCompletionCandidate Source, CompletionInboxItem? Inbox)
{
    public IReadOnlyList<ArtifactRecord> Artifacts { get; init; } = [];
}
public sealed record TenantCompletionPage(IReadOnlyList<TenantCompletionView> Items, bool HasMore, Guid? NextCursor,
    int PendingCount, bool PendingCountHasMore);

// A narrow facet of the existing tenant Run transaction, bound to the actual mailbox frame.
public interface ITenantCompletionRuntimeStore
{
    ValueTask<TenantCompletionPage> ReadTenantCompletionsAsync(TenantRuntimeFrame frame, string tool, Guid? childSessionId,
        Guid? cursor, bool pendingOnly, int limit, CancellationToken ct = default);
    ValueTask<TenantCompletionView> MutateTenantCompletionAsync(TenantRuntimeFrame frame, Guid childSessionId,
        long expectedRevision, string toolCallId, Guid token, string? usage, CancellationToken ct = default);
    ValueTask<bool> HasTenantCompletionClaimAsync(TenantRuntimeFrame frame, bool acknowledgedOnly, CancellationToken ct = default);
}

public interface ITenantRuntimeStore : ITenantCompletionRuntimeStore
{
    ValueTask<TenantCompletionPage> ITenantCompletionRuntimeStore.ReadTenantCompletionsAsync(TenantRuntimeFrame frame, string tool, Guid? childSessionId, Guid? cursor, bool pendingOnly, int limit, CancellationToken ct) => throw new NotSupportedException();
    ValueTask<TenantCompletionView> ITenantCompletionRuntimeStore.MutateTenantCompletionAsync(TenantRuntimeFrame frame, Guid childSessionId, long expectedRevision, string toolCallId, Guid token, string? usage, CancellationToken ct) => throw new NotSupportedException();
    ValueTask<bool> ITenantCompletionRuntimeStore.HasTenantCompletionClaimAsync(TenantRuntimeFrame frame, bool acknowledgedOnly, CancellationToken ct) => ValueTask.FromResult(false);
    ValueTask<AgentCore.Application.Security.TenantBackgroundCompletion?> ReadTenantBackgroundCompletionAsync(AgentRunOwner owner, Guid childRunId, CancellationToken ct = default) => throw new NotSupportedException();
    ValueTask<bool> SettleTenantReportSourceReadAsync(AgentCore.Domain.Security.ApprovalProcessedSignal signal, Guid auditId, CancellationToken ct = default) => throw new NotSupportedException();
    ValueTask<bool> SettleTenantBackgroundReportApprovalAsync(AgentCore.Domain.Security.ApprovalProcessedSignal signal, AgentRun? report, string? refusal, Guid auditId, CancellationToken ct = default) => throw new NotSupportedException();
    ValueTask<IReadOnlyList<AgentCore.Application.Security.TenantBackgroundCompletion>> ListTenantBackgroundCompletionsAsync(int limit, CancellationToken ct = default) => throw new NotSupportedException();
    ValueTask<AgentRunAdmissionResult> AdmitTenantBackgroundCompletionAsync(AgentRun report, long expectedParentRevision, Guid auditId, CancellationToken ct = default) => throw new NotSupportedException();
    ValueTask SkipTenantBackgroundCompletionAsync(AgentRunOwner owner, Guid childRunId, string reason, CancellationToken ct = default) => throw new NotSupportedException();
    // Internal child admission. The owning transaction rechecks the exact parent claim,
    // current executor permissions/configuration and immutable causal identity.
    ValueTask<AgentRunAdmissionResult> AdmitTenantImmediateAsync(TenantRuntimeFrame parent, SessionSnapshot snapshot,
        AgentRun child, Guid auditId, CancellationToken ct = default) => throw new NotSupportedException();
    ValueTask<AgentRun> CommitTenantOutcomeAsync(TenantRuntimeFrame frame, SessionSnapshot snapshot, long expectedRevision,
        AgentRunCommand.Complete completion, Guid? draftEntryId, Guid diagnosticId, CancellationToken ct = default) => throw new NotSupportedException();
    ValueTask<SessionSnapshot?> LoadTenantRuntimeAsync(TenantRuntimeFrame frame, CancellationToken ct = default);
    ValueTask SaveTenantRuntimeAsync(TenantRuntimeFrame frame, SessionSnapshot snapshot, long expectedRevision, CancellationToken ct = default);
    ValueTask<ConversationHistoryPage?> ReadTenantRuntimeHistoryAsync(TenantRuntimeFrame frame, long? after, long? before, int limit, CancellationToken ct = default);
    ValueTask<IReadOnlyList<AgentRun>> ListRunnableTenantRunsAsync(DateTimeOffset at, int limit, CancellationToken ct = default);
    ValueTask<AgentRun> ApplyTenantRuntimeCommandAsync(AgentRunOwner owner, Guid runId, AgentRunCommand command, Guid diagnosticId, CancellationToken ct = default, Guid? runtimeGeneration = null);
}
