using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Browser;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    private sealed class BrowserRestartClock : TimeProvider
    {
        public TimeSpan Offset;
        public override DateTimeOffset GetUtcNow() => TimeProvider.System.GetUtcNow() + Offset;
        public override long GetTimestamp() => TimeProvider.System.GetTimestamp();
        public override long TimestampFrequency => TimeProvider.System.TimestampFrequency;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => TimeProvider.System.CreateTimer(callback, state, dueTime, period);
    }
    private sealed class BrowserRestartExecution
    {
        public bool Enabled = true;
        public ITenantRunExecution Wrap(ITenantRunExecution inner) => new Gate(this, inner);
        private sealed class Gate(BrowserRestartExecution owner, ITenantRunExecution inner) : ITenantRunExecution
        {
            public ValueTask<int> ExecuteRunnableAsync(int limit, CancellationToken ct = default) => Volatile.Read(ref owner.Enabled)
                ? inner.ExecuteRunnableAsync(limit, ct) : ValueTask.FromResult(0);
        }
    }
    private sealed class LostBrowserAcknowledgement(IBrowser native) : IBrowser
    {
        public BrowserProviderDescriptor Provider => native.Provider;
        public BrowserHostPolicy HostPolicy => native.HostPolicy;
        public bool IsAvailable => native.IsAvailable;
        public async ValueTask<BrowserResult> ExecuteAsync(BrowserRequest request, CancellationToken ct = default)
        {
            var result = await native.ExecuteAsync(request, ct);
            Assert.Null(result.ErrorCode); Assert.True(result.EffectConfirmedBySdk);
            return new("ack_lost", EffectAttempted: true);
        }
    }
    private static WebApplicationFactory<Program> BrowserRestartHost(TenantApprovalSqliteFactory source, string root, string origin,
        BrowserRestartClock clock, BrowserRestartExecution execution) => source.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => {
            var options = (BrowserOptions)services.Single(d => d.ServiceType == typeof(BrowserOptions)).ImplementationInstance!;
            options.Enabled = true; options.Headless = true; options.Channel = null; options.FixtureEnabled = false;
            options.ProfileRoot = Path.Combine(root, "browser"); options.PolicyMode = "Restricted"; options.ProfileMode = "EphemeralSession";
            options.NavigationOrigins = [origin]; options.TargetOrigins = null; options.InteractionOrigins = null; options.ResourceOrigins = [];
            services.Replace(ServiceDescriptor.Singleton<TimeProvider>(clock));
            services.Replace(ServiceDescriptor.Singleton<ITenantRunExecution>(p => execution.Wrap(new TenantRunExecution(p.GetRequiredService<ITenantRuntimeStore>(),
                p.GetRequiredService<ITenantRunStore>(), p.GetRequiredService<ITenantRunDispatcher>(), p.GetRequiredService<IIdGenerator>(), p.GetRequiredService<TimeProvider>()))));
        }));
    private static async Task<AgentRun> AwaitBrowserRestartRun(ITenantRunStore runs, Guid tenant, Guid source, bool waiting)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        while (true)
        {
            var run = (await CurrentApprovalExecution(runs, tenant, source))!;
            if (waiting ? run.Status == AgentRunStatus.WaitingForApproval || run.IsTerminal : run.IsTerminal) return run;
            await Task.Delay(25, deadline.Token);
        }
    }

    [TenantBrowserChromiumTheory] [InlineData("pending")] [InlineData("completed")] [InlineData("unknown")]
    [InlineData("revoked")] [InlineData("abandoned")] [InlineData("warm")]
    public async Task Tenant_Browser_SQLite_recreation_retains_exact_consent_or_saved_result_without_native_replay(string stage)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-browser-restart-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        await using var page = new LoopbackBrowserFixtureHost(NullLogger.Instance); await page.StartAsync(0, CancellationToken.None); Assert.True(page.IsAvailable);
        var clock = new BrowserRestartClock(); var proposal = stage == "warm" ? """{"operation":"run_browser_command","command":{"tool":"browser.close","arguments":{}}}"""
            : JsonSerializer.Serialize(new { operation = "run_browser_command", command = new { tool = ToolCatalog.BrowserNavigate,
            arguments = new { url = page.Origin + "/browser-native.html" } } });
        Guid tenant = default, sourceId = default, requestId = default, sessionId = default, instance = default, executor = default, browserAssignment = default;
        string? savedTool = null; TenantOperationReceipt? receipt = null;
        try
        {
            var model = new ApprovalRecordingModel { ProposedBrowserCommand = proposal }; var execution = new BrowserRestartExecution();
            await using (var baseHost = new TenantApprovalSqliteFactory(root, model))
            await using (var first = BrowserRestartHost(baseHost, root, page.Origin!, clock, execution))
            {
                using var owner = first.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
                await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
                tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid(); var api = $"/api/v2/tenants/{tenant}";
                var human = (await Json(await owner.GetAsync(api + "/access/me"))).GetProperty("principalId").GetGuid();
                var agent = await Json(await owner.PostAsJsonAsync(api + "/agents", new { templateId = "general-assistant", version = 32, expectedRevision = await Revision(owner, tenant) }));
                instance = agent.GetProperty("instance").GetProperty("instanceId").GetGuid(); executor = agent.GetProperty("principalId").GetGuid();
                var session = await Json(await owner.PostAsJsonAsync(api + "/sessions", new { agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1 }));
                sessionId = session.GetProperty("sessionId").GetGuid(); var access = first.Services.GetRequiredService<TenantAccessService>();
                var role = await access.SaveRoleAsync(human, tenant, null, "Reviewed browser", ["browser.read", "browser.use"], await Revision(owner, tenant));
                browserAssignment = (await access.AssignRoleAsync(human, tenant, executor, role.RoleId, AccessScope.ForResource(tenant, "agent", instance.ToString("D")), await Revision(owner, tenant))).AssignmentId;
                role = await access.SaveRoleAsync(human, tenant, null, "Read own Session", ["sessions.read"], await Revision(owner, tenant));
                await access.AssignRoleAsync(human, tenant, executor, role.RoleId, TenantSessionRules.Scope(tenant, sessionId), await Revision(owner, tenant));
                var native = Assert.IsType<NativePlaywrightBrowser>(first.Services.GetRequiredService<IBrowser>());
                if (stage == "warm")
                {
                    var scope = new TenantBrowserOwner(tenant, instance, executor, sessionId);
                    Assert.Null(await native.BindTenantIsolationAsync(scope, BrowserProfileMode.EphemeralSession));
                    var command = new BrowserNavigate(page.Origin + "/browser-native.html"); var target = await native.PrepareTenantCommandAsync(scope, command);
                    // Trusted fixture navigation supplies a live view; it is not a model-authorized mutation.
                    Assert.Null((await native.ExecuteAsync(new(sessionId, command) { TenantOwner = scope, TenantWitness = target.Witness })).ErrorCode);
                }
                sourceId = (await Json(await owner.PostAsJsonAsync(api + $"/sessions/{sessionId}/messages", new { messageId = Guid.NewGuid(), text = "Review the exact Browser navigation", expectedRevision = await Revision(owner, tenant), expectedSessionRevision = 1 }))).GetProperty("runId").GetGuid();
                var runs = first.Services.GetRequiredService<ITenantRunStore>(); var waiting = await AwaitBrowserRestartRun(runs, tenant, sourceId, true);
                Assert.Equal(AgentRunStatus.WaitingForApproval, waiting.Status); requestId = waiting.ApprovalRequestId!.Value; Volatile.Write(ref execution.Enabled, false);
                var capacity = first.Services.GetRequiredService<AgentCore.Api.Realtime.TenantRuntimeCapacity>();
                using (var drained = new CancellationTokenSource(TimeSpan.FromSeconds(15))) { while (!capacity.TryReserveTenantRuntime()) await Task.Delay(25, drained.Token); capacity.ReleaseTenantRuntime(); }
                Assert.Equal(stage == "warm" ? 1 : 0, (await native.ObserveTenantSessionAsync(new(tenant, instance, executor, sessionId))).LiveContexts);
                if (stage is not ("pending" or "warm"))
                {
                    var request = (await first.Services.GetRequiredService<ITenantApprovalStore>().FindApprovalAsync(tenant, requestId))!;
                    Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync(api + $"/approvals/{requestId}/decision", new { decision = "approve", actionHash = request.Operation.ActionHash,
                        expectedRevision = await Revision(owner, tenant), expectedRequestRevision = request.Revision })).StatusCode);
                    request = (await first.Services.GetRequiredService<ITenantApprovalStore>().FindApprovalAsync(tenant, requestId))!;
                    await first.Services.GetRequiredService<TenantApprovalService>().DispatchPendingAsync();
                    var queued = (await CurrentApprovalExecution(runs, tenant, sourceId))!; var at = clock.GetUtcNow();
                    var claimed = await first.Services.GetRequiredService<ITenantRuntimeStore>().ApplyTenantRuntimeCommandAsync(queued.Owner, queued.AgentRunId,
                        new AgentRunCommand.Claim(queued.Revision, at, Guid.NewGuid(), at.AddMinutes(5)), Guid.NewGuid());
                    var operations = first.Services.GetRequiredService<ITenantApprovedOperationStore>();
                    if (stage == "abandoned")
                    {
                        // Crash after the durable start. Recovery cannot infer whether native I/O happened.
                        var decision = (await operations.FindApprovalDecisionAsync(tenant, requestId))!;
                        var state = (await first.Services.GetRequiredService<IIamStore>().ReadTenantAsync(tenant))!;
                        var commit = new TenantOperationCommit(requestId, request.Operation.OperationId, claimed.AgentRunId, claimed.Claim!.Generation,
                            claimed.Revision, request.Revision, decision.DecisionId, null, request.Operation.ActionHash, TenantOperationOutcome.Completed, "operation_completed");
                        var start = await operations.TryStartBrowserEffectAsync(state with { Tenant = state.Tenant with { Revision = state.Tenant.Revision + 1 } }, state.Tenant.Revision, commit,
                            new(Guid.NewGuid(), tenant, executor, TenantBrowserEffectRules.DispatchAction, request.ResourceId, "allowed", "browser_command_dispatched", clock.GetUtcNow()));
                        Assert.NotNull(start?.Fence); Assert.Null(await operations.FindOperationReceiptAsync(tenant, request.Operation.OperationId));
                        Assert.Equal(0, (await native.ObserveTenantSessionAsync(new(tenant, instance, executor, sessionId))).LiveContexts);
                    }
                    else
                    {
                        // Use the real pre-mailbox external stage, then stop before Runtime delivery.
                        if (stage == "unknown")
                        {
                            var services = first.Services;
                            var effects = new TenantBrowserEffectService(services.GetRequiredService<IIamStore>(), runs, services.GetRequiredService<ITenantApprovalStore>(), operations,
                                services.GetRequiredService<TenantBrowserOperationTarget>(), services.GetRequiredService<IamAuthorizer>(), new LostBrowserAcknowledgement(native),
                                services.GetRequiredService<TenantOperationAuthorizer>(), services.GetRequiredService<IIdGenerator>(), clock);
                            await effects.ExecuteAsync(new(tenant, claimed.AgentRunId), requestId, request.Operation.OperationId, claimed.Claim!.Generation, request.Operation.ActionHash);
                        }
                        else await first.Services.GetRequiredService<TenantRunContinuationService>().PrepareExternalEffectAsync(claimed);
                        receipt = (await operations.FindOperationReceiptAsync(tenant, request.Operation.OperationId))!;
                        Assert.Equal(stage == "unknown" ? TenantOperationOutcome.Indeterminate : TenantOperationOutcome.Completed, receipt.Outcome);
                        var saved = (await runs.FindTenantRunAsync(tenant, claimed.AgentRunId))!; Assert.Equal(AgentRunStatus.Running, saved.Status);
                        Assert.True(saved.Checkpoint!.AwaitingRuntimeAdmission); Assert.Null(saved.Checkpoint.BudgetRecordedAtUtc);
                        Assert.True(AgentRunToolCallCheckpoint.TryRead(saved.Checkpoint, out var transcript)); savedTool = Assert.Single(transcript!, m => m.Role == ModelRole.Tool && m.Name == ToolCatalog.RequestAuthorization).Text;
                        Assert.DoesNotContain(model.Requests, r => r.Messages.Any(m => m.Role == ModelRole.Tool && m.Text == savedTool));
                        Assert.Equal(1, (await native.ObserveTenantSessionAsync(new(tenant, instance, executor, sessionId))).OpenPages);
                    }
                }
                Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsync("/api/v2/auth/logout", null)).StatusCode);
            }
            clock.Offset = TimeSpan.FromMinutes(6);
            var resumedModel = new ApprovalRecordingModel { ProposedBrowserCommand = proposal }; var resumedExecution = new BrowserRestartExecution { Enabled = false };
            await using (var baseHost = new TenantApprovalSqliteFactory(root, resumedModel))
            await using (var second = BrowserRestartHost(baseHost, root, page.Origin!, clock, resumedExecution))
            {
                using var owner = second.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
                await Login(owner, "owner@example.test", "explicit-synthetic-test-password"); var api = $"/api/v2/tenants/{tenant}";
                var native = Assert.IsType<NativePlaywrightBrowser>(second.Services.GetRequiredService<IBrowser>());
                Assert.Equal(0, (await native.ObserveTenantSessionAsync(new(tenant, instance, executor, sessionId))).LiveContexts);
                var request = (await second.Services.GetRequiredService<ITenantApprovalStore>().FindApprovalAsync(tenant, requestId))!;
                if (stage == "pending")
                {
                    var view = await Json(await owner.GetAsync(api + $"/approvals/{requestId}")); Assert.True(view.GetProperty("review").GetProperty("canDecide").GetBoolean());
                    Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync(api + $"/approvals/{requestId}/decision", new { decision = "approve", actionHash = request.Operation.ActionHash,
                        expectedRevision = view.GetProperty("securityRevision").GetInt64(), expectedRequestRevision = request.Revision })).StatusCode);
                }
                if (stage == "warm")
                {
                    Assert.NotNull(TenantApprovalRules.BrowserTarget(request.Operation.BrowserCommand!).ContextId);
                    var view = await Json(await owner.GetAsync(api + $"/approvals/{requestId}"));
                    Assert.False(view.GetProperty("review").GetProperty("canDecide").GetBoolean()); Assert.True(view.GetProperty("review").GetProperty("canCancel").GetBoolean());
                    Assert.Equal("browser_target_changed", view.GetProperty("review").GetProperty("decisionUnavailableReason").GetString());
                    Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsJsonAsync(api + $"/approvals/{requestId}/decision", new { decision = "approve", actionHash = request.Operation.ActionHash,
                        expectedRevision = view.GetProperty("securityRevision").GetInt64(), expectedRequestRevision = request.Revision })).StatusCode);
                    Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync(api + $"/approvals/{requestId}/cancel", new { actionHash = request.Operation.ActionHash,
                        expectedRevision = await Revision(owner, tenant), expectedRequestRevision = request.Revision })).StatusCode);
                }
                if (stage == "revoked")
                {
                    var human = (await Json(await owner.GetAsync(api + "/access/me"))).GetProperty("principalId").GetGuid();
                    await second.Services.GetRequiredService<TenantAccessService>().RevokeAssignmentAsync(human, tenant, browserAssignment, await Revision(owner, tenant));
                }
                Volatile.Write(ref resumedExecution.Enabled, true);
                var completed = await AwaitBrowserRestartRun(second.Services.GetRequiredService<ITenantRunStore>(), tenant, sourceId, false);
                if (stage is "revoked" or "abandoned")
                {
                    Assert.Equal(AgentRunStatus.Failed, completed.Status);
                    Assert.Equal(stage == "revoked" ? "authority-unavailable" : "side-effect-indeterminate", completed.Failure?.Code);
                    Assert.Empty(resumedModel.Requests);
                }
                else
                {
                    if (stage == "unknown")
                    {
                        Assert.Equal(AgentRunStatus.Failed, completed.Status); Assert.Equal("side-effect-indeterminate", completed.Failure?.Code);
                        Assert.Equal("External effect outcome is unknown and was not replayed.", completed.Failure?.Summary);
                    }
                    else Assert.True(completed.Status == AgentRunStatus.Completed && completed.Result?.OutcomeKind == AgentRunOutcomeKind.Response, $"{completed.Status}: {completed.Failure?.Code}: {completed.Failure?.Summary}");
                    Assert.True(AgentRunToolCallCheckpoint.TryRead(completed.Checkpoint, out var messages));
                    var tool = Assert.Single(messages!, m => m.Role == ModelRole.Tool && m.Name == ToolCatalog.RequestAuthorization).Text;
                    Assert.Contains(resumedModel.Requests, r => r.Messages.Any(m => m.Role == ModelRole.Tool && m.Text == tool));
                    using var result = JsonDocument.Parse(tool); Assert.False(result.RootElement.GetProperty("replayAllowed").GetBoolean());
                    Assert.False(result.RootElement.GetProperty("applicationOutcomeVerified").GetBoolean());
                    Assert.Equal(stage is not ("unknown" or "warm"), result.RootElement.GetProperty("commandCompleted").GetBoolean());
                    if (stage == "unknown") Assert.All(resumedModel.Requests, r => Assert.DoesNotContain(r.Tools ?? [], t => t.Name.StartsWith("browser.", StringComparison.Ordinal) || t.Name == ToolCatalog.RequestAuthorization));
                    if (savedTool is not null) Assert.Equal(savedTool, tool);
                }
                var currentReceipt = await second.Services.GetRequiredService<ITenantApprovedOperationStore>().FindOperationReceiptAsync(tenant, request.Operation.OperationId);
                if (receipt is not null) Assert.Equal(receipt, currentReceipt);
                if (stage == "warm") Assert.Null(currentReceipt);
                else Assert.Equal(stage is "unknown" or "abandoned" ? TenantOperationOutcome.Indeterminate : TenantOperationOutcome.Completed, currentReceipt!.Outcome);
                Assert.Equal(stage == "pending" ? 1 : 0, (await native.ObserveTenantSessionAsync(new(tenant, instance, executor, sessionId))).LiveContexts);
                var audits = await second.Services.GetRequiredService<IIamStore>().ReadAuditAsync(tenant, 200);
                Assert.Single(audits, a => a.Action == "approvals.request");
                Assert.Equal(stage == "warm" ? 0 : 1, audits.Count(a => a.Action == "operations.browser.command.dispatch"));
                Assert.Equal(stage == "warm" ? 0 : 1, audits.Count(a => a.Action == "operations.browser.command"));
                Assert.Null(await second.Services.GetRequiredService<ITenantApprovalStore>().FindOperationGrantAsync(tenant, requestId));
            }
        }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
