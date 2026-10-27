using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    private sealed class IndependentWaitModel(string mode) : TenantBackgroundModel
    {
        public Guid ForeignSession { get; set; }
        public override async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Enqueue(request); await Task.Yield(); cancellationToken.ThrowIfCancellationRequested();
            Assert.DoesNotContain(request.Tools!, t => t.Name == "background.start");
            var tools = request.Messages.Where(m => m.Role == ModelRole.Tool).ToArray();
            var reads = tools.Where(m => m.Name == ToolCatalog.RequestAuthorization).ToArray();
            var waits = tools.Where(m => m.Name == ToolCatalog.ExecutionWait).ToArray();
            ModelToolCall? call = null;
            if (mode is "review" or "source-revoked" && reads.Length == 0)
                call = new("read-before-wait", ToolCatalog.RequestAuthorization,
                    """{"operation":"inspect_workspace","read":{"tool":"workspace.read","arguments":{"path":"/home/wait-source.txt"}}}""");
            else if (waits.Length == 0 || mode == "repeat" && waits.Length == 1)
            {
                Assert.Contains(request.Tools!, t => t.Name == ToolCatalog.ExecutionWait);
                if (reads.Length > 0) Assert.Contains("\"readAuthorized\":true", reads[^1].Text);
                call = new("independent-wait-" + (waits.Length + 1), ToolCatalog.ExecutionWait, mode == "foreign"
                    ? JsonSerializer.Serialize(new { mode = "tasks", taskIds = new[] { ForeignSession }, until = "all", timeoutSeconds = 30 })
                    : """{"mode":"duration","seconds":30}""");
            }
            if (call is not null)
            {
                yield return new ModelToolCallEvent(call); yield return new ModelCompleted(ModelStopReason.ToolCalls); yield break;
            }
            if (mode == "foreign") Assert.DoesNotContain("\"reason\":\"condition_met\"", waits.Single().Text);
            else foreach (var wait in waits) Assert.Contains("\"reason\":\"elapsed\"", wait.Text);
            yield return new ModelToolCallEvent(new("independent-wait-done", ToolCatalog.WorkComplete,
                """{"outcome":"Response","summary":"Independent wait finished from its saved result.","attentionRequired":false}"""));
            yield return new ModelCompleted(ModelStopReason.ToolCalls);
        }
    }

    public static TheoryData<bool, string, string> IndependentWaitCases
    {
        get
        {
            var cases = new TheoryData<bool, string, string>();
            foreach (var sqlite in new[] { false, true })
            foreach (var origin in new[] { "scheduled", "manual" })
            foreach (var mode in new[] { "duration", "repeat", "cancel", "foreign", "review", "source-revoked" }) cases.Add(sqlite, origin, mode);
            foreach (var origin in new[] { "scheduled", "manual" })
            foreach (var mode in new[] { "restart", "resume-restart" }) cases.Add(true, origin, mode);
            cases.Add(false, "manual", "requester-revoked"); cases.Add(true, "manual", "requester-revoked");
            return cases;
        }
    }

    [Theory(Timeout = 120000)] [MemberData(nameof(IndependentWaitCases))]
    public async Task Independent_Automation_wait_preserves_same_Run_fresh_generation_current_authority_and_durable_recovery(bool sqlite, string origin, string mode)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-independent-wait-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        WebApplicationFactory<Program>? reopened = null; HttpClient? reopenedOwner = null; HttpClient? invoker = null;
        var executionEnabled = true; var clock = new FakeTimeProvider(DateTimeOffset.UtcNow); var model = new IndependentWaitModel(mode);
        void Configure(IServiceCollection services)
        {
            services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(clock);
            services.AddSingleton<ILoggerProvider>(new BackgroundFailureLogs(model));
            services.Replace(ServiceDescriptor.Singleton<ITenantRunExecution>(p => new AttachmentExecutionGate(() => Volatile.Read(ref executionEnabled), new TenantRunExecution(
                p.GetRequiredService<ITenantRuntimeStore>(), p.GetRequiredService<ITenantRunStore>(), p.GetRequiredService<ITenantRunDispatcher>(), p.GetRequiredService<IIdGenerator>(), p.GetRequiredService<TimeProvider>(), p.GetRequiredService<TenantBackgroundCompletionService>()))));
        }
        await using WebApplicationFactory<Program> first = sqlite ? new NativeTenantSqliteFactory(root, model, Configure) : new NativeTenantMemoryFactory(root, model, Configure);
        var factory = first;
        using var firstOwner = first.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") }); var owner = firstOwner;
        try
        {
            var setup = await StartTenantBackground(factory, owner, submit: false,
                fileCapabilities: [ToolCatalog.ExecutionWait, ToolCatalog.WorkspaceRead], independentTask: true);
            model.ForeignSession = setup.Session;
            var tenant = setup.Tenant; var api = $"/api/v2/tenants/{tenant}";
            var sessions = factory.Services.GetRequiredService<ITenantSessionStore>(); var parent = (await sessions.FindTenantSessionAsync(tenant, setup.Session))!;
            var human = (await Json(await owner.GetAsync(api + "/access/me"))).GetProperty("principalId").GetGuid();
            var access = factory.Services.GetRequiredService<TenantAccessService>();
            var execution = await access.SaveRoleAsync(human, tenant, null, "Independent wait executor", ["automations.run"], await Revision(owner, tenant));
            await access.AssignRoleAsync(human, tenant, parent.Owner.ExecutorPrincipalId, execution.RoleId, AccessScope.ForTenant(tenant), await Revision(owner, tenant));
            RoleAssignment? sourceRead = null; RoleAssignment? requesterRead = null; var expectedHuman = human;
            if (mode is "review" or "source-revoked")
            {
                var reads = await access.SaveRoleAsync(human, tenant, null, "Independent wait source reader", ["agents.read", "sessions.read", "workspaces.read"], await Revision(owner, tenant));
                sourceRead = await access.AssignRoleAsync(human, tenant, parent.Owner.ExecutorPrincipalId, reads.RoleId, AccessScope.ForTenant(tenant), await Revision(owner, tenant));
                Assert.Equal(HttpStatusCode.NoContent, (await owner.PutAsync(api + $"/agents/{parent.Owner.AgentInstanceId}/workspace/content?path=%2Fhome%2Fwait-source.txt&expectedRevision={await Revision(owner, tenant)}",
                    new ByteArrayContent("Immutable independent pre-wait evidence"u8.ToArray()))).StatusCode);
                Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(api + "/operation-policies/workspaces.read", new { mode = "always", independentReview = false,
                    expectedRevision = await Revision(owner, tenant), expectedPolicyRevision = 0 })).StatusCode);
            }
            if (mode == "requester-revoked")
            {
                var account = await factory.Services.GetRequiredService<IdentityService>().RegisterAsync("wait-invoker@example.test", "Actual wait invoker", "strong-wait-invoker-password");
                var directory = await access.SaveRoleAsync(human, tenant, null, "Unrelated membership", ["members.read"], await Revision(owner, tenant));
                var invitation = await access.InviteAsync(human, tenant, account.LoginKey, directory.RoleId, await Revision(owner, tenant));
                var member = await access.AcceptInvitationAsync(account.AccountId, tenant, invitation.Invitation.InvitationId, invitation.Token); expectedHuman = member.PrincipalId;
                var invoke = await access.SaveRoleAsync(human, tenant, null, "Invoke without Chat", ["agents.read", "automations.read", "automations.run", "sessions.create", "agent-runs.create"], await Revision(owner, tenant));
                requesterRead = await access.AssignRoleAsync(human, tenant, member.PrincipalId, invoke.RoleId, AccessScope.ForTenant(tenant), await Revision(owner, tenant));
                invoker = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") });
                await Login(invoker, account.LoginKey, "strong-wait-invoker-password");
            }
            var id = await TenantCredentialAccessTests.AdmitIndependentTask(factory, owner, tenant, parent.Owner.AgentInstanceId,
                "Wait within the actual admitted task without any parent Chat authority.", clock, origin, invoker);
            var runs = factory.Services.GetRequiredService<ITenantRunStore>(); var runtime = factory.Services.GetRequiredService<ITenantRuntimeStore>();
            var initial = (await runs.FindTenantRunAsync(tenant, id))!;
            Assert.NotEqual(setup.Session, initial.SessionId); Assert.Null(initial.Admission.ExecutionContext!.ParentRunId); Assert.Null(initial.Owner.ProfileIdOrNull);
            Assert.Equal(origin == "scheduled" ? (Guid?)null : expectedHuman, initial.Admission.ExecutionContext.OriginalInitiatorPrincipalId);
            var reviews = new List<TenantApprovalRequest>(); var beforeWakeRequests = 0;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            async Task<AgentRun> PumpUntil(Func<AgentRun, bool> condition)
            {
                var trace = new List<string>();
                while (true)
                {
                    await factory.Services.GetRequiredService<TenantApprovalService>().DispatchPendingAsync(timeout.Token);
                    await factory.Services.GetRequiredService<ITenantRunExecution>().ExecuteRunnableAsync(8, timeout.Token);
                    var current = (await CurrentApprovalExecution(runs, tenant, id))!;
                    var state = $"{clock.GetUtcNow():O} {current.Status} rev={current.Revision} next={current.NextRetryAtUtc:O} wait={current.Wait?.DeadlineUtc:O} signal={current.Checkpoint?.AwaitingSignalAdmission} generation={current.Claim?.Generation} model={model.Requests.Count}";
                    if (trace.LastOrDefault() != state) { trace.Add(state); if (trace.Count > 20) trace.RemoveAt(0); }
                    if (current.Status == AgentRunStatus.WaitingForApproval && reviews.All(r => r.RequestId != current.ApprovalRequestId))
                    {
                        var request = (await factory.Services.GetRequiredService<ITenantApprovalStore>().FindApprovalAsync(tenant, current.ApprovalRequestId!.Value))!; reviews.Add(request);
                        Assert.Equal(origin == "scheduled" ? (Guid?)null : expectedHuman, request.OriginalInitiatorPrincipalId);
                        var detail = await Json(await owner.GetAsync(api + $"/approvals/{request.RequestId}"));
                        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync(api + $"/approvals/{request.RequestId}/decision", new { decision = "approve", actionHash = request.Operation.ActionHash,
                            expectedRevision = detail.GetProperty("securityRevision").GetInt64(), expectedRequestRevision = request.Revision })).StatusCode);
                    }
                    if (condition(current)) return current;
                    // The incumbent host may defer a signal while the suspended generation releases capacity.
                    // Advance only its persisted retry deadline, rather than waiting on a frozen clock.
                    if (current.Status == AgentRunStatus.WaitingToRetry && current.NextRetryAtUtc is { } retry && retry > clock.GetUtcNow())
                        clock.Advance(retry - clock.GetUtcNow());
                    Assert.False(current.IsTerminal, $"Unexpected terminal {current.Status}: {current.Failure?.Code} {current.Failure?.Summary}; {current.Result?.Text}; " + string.Join(" | ", model.Failures));
                    try { await Task.Delay(20, timeout.Token); }
                    catch (OperationCanceledException) { throw new Xunit.Sdk.XunitException("Independent wait did not settle: " + string.Join(" | ", trace) + " Errors: " + string.Join(" | ", model.Failures)); }
                }
            }
            var waiting = await PumpUntil(r => mode == "foreign" ? r.IsTerminal : r.Status == AgentRunStatus.WaitingForSignal);
            if (mode != "foreign")
            {
                Assert.Null(waiting.Claim); Assert.Equal(1, waiting.WaitCount); Assert.Equal(1, waiting.AttemptCount); Assert.NotNull(waiting.Wait!.SuspendedGeneration);
                var before = model.Requests.Count; beforeWakeRequests = before;
                await factory.Services.GetRequiredService<ITenantRunExecution>().ExecuteRunnableAsync(8);
                Assert.Equal(waiting.Revision, (await runs.FindTenantRunAsync(tenant, waiting.AgentRunId))!.Revision); Assert.Equal(before, model.Requests.Count);
                await Assert.ThrowsAsync<AccessDeniedException>(async () => await runtime.ApplyTenantRuntimeCommandAsync(waiting.Owner, waiting.AgentRunId,
                    new AgentRunCommand.Renew(waiting.Revision, clock.GetUtcNow(), waiting.Wait.SuspendedGeneration!.Value, clock.GetUtcNow().AddMinutes(5)), Guid.NewGuid(), runtimeGeneration: waiting.Wait.SuspendedGeneration));
                await ((AgentCore.Api.Realtime.TenantSessionRuntimeHost)factory.Services.GetRequiredService<ITenantRunDispatcher>())
                    .WaitForGenerationReleaseAsync(waiting.SessionId, waiting.AgentRunId, waiting.Wait.SuspendedGeneration.Value, timeout.Token);
                Volatile.Write(ref executionEnabled, false);
                if (mode == "source-revoked") await access.RevokeAssignmentAsync(human, tenant, sourceRead!.AssignmentId, await Revision(owner, tenant));
                if (mode == "requester-revoked") await access.RevokeAssignmentAsync(human, tenant, requesterRead!.AssignmentId, await Revision(owner, tenant));
                if (mode == "cancel")
                    Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync(api + $"/agent-runs/{waiting.AgentRunId}/cancel", new { expectedRevision = await Revision(owner, tenant), expectedRunRevision = waiting.Revision })).StatusCode);
                clock.Advance(TimeSpan.FromSeconds(30));
                if (mode == "resume-restart")
                {
                    waiting = await runtime.ApplyTenantRuntimeCommandAsync(waiting.Owner, waiting.AgentRunId,
                        new AgentRunCommand.ResumeWait(waiting.Revision, clock.GetUtcNow(), Guid.NewGuid(), clock.GetUtcNow().AddMinutes(5)), Guid.NewGuid());
                    Assert.Equal(1, waiting.AttemptCount);
                }
                if (mode is "restart" or "resume-restart")
                {
                    await first.DisposeAsync(); model = new IndependentWaitModel(mode) { ForeignSession = setup.Session };
                    reopened = new NativeTenantSqliteFactory(root, model, Configure); factory = reopened;
                    reopenedOwner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") }); owner = reopenedOwner;
                    await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
                    runs = factory.Services.GetRequiredService<ITenantRunStore>(); runtime = factory.Services.GetRequiredService<ITenantRuntimeStore>();
                    var retained = (await runs.FindTenantRunAsync(tenant, waiting.AgentRunId))!;
                    Assert.Equal(JsonSerializer.Serialize(waiting.Wait), JsonSerializer.Serialize(retained.Wait));
                    Assert.Equal(JsonSerializer.Serialize(waiting.Checkpoint), JsonSerializer.Serialize(retained.Checkpoint));
                    if (mode == "resume-restart") clock.Advance(TimeSpan.FromMinutes(6));
                }
                if (mode == "repeat")
                {
                    Volatile.Write(ref executionEnabled, true);
                    waiting = await PumpUntil(r => r.Status == AgentRunStatus.WaitingForSignal && r.WaitCount == 2);
                    await ((AgentCore.Api.Realtime.TenantSessionRuntimeHost)factory.Services.GetRequiredService<ITenantRunDispatcher>())
                        .WaitForGenerationReleaseAsync(waiting.SessionId, waiting.AgentRunId, waiting.Wait!.SuspendedGeneration!.Value, timeout.Token);
                    Assert.Equal(1, waiting.AttemptCount); Volatile.Write(ref executionEnabled, false); clock.Advance(TimeSpan.FromSeconds(30));
                }
            }
            Volatile.Write(ref executionEnabled, true);
            var done = mode == "foreign" ? waiting : await PumpUntil(r => r.IsTerminal);
            Assert.Equal(mode == "cancel" ? AgentRunStatus.Cancelled : mode is "source-revoked" or "requester-revoked" ? AgentRunStatus.Failed : AgentRunStatus.Completed, done.Status);
            Assert.Equal(waiting.AgentRunId, done.AgentRunId); Assert.Equal(waiting.ActivationId, done.ActivationId); Assert.Equal(waiting.ResponseId, done.ResponseId);
            Assert.Equal(1, done.AttemptCount); Assert.Null(done.Wait);
            Assert.Equal(mode == "foreign" ? 0 : mode == "repeat" ? 2 : 1, done.WaitCount);
            var successful = mode is not ("source-revoked" or "requester-revoked" or "cancel");
            var history = await Json(await owner.GetAsync(api + $"/sessions/{done.SessionId}/messages?limit=100"));
            var answers = history.GetProperty("items").EnumerateArray().Where(e => e.GetProperty("role").GetString() == "assistant" && e.GetProperty("text").GetString() == "Independent wait finished from its saved result.").ToArray();
            Assert.Equal(successful ? 1 : 0, answers.Length);
            Assert.True(AgentRunToolCallCheckpoint.TryRead(done.Checkpoint, out var messages));
            Assert.Equal(mode == "foreign" ? 1 : mode == "repeat" ? 2 : mode == "cancel" ? 0 : 1, messages!.Count(m => m.Role == ModelRole.Tool && m.Name == ToolCatalog.ExecutionWait));
            Assert.Equal(mode is "review" or "source-revoked" ? 1 : 0, reviews.Count);
            if (reviews.Count > 0) Assert.Equal(ExecutionOrigin.ApprovalContinuation, done.Admission.ExecutionContext!.Origin);
            if (mode is "source-revoked" or "requester-revoked") Assert.Equal(beforeWakeRequests, model.Requests.Count);
            await factory.Services.GetRequiredService<ITenantRunExecution>().ExecuteRunnableAsync(8);
            Assert.Equal(done.Revision, (await runs.FindTenantRunAsync(tenant, done.AgentRunId))!.Revision);
            Assert.Equal(0, factory.Services.GetRequiredService<AgentCore.Api.Http.OutboundHttpProbe>().Attempts);
        }
        finally
        {
            invoker?.Dispose(); reopenedOwner?.Dispose(); if (reopened is not null) await reopened.DisposeAsync(); await first.DisposeAsync();
            SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
