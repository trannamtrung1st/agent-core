using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AgentCore.Application.Context;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    private sealed class IndependentChildModel(string mode) : TenantBackgroundModel
    {
        public TaskCompletionSource RootEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Start { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Acknowledged { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Finish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Enqueue(request); await Task.Yield(); cancellationToken.ThrowIfCancellationRequested();
            if (request.Context!.Sections.Any(s => s.Id == ContextSectionId.CoreBackgroundReport))
            {
                yield return new ModelSemanticResponseReady(new("Independent child report delivered.", new(ModelSpeechMode.None, null), []));
                yield return new ModelCompleted(ModelStopReason.Completed); yield break;
            }
            var task = Assert.Single(request.Context.Sections, s => s.Id == ContextSectionId.RunTask);
            var child = task.Source == ContextSourceKind.AgentRun;
            var leaf = child && (mode != "nested" || task.Message.Text.Contains("leaf evidence", StringComparison.Ordinal));
            var results = request.Messages.Where(m => m.Role == ModelRole.Tool).ToArray(); ModelToolCall call;
            if (leaf)
            {
                if (mode == "child-review" && !results.Any(m => m.ToolCallId == "child-read"))
                    call = new("child-read", ToolCatalog.RequestAuthorization, """{"operation":"inspect_workspace","read":{"tool":"workspace.read","arguments":{"path":"/home/child-evidence.txt"}}}""");
                else
                {
                    if (mode == "child-review") Assert.Contains("\"readAuthorized\":true", results.Single(m => m.ToolCallId == "child-read").Text);
                    call = new("leaf-finish", ToolCatalog.WorkComplete, """{"outcome":"Response","summary":"Independent child evidence","attentionRequired":false}""");
                }
            }
            else
            {
                if (!child) { RootEntered.TrySetResult(); await Start.Task.WaitAsync(cancellationToken); }
                var start = results.SingleOrDefault(m => m.ToolCallId == "start-request");
                var direct = results.SingleOrDefault(m => m.Name == ToolCatalog.BackgroundStart);
                var objective = mode == "nested" && !child ? "Independent level-one evidence" : "Independent leaf evidence";
                var proposal = new { objective, title = "Independent exact child", reportCompletion = true };
                if (start is null)
                    call = new("start-request", ToolCatalog.RequestAuthorization, JsonSerializer.Serialize(new { operation = "start_background_work", task = proposal }));
                else
                {
                    using var startJson = JsonDocument.Parse(start.Text);
                    if (startJson.RootElement.TryGetProperty("decision", out var decision) && decision.GetString() == "allowed" && direct is null)
                        call = new("direct-start", ToolCatalog.BackgroundStart, JsonSerializer.Serialize(proposal));
                    else
                    {
                        using var actual = JsonDocument.Parse(direct?.Text ?? start.Text);
                        if (!actual.RootElement.TryGetProperty("backgroundSessionId", out var childId) || childId.ValueKind != JsonValueKind.String)
                        {
                            call = mode == "reject" && !results.Any(m => m.ToolCallId == "repeat-refused")
                                ? new("repeat-refused", ToolCatalog.RequestAuthorization, JsonSerializer.Serialize(new { operation = "start_background_work", task = proposal }))
                                : new("refused-finish", ToolCatalog.WorkComplete, """{"outcome":"Response","summary":"Independent child creation refused.","attentionRequired":false}""");
                        }
                        else if (mode == "grant-read-denied")
                        {
                            var denied = results.SingleOrDefault(m => m.Name == ToolCatalog.BackgroundInspect);
                            if (denied is null) call = new("denied-inspect", ToolCatalog.BackgroundInspect, JsonSerializer.Serialize(new { backgroundSessionId = childId.GetGuid() }));
                            else
                            {
                                Assert.DoesNotContain("Independent child evidence", denied.Text); Assert.DoesNotContain("sourceAgentRunId", denied.Text);
                                call = new("unreadable-finish", ToolCatalog.WorkComplete, """{"outcome":"Response","summary":"Independent child completion could not be read.","attentionRequired":false}""");
                            }
                        }
                        else if (mode.StartsWith("report", StringComparison.Ordinal))
                            call = new("report-parent-finish", ToolCatalog.WorkComplete, """{"outcome":"Response","summary":"Independent parent requested its child report.","attentionRequired":false}""");
                        else
                        {
                            var session = childId.GetGuid(); var wait = results.SingleOrDefault(m => m.Name == ToolCatalog.ExecutionWait);
                            if (wait is null)
                                call = mode is "completion-review" or "restart-read"
                                    ? new("wait", ToolCatalog.ExecutionWait, """{"mode":"duration","seconds":1}""")
                                    : new("wait", ToolCatalog.ExecutionWait, JsonSerializer.Serialize(new { mode = "background", backgroundSessionIds = new[] { session }, until = "all", timeoutSeconds = 30 }));
                            else if (mode is "completion-review" or "restart-read" && !results.Any(m => m.ToolCallId == "completion-read"))
                                call = new("completion-read", ToolCatalog.RequestAuthorization, JsonSerializer.Serialize(new { operation = "inspect_background_completion", read = new { backgroundSessionId = session } }));
                            else if (!results.Any(m => m.Name == ToolCatalog.BackgroundList))
                                call = new("list", ToolCatalog.BackgroundList, """{"scope":"pending_results","limit":1}""");
                            else if (!results.Any(m => m.Name == ToolCatalog.BackgroundInspect))
                            {
                                using var list = JsonDocument.Parse(results.Single(m => m.Name == ToolCatalog.BackgroundList).Text);
                                Assert.Equal(1, list.RootElement.GetProperty("pendingCount").GetInt32());
                                call = new("inspect", ToolCatalog.BackgroundInspect, JsonSerializer.Serialize(new { backgroundSessionId = session }));
                            }
                            else if (!results.Any(m => m.Name == ToolCatalog.BackgroundTake))
                            {
                                using var inspect = JsonDocument.Parse(results.Single(m => m.Name == ToolCatalog.BackgroundInspect).Text);
                                Assert.Equal("Independent child evidence", inspect.RootElement.GetProperty("summary").GetString());
                                call = new("take", ToolCatalog.BackgroundTake, JsonSerializer.Serialize(new { backgroundSessionId = session, revision = inspect.RootElement.GetProperty("inbox").GetProperty("revision").GetInt64() }));
                            }
                            else if (!results.Any(m => m.Name == ToolCatalog.BackgroundAcknowledge))
                            {
                                using var take = JsonDocument.Parse(results.Single(m => m.Name == ToolCatalog.BackgroundTake).Text);
                                call = new("ack", ToolCatalog.BackgroundAcknowledge, JsonSerializer.Serialize(new { backgroundSessionId = session, revision = take.RootElement.GetProperty("inbox").GetProperty("revision").GetInt64(),
                                    token = take.RootElement.GetProperty("token").GetGuid(), usage = "Use the exact independent child evidence" }));
                            }
                            else
                            {
                                using var ack = JsonDocument.Parse(results.Single(m => m.Name == ToolCatalog.BackgroundAcknowledge).Text);
                                Assert.True(ack.RootElement.GetProperty("acknowledgmentPending").GetBoolean());
                                if (!child) { Acknowledged.TrySetResult(); await Finish.Task.WaitAsync(cancellationToken); }
                                call = new("parent-finish", ToolCatalog.WorkComplete, """{"outcome":"Response","summary":"Independent child evidence","attentionRequired":false}""");
                            }
                        }
                    }
                }
            }
            Assert.Contains(request.Tools!, t => t.Name == call.Name);
            yield return new ModelToolCallEvent(call); yield return new ModelCompleted(ModelStopReason.ToolCalls);
        }
    }

    public static TheoryData<bool, string, string> IndependentChildCases
    {
        get
        {
            var cases = new TheoryData<bool, string, string>();
            foreach (var sqlite in new[] { false, true })
            foreach (var origin in new[] { "scheduled", "manual" })
            foreach (var mode in new[] { "allowed", "review", "grant-run", "grant-session", "reject", "child-review", "completion-review", "nested" }) cases.Add(sqlite, origin, mode);
            foreach (var sqlite in new[] { false, true })
            foreach (var mode in new[] { "report", "report-review", "report-read", "revoked-read", "cancel-ack" }) cases.Add(sqlite, "scheduled", mode);
            foreach (var origin in new[] { "scheduled", "manual" }) cases.Add(true, origin, "restart-creation");
            cases.Add(true, "scheduled", "restart-read"); cases.Add(true, "scheduled", "report-restart");
            cases.Add(false, "manual", "report-review"); cases.Add(true, "manual", "report-review");
            foreach (var sqlite in new[] { false, true }) foreach (var origin in new[] { "scheduled", "manual" }) cases.Add(sqlite, origin, "report-policy");
            foreach (var sqlite in new[] { false, true }) { cases.Add(sqlite, "scheduled", "grant-read-denied"); cases.Add(sqlite, "scheduled", "grant-missing-secondary"); }
            return cases;
        }
    }

    [Theory(Timeout = 180000)] [MemberData(nameof(IndependentChildCases))]
    public async Task Independent_tasks_create_exact_children_and_consume_or_report_only_current_owned_completion(bool sqlite, string origin, string mode)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-independent-child-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow); var model = new IndependentChildModel(mode); var enabled = true;
        WebApplicationFactory<Program>? reopened = null; HttpClient? reopenedOwner = null;
        void Configure(IServiceCollection services)
        {
            services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(clock); services.AddSingleton<ILoggerProvider>(new BackgroundFailureLogs(model));
            services.Replace(ServiceDescriptor.Singleton<ITenantRunExecution>(p => new AttachmentExecutionGate(() => Volatile.Read(ref enabled), new TenantRunExecution(
                p.GetRequiredService<ITenantRuntimeStore>(), p.GetRequiredService<ITenantRunStore>(), p.GetRequiredService<ITenantRunDispatcher>(), p.GetRequiredService<IIdGenerator>(), p.GetRequiredService<TimeProvider>()))));
        }
        await using WebApplicationFactory<Program> first = sqlite ? new NativeTenantSqliteFactory(root, model, Configure) : new NativeTenantMemoryFactory(root, model, Configure);
        var factory = first; using var firstOwner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") }); var owner = firstOwner;
        try
        {
            var setup = await StartTenantBackground(factory, owner, createAuthority: false, submit: false, independentTask: true,
                fileCapabilities: [ToolCatalog.BackgroundStart, ToolCatalog.ExecutionWait, ToolCatalog.BackgroundList, ToolCatalog.BackgroundInspect, ToolCatalog.BackgroundTake, ToolCatalog.BackgroundAcknowledge, ToolCatalog.WorkspaceRead]);
            var tenant = setup.Tenant; var api = $"/api/v2/tenants/{tenant}"; var sessions = factory.Services.GetRequiredService<ITenantSessionStore>();
            var seed = (await sessions.FindTenantSessionAsync(tenant, setup.Session))!; var human = (await Json(await owner.GetAsync(api + "/access/me"))).GetProperty("principalId").GetGuid();
            var access = factory.Services.GetRequiredService<TenantAccessService>();
            async Task<RoleAssignment> Grant(string name, string[] permissions)
            {
                var role = await access.SaveRoleAsync(human, tenant, null, name, permissions, await Revision(owner, tenant));
                return await access.AssignRoleAsync(human, tenant, seed.Owner.ExecutorPrincipalId, role.RoleId, AccessScope.ForTenant(tenant), await Revision(owner, tenant));
            }
            await Grant("Independent occurrence executor", ["automations.run"]);
            var createSession = await Grant("Create one child Session", ["sessions.create"]); var createRun = await Grant("Create one child Run", ["agent-runs.create"]);
            var reads = await Grant("Independent child source reader", ["agents.read", "sessions.read", "agent-runs.read", "workspaces.read"]);
            if (mode == "child-review")
                Assert.Equal(HttpStatusCode.NoContent, (await owner.PutAsync(api + $"/agents/{seed.Owner.AgentInstanceId}/workspace/content?path=%2Fhome%2Fchild-evidence.txt&expectedRevision={await Revision(owner, tenant)}", new ByteArrayContent("Original independent child bytes"u8.ToArray()))).StatusCode);
            var initialId = await TenantCredentialAccessTests.AdmitIndependentTask(factory, owner, tenant, seed.Owner.AgentInstanceId,
                "Independent parent owns its exact child objective and completion.", clock, origin);
            var runs = factory.Services.GetRequiredService<ITenantRunStore>(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            while (!model.RootEntered.Task.IsCompleted) { await factory.Services.GetRequiredService<ITenantRunExecution>().ExecuteRunnableAsync(8, timeout.Token); await Task.Delay(20, timeout.Token); }
            async Task Policy(string permission)
            {
                Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(api + $"/operation-policies/{permission}", new { mode = "always", independentReview = false, expectedRevision = await Revision(owner, tenant), expectedPolicyRevision = 0 })).StatusCode);
            }
            if (mode is "review" or "reject" or "report-review" or "restart-creation" or "report-restart") await Policy("agent-runs.create");
            if (mode is "completion-review" or "restart-read" or "report-read") await Policy("agent-runs.read");
            if (mode == "child-review") await Policy("workspaces.read");
            if (mode is "grant-run" or "grant-read-denied" or "grant-missing-secondary") await access.RevokeAssignmentAsync(human, tenant, createRun.AssignmentId, await Revision(owner, tenant));
            if (mode is "grant-session" or "grant-missing-secondary") await access.RevokeAssignmentAsync(human, tenant, createSession.AssignmentId, await Revision(owner, tenant));
            if (mode == "grant-read-denied") await access.RevokeAssignmentAsync(human, tenant, reads.AssignmentId, await Revision(owner, tenant));
            if (mode is not ("revoked-read" or "cancel-ack")) model.Finish.TrySetResult();
            model.Start.TrySetResult();
            var initial = (await runs.FindTenantRunAsync(tenant, initialId))!; var decisions = new List<TenantApprovalRequest>(); var seen = new HashSet<Guid>();
            AgentRun current = initial; var boundaryApplied = false;
            while (true)
            {
                await factory.Services.GetRequiredService<TenantApprovalService>().DispatchPendingAsync(timeout.Token);
                await factory.Services.GetRequiredService<ITenantRunExecution>().ExecuteRunnableAsync(8, timeout.Token);
                current = (await CurrentApprovalExecution(runs, tenant, initialId))!;
                var all = (await runs.ListTenantSessionRunsAsync(tenant, null, null, 0, 100, instanceId: seed.Owner.AgentInstanceId, ct: timeout.Token)).Items;
                var children = all.Where(r => r.Admission.ExecutionContext?.Origin == ExecutionOrigin.ImmediateBackground).ToArray();
                if (current.IsTerminal && children.Length == 0 && mode is not ("reject" or "grant-missing-secondary"))
                {
                    AgentRunToolCallCheckpoint.TryRead(current.Checkpoint, out var saved);
                    Assert.Fail($"Parent ended before creating its child: {current.Status} {current.Failure?.Code} {current.Failure?.Summary}; {current.Result?.Text}; "
                        + string.Join(" | ", saved?.Where(m => m.Role == ModelRole.Tool).Select(m => m.Name + ":" + m.Text) ?? []) + " Errors: " + string.Join(" | ", model.Failures));
                }
                var terminalChildren = new List<AgentRun>();
                foreach (var child in children) { var last = (await CurrentApprovalExecution(runs, tenant, child.AgentRunId))!; if (last.IsTerminal) terminalChildren.Add(last); }
                if (current.Wait is { Mode: AgentRunWaitMode.Duration } wait && terminalChildren.Count == children.Length && children.Length > 0 && wait.DeadlineUtc > clock.GetUtcNow()) clock.Advance(wait.DeadlineUtc - clock.GetUtcNow());
                if (current.Status == AgentRunStatus.WaitingToRetry && current.NextRetryAtUtc > clock.GetUtcNow()) clock.Advance(current.NextRetryAtUtc.Value - clock.GetUtcNow());
                foreach (var child in children)
                {
                    var last = (await CurrentApprovalExecution(runs, tenant, child.AgentRunId))!;
                    if (last.Status == AgentRunStatus.WaitingToRetry && last.NextRetryAtUtc > clock.GetUtcNow()) clock.Advance(last.NextRetryAtUtc.Value - clock.GetUtcNow());
                }
                if (mode.StartsWith("report", StringComparison.Ordinal) && current.IsTerminal && terminalChildren.Count == children.Length && children.Length > 0)
                {
                    if (mode == "report-policy" && !boundaryApplied)
                    {
                        boundaryApplied = true;
                        var agent = (await factory.Services.GetRequiredService<ITenantAgentStore>().FindTenantAgentAsync(tenant, seed.Owner.AgentInstanceId))!;
                        var changed = await owner.PatchAsJsonAsync(api + $"/agents/{seed.Owner.AgentInstanceId}/settings/triggerPolicy", new {
                            expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = agent.Instance.Revision, set = new { enabled = false }, clear = Array.Empty<string>() });
                        Assert.True(changed.IsSuccessStatusCode, await changed.Content.ReadAsStringAsync());
                    }
                    await factory.Services.GetRequiredService<TenantBackgroundCompletionService>().AdmitPendingAsync(8, timeout.Token);
                }
                var pending = await factory.Services.GetRequiredService<ITenantApprovalStore>().ListApprovalsAsync(tenant, null, null, new(Status: TenantApprovalStatus.Pending, PageSize: 100), ct: timeout.Token);
                // Canonical creation/read/reports are visible through their own stored request IDs;
                // enumerate active Runs as well because some protected observations are filtered from a raw page.
                var requests = new List<TenantApprovalRequest>(pending.Items);
                foreach (var run in all.Where(r => r.Status == AgentRunStatus.WaitingForApproval && r.ApprovalRequestId is not null))
                    if (await factory.Services.GetRequiredService<ITenantApprovalStore>().FindApprovalAsync(tenant, run.ApprovalRequestId!.Value, timeout.Token) is { } item && requests.All(r => r.RequestId != item.RequestId)) requests.Add(item);
                var restarted = false;
                foreach (var request in requests.Where(r => r.Status == TenantApprovalStatus.Pending && !seen.Contains(r.RequestId)))
                {
                    var restartAt = mode == "restart-creation" && request.Operation.BackgroundStart is not null || mode == "restart-read" && request.Operation.CompletionRead is not null
                        || mode == "report-restart" && request.Operation.BackgroundReport is not null;
                    if (restartAt && reopened is null)
                    {
                        Volatile.Write(ref enabled, false); await first.DisposeAsync(); model = new IndependentChildModel(mode); model.Start.TrySetResult(); model.Finish.TrySetResult();
                        reopened = new NativeTenantSqliteFactory(root, model, Configure); factory = reopened;
                        reopenedOwner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") }); owner = reopenedOwner;
                        await Login(owner, "owner@example.test", "explicit-synthetic-test-password"); runs = factory.Services.GetRequiredService<ITenantRunStore>(); sessions = factory.Services.GetRequiredService<ITenantSessionStore>();
                        Assert.Equal(JsonSerializer.Serialize(request), JsonSerializer.Serialize((await factory.Services.GetRequiredService<ITenantApprovalStore>().FindApprovalAsync(tenant, request.RequestId))!));
                        Volatile.Write(ref enabled, true); restarted = true; break;
                    }
                    seen.Add(request.RequestId); decisions.Add(request);
                    Assert.Equal(initial.ActivationId, request.RootActivationId); Assert.Equal(origin == "scheduled" ? (Guid?)null : human, request.OriginalInitiatorPrincipalId);
                    var detail = await Json(await owner.GetAsync(api + $"/approvals/{request.RequestId}"));
                    Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync(api + $"/approvals/{request.RequestId}/decision", new { decision = mode == "reject" ? "reject" : "approve", actionHash = request.Operation.ActionHash,
                        expectedRevision = detail.GetProperty("securityRevision").GetInt64(), expectedRequestRevision = request.Revision })).StatusCode);
                }
                if (restarted) continue;
                if (!boundaryApplied && model.Acknowledged.Task.IsCompleted && mode is "revoked-read" or "cancel-ack")
                {
                    boundaryApplied = true;
                    if (mode == "revoked-read") await access.RevokeAssignmentAsync(human, tenant, reads.AssignmentId, await Revision(owner, tenant));
                    else
                    {
                        current = (await CurrentApprovalExecution(runs, tenant, initialId))!;
                        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync(api + $"/agent-runs/{current.AgentRunId}/cancel", new { expectedRevision = await Revision(owner, tenant), expectedRunRevision = current.Revision })).StatusCode);
                    }
                    model.Finish.TrySetResult();
                }
                if (current.IsTerminal && (mode is "reject" or "grant-missing-secondary" || terminalChildren.Count == children.Length && children.Length > 0))
                {
                    if (!mode.StartsWith("report", StringComparison.Ordinal)) break;
                    if (all.Any(r => r.Admission.OutputContract == AgentRunOutputContract.CompletionReport && r.IsTerminal)) break;
                    if (mode == "report-policy" && (await runs.FindTenantCompletionAsync(children[0].Owner, terminalChildren[0].AgentRunId))?.Status == CompletionInboxStatus.Skipped) break;
                }
                await Task.Delay(20, timeout.Token);
            }
            Assert.Equal(mode == "revoked-read" ? AgentRunStatus.Failed : mode == "cancel-ack" ? AgentRunStatus.Cancelled : AgentRunStatus.Completed, current.Status);
            var schedulingPolicyBlocksReport = mode == "report-policy" && origin == "scheduled";
            var finalRuns = (await runs.ListTenantSessionRunsAsync(tenant, null, null, 0, 100, instanceId: seed.Owner.AgentInstanceId)).Items;
            var admitted = finalRuns.Where(r => r.Admission.ExecutionContext?.Origin == ExecutionOrigin.ImmediateBackground).ToArray();
            Assert.Equal(mode is "reject" or "grant-missing-secondary" ? 0 : mode == "nested" ? 2 : 1, admitted.Length);
            foreach (var child in admitted)
            {
                Assert.Equal(initial.Owner, child.Owner); Assert.Equal(initial.ActivationId, child.Admission.ExecutionContext!.RootActivationId);
                Assert.Equal(origin == "scheduled" ? (Guid?)null : human, child.Admission.ExecutionContext.OriginalInitiatorPrincipalId);
                var session = (await sessions.FindTenantSessionAsync(tenant, child.SessionId))!; Assert.Equal(seed.Owner.ExecutorPrincipalId, session.Owner.CreatedByPrincipalId);
                var leaf = (await CurrentApprovalExecution(runs, tenant, child.AgentRunId))!; Assert.Equal(AgentRunStatus.Completed, leaf.Status);
                var inbox = (await runs.FindTenantCompletionAsync(child.Owner, leaf.AgentRunId))!;
                Assert.Equal(schedulingPolicyBlocksReport ? CompletionInboxStatus.Skipped : mode.StartsWith("report", StringComparison.Ordinal) ? CompletionInboxStatus.Delivered : mode is "revoked-read" or "cancel-ack" or "grant-read-denied" ? CompletionInboxStatus.Pending : CompletionInboxStatus.Handled, inbox.Status);
            }
            Assert.Equal(mode.StartsWith("report", StringComparison.Ordinal) && !schedulingPolicyBlocksReport ? 1 : 0, finalRuns.Count(r => r.Admission.OutputContract == AgentRunOutputContract.CompletionReport));
            if (mode is "review" or "restart-creation" or "reject") Assert.Single(decisions, r => r.Operation.BackgroundStart is not null);
            if (mode.StartsWith("grant-", StringComparison.Ordinal) && mode != "grant-missing-secondary")
            {
                var request = Assert.Single(decisions); Assert.Equal(ApprovalKind.ExceptionalGrant, request.Kind);
                Assert.Equal(mode == "grant-session" ? "sessions.create" : "agent-runs.create", request.Permission);
                Assert.Equal(OperationGrantStatus.Consumed, (await factory.Services.GetRequiredService<ITenantApprovalStore>().FindOperationGrantAsync(tenant, request.RequestId))!.Status);
            }
            if (mode == "grant-missing-secondary") Assert.Empty(decisions);
            if (sqlite)
            {
                await using var db = factory.Services.GetRequiredService<IDbContextFactory<AgentCoreDbContext>>().CreateDbContext(); Assert.False(db.Database.HasPendingModelChanges());
                foreach (var request in decisions)
                {
                    if (origin == "manual")
                    {
                        var spoof = await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE TenantApprovalRequests SET OriginalInitiatorPrincipalId = NULL, PayloadJson = json_set(PayloadJson, '$.OriginalInitiatorPrincipalId', NULL) WHERE RequestId = {request.RequestId}"));
                        Assert.Contains("independent task consent requires its exact owned scheduled lineage", spoof.Message);
                    }
                    else
                    {
                        var otherRoot = Guid.NewGuid().ToString("D");
                        var spoof = await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE TenantApprovalRequests SET RootActivationId = {otherRoot}, PayloadJson = json_set(PayloadJson, '$.RootActivationId', {otherRoot}) WHERE RequestId = {request.RequestId}"));
                        Assert.Contains("independent task consent requires its exact owned scheduled lineage", spoof.Message);
                    }
                }
                if (admitted.Length == 0 && decisions.Count == 0)
                {
                    var savedId = initialId.ToString("D");
                    var before = await db.AgentRuns.AsNoTracking().Where(r => r.AgentRunId == savedId).Select(r => r.PayloadJson).SingleAsync();
                    await db.GetService<IMigrator>().MigrateAsync("20261020190000_TenantIndependentAccountConsent");
                    await db.Database.MigrateAsync();
                    Assert.Equal(before, await db.AgentRuns.AsNoTracking().Where(r => r.AgentRunId == savedId).Select(r => r.PayloadJson).SingleAsync());
                    Assert.False(db.Database.HasPendingModelChanges());
                    await db.Database.OpenConnectionAsync(); using var command = db.Database.GetDbConnection().CreateCommand();
                    command.CommandText = "SELECT count(*) FROM sqlite_master WHERE type = 'trigger'"; Assert.Equal(43L, await command.ExecuteScalarAsync());
                    command.CommandText = "PRAGMA foreign_key_check"; using var check = await command.ExecuteReaderAsync(); Assert.False(await check.ReadAsync());
                }
                else
                {
                    await Assert.ThrowsAsync<SqliteException>(() => db.GetService<IMigrator>().MigrateAsync("20261020190000_TenantIndependentAccountConsent"));
                    Assert.Contains("20261020200000_TenantIndependentChildControls", await db.Database.GetAppliedMigrationsAsync());
                }
            }
            Assert.Equal(0, factory.Services.GetRequiredService<AgentCore.Api.Http.OutboundHttpProbe>().Attempts);
        }
        finally
        {
            model.Start.TrySetResult(); model.Finish.TrySetResult(); reopenedOwner?.Dispose(); if (reopened is not null) await reopened.DisposeAsync(); await first.DisposeAsync();
            SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
