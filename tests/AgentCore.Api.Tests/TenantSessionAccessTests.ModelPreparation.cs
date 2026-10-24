using AgentCore.Application.Agents;
using System.Net.Http.Json;
using System.Collections.Concurrent;
using System.Net;
using AgentCore.Contracts.Realtime;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    private sealed class ModelPreparationGate(string kind)
    {
        public bool Enabled = true;
        public int Armed;
        public int ReadCaptured;
        public long BeforeRevision, AfterRevision;
        public Exception? LoggedError;
        public IReadOnlyList<KeyValuePair<string, object?>> LoggedFields = [];
        public bool Race => kind == "progress-race";
        public bool Late => kind == "late-io";
        public bool ApprovedOnly => kind == "consent-io";
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Observed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Captured { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Saved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly IOException Error = new("Controlled private model preparation failure; never publish this message.");
    }
    private sealed class ModelPreparationBrain(ModelPreparationGate gate) : IAgentBrain
    {
        private readonly DefaultAgentBrain inner = new(new PromptContextBuilder());
        public async ValueTask<AgentDecision> DecideAsync(AgentContext context, Guid response, CancellationToken ct = default)
        { var decision = await inner.DecideAsync(context, response, ct); if (Volatile.Read(ref gate.Enabled) && (!gate.ApprovedOnly || context.ApprovalOutcome is not null)) Volatile.Write(ref gate.Armed, 1); return decision; }
    }
    private sealed class ModelPreparationSessions(ITenantSessionStore inner, ModelPreparationGate gate) : ITenantSessionStore
    {
        public async ValueTask<TenantSession?> FindTenantSessionAsync(Guid tenantId, Guid sessionId, CancellationToken ct = default)
        {
            var result = await inner.FindTenantSessionAsync(tenantId, sessionId, ct);
            if (Volatile.Read(ref gate.Enabled) && Volatile.Read(ref gate.Armed) == 1 && Interlocked.CompareExchange(ref gate.ReadCaptured, 1, 0) == 0)
            {
                gate.BeforeRevision = result!.Snapshot.Revision; gate.Captured.TrySetResult();
                if (!gate.Race) { if (gate.Late) await gate.Release.Task; throw gate.Error; }
                await gate.Saved.Task.WaitAsync(ct);
            }
            return result;
        }
        public ValueTask<TenantSession?> TryChangeTenantSessionAsync(TenantSecurityState next, long expectedSecurityRevision, TenantSessionChange change, TenantSessionInitiator initiator, DateTimeOffset at, AuthorizationAudit audit, CancellationToken ct = default) => inner.TryChangeTenantSessionAsync(next, expectedSecurityRevision, change, initiator, at, audit, ct);
        public ValueTask<TenantSessionPage> ListTenantSessionsAsync(Guid tenantId, IReadOnlyList<Guid>? allowedIds, int page, int pageSize, CancellationToken ct = default, bool includeArchived = true, SessionSurface? surface = null, Guid? instanceId = null, bool recentFirst = false) => inner.ListTenantSessionsAsync(tenantId, allowedIds, page, pageSize, ct, includeArchived, surface, instanceId, recentFirst);
        public ValueTask<bool> TryCreateTenantSessionAsync(TenantSecurityState next, long expectedSecurityRevision, TenantSessionRoot owner, SessionSnapshot snapshot, TenantSessionInitiator initiator, long expectedInstanceRevision, AuthorizationAudit audit, CancellationToken ct = default) => inner.TryCreateTenantSessionAsync(next, expectedSecurityRevision, owner, snapshot, initiator, expectedInstanceRevision, audit, ct);
    }
    private sealed class ModelPreparationStorage(ITenantRuntimeStore inner, ModelPreparationGate gate) : ITenantRuntimeStore, ITenantRuntimeInputStore, ITenantDeliveryReceiptStore, ITenantSessionForegroundStore
    {
        public ValueTask<TenantCompletionPage> ReadTenantCompletionsAsync(TenantRuntimeFrame frame, string tool, Guid? child, Guid? cursor, bool pending, int limit, CancellationToken ct = default) => inner.ReadTenantCompletionsAsync(frame, tool, child, cursor, pending, limit, ct);
        public ValueTask<TenantCompletionView> MutateTenantCompletionAsync(TenantRuntimeFrame frame, Guid child, long revision, string call, Guid token, string? usage, CancellationToken ct = default) => inner.MutateTenantCompletionAsync(frame, child, revision, call, token, usage, ct);
        public ValueTask<bool> HasTenantCompletionClaimAsync(TenantRuntimeFrame frame, bool acknowledged, CancellationToken ct = default) => inner.HasTenantCompletionClaimAsync(frame, acknowledged, ct);
        public ValueTask<TenantBackgroundCompletion?> ReadTenantBackgroundCompletionAsync(AgentRunOwner owner, Guid child, CancellationToken ct = default) => inner.ReadTenantBackgroundCompletionAsync(owner, child, ct);
        public ValueTask<bool> SettleTenantReportSourceReadAsync(ApprovalProcessedSignal signal, Guid audit, CancellationToken ct = default) => inner.SettleTenantReportSourceReadAsync(signal, audit, ct);
        public ValueTask<bool> SettleTenantBackgroundReportApprovalAsync(ApprovalProcessedSignal signal, AgentRun? report, string? refusal, Guid audit, CancellationToken ct = default) => inner.SettleTenantBackgroundReportApprovalAsync(signal, report, refusal, audit, ct);
        public ValueTask<IReadOnlyList<TenantBackgroundCompletion>> ListTenantBackgroundCompletionsAsync(int limit, CancellationToken ct = default) => inner.ListTenantBackgroundCompletionsAsync(limit, ct);
        public ValueTask<AgentRunAdmissionResult> AdmitTenantBackgroundCompletionAsync(AgentRun report, long revision, Guid audit, CancellationToken ct = default) => inner.AdmitTenantBackgroundCompletionAsync(report, revision, audit, ct);
        public ValueTask SkipTenantBackgroundCompletionAsync(AgentRunOwner owner, Guid child, string reason, CancellationToken ct = default) => inner.SkipTenantBackgroundCompletionAsync(owner, child, reason, ct);
        public ValueTask<AgentRunAdmissionResult> AdmitTenantImmediateAsync(TenantRuntimeFrame parent, SessionSnapshot snapshot, AgentRun child, Guid audit, CancellationToken ct = default) => inner.AdmitTenantImmediateAsync(parent, snapshot, child, audit, ct);
        public ValueTask<AgentRun> CommitTenantOutcomeAsync(TenantRuntimeFrame frame, SessionSnapshot snapshot, long revision, AgentRunCommand.Complete completion, Guid? draft, Guid diagnostic, CancellationToken ct = default, TenantConfigurationContinuationAdmission? continuation = null) => inner.CommitTenantOutcomeAsync(frame, snapshot, revision, completion, draft, diagnostic, ct, continuation);
        public ValueTask<TenantHumanInputCommit> SaveTenantRuntimeInputAsync(TenantRuntimeFrame frame, SessionSnapshot snapshot, long revision, TenantHumanInput input, Guid audit, CancellationToken ct = default) =>
            ((ITenantRuntimeInputStore)inner).SaveTenantRuntimeInputAsync(frame, snapshot, revision, input, audit, ct);
        public ValueTask SaveTenantRuntimeReceiptAsync(TenantRuntimeFrame frame, SessionSnapshot snapshot, long revision, TenantDeliveryReceipt receipt, CancellationToken ct = default) =>
            ((ITenantDeliveryReceiptStore)inner).SaveTenantRuntimeReceiptAsync(frame, snapshot, revision, receipt, ct);
        public ValueTask<long> SaveTenantQuiescentReceiptAsync(TenantDeliveryReceipt receipt, CancellationToken ct = default) =>
            ((ITenantDeliveryReceiptStore)inner).SaveTenantQuiescentReceiptAsync(receipt, ct);
        public ValueTask SaveTenantRuntimeForegroundAsync(TenantRuntimeFrame frame, SessionSnapshot snapshot, long revision, TenantSessionForeground request, Guid audit, CancellationToken ct = default) =>
            ((ITenantSessionForegroundStore)inner).SaveTenantRuntimeForegroundAsync(frame, snapshot, revision, request, audit, ct);
        public ValueTask ContinueTenantQuiescentAsync(TenantSessionForeground request, Guid audit, CancellationToken ct = default) =>
            ((ITenantSessionForegroundStore)inner).ContinueTenantQuiescentAsync(request, audit, ct);
        public ValueTask<SessionSnapshot?> LoadTenantRuntimeAsync(TenantRuntimeFrame frame, CancellationToken ct = default) => inner.LoadTenantRuntimeAsync(frame, ct);
        public async ValueTask SaveTenantRuntimeAsync(TenantRuntimeFrame frame, SessionSnapshot snapshot, long expectedRevision, CancellationToken ct = default)
        {
            if (Volatile.Read(ref gate.Enabled) && gate.Race && Volatile.Read(ref gate.Armed) == 1 && !gate.Saved.Task.IsCompleted
                && snapshot.Entries.Any(e => e.Role == ConversationRole.Assistant && e.Status == EntryStatus.Streaming))
            {
                await gate.Captured.Task.WaitAsync(ct);
                await inner.SaveTenantRuntimeAsync(frame, snapshot, expectedRevision, ct);
                gate.AfterRevision = snapshot.Revision; gate.Saved.TrySetResult(); return;
            }
            await inner.SaveTenantRuntimeAsync(frame, snapshot, expectedRevision, ct);
        }
        public ValueTask<ConversationHistoryPage?> ReadTenantRuntimeHistoryAsync(TenantRuntimeFrame frame, long? after, long? before, int limit, CancellationToken ct = default) => inner.ReadTenantRuntimeHistoryAsync(frame, after, before, limit, ct);
        public ValueTask<IReadOnlyList<AgentRun>> ListRunnableTenantRunsAsync(DateTimeOffset at, int limit, CancellationToken ct = default) => inner.ListRunnableTenantRunsAsync(at, limit, ct);
        public ValueTask<AgentRun> ApplyTenantRuntimeCommandAsync(AgentRunOwner owner, Guid runId, AgentRunCommand command, Guid diagnosticId, CancellationToken ct = default, Guid? runtimeGeneration = null) => inner.ApplyTenantRuntimeCommandAsync(owner, runId, command, diagnosticId, ct, runtimeGeneration);
    }
    private sealed class ModelPreparationRuns(ITenantRunStore inner, ModelPreparationGate gate) : ITenantRunStore
    {
        public ValueTask<TenantSessionRoot?> ResolveTenantSessionOwnerAsync(Guid sessionId, CancellationToken ct = default) => inner.ResolveTenantSessionOwnerAsync(sessionId, ct);
        public ValueTask<AgentRun?> TryCancelTenantRunAsync(TenantSecurityState next, long expectedSecurityRevision, Guid runId, long expectedRunRevision, TenantSessionInitiator initiator, DateTimeOffset at, AuthorizationAudit audit, CancellationToken ct = default, ConversationEntry? response = null) => inner.TryCancelTenantRunAsync(next, expectedSecurityRevision, runId, expectedRunRevision, initiator, at, audit, ct, response);
        public ValueTask<TenantRunAdmissionResult?> TryAdmitTenantHumanRunAsync(TenantSecurityState next, long expectedSecurityRevision, TenantHumanRunAdmission admission, TenantSessionInitiator initiator, AuthorizationAudit audit, CancellationToken ct = default) => inner.TryAdmitTenantHumanRunAsync(next, expectedSecurityRevision, admission, initiator, audit, ct);
        public ValueTask<AgentRun?> FindTenantRunAsync(Guid tenantId, Guid runId, CancellationToken ct = default) => inner.FindTenantRunAsync(tenantId, runId, ct);
        public ValueTask<IReadOnlyList<Guid>> ListTenantRunIdsAsync(Guid tenantId, Guid sessionId, CancellationToken ct = default)
        {
            if (Volatile.Read(ref gate.Enabled) && Volatile.Read(ref gate.Armed) == 1 && Interlocked.CompareExchange(ref gate.ReadCaptured, 1, 0) == 0)
            { gate.Captured.TrySetResult(); return ValueTask.FromException<IReadOnlyList<Guid>>(gate.Error); }
            return inner.ListTenantRunIdsAsync(tenantId, sessionId, ct);
        }
    }
    private sealed class ModelPreparationObserver(ModelPreparationGate gate) : ILoggerProvider
    {
        public ILogger CreateLogger(string name) => new Observer(gate);
        public void Dispose() { }
        private sealed class Observer(ModelPreparationGate gate) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel level) => level >= LogLevel.Error;
            public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> format)
            {
                if (error is not null && error.HResult == gate.Error.HResult
                    && error.Message.StartsWith(typeof(IOException).FullName + " HResult=", StringComparison.Ordinal)
                    && error.StackTrace?.Contains(nameof(ModelPreparationSessions), StringComparison.Ordinal) == true)
                {
                    gate.LoggedError = error;
                    gate.LoggedFields = state is IEnumerable<KeyValuePair<string, object?>> fields ? fields.ToArray() : [];
                    gate.Observed.TrySetResult();
                }
            }
        }
    }
    [Theory] [InlineData(false, "progress-race")] [InlineData(true, "progress-race")]
    [InlineData(false, "io")] [InlineData(true, "io")]
    public async Task Tenant_model_preparation_handles_owned_progress_commit_and_unexpected_prelude_fault_without_stalling(bool sqlite, string kind)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-model-preparation-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var gate = new ModelPreparationGate(kind); var model = new AttachmentReadModel(); var logs = new DiagnosticLineLoggerProvider();
            void Configure(IServiceCollection services)
            {
                services.AddLogging(logging => logging.AddProvider(logs));
                services.Replace(ServiceDescriptor.Singleton<IAgentBrain>(new ModelPreparationBrain(gate)));
                services.Replace(ServiceDescriptor.Singleton<ITenantSessionStore>(p => new ModelPreparationSessions((ITenantSessionStore)p.GetRequiredService<IIamStore>(), gate)));
                services.Replace(ServiceDescriptor.Singleton<ITenantRuntimeStore>(p => new ModelPreparationStorage((ITenantRuntimeStore)p.GetRequiredService<IIamStore>(), gate)));
            }
            await using WebApplicationFactory<Program> host = sqlite ? new NativeTenantSqliteFactory(root, model, Configure) : new NativeTenantMemoryFactory(root, model, Configure);
            using var owner = host.CreateClient(); var value = await PrepareAttachmentRead(host.Services, owner, "text", virtualTool: AgentCore.Application.Tools.ToolCatalog.WorkspaceList); var v = value.Execution;
            var runId = await SendNamespaceMessage(owner, v, host.Services, [v.Id]);
            try { await gate.Captured.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (TimeoutException)
            {
                var observed = (await host.Services.GetRequiredService<ITenantRunStore>().FindTenantRunAsync(v.Tenant, runId))!;
                throw new Xunit.Sdk.XunitException($"{kind}: source read not reached; armed={gate.Armed}; modelCalls={model.Requests.Count}; status={observed.Status}; failure={observed.Failure?.Code}: {observed.Failure?.Summary}; {string.Join("\n", logs.Errors.Select(e => e.ToString()))}");
            }
            AgentRun done;
            using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            {
                while (true)
                {
                    done = (await host.Services.GetRequiredService<ITenantRunStore>().FindTenantRunAsync(v.Tenant, runId, deadline.Token))!;
                    if (done.IsTerminal) break;
                    await Task.Delay(25, deadline.Token);
                }
            }
            var history = (await host.Services.GetRequiredService<ITenantConversationStore>().ReadTenantConversationAsync(v.Tenant, v.Session, null, null, 100))!.History.Items;
            if (gate.Race)
            {
                Assert.True(gate.AfterRevision > gate.BeforeRevision); Assert.Equal(AgentRunStatus.Completed, done.Status); Assert.Single(model.Requests);
                Assert.Contains("Private read body", string.Join("\n", model.Requests.Single().Messages.Select(m => m.Text)));
                Assert.Equal(EntryStatus.Completed, Assert.Single(history, e => e.Role == ConversationRole.Assistant).Status);
            }
            else
            {
                Assert.Equal(AgentRunStatus.Failed, done.Status); Assert.Equal("preparation-failed", done.Failure!.Code); Assert.Empty(model.Requests);
                Assert.Null(done.Claim); Assert.Equal(1, done.AttemptCount); Assert.Equal(AgentRunSideEffectDisposition.None, done.SideEffect.Disposition);
                Assert.DoesNotContain("private model preparation", done.Failure.Summary);
                Assert.Equal(EntryStatus.Failed, Assert.Single(history, e => e.Role == ConversationRole.Assistant).Status);
            }
            Volatile.Write(ref gate.Enabled, false);
            var next = await SendNamespaceMessage(owner, v, host.Services);
            Assert.Equal(AgentRunStatus.Completed, (await AttachmentTerminal(host.Services.GetRequiredService<ITenantRunStore>(), v.Tenant, next)).Status);
            Assert.Equal(0, host.Services.GetRequiredService<AgentCore.Api.Http.OutboundHttpProbe>().Attempts);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_model_preparation_late_fault_after_cancel_preserves_new_Run_and_history(bool sqlite)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-model-preparation-late-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var gate = new ModelPreparationGate("late-io"); var model = new PreparationHoldModel();
        try
        {
            void Configure(IServiceCollection services)
            {
                services.AddLogging(logs => logs.AddProvider(new ModelPreparationObserver(gate)));
                services.Replace(ServiceDescriptor.Singleton<IAgentBrain>(new ModelPreparationBrain(gate)));
                services.Replace(ServiceDescriptor.Singleton<ITenantSessionStore>(p => new ModelPreparationSessions((ITenantSessionStore)p.GetRequiredService<IIamStore>(), gate)));
            }
            await using WebApplicationFactory<Program> host = sqlite ? new NativeTenantSqliteFactory(root, model, Configure) : new NativeTenantMemoryFactory(root, model, Configure);
            using var owner = host.CreateClient(); var value = await PrepareAttachmentRead(host.Services, owner, "text", virtualTool: AgentCore.Application.Tools.ToolCatalog.WorkspaceList); var v = value.Execution;
            var runs = host.Services.GetRequiredService<ITenantRunStore>();
            var first = await SendNamespaceMessage(owner, v, host.Services, [v.Id]); await gate.Captured.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var active = (await runs.FindTenantRunAsync(v.Tenant, first))!;
            Assert.Equal(System.Net.HttpStatusCode.OK, (await owner.PostAsJsonAsync($"/api/v2/tenants/{v.Tenant}/agent-runs/{first}/cancel", new {
                expectedRevision = await Revision(owner, v.Tenant), expectedRunRevision = active.Revision })).StatusCode);
            Assert.False(gate.Release.Task.IsCompleted); Assert.Equal(0, model.Calls); Volatile.Write(ref gate.Enabled, false);
            var next = await SendNamespaceMessage(owner, v, host.Services); await model.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            gate.Release.TrySetResult(); await gate.Observed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.NotSame(gate.Error, gate.LoggedError); Assert.NotNull(gate.LoggedError);
            Assert.DoesNotContain(gate.Error.Message, gate.LoggedError.ToString());
            Assert.Contains(gate.LoggedFields, field => field.Key == "DiagnosticId" && field.Value is Guid diagnostic && diagnostic != Guid.Empty);
            Assert.Equal(AgentRunStatus.Cancelled, (await runs.FindTenantRunAsync(v.Tenant, first))!.Status);
            Assert.Equal(AgentRunStatus.Running, (await runs.FindTenantRunAsync(v.Tenant, next))!.Status);
            model.Release.TrySetResult(); var done = await AttachmentTerminal(runs, v.Tenant, next); Assert.Equal(AgentRunStatus.Completed, done.Status); Assert.Equal(1, model.Calls);
            var history = (await host.Services.GetRequiredService<ITenantConversationStore>().ReadTenantConversationAsync(v.Tenant, v.Session, null, null, 100))!.History.Items;
            Assert.Equal(EntryStatus.Interrupted, Assert.Single(history, e => e.ResponseId == active.ResponseId).Status);
            Assert.Equal(EntryStatus.Completed, Assert.Single(history, e => e.ResponseId == done.ResponseId).Status);
        }
        finally { gate.Release.TrySetResult(); model.Release.TrySetResult(); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory] [InlineData(false, true)] [InlineData(true, true)] [InlineData(false, false)] [InlineData(true, false)]
    public Task Tenant_model_preparation_failure_after_consent_keeps_exact_completed_effect_and_reopened_receipt(bool sqlite, bool review) =>
        ArtifactReviewFlow(sqlite, review, "approve", false, failDuringModelPreparation: true);

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_model_preparation_fault_emits_failed_completion_over_actual_MessagePack_after_saved_history(bool sqlite)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-model-preparation-wire-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var gate = new ModelPreparationGate("facts-io"); var model = new AttachmentReadModel();
            void Configure(IServiceCollection services)
            {
                services.Replace(ServiceDescriptor.Singleton<IAgentBrain>(new ModelPreparationBrain(gate)));
                services.Replace(ServiceDescriptor.Singleton<ITenantRunStore>(p => new ModelPreparationRuns((ITenantRunStore)p.GetRequiredService<IIamStore>(), gate)));
            }
            await using WebApplicationFactory<Program> host = sqlite ? new NativeTenantSqliteFactory(root, model, Configure) : new NativeTenantMemoryFactory(root, model, Configure);
            host.UseKestrel(0); using var ready = host.CreateClient(); var cookies = new CookieContainer();
            using var owner = new HttpClient(new HttpClientHandler { CookieContainer = cookies }) { BaseAddress = ready.BaseAddress };
            var value = await PrepareAttachmentRead(host.Services, owner, "text", virtualTool: AgentCore.Application.Tools.ToolCatalog.WorkspaceList); var v = value.Execution;
            var events = new ConcurrentQueue<ServerEvent>(); await using var hub = TenantSocket(ready.BaseAddress!, cookies, events); await hub.StartAsync();
            Assert.True((await hub.InvokeAsync<CommandAck>("AttachTenant", TenantAttach(v.Tenant, v.Session))).Accepted);
            var runId = await SendNamespaceMessage(owner, v, host.Services, [v.Id]);
            var run = (await host.Services.GetRequiredService<ITenantRunStore>().FindTenantRunAsync(v.Tenant, runId))!;
            var completed = await AwaitTenantWire(() => events.FirstOrDefault(e => e.Type == "agent.response.completed" && e.ResponseId == run.ResponseId!.Value.ToString("D")));
            Assert.Equal("failed", completed.Payload["status"]);
            Assert.Contains(events, e => e.Type == "agent.response.started" && e.ResponseId == completed.ResponseId && e.Sequence < completed.Sequence);
            var factual = (await host.Services.GetRequiredService<ITenantRunStore>().FindTenantRunAsync(v.Tenant, runId))!;
            Assert.Equal(AgentRunStatus.Failed, factual.Status); Assert.Equal("preparation-failed", factual.Failure!.Code); Assert.Empty(model.Requests);
            var history = (await host.Services.GetRequiredService<ITenantConversationStore>().ReadTenantConversationAsync(v.Tenant, v.Session, null, null, 100))!.History.Items;
            Assert.Equal(EntryStatus.Failed, Assert.Single(history, e => e.ResponseId == run.ResponseId).Status);
            Assert.DoesNotContain(events, e => e.Type == "agent.text.delta" && e.ResponseId == completed.ResponseId);
            Volatile.Write(ref gate.Enabled, false); var next = await SendNamespaceMessage(owner, v, host.Services);
            var done = await AttachmentTerminal(host.Services.GetRequiredService<ITenantRunStore>(), v.Tenant, next);
            var nextDone = await AwaitTenantWire(() => events.FirstOrDefault(e => e.Type == "agent.response.completed" && e.ResponseId == done.ResponseId!.Value.ToString("D")));
            Assert.Equal("completed", nextDone.Payload["status"]); Assert.Equal(AgentRunStatus.Completed, done.Status); Assert.Single(model.Requests);
            Assert.Single(events, e => e.Type == "agent.response.completed" && e.ResponseId == completed.ResponseId);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

}
