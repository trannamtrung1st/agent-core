using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Data.Common;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using AgentCore.Application.Context;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Api.Tests;

public sealed partial class TenantCredentialAccessTests
{
    private sealed class CompletionDatabaseFailures : DbCommandInterceptor
    {
        public readonly System.Collections.Concurrent.ConcurrentQueue<string> Failures = new();
        public override void CommandFailed(DbCommand command, CommandErrorEventData eventData) => Failures.Enqueue(eventData.Exception.Message);
        public override Task CommandFailedAsync(DbCommand command, CommandErrorEventData eventData, CancellationToken ct = default)
        { Failures.Enqueue(eventData.Exception.Message); return Task.CompletedTask; }
    }
    private sealed class AutomationCompletionModel : ILanguageModel
    {
        public ModelCapabilities Capabilities => new(true, true, true);
        public int Reports;
        public bool Quiet;
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield(); ct.ThrowIfCancellationRequested();
            if (request.Context?.Sections.Any(s => s.Id == ContextSectionId.CoreBackgroundReport) == true)
            {
                Interlocked.Increment(ref Reports);
                yield return new ModelSemanticResponseReady(new("The configured task finished.", new(ModelSpeechMode.None, null), []));
                yield return new ModelCompleted(ModelStopReason.Completed);
            }
            else
            {
                Assert.Contains(request.Tools ?? [], t => t.Name == ToolCatalog.WorkComplete);
                Assert.DoesNotContain(request.Tools ?? [], t => t.Name == "background.start");
                yield return new ModelToolCallEvent(new("finish-configured-work", ToolCatalog.WorkComplete,
                    JsonSerializer.Serialize(new { outcome = Quiet ? "NoAction" : "Response", summary = "Configured work completed independently.", attentionRequired = false })));
                yield return new ModelCompleted(ModelStopReason.ToolCalls);
            }
        }
    }
    public static TheoryData<bool, string> AutomationCompletionCases
    {
        get
        {
            var data = new TheoryData<bool, string>();
            foreach (var sqlite in new[] { false, true })
            foreach (var mode in new[] { "manual", "scheduled", "admission-review", "source-review", "creation-review", "communication-review", "grant", "reject", "revoked", "archived", "frozen-destination", "frozen-fixed-destination", "foreign-destination", "quiet", "destination-review", "policy-before-output", "revoke-before-output" }) data.Add(sqlite, mode);
            foreach (var recovery in new[] { "restart-source", "restart-creation", "restart-approved", "restart-admission-pending", "restart-admission-approved" }) data.Add(true, recovery);
            return data;
        }
    }
    [Theory(Timeout = 120000)] [MemberData(nameof(AutomationCompletionCases))]
    public async Task Configured_Automation_completion_preserves_independent_roots_and_exact_current_destination_consent(bool sqlite, string mode)
    {
        var recovery = mode.StartsWith("restart-", StringComparison.Ordinal) ? mode : null;
        var admissionRecovery = recovery is "restart-admission-pending" or "restart-admission-approved";
        mode = admissionRecovery ? "admission-review" : recovery is "restart-source" ? "source-review" : recovery is not null ? "creation-review" : mode;
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow); var model = new AutomationCompletionModel { Quiet = mode == "quiet" };
        var databaseFailures = new CompletionDatabaseFailures();
        void Configure(IServiceCollection services)
        {
            services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(clock);
            if (sqlite) services.AddDbContextFactory<AgentCoreDbContext>(options => options.AddInterceptors(databaseFailures));
            // This scenario drives the incumbent coordinator explicitly, including after cold recovery.
            foreach (var host in services.Where(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(AgentCore.Api.AgentRunHostedService)).ToArray()) services.Remove(host);
        }
        await using var first = new OAuthFactory(sqlite: sqlite, preserve: recovery is not null, execute: true, model: model, servicesOverride: Configure);
        OAuthFactory? reopened = null; HttpClient? reopenedOwner = null;
        try
        {
        var factory = first; using var firstOwner = ConnectedClient(factory); var owner = firstOwner;
        Assert.DoesNotContain(factory.Services.GetServices<IHostedService>(), service => service is AgentCore.Api.AgentRunHostedService);
        await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
        var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid(); var api = $"/api/v2/tenants/{tenant}";
        var human = (await Json(await owner.GetAsync(api + "/access/me"))).GetProperty("principalId").GetGuid();
        // Independent configured reporting must also work without background.start in the Definition.
        var draft = await Json(await owner.PostAsJsonAsync(api + "/agent-definitions", new { name = "Independent configured reporter", expectedRevision = await Revision(owner, tenant) }));
        var candidate = JsonNode.Parse(draft.GetProperty("candidate").GetRawText())!;
        var baseline = (await factory.Services.GetRequiredService<IBuiltInAgentDefinitionStore>().GetAsync("general-assistant", 35))!;
        candidate["triggerPolicy"] = JsonSerializer.SerializeToNode(baseline.TriggerPolicy, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var draftId = draft.GetProperty("draftId").GetGuid();
        var definition = await Json(await owner.PutAsJsonAsync(api + $"/definition-drafts/{draftId}", new { candidate, expectedRevision = await Revision(owner, tenant), expectedDraftRevision = 1 }));
        await Json(await owner.PostAsJsonAsync(api + $"/definition-drafts/{draftId}/publish", new { expectedRevision = await Revision(owner, tenant), expectedDraftRevision = definition.GetProperty("revision").GetInt64() }));
        var instance = (await Json(await owner.PostAsJsonAsync(api + "/agents/from-publication", new { definitionId = draft.GetProperty("definitionId").GetString(), version = 1, expectedRevision = await Revision(owner, tenant) }))).GetProperty("instance").GetProperty("instanceId").GetGuid();
        var agent = (await factory.Services.GetRequiredService<ITenantAgentStore>().FindTenantAgentAsync(tenant, instance))!;
        var access = factory.Services.GetRequiredService<TenantAccessService>();
        string[] permissions = ["automations.run", "sessions.create", "agent-runs.create", "sessions.read", "agent-runs.read", "agents.chat", "spaces.read"];
        var role = await access.SaveRoleAsync(human, tenant, null, "Configured completion executor", permissions, await Revision(owner, tenant));
        var assignment = await access.AssignRoleAsync(human, tenant, agent.PrincipalId, role.RoleId, AccessScope.ForTenant(tenant), await Revision(owner, tenant));
        async Task<Guid> Conversation(Guid targetInstance) => (await Json(await owner.PostAsJsonAsync(api + "/sessions", new { retentionKind = "saved", agentInstanceId = targetInstance, mode = "text", expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1 }))).GetProperty("sessionId").GetGuid();
        var destination = await Conversation(instance); var other = await Conversation(instance); var route = api + $"/agents/{instance}/automations";
        object Timing() => mode == "frozen-fixed-destination"
            ? new { kind = "fixedInterval", interval = 3600, anchorAtUtc = clock.GetUtcNow().AddMinutes(30).ToString("O"), endAtUtc = clock.GetUtcNow().AddDays(1).ToString("O"), maxOccurrences = 10 }
            : (object)new { kind = "oneShot", timeZone = "UTC", atUtc = clock.GetUtcNow().AddMinutes(30).ToString("O") };
        object Body(Guid target, long revision = 0) => new { enabled = true, name = "Configured report", instructions = "Finish independent work, then report only to the configured conversation.", schedule = Timing(),
            reportToSessionId = target, expectedRevision = Revision(owner, tenant).GetAwaiter().GetResult(), expectedInstanceRevision = 1, expectedAutomationRevision = revision, requiresTools = true };
        if (mode == "foreign-destination")
        {
            var foreign = (await Json(await owner.PostAsJsonAsync(api + "/agents", new { templateId = "general-assistant", version = 36, expectedRevision = await Revision(owner, tenant) }))).GetProperty("instance").GetProperty("instanceId").GetGuid();
            var foreignSession = await Conversation(foreign);
            Assert.Equal(HttpStatusCode.Forbidden, (await owner.PostAsJsonAsync(route, Body(foreignSession))).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await owner.PostAsJsonAsync(route, Body(Guid.NewGuid()))).StatusCode);
            if (sqlite)
            {
                var valid = (await Json(await owner.PostAsJsonAsync(route, Body(destination)))).GetProperty("automationId").GetGuid();
                await using var db = factory.Services.GetRequiredService<IDbContextFactory<AgentCoreDbContext>>().CreateDbContext();
                await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE Automations SET ReportToSessionId={foreignSession.ToString("D")} WHERE AutomationId={valid.ToString("D")}"));
                Assert.Equal(destination, (await factory.Services.GetRequiredService<ITenantAutomationStore>().FindTenantAutomationAsync(tenant, valid))!.CompletionDelivery.SessionId);
            }
            return;
        }
        var created = await owner.PostAsJsonAsync(route, Body(destination)); Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var saved = await Json(created); var automation = saved.GetProperty("automationId").GetGuid();
        Assert.Equal("toSession", saved.GetProperty("completionDelivery").GetString()); Assert.Equal(destination, saved.GetProperty("reportToSessionId").GetGuid());
        var automationService = factory.Services.GetRequiredService<TenantAutomationService>(); var automationStore = factory.Services.GetRequiredService<ITenantAutomationStore>();
        Guid childId;
        if (mode == "admission-review") Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(api + "/operation-policies/agent-runs.create", new { mode = "always", independentReview = false, expectedRevision = await Revision(owner, tenant), expectedPolicyRevision = 0 })).StatusCode);
        if (mode == "scheduled")
        {
            clock.Advance(TimeSpan.FromMinutes(31)); await automationService.AdmitDueAsync();
            childId = Assert.Single((await factory.Services.GetRequiredService<ITenantRunStore>().ListTenantSessionRunsAsync(tenant, null, null, 0, 100, instanceId: instance)).Items).AgentRunId;
        }
        else
        {
            var admitted = await owner.PostAsJsonAsync(route + $"/{automation}/run", new { expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1, expectedAutomationRevision = 1 });
            if (mode == "admission-review")
            {
                Assert.True(admitted.StatusCode == HttpStatusCode.Accepted, await admitted.Content.ReadAsStringAsync() + " Database: " + string.Join("; ", databaseFailures.Failures));
                var ack = await Json(admitted); var requestId = ack.GetProperty("approvalRequestId").GetGuid();
                var request = (await factory.Services.GetRequiredService<ITenantApprovalStore>().FindApprovalAsync(tenant, requestId))!;
                childId = request.Operation.AutomationAdmission!.RunId;
                Assert.Null(await factory.Services.GetRequiredService<ITenantRunStore>().FindTenantRunAsync(tenant, childId));
                var detail = await Json(await owner.GetAsync(api + $"/approvals/{requestId}"));
                Assert.Contains(destination.ToString("D"), detail.GetProperty("review").GetProperty("changes").EnumerateArray().Single(c => c.GetProperty("field").GetString() == "admissionDestination").GetProperty("after").GetString());
                if (admissionRecovery)
                {
                    if (recovery == "restart-admission-approved")
                    {
                        var approved = await owner.PostAsJsonAsync(api + $"/approvals/{requestId}/decision", new { decision = "approve", actionHash = request.Operation.ActionHash, expectedRevision = await Revision(owner, tenant), expectedRequestRevision = request.Revision });
                        Assert.True(approved.IsSuccessStatusCode, await approved.Content.ReadAsStringAsync());
                    }
                    var path = first.Root; await first.DisposeAsync();
                    reopened = new OAuthFactory(path, sqlite: true, preserve: true, execute: true, model: model, servicesOverride: Configure); factory = reopened;
                    reopenedOwner = ConnectedClient(factory); owner = reopenedOwner;
                    await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
                    Assert.Equal(request.Operation, (await factory.Services.GetRequiredService<ITenantApprovalStore>().FindApprovalAsync(tenant, requestId))!.Operation);
                    Assert.Null(await factory.Services.GetRequiredService<ITenantRunStore>().FindTenantRunAsync(tenant, childId));
                }
                if (recovery != "restart-admission-approved")
                {
                    var decision = await owner.PostAsJsonAsync(api + $"/approvals/{requestId}/decision", new { decision = "approve", actionHash = request.Operation.ActionHash, expectedRevision = await Revision(owner, tenant), expectedRequestRevision = request.Revision });
                    Assert.True(decision.IsSuccessStatusCode, await decision.Content.ReadAsStringAsync());
                }
                await factory.Services.GetRequiredService<TenantApprovalService>().DispatchPendingAsync();
            }
            else { Assert.True(admitted.StatusCode == HttpStatusCode.Created, await admitted.Content.ReadAsStringAsync()); childId = (await Json(admitted)).GetProperty("runId").GetGuid(); }
        }
        var runs = factory.Services.GetRequiredService<ITenantRunStore>(); var execution = factory.Services.GetRequiredService<ITenantRunExecution>();
        async Task<AgentRun> Execute(Guid id)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30)); AgentRun run;
            do { await execution.ExecuteRunnableAsync(8, deadline.Token); run = (await runs.FindTenantRunAsync(tenant, id, deadline.Token))!; if (!run.IsTerminal) await Task.Delay(20, deadline.Token); } while (!run.IsTerminal);
            Assert.True(run.Status == AgentRunStatus.Completed, $"{run.Status}: {run.Failure?.Code} {run.Failure?.Summary}"); return run;
        }
        var child = await Execute(childId); var childRevision = child.Revision;
        Assert.Equal(mode == "scheduled" ? (Guid?)null : human, child.Admission.ExecutionContext!.OriginalInitiatorPrincipalId);
        Assert.Null(child.Admission.ExecutionContext.ParentRunId); Assert.Null(child.Admission.Activation.SourceSessionId);
        Assert.Equal(child.ActivationId, child.Admission.ExecutionContext.RootActivationId);
        var runtime = factory.Services.GetRequiredService<ITenantRuntimeStore>(); var source = (await runtime.ReadTenantBackgroundCompletionAsync(child.Owner, childId))!;
        Assert.NotNull(source.DestinationOccurrence); Assert.Equal(destination, source.DestinationOccurrence!.CompletionDelivery.SessionId);
        TenantBackgroundCompletionFactory.RequireParent(source);
        Assert.Throws<AccessDeniedException>(() => TenantBackgroundCompletionFactory.RequireParent(source with { DestinationOccurrence = null }));
        Assert.Throws<AccessDeniedException>(() => TenantBackgroundCompletionFactory.RequireParent(source with { Parent = (factory.Services.GetRequiredService<ITenantSessionStore>().FindTenantSessionAsync(tenant, other)).Result }));
        if (mode is "frozen-destination" or "frozen-fixed-destination")
        {
            var current = (await automationStore.FindTenantAutomationAsync(tenant, automation))!;
            var changed = await owner.PutAsJsonAsync(route + $"/{automation}", Body(other, current.Revision));
            Assert.True(changed.StatusCode == HttpStatusCode.OK, await changed.Content.ReadAsStringAsync() + " Database: " + string.Join("; ", databaseFailures.Failures));
        }
        if (mode is "revoked" or "grant")
        {
            await access.RevokeAssignmentAsync(human, tenant, assignment.AssignmentId, await Revision(owner, tenant));
            var reduced = await access.SaveRoleAsync(human, tenant, null, "Reduced completion executor", permissions.Where(p => p != (mode == "grant" ? "agent-runs.create" : "agents.chat")).ToArray(), await Revision(owner, tenant));
            await access.AssignRoleAsync(human, tenant, agent.PrincipalId, reduced.RoleId, AccessScope.ForTenant(tenant), await Revision(owner, tenant));
        }
        if (mode == "archived")
        {
            var target = (await factory.Services.GetRequiredService<ITenantSessionStore>().FindTenantSessionAsync(tenant, destination))!;
            var response = await owner.PostAsJsonAsync(api + $"/sessions/{destination}/archive", new { action = "archive", expectedRevision = await Revision(owner, tenant), expectedSessionRevision = target.Snapshot.Revision });
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        }
        async Task Policy(string key) => Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(api + "/operation-policies/" + key,
            new { mode = "always", independentReview = false, expectedRevision = await Revision(owner, tenant), expectedPolicyRevision = 0 })).StatusCode);
        if (mode == "source-review") await Policy("agent-runs.read");
        if (mode is "creation-review" or "reject") await Policy("agent-runs.create");
        if (mode == "communication-review") await Policy("agents.chat");
        if (mode == "destination-review") await Policy("sessions.read");
        var completions = factory.Services.GetRequiredService<TenantBackgroundCompletionService>(); var approvals = factory.Services.GetRequiredService<ITenantApprovalStore>(); var approvalService = factory.Services.GetRequiredService<TenantApprovalService>();
        await completions.AdmitPendingAsync(10, CancellationToken.None);
        if (mode is "source-review" or "creation-review" or "admission-review" or "communication-review" or "destination-review" or "grant" or "reject")
        {
            for (var consentStep = 0; consentStep < (mode == "destination-review" ? 2 : 1); consentStep++)
            {
            var request = Assert.Single((await approvals.ListApprovalsAsync(tenant, null, null, new(PageSize: 100))).Items, r => r.Status == TenantApprovalStatus.Pending);
            Assert.Equal(mode == "grant" ? ApprovalKind.ExceptionalGrant : ApprovalKind.Review, request.Kind);
            if (mode == "source-review" || mode == "destination-review" && consentStep == 0) Assert.NotNull(request.Operation.ReportSourceRead); else Assert.NotNull(request.Operation.BackgroundReport);
            Assert.Null((await runs.FindTenantCompletionAsync(child.Owner, childId))!.ReportActivationId);
            if (recovery is not null && !admissionRecovery)
            {
                if (recovery == "restart-approved")
                {
                    var accepted = await owner.PostAsJsonAsync(api + $"/approvals/{request.RequestId}/decision", new { decision = "approve", actionHash = request.Operation.ActionHash, expectedRevision = await Revision(owner, tenant), expectedRequestRevision = request.Revision });
                    Assert.True(accepted.IsSuccessStatusCode, await accepted.Content.ReadAsStringAsync());
                }
                var path = first.Root; await first.DisposeAsync();
                reopened = new OAuthFactory(path, sqlite: true, preserve: true, execute: true, model: model, servicesOverride: Configure); factory = reopened;
                reopenedOwner = ConnectedClient(factory); owner = reopenedOwner;
                Assert.DoesNotContain(factory.Services.GetServices<IHostedService>(), service => service is AgentCore.Api.AgentRunHostedService);
                await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
                runs = factory.Services.GetRequiredService<ITenantRunStore>(); execution = factory.Services.GetRequiredService<ITenantRunExecution>();
                runtime = factory.Services.GetRequiredService<ITenantRuntimeStore>(); approvals = factory.Services.GetRequiredService<ITenantApprovalStore>();
                completions = factory.Services.GetRequiredService<TenantBackgroundCompletionService>(); approvalService = factory.Services.GetRequiredService<TenantApprovalService>();
                Assert.Equal(request.Operation, (await approvals.FindApprovalAsync(tenant, request.RequestId))!.Operation);
                Assert.Equal(JsonSerializer.Serialize(source.DestinationOccurrence), JsonSerializer.Serialize((await runtime.ReadTenantBackgroundCompletionAsync(child.Owner, childId))!.DestinationOccurrence));
                Assert.Null((await runs.FindTenantCompletionAsync(child.Owner, childId))!.ReportActivationId);
            }
            var detail = await Json(await owner.GetAsync(api + $"/approvals/{request.RequestId}"));
            if (recovery != "restart-approved") Assert.True(detail.GetProperty("review").GetProperty("canDecide").GetBoolean(), detail.GetRawText());
            if (recovery != "restart-approved")
            {
            var decision = await owner.PostAsJsonAsync(api + $"/approvals/{request.RequestId}/decision", new { decision = mode == "reject" ? "reject" : "approve", actionHash = request.Operation.ActionHash, expectedRevision = await Revision(owner, tenant), expectedRequestRevision = request.Revision });
            Assert.True(decision.IsSuccessStatusCode, await decision.Content.ReadAsStringAsync());
            }
            await approvalService.DispatchPendingAsync(); await completions.AdmitPendingAsync(10, CancellationToken.None);
            }
        }
        var inbox = (await runs.FindTenantCompletionAsync(child.Owner, childId))!;
        if (mode is "revoked" or "archived" or "reject" or "quiet")
        {
            Assert.Equal(CompletionInboxStatus.Skipped, inbox.Status); Assert.Null(inbox.ReportActivationId); Assert.Equal(0, model.Reports);
        }
        else
        {
            Assert.Equal(CompletionInboxStatus.DeliveryQueued, inbox.Status);
            var report = Assert.Single((await runs.ListTenantSessionRunsAsync(tenant, destination, null, 0, 100)).Items);
            Assert.Equal(ExecutionOrigin.CompletionReport, report.Admission.ExecutionContext!.Origin); Assert.Equal(child.AgentRunId, report.Admission.ExecutionContext.ParentRunId);
            Assert.Equal(child.Admission.ExecutionContext.OriginalInitiatorPrincipalId, report.Admission.ExecutionContext.OriginalInitiatorPrincipalId);
            Assert.Equal(child.ActivationId, report.Admission.ExecutionContext.RootActivationId);
            Assert.Equal(child.AgentRunId, (await TenantExecutionLineage.RootAsync(report, runs, CancellationToken.None)).AgentRunId);
            if (mode is "policy-before-output" or "revoke-before-output")
            {
                if (mode == "policy-before-output") await Policy("agents.chat");
                else await access.RevokeAssignmentAsync(human, tenant, assignment.AssignmentId, await Revision(owner, tenant));
                using var deniedDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(20)); AgentRun failed;
                do { await execution.ExecuteRunnableAsync(8, deniedDeadline.Token); failed = (await runs.FindTenantRunAsync(tenant, report.AgentRunId))!;
                    if (!failed.IsTerminal) await Task.Delay(25, deniedDeadline.Token); } while (!failed.IsTerminal);
                Assert.NotEqual(AgentRunStatus.Completed, failed.Status);
                var target = (await factory.Services.GetRequiredService<ITenantSessionStore>().FindTenantSessionAsync(tenant, destination))!;
                Assert.DoesNotContain(target.Snapshot.Entries, e => e.Role == ConversationRole.Assistant);
                Assert.Equal(childRevision, (await runs.FindTenantRunAsync(tenant, childId))!.Revision); return;
            }
            var delivered = await Execute(report.AgentRunId); Assert.Equal(0, model.Reports); // Core formats the bounded receipt without a provider call.
            Assert.Contains("Configured work completed independently.", delivered.Result!.Text);
            Assert.Equal(CompletionInboxStatus.Delivered, (await runs.FindTenantCompletionAsync(child.Owner, childId))!.Status);
            await completions.AdmitPendingAsync(10, CancellationToken.None); Assert.Single((await runs.ListTenantSessionRunsAsync(tenant, destination, null, 0, 100)).Items);
            Assert.Empty((await runs.ListTenantSessionRunsAsync(tenant, other, null, 0, 100)).Items);
        }
        Assert.Equal(childRevision, (await runs.FindTenantRunAsync(tenant, childId))!.Revision); Assert.Equal(0, factory.Outbound);
        }
        finally
        {
            reopenedOwner?.Dispose(); if (reopened is not null) await reopened.DisposeAsync();
            if (recovery is not null) { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(first.Root)) Directory.Delete(first.Root, true); }
        }
    }
}
