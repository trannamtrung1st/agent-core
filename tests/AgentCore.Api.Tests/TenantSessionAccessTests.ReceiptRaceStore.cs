using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Security;
using AgentCore.Domain.Conversation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    // Test-only gates around real stores; all authority, SQL and Runtime behavior stay production code.
    private sealed class TenantReceiptRaceStore : ITenantRuntimeStore, ITenantDeliveryReceiptStore, ITenantRuntimeInputStore
    {
        private ITenantRuntimeStore inner = null!;
        public bool HoldReceipt;
        private int holdLoad;
        private bool countLoads;
        public int LoadsAfterArm;
        public TaskCompletionSource ReceiptEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseReceipt { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SnapshotLoaded { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseSnapshot { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Install(IServiceCollection services) => services.Replace(ServiceDescriptor.Singleton<ITenantRuntimeStore>(p => {
            inner = (ITenantRuntimeStore)p.GetRequiredService<IIamStore>(); return this;
        }));
        public void HoldNextSnapshot() { countLoads = true; Interlocked.Exchange(ref holdLoad, 1); }
        public async ValueTask<SessionSnapshot?> LoadTenantRuntimeAsync(TenantRuntimeFrame frame, CancellationToken ct = default)
        {
            var saved = await inner.LoadTenantRuntimeAsync(frame, ct);
            if (countLoads) Interlocked.Increment(ref LoadsAfterArm);
            if (Interlocked.Exchange(ref holdLoad, 0) == 1) { SnapshotLoaded.TrySetResult(); await ReleaseSnapshot.Task.WaitAsync(ct); }
            return saved;
        }
        public async ValueTask SaveTenantRuntimeReceiptAsync(TenantRuntimeFrame frame, SessionSnapshot snapshot, long expectedRevision, TenantDeliveryReceipt receipt, CancellationToken ct = default)
        {
            if (HoldReceipt) { ReceiptEntered.TrySetResult(); await ReleaseReceipt.Task.WaitAsync(ct); }
            await ((ITenantDeliveryReceiptStore)inner).SaveTenantRuntimeReceiptAsync(frame, snapshot, expectedRevision, receipt, ct);
        }
        public ValueTask<long> SaveTenantQuiescentReceiptAsync(TenantDeliveryReceipt receipt, CancellationToken ct = default) =>
            ((ITenantDeliveryReceiptStore)inner).SaveTenantQuiescentReceiptAsync(receipt, ct);
        public ValueTask<TenantHumanInputCommit> SaveTenantRuntimeInputAsync(TenantRuntimeFrame frame, SessionSnapshot snapshot,
            long expectedRevision, TenantHumanInput input, Guid auditId, CancellationToken ct = default) =>
            ((ITenantRuntimeInputStore)inner).SaveTenantRuntimeInputAsync(frame, snapshot, expectedRevision, input, auditId, ct);
        public ValueTask<AgentRun> CommitTenantOutcomeAsync(TenantRuntimeFrame frame, SessionSnapshot snapshot, long expectedRevision,
            AgentRunCommand.Complete completion, Guid? draftEntryId, Guid diagnosticId, CancellationToken ct = default) => inner.CommitTenantOutcomeAsync(frame, snapshot, expectedRevision, completion, draftEntryId, diagnosticId, ct);
        public ValueTask<TenantBackgroundCompletion?> ReadTenantBackgroundCompletionAsync(AgentRunOwner owner, Guid childRunId, CancellationToken ct = default) => inner.ReadTenantBackgroundCompletionAsync(owner, childRunId, ct);
        public ValueTask<IReadOnlyList<TenantBackgroundCompletion>> ListTenantBackgroundCompletionsAsync(int limit, CancellationToken ct = default) => inner.ListTenantBackgroundCompletionsAsync(limit, ct);
        public ValueTask<bool> SettleTenantReportSourceReadAsync(ApprovalProcessedSignal signal, Guid auditId, CancellationToken ct = default) => inner.SettleTenantReportSourceReadAsync(signal, auditId, ct);
        public ValueTask<bool> SettleTenantBackgroundReportApprovalAsync(ApprovalProcessedSignal signal, AgentRun? report, string? refusal, Guid auditId, CancellationToken ct = default) => inner.SettleTenantBackgroundReportApprovalAsync(signal, report, refusal, auditId, ct);
        public ValueTask<AgentRunAdmissionResult> AdmitTenantBackgroundCompletionAsync(AgentRun report, long expectedParentRevision, Guid auditId, CancellationToken ct = default) => inner.AdmitTenantBackgroundCompletionAsync(report, expectedParentRevision, auditId, ct);
        public ValueTask SkipTenantBackgroundCompletionAsync(AgentRunOwner owner, Guid childRunId, string reason, CancellationToken ct = default) => inner.SkipTenantBackgroundCompletionAsync(owner, childRunId, reason, ct);
        public ValueTask SaveTenantRuntimeAsync(TenantRuntimeFrame frame, SessionSnapshot snapshot, long expectedRevision, CancellationToken ct = default) => inner.SaveTenantRuntimeAsync(frame, snapshot, expectedRevision, ct);
        public ValueTask<ConversationHistoryPage?> ReadTenantRuntimeHistoryAsync(TenantRuntimeFrame frame, long? after, long? before, int limit, CancellationToken ct = default) => inner.ReadTenantRuntimeHistoryAsync(frame, after, before, limit, ct);
        public ValueTask<IReadOnlyList<AgentRun>> ListRunnableTenantRunsAsync(DateTimeOffset at, int limit, CancellationToken ct = default) => inner.ListRunnableTenantRunsAsync(at, limit, ct);
        public ValueTask<AgentRun> ApplyTenantRuntimeCommandAsync(AgentRunOwner owner, Guid runId, AgentRunCommand command, Guid diagnosticId, CancellationToken ct = default, Guid? runtimeGeneration = null) => inner.ApplyTenantRuntimeCommandAsync(owner, runId, command, diagnosticId, ct, runtimeGeneration);
    }
}
