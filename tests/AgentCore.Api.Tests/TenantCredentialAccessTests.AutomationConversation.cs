using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using AgentCore.Infrastructure.Providers.Synthetic;

namespace AgentCore.Api.Tests;

public sealed partial class TenantCredentialAccessTests
{
    private sealed class AutomationConversationModel : ILanguageModel
    {
        public ModelCapabilities Capabilities => new(true, true, true);
        public int Calls;
        public System.Collections.Concurrent.ConcurrentQueue<ModelRequest> Requests { get; } = new();
        public bool HoldAutomation;
        public bool UseDefault;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Enqueue(request); await Task.Yield(); ct.ThrowIfCancellationRequested(); var call = Interlocked.Increment(ref Calls);
            if (request.Context?.Sections.Any(s => s.Id == AgentCore.Application.Context.ContextSectionId.RunTask) == true)
                Assert.DoesNotContain(request.Context.Sections, s => s.Id == AgentCore.Application.Context.ContextSectionId.CoreBackground);
            if (UseDefault)
            {
                await foreach (var item in new ScriptedLanguageModel().GenerateAsync(request, ct)) yield return item;
                yield break;
            }
            if (call == 2 && HoldAutomation) { Entered.TrySetResult(); await Release.Task.WaitAsync(ct); }
            Assert.DoesNotContain(request.Tools ?? [], t => t.Name == ToolCatalog.WorkComplete);
            yield return new ModelSemanticResponseReady(new("An exact conversation task completed.", new(ModelSpeechMode.None, null), []));
            yield return new ModelCompleted(ModelStopReason.Completed);
        }
    }
    public static TheoryData<bool, string> ConversationAutomationCases
    {
        get
        {
            var cases = new TheoryData<bool, string>();
            foreach (var sqlite in new[] { false, true })
            foreach (var mode in new[] { "manual", "scheduled", "retirement", "chat-review", "read-review", "run-review", "grant", "reject", "stale-target", "no-reviewer", "missing-chat", "missing-read", "archived", "foreign", "invalid-report", "busy", "frozen-target", "cancel-registration", "revoke-before-output", "policy-before-output", "conversation-model", "conflicting-model" }) cases.Add(sqlite, mode);
            cases.Add(true, "restart-pending"); cases.Add(true, "restart-approved"); cases.Add(true, "database-guards"); cases.Add(false, "synthetic-default");
            cases.Add(false, "scoped-inbox"); cases.Add(true, "scoped-inbox");
            cases.Add(false, "request-boundary"); cases.Add(true, "request-boundary");
            return cases;
        }
    }
    [Theory(Timeout = 120000)] [MemberData(nameof(ConversationAutomationCases))]
    public async Task Existing_conversation_Automation_keeps_the_actual_conversation_and_fresh_independent_authority(bool sqlite, string mode)
    {
        var recovery = mode is "restart-pending" or "restart-approved";
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow); var model = new AutomationConversationModel { HoldAutomation = mode is "revoke-before-output" or "policy-before-output", UseDefault = mode == "synthetic-default" };
        void Configure(IServiceCollection services)
        {
            if (!model.UseDefault) { services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(clock); }
            foreach (var host in services.Where(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(AgentCore.Api.AgentRunHostedService)).ToArray()) services.Remove(host);
        }
        await using var firstFactory = new OAuthFactory(sqlite: sqlite, preserve: recovery, execute: true, model: model, servicesOverride: Configure);
        var factory = firstFactory; OAuthFactory? reopened = null; HttpClient? reopenedOwner = null;
        using var firstOwner = ConnectedClient(factory); var owner = firstOwner;
        var retiring = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRetiring = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var collision = new TaskCompletionSource<AgentRun>(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
        await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
        var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid(); var api = $"/api/v2/tenants/{tenant}";
        var human = (await Json(await owner.GetAsync(api + "/access/me"))).GetProperty("principalId").GetGuid();
        var instance = (await Json(await owner.PostAsJsonAsync(api + "/agents", new { templateId = "general-assistant", version = 32, expectedRevision = await Revision(owner, tenant) }))).GetProperty("instance").GetProperty("instanceId").GetGuid();
        var agent = (await factory.Services.GetRequiredService<ITenantAgentStore>().FindTenantAgentAsync(tenant, instance))!;
        var access = factory.Services.GetRequiredService<TenantAccessService>();
        // Existing execution needs no Agent Session-create authority.
        var profileBoundary = mode is "manual" or "scheduled";
        string[] permissions = ["automations.run", "agent-runs.create", "sessions.read", "agent-runs.read", "agents.chat", ..profileBoundary ? new[] { "memory.read" } : Array.Empty<string>()];
        var role = await access.SaveRoleAsync(human, tenant, null, "Existing conversation executor", permissions, await Revision(owner, tenant));
        var assignment = await access.AssignRoleAsync(human, tenant, agent.PrincipalId, role.RoleId, AccessScope.ForTenant(tenant), await Revision(owner, tenant));
        if (profileBoundary)
        {
            var profile = await Json(await owner.GetAsync(api + "/me/profile"));
            Assert.Equal(HttpStatusCode.OK, (await owner.PatchAsJsonAsync(api + "/me/profile", new { expectedRevision = await Revision(owner, tenant),
                expectedProfileRevision = profile.GetProperty("profile").GetProperty("revision").GetInt64(), values = new { preferredName = "PRIVATE_AUTOMATION_CREATOR_PROFILE" } })).StatusCode);
        }
        var explicitModel = mode is "conversation-model" or "conflicting-model";
        var destination = (await Json(await owner.PostAsJsonAsync(api + "/sessions", new { agentInstanceId = instance, mode = "text", offeringId = explicitModel ? "dca30000-0000-4000-8000-000000000002" : null, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1 }))).GetProperty("sessionId").GetGuid();
        var sessions = factory.Services.GetRequiredService<ITenantSessionStore>(); var runs = factory.Services.GetRequiredService<ITenantRunStore>();
        var execution = factory.Services.GetRequiredService<ITenantRunExecution>();
        if (mode == "retirement")
        {
            var host = factory.Services.GetRequiredService<AgentCore.Api.Realtime.TenantSessionRuntimeHost>();
            host.BeforeRuntimeRelease = previous => {
                if (previous.Admission.Activation.Kind != ActivationKind.UserTurn) return Task.CompletedTask;
                retiring.TrySetResult(); return releaseRetiring.Task;
            };
            host.RuntimeCollisionObserved = (current, _) => collision.TrySetResult(current);
        }

        async Task<AgentRun> Execute(Guid id, bool success = true)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30)); AgentRun run;
            do { await execution.ExecuteRunnableAsync(8, deadline.Token); run = (await runs.FindTenantRunAsync(tenant, id, deadline.Token))!; if (!run.IsTerminal) await Task.Delay(20, deadline.Token); } while (!run.IsTerminal);
            if (success) Assert.True(run.Status == AgentRunStatus.Completed, $"{run.Status}: {run.Failure?.Code} {run.Failure?.Summary}"); return run;
        }
        var input = await owner.PostAsJsonAsync(api + $"/sessions/{destination}/messages", new { messageId = Guid.NewGuid(), text = "Retain this actual human conversation history.", expectedRevision = await Revision(owner, tenant), expectedSessionRevision = 1 });
        Assert.True(input.IsSuccessStatusCode, await input.Content.ReadAsStringAsync()); var first = await Execute((await Json(input)).GetProperty("runId").GetGuid());
        if (profileBoundary)
        {
            Assert.NotNull(AgentCore.Application.Execution.AgentRunToolCallCheckpoint.ReadExecutionState(first.Checkpoint).TenantProfileSource);
            Assert.Contains(Assert.Single(model.Requests).Context!.Sections, s => s.Id == AgentCore.Application.Context.ContextSectionId.Profile
                && s.Message.Text.Contains("PRIVATE_AUTOMATION_CREATOR_PROFILE", StringComparison.Ordinal));
        }
        if (mode == "retirement") await retiring.Task.WaitAsync(TimeSpan.FromSeconds(20));
        var before = (await sessions.FindTenantSessionAsync(tenant, destination))!;
        Assert.Equal(human, before.Owner.CreatedByPrincipalId); Assert.Equal(SessionOriginKind.UserChat, before.Snapshot.Origin.Kind);
        var consent = recovery || mode is "chat-review" or "read-review" or "run-review" or "reject" or "stale-target" or "database-guards" or "scoped-inbox";
        if (consent || mode == "no-reviewer")
        {
            var key = mode == "read-review" ? "sessions.read" : mode == "run-review" ? "agent-runs.create" : "agents.chat";
            Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(api + "/operation-policies/" + key, new { mode = "always", independentReview = mode == "no-reviewer", expectedRevision = await Revision(owner, tenant), expectedPolicyRevision = 0 })).StatusCode);
        }
        var route = api + $"/agents/{instance}/automations";
        object Body(Guid target, Guid? report = null, long revision = 0) => new { enabled = true, name = "Use the exact conversation", instructions = "Return the bounded result in the selected conversation.", targetSessionId = target, reportToSessionId = report,
            schedule = new { kind = "oneShot", timeZone = "UTC", atUtc = clock.GetUtcNow().AddMinutes(2).ToString("O") }, expectedRevision = Revision(owner, tenant).GetAwaiter().GetResult(), expectedInstanceRevision = 1, expectedAutomationRevision = revision, requiresTools = true, offeringId = mode == "conflicting-model" ? "dca30000-0000-4000-8000-000000000001" : null };
        if (mode == "foreign")
        {
            var foreign = (await Json(await owner.PostAsJsonAsync(api + "/agents", new { templateId = "general-assistant", version = 32, expectedRevision = await Revision(owner, tenant) }))).GetProperty("instance").GetProperty("instanceId").GetGuid();
            var target = (await Json(await owner.PostAsJsonAsync(api + "/sessions", new { agentInstanceId = foreign, mode = "text", expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1 }))).GetProperty("sessionId").GetGuid();
            Assert.Equal(HttpStatusCode.Forbidden, (await owner.PostAsJsonAsync(route, Body(target))).StatusCode); return;
        }
        if (mode == "invalid-report") { Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync(route, Body(destination, destination))).StatusCode); return; }
        var created = await owner.PostAsJsonAsync(route, Body(destination));
        if (mode == "conflicting-model") { Assert.Equal(HttpStatusCode.Forbidden, created.StatusCode); return; }
        Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync()); var saved = await Json(created); var automation = saved.GetProperty("automationId").GetGuid();
        Assert.Equal("existingSession", saved.GetProperty("executionTarget").GetString()); Assert.Equal("none", saved.GetProperty("completionDelivery").GetString());
        if (mode == "request-boundary")
        {
            var isolation = api + $"/agents/{instance}/state-isolation";
            var preview = await Json(await owner.PostAsync(isolation + "/presets/Stateless", null));
            Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync(isolation + "/apply", new {
                expectedRevision = preview.GetProperty("securityRevision").GetInt64(), expected = preview.GetProperty("plan").GetProperty("expected"),
                draft = preview.GetProperty("plan").GetProperty("draft"), fingerprint = preview.GetProperty("plan").GetProperty("fingerprint").GetString() })).StatusCode);
            var response = await owner.PostAsJsonAsync(route + $"/{automation}/run", new { expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 2, expectedAutomationRevision = 1 });
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode); Assert.Contains("automation_conversation_unavailable", await response.Content.ReadAsStringAsync());
            Assert.Single((await runs.ListTenantSessionRunsAsync(tenant, destination, null, 0, 100)).Items);
            Assert.Empty((await factory.Services.GetRequiredService<ITenantApprovalStore>().ListApprovalsAsync(tenant, null, null, new(PageSize: 100))).Items);
            return;
        }
        async Task Reduce(string permission)
        {
            await access.RevokeAssignmentAsync(human, tenant, assignment.AssignmentId, await Revision(owner, tenant));
            var reduced = await access.SaveRoleAsync(human, tenant, null, "Current conversation executor", permissions.Where(p => p != permission).ToArray(), await Revision(owner, tenant));
            await access.AssignRoleAsync(human, tenant, agent.PrincipalId, reduced.RoleId, AccessScope.ForTenant(tenant), await Revision(owner, tenant));
        }
        if (mode is "missing-chat" or "missing-read" or "grant") await Reduce(mode == "grant" ? "agent-runs.create" : mode == "missing-chat" ? "agents.chat" : "sessions.read");
        if (mode == "archived") Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync(api + $"/sessions/{destination}/archive", new { expectedRevision = await Revision(owner, tenant), expectedSessionRevision = before.Snapshot.Revision })).StatusCode);
        if (mode == "busy")
        {
            var queued = await owner.PostAsJsonAsync(api + $"/sessions/{destination}/messages", new { messageId = Guid.NewGuid(), text = "A separate actual human message.", expectedRevision = await Revision(owner, tenant), expectedSessionRevision = before.Snapshot.Revision });
            Assert.True(queued.IsSuccessStatusCode, await queued.Content.ReadAsStringAsync());
        }
        if (mode == "scheduled") { clock.Advance(TimeSpan.FromMinutes(3)); await factory.Services.GetRequiredService<TenantAutomationService>().AdmitDueAsync(); }
        else
        {
            var response = await owner.PostAsJsonAsync(route + $"/{automation}/run", new { expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1, expectedAutomationRevision = 1 });
            if (mode is "missing-chat" or "missing-read" or "archived" or "busy" or "no-reviewer")
            {
                Assert.Equal(mode == "busy" ? HttpStatusCode.Conflict : HttpStatusCode.Forbidden, response.StatusCode);
                Assert.DoesNotContain((await runs.ListTenantSessionRunsAsync(tenant, destination, null, 0, 100)).Items, r => r.Admission.TaskIntent?.Source == AgentRunTaskSource.Automation); return;
            }
            Assert.True(response.StatusCode == (consent || mode == "grant" ? HttpStatusCode.Accepted : HttpStatusCode.Created), await response.Content.ReadAsStringAsync());
        }
        if (consent || mode == "grant")
        {
            var request = Assert.Single((await factory.Services.GetRequiredService<ITenantApprovalStore>().ListApprovalsAsync(tenant, null, null, new(PageSize: 100))).Items);
            Assert.Equal(mode == "read-review" ? "sessions.read" : mode is "run-review" or "grant" ? "agent-runs.create" : "agents.chat", request.Permission); Assert.Equal(mode == "grant" ? ApprovalKind.ExceptionalGrant : ApprovalKind.Review, request.Kind);
            Assert.Equal(before.Snapshot.Revision, request.Operation.AutomationAdmission!.Conversation!.SessionRevision);
            Assert.Equal(destination, request.Operation.AutomationAdmission.Current.ExecutionTarget.SessionId);
            Assert.Single((await runs.ListTenantSessionRunsAsync(tenant, destination, null, 0, 100)).Items);
            if (mode == "scoped-inbox")
            {
                var identity = factory.Services.GetRequiredService<IdentityService>();
                var account = await identity.RegisterAsync("conversation-consent-reader@example.test", "Exact target reader", "strong-conversation-reader-password");
                var memberRole = await access.SaveRoleAsync(human, tenant, null, "Consent prerequisites without target access", ["approvals.read", "automations.read", "automations.run", "agents.read", "agent-runs.create"], await Revision(owner, tenant));
                var invitation = await access.InviteAsync(human, tenant, account.LoginKey, memberRole.RoleId, await Revision(owner, tenant));
                var member = await access.AcceptInvitationAsync(account.AccountId, tenant, invitation.Invitation.InvitationId, invitation.Token);
                using var reader = ConnectedClient(factory); await Login(reader, account.LoginKey, "strong-conversation-reader-password");
                async Task Hidden()
                {
                    var page = await Json(await reader.GetAsync(api + "/approvals/?pageSize=1")); Assert.Equal(0, page.GetProperty("total").GetInt32()); Assert.Empty(page.GetProperty("items").EnumerateArray());
                    Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync(api + $"/approvals/{request.RequestId}")).StatusCode);
                }
                await Hidden();
                var raw = await access.SaveRoleAsync(human, tenant, null, "Read exact conversation", ["sessions.read"], await Revision(owner, tenant));
                var exactRead = await access.AssignRoleAsync(human, tenant, member.PrincipalId, raw.RoleId, TenantSessionRules.Scope(tenant, destination), await Revision(owner, tenant));
                await Hidden();
                var chat = await access.SaveRoleAsync(human, tenant, null, "Chat with this Agent", ["agents.chat"], await Revision(owner, tenant));
                await access.AssignRoleAsync(human, tenant, member.PrincipalId, chat.RoleId, AccessScope.ForResource(tenant, "agent", instance.ToString("D")), await Revision(owner, tenant));
                var visible = await Json(await reader.GetAsync(api + "/approvals/?pageSize=1")); Assert.Equal(1, visible.GetProperty("total").GetInt32()); Assert.Single(visible.GetProperty("items").EnumerateArray());
                Assert.Empty((await Json(await reader.GetAsync(api + "/approvals/?page=1&pageSize=1"))).GetProperty("items").EnumerateArray());
                Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync(api + $"/approvals/{request.RequestId}")).StatusCode);
                await access.RevokeAssignmentAsync(human, tenant, exactRead.AssignmentId, await Revision(owner, tenant)); await Hidden();
            }
            if (mode == "database-guards")
            {
                await using var db = factory.Services.GetRequiredService<IDbContextFactory<AgentCoreDbContext>>().CreateDbContext();
                var foreign = Guid.NewGuid().ToString("D");
                Assert.Equal(19, (await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE Automations SET TargetSessionId = {foreign} WHERE AutomationId = {automation.ToString("D")}"))).SqliteErrorCode);
                Assert.Equal(19, (await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE TriggerOccurrences SET TargetSessionId = {foreign} WHERE AutomationId = {automation.ToString("D")}"))).SqliteErrorCode);
                Assert.Equal(19, (await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE TenantApprovalRequests SET PayloadJson = json_set(PayloadJson, '$.Operation.AutomationAdmission.Conversation.InputBoundarySequence', 999) WHERE RequestId = {request.RequestId}"))).SqliteErrorCode);
                await Assert.ThrowsAsync<SqliteException>(() => db.GetService<IMigrator>().MigrateAsync("20261021050000_TenantAutomationCompletionDestination"));
                Assert.Contains("20261021060000_TenantAutomationConversation", await db.Database.GetAppliedMigrationsAsync());
                // EF commits each completed migration before an older populated guard refuses.
                // Restore any empty successor schemas removed by this disposable downgrade probe.
                await db.GetService<IMigrator>().MigrateAsync();
                Assert.Contains("20261021144350_TenantUserProfiles", await db.Database.GetAppliedMigrationsAsync());
                Assert.False(db.Database.HasPendingModelChanges());
            }
            if (recovery)
            {
                if (mode == "restart-approved")
                {
                    var approved = await owner.PostAsJsonAsync(api + $"/approvals/{request.RequestId}/decision", new { decision = "approve", actionHash = request.Operation.ActionHash, expectedRevision = await Revision(owner, tenant), expectedRequestRevision = request.Revision });
                    Assert.True(approved.IsSuccessStatusCode, await approved.Content.ReadAsStringAsync());
                }
                await firstFactory.DisposeAsync();
                reopened = new OAuthFactory(firstFactory.Root, sqlite: true, preserve: true, execute: true, model: model, servicesOverride: Configure); factory = reopened;
                reopenedOwner = ConnectedClient(factory); owner = reopenedOwner; await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
                sessions = factory.Services.GetRequiredService<ITenantSessionStore>(); runs = factory.Services.GetRequiredService<ITenantRunStore>(); execution = factory.Services.GetRequiredService<ITenantRunExecution>();
                Assert.Equal(request.Operation, (await factory.Services.GetRequiredService<ITenantApprovalStore>().FindApprovalAsync(tenant, request.RequestId))!.Operation);
                Assert.Single((await runs.ListTenantSessionRunsAsync(tenant, destination, null, 0, 100)).Items);
            }
            if (mode == "stale-target")
            {
                Assert.Equal(HttpStatusCode.OK, (await owner.PatchAsJsonAsync(api + $"/sessions/{destination}", new { title = "Changed exact conversation witness", expectedRevision = await Revision(owner, tenant), expectedSessionRevision = before.Snapshot.Revision })).StatusCode);
                Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsJsonAsync(api + $"/approvals/{request.RequestId}/decision", new { decision = "approve", actionHash = request.Operation.ActionHash, expectedRevision = await Revision(owner, tenant), expectedRequestRevision = request.Revision })).StatusCode); return;
            }
            if (mode != "restart-approved")
            {
                var decision = await owner.PostAsJsonAsync(api + $"/approvals/{request.RequestId}/decision", new { decision = mode == "reject" ? "reject" : "approve", actionHash = request.Operation.ActionHash, expectedRevision = await Revision(owner, tenant), expectedRequestRevision = request.Revision });
                Assert.True(decision.IsSuccessStatusCode, await decision.Content.ReadAsStringAsync());
            }
            await factory.Services.GetRequiredService<TenantApprovalService>().DispatchPendingAsync();
        }
        if (mode == "reject") { Assert.Single((await runs.ListTenantSessionRunsAsync(tenant, destination, null, 0, 100)).Items); return; }
        var run = Assert.Single((await runs.ListTenantSessionRunsAsync(tenant, destination, null, 0, 100)).Items, r => r.AgentRunId != first.AgentRunId);
        Assert.Equal(before.Owner, (await sessions.FindTenantSessionAsync(tenant, destination))!.Owner);
        Assert.Equal(before.Snapshot.Revision, (await sessions.FindTenantSessionAsync(tenant, destination))!.Snapshot.Revision);
        Assert.Equal(AgentRunOutputContract.ConversationResponse, run.Admission.OutputContract); Assert.Empty(run.Admission.Activation.SourceEntryIds);
        if (explicitModel) Assert.Equal("dca30000-0000-4000-8000-000000000002", run.PinnedModel.OfferingId);
        Assert.Equal(mode == "scheduled" ? null : (Guid?)human, run.Admission.ExecutionContext!.OriginalInitiatorPrincipalId);
        Assert.Equal(run.ActivationId, run.Admission.ExecutionContext.RootActivationId); Assert.Null(run.Admission.ExecutionContext.ParentRunId);
        Assert.Null(run.Admission.Activation.SourceSessionId); Assert.Null(run.Admission.Activation.SourceAgentRunId);
        if (mode == "frozen-target")
        {
            var other = (await Json(await owner.PostAsJsonAsync(api + "/sessions", new { agentInstanceId = instance, mode = "text", expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1 }))).GetProperty("sessionId").GetGuid();
            Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(route + $"/{automation}", Body(other, revision: 2))).StatusCode);
        }
        if (mode == "cancel-registration") Assert.Equal(HttpStatusCode.OK, (await owner.DeleteAsync(route + $"/{automation}?expectedRevision={await Revision(owner, tenant)}&expectedInstanceRevision=1&expectedAutomationRevision=2")).StatusCode);
        if (model.HoldAutomation)
        {
            var finishing = Execute(run.AgentRunId, false); await model.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
            if (mode == "revoke-before-output") await Reduce("agents.chat");
            else Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(api + "/operation-policies/agents.chat", new { mode = "always", independentReview = false, expectedRevision = await Revision(owner, tenant), expectedPolicyRevision = 0 })).StatusCode);
            model.Release.TrySetResult(); var refused = await finishing; Assert.NotEqual(AgentRunStatus.Completed, refused.Status);
            var retained = (await Json(await owner.GetAsync(api + $"/sessions/{destination}/messages"))).GetProperty("items").EnumerateArray().ToArray();
            Assert.All(retained.Where(row => row.GetProperty("sequence").GetInt64() > before.Snapshot.DurableLastEntrySequence), row =>
            {
                Assert.Equal("failed", row.GetProperty("status").GetString()); Assert.True(string.IsNullOrEmpty(row.GetProperty("text").GetString()), row.GetRawText());
            });
            Assert.DoesNotContain(retained.Where(row => row.GetProperty("sequence").GetInt64() > before.Snapshot.DurableLastEntrySequence), row => row.GetProperty("status").GetString() == "completed"); return;
        }
        if (mode == "retirement")
        {
            var pass = execution.ExecuteRunnableAsync(8).AsTask();
            var claimed = await collision.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Equal(run.AgentRunId, claimed.AgentRunId); Assert.NotEqual(first.AgentRunId, claimed.AgentRunId);
            Assert.Equal(1, claimed.AttemptCount); Assert.Equal(AgentRunStatus.Running, claimed.Status);
            releaseRetiring.TrySetResult();
            Assert.Equal(1, await pass.WaitAsync(TimeSpan.FromSeconds(20)));
        }
        var completed = await Execute(run.AgentRunId); Assert.Equal(model.UseDefault ? "Hello!" : "An exact conversation task completed.", completed.Result!.Text); Assert.Equal(2, model.Calls);
        if (mode == "retirement") Assert.Equal(1, completed.AttemptCount);

        if (profileBoundary)
        {
            Assert.Null(AgentCore.Application.Execution.AgentRunToolCallCheckpoint.ReadExecutionState(completed.Checkpoint).TenantProfileSource);
            Assert.DoesNotContain(model.Requests.Last().Context!.Sections, s => s.Id == AgentCore.Application.Context.ContextSectionId.Profile);
            Assert.DoesNotContain("PRIVATE_AUTOMATION_CREATOR_PROFILE", string.Join('\n', model.Requests.Last().Messages.Select(m => m.Text)));
        }
        var history = (await Json(await owner.GetAsync(api + $"/sessions/{destination}/messages"))).GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(3, history.Length); Assert.Equal("Retain this actual human conversation history.", history[0].GetProperty("text").GetString());
        Assert.Equal(2, history.Count(e => e.GetProperty("role").GetString()!.Equals("assistant", StringComparison.OrdinalIgnoreCase)));
        var after = (await sessions.FindTenantSessionAsync(tenant, destination))!; Assert.Equal(before.Owner, after.Owner); Assert.Equal(before.Snapshot.Origin, after.Snapshot.Origin);
        Assert.Equal(mode == "frozen-target" ? 2 : 1, (await sessions.ListTenantSessionsAsync(tenant, null, 0, 100)).Items.Count); Assert.Equal(2, (await runs.ListTenantSessionRunsAsync(tenant, destination, null, 0, 100)).Items.Count); Assert.Equal(0, factory.Outbound);
        }
        finally
        {
            releaseRetiring.TrySetResult();
            reopenedOwner?.Dispose(); if (reopened is not null) await reopened.DisposeAsync();
            if (recovery) { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(firstFactory.Root)) Directory.Delete(firstFactory.Root, true); }
        }
    }
}
