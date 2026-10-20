using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
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
    private sealed class BackgroundCreationModel : TenantBackgroundModel
    {
        public bool RepeatRefusal { get; init; }
        public override async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (Origin(request) != ExecutionOrigin.UserMessage)
            { await foreach (var item in base.GenerateAsync(request, cancellationToken)) yield return item; yield break; }
            Requests.Enqueue(request);
            var authorization = request.Messages.LastOrDefault(m => m.Role == ModelRole.Tool && m.Name == ToolCatalog.RequestAuthorization);
            var direct = request.Messages.LastOrDefault(m => m.Role == ModelRole.Tool && m.Name == ToolCatalog.BackgroundStart);
            if (authorization is null)
            {
                Assert.Contains("start_background_work", Assert.Single(request.Tools!, t => t.Name == ToolCatalog.RequestAuthorization).ParametersJson);
                yield return new ModelToolCallEvent(new("create-exact-background", ToolCatalog.RequestAuthorization,
                    """{"operation":"start_background_work","task":{"objective":"Perform the exact reviewed background check","title":"Reviewed background check","reportCompletion":true}}"""));
                yield return new ModelCompleted(ModelStopReason.ToolCalls); yield break;
            }
            using var result = JsonDocument.Parse(authorization.Text);
            if (RepeatRefusal && request.Messages.Count(m => m.Role == ModelRole.Tool && m.Name == ToolCatalog.RequestAuthorization) == 1
                && result.RootElement.TryGetProperty("outcome", out var refused) && refused.GetString() == "denied")
            {
                yield return new ModelToolCallEvent(new("repeat-refused-creation", ToolCatalog.RequestAuthorization,
                    """{"operation":"start_background_work","task":{"objective":"Perform the exact reviewed background check","title":"Reviewed background check","reportCompletion":true}}"""));
                yield return new ModelCompleted(ModelStopReason.ToolCalls); yield break;
            }
            if (result.RootElement.TryGetProperty("decision", out var decision) && decision.GetString() == "allowed" && direct is null)
            {
                yield return new ModelToolCallEvent(new("create-direct-background", ToolCatalog.BackgroundStart,
                    """{"objective":"Perform the exact reviewed background check","title":"Reviewed background check","reportCompletion":true}"""));
                yield return new ModelCompleted(ModelStopReason.ToolCalls); yield break;
            }
            var started = direct is not null || result.RootElement.TryGetProperty("started", out var flag) && flag.ValueKind == JsonValueKind.True;
            yield return new ModelSemanticResponseReady(new(started ? "Reviewed background started." : "Reviewed background refused.", new(ModelSpeechMode.None, null), []));
            yield return new ModelCompleted(ModelStopReason.Completed);
        }
    }
    [Theory(Timeout = 120000)] [InlineData(false, "review")] [InlineData(true, "review")]
    [InlineData(false, "grant-run")] [InlineData(true, "grant-run")]
    [InlineData(false, "grant-session")] [InlineData(true, "grant-session")]
    [InlineData(false, "allowed")] [InlineData(true, "allowed")]
    [InlineData(false, "missing")] [InlineData(true, "missing")]
    [InlineData(false, "reject")] [InlineData(true, "reject")]
    [InlineData(false, "reject-repeat")] [InlineData(true, "reject-repeat")]
    [InlineData(false, "cancel")] [InlineData(true, "cancel")]
    [InlineData(false, "revoked")] [InlineData(true, "revoked")]
    [InlineData(false, "changed")] [InlineData(true, "changed")]
    public Task Tenant_background_creation_consent_commits_exact_child_and_receipt_with_current_compound_authority(bool sqlite, string mode) => BackgroundCreationFlow(sqlite, mode);

    [Fact(Timeout = 120000)]
    public Task Tenant_background_creation_pending_Review_survives_actual_SQLite_host_recreation() => BackgroundCreationFlow(true, "review", restart: true);

    private async Task BackgroundCreationFlow(bool sqlite, string mode, bool restart = false)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-background-creation-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        WebApplicationFactory<Program>? reopened = null; HttpClient? reopenedOwner = null;
        try
        {
            var model = new BackgroundCreationModel { RepeatRefusal = mode == "reject-repeat" };
            await using WebApplicationFactory<Program> initial = sqlite ? new TenantBackgroundSqliteFactory(root, model) : new TenantBackgroundMemoryFactory(root, model);
            var factory = initial;
            using var firstOwner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") }); var owner = firstOwner;
            var setup = await StartTenantBackground(factory, owner, createAuthority: false, submit: false);
            var tenant = setup.Tenant; var api = $"/api/v2/tenants/{tenant}";
            var sessions = factory.Services.GetRequiredService<ITenantSessionStore>(); var runs = factory.Services.GetRequiredService<ITenantRunStore>();
            var parent = (await sessions.FindTenantSessionAsync(tenant, setup.Session))!;
            var iam = factory.Services.GetRequiredService<IIamStore>(); var access = factory.Services.GetRequiredService<TenantAccessService>();
            var actor = (await Json(await owner.GetAsync(api + "/access/me"))).GetProperty("principalId").GetGuid();
            RoleAssignment? assignment = null;
            if (mode != "missing")
            {
                var permissions = mode == "grant-run" ? new[] { "sessions.create" } : mode == "grant-session" ? new[] { "agent-runs.create" } : new[] { "sessions.create", "agent-runs.create" };
                var role = await access.SaveRoleAsync(actor, tenant, null, "Exact child creation", permissions, await Revision(owner, tenant));
                assignment = await access.AssignRoleAsync(actor, tenant, parent.Owner.ExecutorPrincipalId, role.RoleId, AccessScope.ForTenant(tenant), await Revision(owner, tenant));
            }
            if (mode is not ("allowed" or "grant-run" or "grant-session" or "missing"))
                Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(api + "/operation-policies/sessions.create", new { mode = "always", independentReview = false, expectedRevision = await Revision(owner, tenant), expectedPolicyRevision = 0 })).StatusCode);
            var sourceId = (await Json(await owner.PostAsJsonAsync(api + $"/sessions/{setup.Session}/messages", new { messageId = Guid.NewGuid(), text = "Start the exact background task after the required creation checks", expectedRevision = await Revision(owner, tenant), expectedSessionRevision = 1 }))).GetProperty("runId").GetGuid();
            var waits = mode is not ("allowed" or "missing"); TenantApprovalRequest? saved = null;
            if (waits)
            {
                var waiting = await AwaitBrowserRestartRun(runs, tenant, sourceId, true);
                Assert.True(waiting.Status == AgentRunStatus.WaitingForApproval, $"{waiting.Status}: {waiting.Failure} {string.Join('\n', model.Failures)}");
                saved = (await factory.Services.GetRequiredService<ITenantApprovalStore>().FindApprovalAsync(tenant, waiting.ApprovalRequestId!.Value))!;
                Assert.NotNull(saved.Operation.BackgroundStart);
                Assert.Equal(mode.StartsWith("grant-") ? ApprovalKind.ExceptionalGrant : ApprovalKind.Review, saved.Kind);
                Assert.Equal(mode == "grant-session" ? "sessions.create" : "agent-runs.create", saved.Permission);
                Assert.Single((await sessions.ListTenantSessionsAsync(tenant, null, 0, 100)).Items);
                if (restart)
                {
                    await factory.DisposeAsync(); model = new BackgroundCreationModel(); reopened = new TenantBackgroundSqliteFactory(root, model); factory = reopened;
                    reopenedOwner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") }); owner = reopenedOwner;
                    await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
                    sessions = factory.Services.GetRequiredService<ITenantSessionStore>(); runs = factory.Services.GetRequiredService<ITenantRunStore>();
                }
                if (mode == "revoked") Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync(api + $"/role-assignments/{assignment!.AssignmentId}?expectedRevision={await Revision(owner, tenant)}")).StatusCode);
                if (mode == "changed") Assert.Equal(HttpStatusCode.OK, (await owner.PatchAsJsonAsync(api + $"/agents/{parent.Owner.AgentInstanceId}/settings/conversationPolicy", new { expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1, set = new { responseLength = "concise" }, clear = Array.Empty<string>() })).StatusCode);
                var detail = await Json(await owner.GetAsync(api + $"/approvals/{saved.RequestId}"));
                var cancel = mode is "cancel" or "revoked" or "changed";
                if (mode is "revoked" or "changed") Assert.False(detail.GetProperty("review").GetProperty("canDecide").GetBoolean());
                var response = await owner.PostAsJsonAsync(api + $"/approvals/{saved.RequestId}/" + (cancel ? "cancel" : "decision"), new { decision = mode.StartsWith("reject", StringComparison.Ordinal) ? "reject" : "approve", actionHash = saved.Operation.ActionHash,
                    expectedRevision = detail.GetProperty("securityRevision").GetInt64(), expectedRequestRevision = saved.Revision });
                Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
            }
            AgentRun final;
            try { final = await AwaitBrowserRestartRun(runs, tenant, sourceId, false); }
            catch (OperationCanceledException)
            {
                var stuck = await CurrentApprovalExecution(runs, tenant, sourceId);
                var calls = model.Requests.Select(r => string.Join(" | ", r.Messages.Where(m => m.Role == ModelRole.Tool).Select(m => m.Name + ":" + m.Text))).ToArray();
                throw new InvalidOperationException($"Creation execution did not settle: {stuck?.Status} revision {stuck?.Revision} approval {stuck?.ApprovalRequestId}; requests {calls.Length}; {string.Join('\n', calls.TakeLast(4))}; {string.Join('\n', model.Failures)}");
            }
            Assert.True(final.Status == AgentRunStatus.Completed, $"{final.Status}: {final.Failure} {string.Join('\n', model.Failures)}");
            var accepted = mode is "allowed" or "review" or "grant-run" or "grant-session";
            var all = (await sessions.ListTenantSessionsAsync(tenant, null, 0, 100)).Items;
            Assert.Equal(accepted ? 2 : 1, all.Count);
            if (accepted)
            {
                var child = Assert.Single(all, s => s.Owner.SessionId != setup.Session);
                var initialChild = (await runs.FindTenantRunAsync(tenant, child.Snapshot.Origin.InitialBackgroundAgentRunId!.Value))!;
                Assert.Equal(final.AgentRunId, initialChild.Admission.ExecutionContext!.ParentRunId);
                Assert.Equal((await runs.FindTenantRunAsync(tenant, sourceId))!.Admission.ExecutionContext!.RootActivationId, initialChild.Admission.ExecutionContext.RootActivationId);
                if (saved is not null)
                {
                    Assert.Equal(saved.Operation.BackgroundStart!.ChildSessionId, child.Owner.SessionId);
                    Assert.Equal(saved.Operation.BackgroundStart.RunId, initialChild.AgentRunId);
                    var receipt = (await factory.Services.GetRequiredService<ITenantApprovedOperationStore>().FindOperationReceiptAsync(tenant, saved.Operation.OperationId))!;
                    Assert.Equal(TenantOperationOutcome.Completed, receipt.Outcome); Assert.Equal(final.AgentRunId, receipt.RunId);
                    var grant = await factory.Services.GetRequiredService<ITenantApprovalStore>().FindOperationGrantAsync(tenant, saved.RequestId);
                    if (mode.StartsWith("grant-")) Assert.Equal(OperationGrantStatus.Consumed, grant!.Status); else Assert.Null(grant);
                }
                var completed = await WaitTenantBackground(factory, tenant, setup.Session, final.AgentRunId, mode != "grant-run");
                Assert.Equal(initialChild.AgentRunId, completed.Child.AgentRunId);
                if (mode == "grant-run")
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)); CompletionInboxItem? item;
                    do { item = await runs.FindTenantCompletionAsync(initialChild.Owner, initialChild.AgentRunId); if (item?.Status != CompletionInboxStatus.Skipped) await Task.Delay(25, timeout.Token); } while (item?.Status != CompletionInboxStatus.Skipped);
                    Assert.Equal("permission_denied", item.SkipReason);
                }
            }
            else if (saved is not null) Assert.Null(await factory.Services.GetRequiredService<ITenantApprovedOperationStore>().FindOperationReceiptAsync(tenant, saved.Operation.OperationId));
            if (mode == "reject-repeat")
                Assert.Contains(model.Requests.SelectMany(r => r.Messages), m => m.Role == ModelRole.Tool && m.Text.Contains("previously_rejected", StringComparison.Ordinal));
            Assert.Equal(0, factory.Services.GetRequiredService<AgentCore.Api.Http.OutboundHttpProbe>().Attempts);
        }
        finally { reopenedOwner?.Dispose(); if (reopened is not null) await reopened.DisposeAsync(); SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
