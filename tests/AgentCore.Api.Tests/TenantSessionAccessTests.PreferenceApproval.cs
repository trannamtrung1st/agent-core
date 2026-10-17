using System.Net;
using System.Net.Http.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    [Theory]
    [InlineData(false, false, "approve", false, false)] [InlineData(true, false, "approve", false, false)]
    [InlineData(false, true, "approve", false, false)] [InlineData(true, true, "approve", false, false)]
    [InlineData(false, false, "reject", false, false)] [InlineData(true, false, "reject", false, false)]
    [InlineData(false, true, "reject", false, false)] [InlineData(true, true, "reject", false, false)]
    [InlineData(false, false, "cancel", false, false)] [InlineData(true, false, "cancel", false, false)]
    [InlineData(false, false, "approve", true, false)] [InlineData(true, false, "approve", true, false)]
    [InlineData(false, false, "approve", false, true)]
    [InlineData(true, false, "approve", false, true)]
    public async Task Model_preference_authorization_persists_exact_parameters_and_resumes_the_saved_effect(bool sqlite, bool review, string decision, bool freeze, bool busy)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-preference-approval-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        AgentCore.Api.Realtime.SessionHost? reservedCapacity = null; var capacityHeld = false;
        try
        {
            var model = new ApprovalRecordingModel();
            await using WebApplicationFactory<Program> factory = sqlite ? new TenantApprovalSqliteFactory(root, model) : new ApprovalExecutionFactory();
            if (factory is ApprovalExecutionFactory memory) model = memory.Model;
            using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
            await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
            factory.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<AgentCore.Api.Realtime.AgentCoreOptions>>().Value.MaxActiveSessions = 1;
            var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid(); var api = $"/api/v2/tenants/{tenant}";
            var created = await Json(await owner.PostAsJsonAsync(api + "/agents", new { templateId = "examiner", version = 1, expectedRevision = await Revision(owner, tenant) }));
            var instance = created.GetProperty("instance").GetProperty("instanceId").GetGuid();
            Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(api + $"/agents/{instance}/self-management", new {
                expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1,
                overrides = new { mode = new { value = "Assisted" }, scopes = new { value = new[] { "Settings" } } }
            })).StatusCode);
            var agents = factory.Services.GetRequiredService<ITenantAgentStore>(); var before = (await agents.FindTenantAgentAsync(tenant, instance))!;
            if (review)
            {
                var state = (await factory.Services.GetRequiredService<IIamStore>().ReadTenantAsync(tenant))!; var actor = Assert.Single(state.Users).PrincipalId;
                var access = factory.Services.GetRequiredService<TenantAccessService>();
                var role = await access.SaveRoleAsync(actor, tenant, null, "Own preferences", ["agents.read", "agents.update"], await Revision(owner, tenant));
                await access.AssignRoleAsync(actor, tenant, before.PrincipalId, role.RoleId, AccessScope.ForResource(tenant, "agent", instance.ToString("D")), await Revision(owner, tenant));
            }
            model.ProposedPreferenceAuthorization = """{"set":{"language":"vi","responseLength":"concise"},"clear":[]}""";
            if (busy) model.ApprovalResponseGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var session = await Json(await owner.PostAsJsonAsync(api + "/sessions", new { agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 2 }));
            var sessionId = session.GetProperty("sessionId").GetGuid();
            var submitted = await owner.PostAsJsonAsync(api + $"/sessions/{sessionId}/messages", new { messageId = Guid.NewGuid(), text = "Request authorization for concise Vietnamese responses", expectedRevision = await Revision(owner, tenant), expectedSessionRevision = 1 });
            Assert.Equal(HttpStatusCode.Accepted, submitted.StatusCode); var runId = (await Json(submitted)).GetProperty("runId").GetGuid();
            if (factory is ApprovalExecutionFactory memoryExecution) Volatile.Write(ref memoryExecution.Enabled, true);
            var runs = factory.Services.GetRequiredService<ITenantRunStore>(); AgentRun? waiting = null;
            using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
                while (!deadline.IsCancellationRequested)
                {
                    waiting = await runs.FindTenantRunAsync(tenant, runId);
                    if (waiting?.Status == AgentRunStatus.WaitingForApproval || waiting?.IsTerminal == true) break;
                    await Task.Delay(25, deadline.Token);
                }
            Assert.NotNull(waiting); Assert.True(waiting.Status == AgentRunStatus.WaitingForApproval, $"{waiting.Status}: {waiting.Failure?.Summary}");
            var approvals = factory.Services.GetRequiredService<ITenantApprovalStore>();
            var request = (await approvals.FindApprovalAsync(tenant, waiting.ApprovalRequestId!.Value))!;
            Assert.Equal(review ? ApprovalKind.Review : ApprovalKind.ExceptionalGrant, request.Kind);
            Assert.Null(request.Operation.Persona); Assert.Equal("vi", request.Operation.Preferences!.Language);
            Assert.Equal(2, (await agents.FindTenantAgentAsync(tenant, instance))!.Instance.Revision);
            var projected = await Json(await owner.GetAsync(api + $"/approvals/{request.RequestId}"));
            Assert.Equal("update_preferences", projected.GetProperty("preparedAction").GetProperty("operation").GetString());
            Assert.Contains("conversation preferences", projected.GetProperty("summary").GetString());
            var reviewProjection = projected.GetProperty("review"); Assert.True(reviewProjection.GetProperty("canDecide").GetBoolean());
            Assert.True(reviewProjection.GetProperty("canCancel").GetBoolean());
            var languagePreview = reviewProjection.GetProperty("changes").EnumerateArray().Single(c => c.GetProperty("field").GetString() == "language");
            Assert.Equal("en", languagePreview.GetProperty("before").GetString()); Assert.Equal("vi", languagePreview.GetProperty("after").GetString());
            var inbox = await Json(await owner.GetAsync(api + "/approvals?page=0&pageSize=1&status=pending&kind=" + (review ? "review" : "exceptionalGrant")));
            Assert.Equal(1, inbox.GetProperty("total").GetInt32()); Assert.Equal(request.RequestId, Assert.Single(inbox.GetProperty("items").EnumerateArray()).GetProperty("requestId").GetGuid());
            Assert.Empty((await Json(await owner.GetAsync(api + "/approvals?page=1&pageSize=1"))).GetProperty("items").EnumerateArray());
            Assert.Equal(HttpStatusCode.BadRequest, (await owner.GetAsync(api + "/approvals?status=0")).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await owner.GetAsync(api + "/approvals?pageSize=101")).StatusCode);
            if (freeze)
                Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync(api + $"/agents/{instance}/self-management/freeze", new {
                    expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 2
                })).StatusCode);
            if (busy)
            {
                reservedCapacity = factory.Services.GetRequiredService<AgentCore.Api.Realtime.SessionHost>();
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                while (!capacityHeld && !deadline.IsCancellationRequested)
                { capacityHeld = reservedCapacity.TryReserveTenantRuntime(); if (!capacityHeld) await Task.Delay(25, deadline.Token); }
                Assert.True(capacityHeld);
            }
            var decided = await owner.PostAsJsonAsync(api + $"/approvals/{request.RequestId}/" + (decision == "cancel" ? "cancel" : "decision"), new {
                decision, actionHash = request.Operation.ActionHash, expectedRevision = await Revision(owner, tenant), expectedRequestRevision = request.Revision
            });
            if (freeze)
            {
                Assert.Equal(HttpStatusCode.Conflict, decided.StatusCode);
                Assert.Null(await approvals.FindOperationGrantAsync(tenant, request.RequestId));
                Assert.Equal(before.Instance.SettingsOverrides, (await agents.FindTenantAgentAsync(tenant, instance))!.Instance.SettingsOverrides);
                Assert.Equal(TenantApprovalStatus.Pending, (await approvals.FindApprovalAsync(tenant, request.RequestId))!.Status);
                return;
            }
            Assert.Equal(HttpStatusCode.OK, decided.StatusCode);
            if (busy)
            {
                AgentRun? deferred = null; using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try
                {
                    while (!deadline.IsCancellationRequested)
                    {
                        deferred = await CurrentApprovalExecution(runs, tenant, runId);
                        if (deferred?.Status == AgentRunStatus.WaitingToRetry) break;
                        await Task.Delay(25, deadline.Token);
                    }
                }
                catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
                Assert.True(deferred?.Status == AgentRunStatus.WaitingToRetry, $"Expected a known-unaccepted successor to defer; observed {deferred?.Status}.");
                Assert.NotNull(deferred);
                Assert.Equal(waiting.AttemptCount, deferred.AttemptCount); Assert.Equal(waiting.Checkpoint!.RemainingOverallBudgetMs, deferred.Checkpoint!.RemainingOverallBudgetMs);
                Assert.Equal(2, (await agents.FindTenantAgentAsync(tenant, instance))!.Instance.Revision);
                reservedCapacity!.ReleaseTenantRuntime(); capacityHeld = false;
                await model.ApprovalResponseEntered.Task.WaitAsync(TimeSpan.FromSeconds(15));
                var active = (await CurrentApprovalExecution(runs, tenant, runId))!; Assert.Equal(AgentRunStatus.Running, active.Status);
                var dispatcher = factory.Services.GetRequiredService<ITenantRunDispatcher>();
                Assert.All(await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => dispatcher.DispatchAsync(active).AsTask())), accepted => Assert.True(accepted));
                var stale = new AgentRun(active.AgentRunId, active.Owner, active.Admission, active.PinnedModel, active.Status, active.Revision, active.AttemptCount, active.MaxAttempts,
                    active.NextRetryAtUtc, new(Guid.NewGuid(), active.Claim!.ClaimedAtUtc, active.Claim.LeaseExpiresAtUtc), active.CancellationRequested, active.CancellationRequestedAtUtc,
                    active.KnownEffectSummary, active.Progress, active.Checkpoint, active.Result, active.Failure, active.SideEffect, active.Approval, active.CreatedAtUtc, active.UpdatedAtUtc,
                    active.PinnedSkillCatalog, active.ActiveSkillKeys, active.SkillLoadCount, active.LoadedCapabilityIds, active.CapabilityLoadCount, active.Wait, active.WaitCount, active.TotalWaitSeconds, active.ApprovalRequestId);
                Assert.False(await dispatcher.DispatchAsync(stale));
                Assert.Equal(active.Claim.Generation, (await runs.FindTenantRunAsync(tenant, active.AgentRunId))!.Claim!.Generation);
                Assert.Single(model.Requests, r => r.Context?.Sections.Any(s => s.Id == AgentCore.Application.Context.ContextSectionId.ApprovalOutcome) == true);
                model.ApprovalResponseGate!.TrySetResult();
            }
            AgentRun? completed = null;
            using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
                while (!deadline.IsCancellationRequested)
                {
                    completed = await CurrentApprovalExecution(runs, tenant, runId);
                    if (completed?.IsTerminal == true) break;
                    await Task.Delay(25, deadline.Token);
                }
            Assert.NotNull(completed); Assert.True(completed.Status == AgentRunStatus.Completed, $"{completed.Status}: {completed.Failure?.Code}: {completed.Failure?.Summary}");
            Assert.NotEqual(runId, completed.AgentRunId); Assert.Equal(request.Operation.OperationId, completed.Admission.ApprovalResume!.OperationId);
            var sourceMetadata = await Json(await owner.GetAsync(api + $"/agent-runs/{runId}"));
            Assert.Equal(completed.AgentRunId, sourceMetadata.GetProperty("continuationRunId").GetGuid());
            Assert.Equal("continued", sourceMetadata.GetProperty("outcomeKind").GetString());
            var after = (await agents.FindTenantAgentAsync(tenant, instance))!.Instance;
            Assert.Equal(decision == "approve" || freeze ? 3 : 2, after.Revision); Assert.Equal(before.Instance.Persona, after.Persona);
            if (decision == "approve" && !freeze) { Assert.Equal("vi", after.SettingsOverrides!.Language!.Value); Assert.Equal("concise", after.SettingsOverrides.ResponseLength!.Value); }
            else Assert.Equal(before.Instance.SettingsOverrides, after.SettingsOverrides);
            var grant = await approvals.FindOperationGrantAsync(tenant, request.RequestId);
            if (!review && decision == "approve") Assert.Equal(freeze ? OperationGrantStatus.Revoked : OperationGrantStatus.Consumed, grant!.Status); else Assert.Null(grant);
            Assert.Contains(model.Requests, r => r.Context?.Sections.Any(s => s.Id == AgentCore.Application.Context.ContextSectionId.ApprovalOutcome) == true);
            Assert.True(AgentCore.Application.Execution.AgentRunToolCallCheckpoint.TryRead(completed.Checkpoint, out var messages));
            Assert.DoesNotContain(AgentCore.Application.Execution.AgentRunToolCallCheckpoint.PendingCalls(messages!), c => c.Name == ToolCatalog.RequestAuthorization);
            Assert.Equal(decision == "approve" ? 1 : 0, (await factory.Services.GetRequiredService<IIamStore>().ReadAuditAsync(tenant, 200)).Count(a => a.Action == "operations.preferences.apply"));
            var auditResponse = await owner.GetAsync(api + "/audit?limit=200"); Assert.Equal(HttpStatusCode.OK, auditResponse.StatusCode);
            var auditRows = (await Json(auditResponse)).GetProperty("items").EnumerateArray().ToArray();
            var preparation = Assert.Single(auditRows, a => a.GetProperty("action").GetString() == "authorization.operation"
                && a.GetProperty("execution").GetProperty("purpose").GetString() == "ApprovalPreparation");
            var preparedContext = preparation.GetProperty("execution").GetProperty("context");
            Assert.Equal(runId, preparedContext.GetProperty("runId").GetGuid()); Assert.Equal(waiting.Admission.ExecutionContext!.RootActivationId, preparedContext.GetProperty("rootActivationId").GetGuid());
            Assert.Equal(before.PrincipalId, preparation.GetProperty("actorPrincipalId").GetGuid());
            if (decision == "approve")
            {
                var effectAudit = Assert.Single(auditRows, a => a.GetProperty("action").GetString() == "operations.preferences.apply").GetProperty("execution");
                Assert.Equal("EffectResult", effectAudit.GetProperty("purpose").GetString()); Assert.Equal(completed.AgentRunId, effectAudit.GetProperty("context").GetProperty("runId").GetGuid());
                Assert.Equal(runId, effectAudit.GetProperty("context").GetProperty("parentRunId").GetGuid()); Assert.Equal(request.RequestId, effectAudit.GetProperty("approvalRequestId").GetGuid());
                Assert.Equal(request.Operation.OperationId, effectAudit.GetProperty("operationId").GetGuid());
                if (grant is not null) Assert.Equal(grant.GrantId, effectAudit.GetProperty("grantId").GetGuid());
            }
            Assert.Null(completed.Owner.ProfileIdOrNull);
            var finalView = await Json(await owner.GetAsync(api + $"/approvals/{request.RequestId}"));
            Assert.False(finalView.GetProperty("review").GetProperty("canDecide").GetBoolean());
            Assert.False(finalView.GetProperty("review").GetProperty("canCancel").GetBoolean());
            Assert.Equal(reviewProjection.GetProperty("changes").GetRawText(), finalView.GetProperty("review").GetProperty("changes").GetRawText());
            if (decision == "approve") Assert.Equal("completed", finalView.GetProperty("operationResult").GetProperty("outcome").GetString());
        }
        finally { if (capacityHeld) reservedCapacity!.ReleaseTenantRuntime(); SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
}
