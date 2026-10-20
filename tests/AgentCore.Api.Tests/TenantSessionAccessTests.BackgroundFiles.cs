using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
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
    private sealed class BackgroundFileModel : TenantBackgroundModel
    {
        public string Tool { get; set; } = ToolCatalog.WorkspaceRead;
        public string Arguments { get; set; } = """{"path":"/home/observation.txt"}""";
        public bool Review { get; init; }
        public bool RepeatRefusal { get; init; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentQueue<ModelRequest> ChildRequests { get; } = new();
        public override async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (Origin(request) != ExecutionOrigin.ImmediateBackground && request.Messages.Any(m => m.Role == ModelRole.User && m.Text.Contains("Keep the queued file available", StringComparison.Ordinal)))
            { yield return new ModelSemanticResponseReady(new("Queued file noted.", new(ModelSpeechMode.None, null), [])); yield return new ModelCompleted(ModelStopReason.Completed); yield break; }
            if (Origin(request) != ExecutionOrigin.ImmediateBackground)
            { await foreach (var item in base.GenerateAsync(request, cancellationToken)) yield return item; yield break; }
            ChildRequests.Enqueue(request); Entered.TrySetResult(); await Release.Task.WaitAsync(cancellationToken);
            Assert.Contains(request.Tools!, t => t.Name == ToolCatalog.WorkComplete);
            Assert.DoesNotContain(request.Tools!, t => t.Name == ToolCatalog.SandboxExec);
            var authorization = Assert.Single(request.Tools!, t => t.Name == ToolCatalog.RequestAuthorization);
            // Native consent and child fanout have independent converted owners;
            // this file observation fixture does not execute either family.
            Assert.DoesNotContain("update_persona", authorization.ParametersJson);
            var callName = Review ? ToolCatalog.RequestAuthorization : Tool;
            var replies = request.Messages.Where(m => m.Role == ModelRole.Tool && m.Name == callName).ToArray();
            if (replies.Length == 0 || RepeatRefusal && replies.Length == 1)
            {
                if (Tool != ToolCatalog.AttachmentsRead || !Review) Assert.Contains(request.Tools!, t => t.Name == Tool);
                var arguments = !Review ? Arguments : Tool == ToolCatalog.ArtifactsVerify
                    ? JsonSerializer.Serialize(new { operation = "verify_artifact", artifactId = JsonSerializer.Deserialize<JsonElement>(Arguments).GetProperty("artifactId").GetGuid() })
                    : Tool == ToolCatalog.AttachmentsRead ? JsonSerializer.Serialize(new { operation = "read_attachment", read = JsonSerializer.Deserialize<JsonElement>(Arguments) })
                    : JsonSerializer.Serialize(new { operation = "inspect_workspace", read = new { tool = Tool, arguments = JsonSerializer.Deserialize<JsonElement>(Arguments) } });
                yield return new ModelToolCallEvent(new(replies.Length == 0 ? "background-file-observation" : "repeat-background-file-observation", callName, arguments));
            }
            else if (Review && replies.Last().Text.Contains("\"decision\":\"allowed\"", StringComparison.Ordinal)
                && replies.Last().Text.Contains("\"applied\":false", StringComparison.Ordinal)
                && !request.Messages.Any(m => m.Role == ModelRole.Tool && m.Name == Tool))
                yield return new ModelToolCallEvent(new("background-file-after-policy", Tool, Arguments));
            else yield return new ModelToolCallEvent(new("background-file-complete", ToolCatalog.WorkComplete,
                """{"outcome":"Response","summary":"File observation finished.","attentionRequired":false}"""));
            yield return new ModelCompleted(ModelStopReason.ToolCalls);
        }
    }

    [Theory(Timeout = 90000)] [InlineData(false)] [InlineData(true)]
    public Task Tenant_background_file_read_uses_actual_owned_model_and_reports_once(bool sqlite) => BackgroundFileFlow(sqlite, "home", "allowed");
    [Theory(Timeout = 90000)] [InlineData(false)] [InlineData(true)]
    public Task Tenant_background_file_read_Review_hands_off_and_reports_only_terminal_successor(bool sqlite) => BackgroundFileFlow(sqlite, "home", "approve");

    [Theory(Timeout = 90000)]
    [InlineData(false, "working", "allowed")] [InlineData(true, "working", "approve")]
    [InlineData(false, "list", "allowed")] [InlineData(true, "list", "approve")]
    [InlineData(false, "search", "allowed")] [InlineData(true, "search", "approve")]
    [InlineData(false, "resource", "allowed")] [InlineData(true, "resource", "approve")]
    [InlineData(false, "resource-list", "approve")] [InlineData(true, "resource-search", "approve")]
    [InlineData(false, "knowledge", "allowed")] [InlineData(true, "knowledge", "approve")]
    [InlineData(false, "docx", "allowed")] [InlineData(true, "docx", "approve")]
    [InlineData(false, "xlsx", "approve")] [InlineData(true, "xlsx", "allowed")]
    [InlineData(false, "artifact", "allowed")] [InlineData(true, "artifact", "approve")]
    public Task Tenant_background_file_families_keep_actual_sources_and_independent_read_consent(bool sqlite, string kind, string decision) => BackgroundFileFlow(sqlite, kind, decision);
    [Theory(Timeout = 90000)] [InlineData(false, "attachment-virtual")] [InlineData(true, "attachment-virtual")] [InlineData(false, "attachment")] [InlineData(true, "attachment-list")]
    public Task Tenant_background_pending_Attachments_do_not_supply_read_evidence(bool sqlite, string kind) => BackgroundFileFlow(sqlite, kind, "pending");
    [Theory(Timeout = 90000)] [InlineData(false, "reject")] [InlineData(true, "reject")] [InlineData(false, "changed")] [InlineData(true, "changed")] [InlineData(false, "revoked")] [InlineData(true, "revoked")]
    public Task Tenant_background_file_refusal_never_substitutes_or_returns_source_bytes(bool sqlite, string decision) => BackgroundFileFlow(sqlite, "home", decision);
    [Theory(Timeout = 120000)] [InlineData("home")] [InlineData("resource")] [InlineData("artifact")]
    public Task Tenant_background_file_consent_survives_actual_SQLite_host_recreation(string kind) => BackgroundFileFlow(true, kind, "approve", restart: true);

    [Theory(Timeout = 90000)] [InlineData(false, "bound")] [InlineData(true, "bound")] [InlineData(false, "bound-review")] [InlineData(true, "bound-review")]
    public Task Tenant_background_capable_definition_reads_actual_live_bound_Attachment_with_current_consent(bool sqlite, string decision) => BackgroundFileFlow(sqlite, decision == "bound" ? "attachment-virtual" : "attachment", decision);

    private async Task BackgroundFileFlow(bool sqlite, string kind, string decision, bool restart = false)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-background-files-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        WebApplicationFactory<Program>? recreated = null; HttpClient? recreatedOwner = null;
        try
        {
            var model = new BackgroundFileModel { Review = decision is not ("allowed" or "revoked" or "pending") || kind == "attachment", RepeatRefusal = decision is "reject" or "changed" };
            await using WebApplicationFactory<Program> initial = sqlite ? new TenantBackgroundSqliteFactory(root, model) : new TenantBackgroundMemoryFactory(root, model);
            var factory = initial;
            using var initialOwner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") }); var owner = initialOwner;
            var setup = await StartTenantBackground(factory, owner, submit: false, attachmentCapability: decision.StartsWith("bound", StringComparison.Ordinal), reportReads: true); var tenant = setup.Tenant; var api = $"/api/v2/tenants/{tenant}";
            var session = (await factory.Services.GetRequiredService<ITenantSessionStore>().FindTenantSessionAsync(tenant, setup.Session))!;
            var instance = session.Owner.AgentInstanceId; var executor = session.Owner.ExecutorPrincipalId;
            var human = (await Json(await owner.GetAsync(api + "/access/me"))).GetProperty("principalId").GetGuid();
            const string body = "Exact background source: thử nghiệm. No inherited authority.";
            var path = kind == "working" ? "/working/observation.txt" : "/home/observation.txt";
            if (kind.StartsWith("resource", StringComparison.Ordinal) || kind == "knowledge")
            {
                var saved = await Json(await owner.PostAsJsonAsync(api + $"/agents/{instance}/resources", new { expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1,
                    input = new { logicalPath = "observation.txt", kind = kind == "knowledge" ? AgentCore.Domain.Definitions.AgentDefinitionResourceKind.Knowledge : AgentCore.Domain.Definitions.AgentDefinitionResourceKind.Reference, mediaType = "text/plain", content = Encoding.UTF8.GetBytes(body) } }));
                path = saved.GetProperty("resource").GetProperty("virtualPath").GetString()!;
                if (kind == "knowledge") { model.Tool = ToolCatalog.KnowledgeRetrieve; model.Arguments = JsonSerializer.Serialize(new { identity = saved.GetProperty("resource").GetProperty("key").GetString() }); }
            }
            var access = factory.Services.GetRequiredService<TenantAccessService>();
            var role = await access.SaveRoleAsync(human, tenant, null, "Background file observations", ["agents.read", "sessions.read", "workspaces.read", "resources.read", "artifacts.read", "attachments.read"], await Revision(owner, tenant));
            var assignment = await access.AssignRoleAsync(human, tenant, executor, role.RoleId, AccessScope.ForTenant(tenant), await Revision(owner, tenant));
            var permission = kind.StartsWith("resource", StringComparison.Ordinal) || kind == "knowledge" ? "resources.read" : kind == "artifact" ? "artifacts.read" : kind.StartsWith("attachment", StringComparison.Ordinal) ? "attachments.read" : "workspaces.read";
            var expectedConsent = model.Review && (!kind.StartsWith("attachment", StringComparison.Ordinal) || decision == "bound-review");
            if (expectedConsent) Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(api + "/operation-policies/" + permission, new { mode = "always", independentReview = false, expectedRevision = await Revision(owner, tenant), expectedPolicyRevision = 0 })).StatusCode);
            var sent = await Json(await owner.PostAsJsonAsync(api + $"/sessions/{setup.Session}/messages", new { messageId = Guid.NewGuid(), text = "Observe the exact owned file in background", expectedRevision = await Revision(owner, tenant), expectedSessionRevision = 1 }));
            var parentRunId = sent.GetProperty("runId").GetGuid();
            await model.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            var sessions = factory.Services.GetRequiredService<ITenantSessionStore>(); var runs = factory.Services.GetRequiredService<ITenantRunStore>();
            var child = (await sessions.ListTenantSessionsAsync(tenant, null, 0, 100)).Items.Single(s => s.Snapshot.Origin.OriginatingAgentRunId == parentRunId);
            var initialId = child.Snapshot.Origin.InitialBackgroundAgentRunId!.Value;
            var workspace = api + (kind == "working" ? $"/sessions/{child.Owner.SessionId}/workspace" : $"/agents/{instance}/workspace");
            byte[] bytes = Encoding.UTF8.GetBytes(body);
            if (kind is "docx" or "xlsx")
            {
                var office = factory.Services.GetRequiredService<IOfficeProcessor>(); path = "/home/observation." + kind;
                bytes = kind == "docx" ? office.CreateDocument(new("Background observation", [new("Section", [body], null)]), default).Bytes
                    : office.CreateWorkbook(new([new("Sheet", [[new("text", Text: body)]])]), default).Bytes;
                model.Tool = kind == "docx" ? ToolCatalog.OfficeDocumentInspect : ToolCatalog.OfficeSpreadsheetInspect;
                model.Arguments = kind == "docx" ? JsonSerializer.Serialize(new { path, offset = 0, limit = 10 }) : JsonSerializer.Serialize(new { path, sheet = "Sheet", range = "A1:A1" });
            }
            if (kind.StartsWith("attachment", StringComparison.Ordinal))
            {
                var currentChild = (await sessions.FindTenantSessionAsync(tenant, child.Owner.SessionId))!;
                Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync(api + $"/sessions/{child.Owner.SessionId}/continue-in-chat", new { expectedRevision = await Revision(owner, tenant), expectedSessionRevision = currentChild.Snapshot.Revision })).StatusCode);
                currentChild = (await sessions.FindTenantSessionAsync(tenant, child.Owner.SessionId))!;
                using var upload = new MultipartFormDataContent(); var content = new ByteArrayContent(bytes); content.Headers.ContentType = new("text/plain"); upload.Add(content, "file", "observation.txt");
                var attachment = (await Json(await owner.PostAsync(api + $"/sessions/{child.Owner.SessionId}/attachments?uploadId={Guid.NewGuid()}&expectedRevision={await Revision(owner, tenant)}&expectedSessionRevision={currentChild.Snapshot.Revision}", upload))).GetProperty("attachmentId").GetGuid();
                currentChild = (await sessions.FindTenantSessionAsync(tenant, child.Owner.SessionId))!;
                if (decision.StartsWith("bound", StringComparison.Ordinal))
                {
                    var queued = await owner.PostAsJsonAsync(api + $"/sessions/{child.Owner.SessionId}/messages", new { messageId = Guid.NewGuid(), text = "Keep the queued file available", attachmentIds = new[] { attachment }, expectedRevision = await Revision(owner, tenant), expectedSessionRevision = currentChild.Snapshot.Revision });
                    Assert.True(queued.StatusCode == HttpStatusCode.Accepted, await queued.Content.ReadAsStringAsync());
                }
                Assert.Equal(decision.StartsWith("bound", StringComparison.Ordinal) ? AttachmentState.Bound : AttachmentState.Pending, (await factory.Services.GetRequiredService<ITenantAttachmentStore>().FindTenantAsync(tenant, child.Owner.SessionId, attachment))!.State);
                model.Tool = kind == "attachment" ? ToolCatalog.AttachmentsRead : kind == "attachment-list" ? ToolCatalog.WorkspaceList : ToolCatalog.WorkspaceRead;
                model.Arguments = kind == "attachment" ? JsonSerializer.Serialize(new { attachmentId = attachment }) : JsonSerializer.Serialize(new { path = kind == "attachment-list" ? "/attachments" : "/attachments/" + attachment.ToString("D") });
            }
            else if (kind == "artifact")
            {
                var held = (await runs.FindTenantRunAsync(tenant, initialId))!;
                var artifact = await factory.Services.GetRequiredService<ITenantArtifactStore>().CreateTenantAsync(new(held.Owner, held.SessionId, held.AgentRunId, held.Claim!.Generation), Guid.NewGuid(), "Exact background source.txt", "text/plain", bytes);
                model.Tool = ToolCatalog.ArtifactsVerify; model.Arguments = JsonSerializer.Serialize(new { artifactId = artifact.ArtifactId });
            }
            else if (!kind.StartsWith("resource", StringComparison.Ordinal) && kind != "knowledge")
                Assert.Equal(HttpStatusCode.NoContent, (await owner.PutAsync(workspace + $"/content?path={Uri.EscapeDataString(path)}&expectedRevision={await Revision(owner, tenant)}", new ByteArrayContent(bytes))).StatusCode);
            if (kind is "home" or "working" or "resource") model.Arguments = JsonSerializer.Serialize(new { path });
            if (kind is "list" or "resource-list") { model.Tool = ToolCatalog.WorkspaceList; model.Arguments = JsonSerializer.Serialize(new { path = path[..path.LastIndexOf('/')] }); }
            if (kind is "search" or "resource-search") { model.Tool = ToolCatalog.WorkspaceSearch; model.Arguments = JsonSerializer.Serialize(new { path = path[..path.LastIndexOf('/')], query = "background source", maxResults = 10 }); }
            if (decision == "revoked") Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync(api + $"/role-assignments/{assignment.AssignmentId}?expectedRevision={await Revision(owner, tenant)}")).StatusCode);
            if (kind == "home" && decision == "allowed")
            {
                var held = (await runs.FindTenantRunAsync(tenant, initialId))!;
                var admission = new ToolExecutionAdmission(true, TriggerKind.ManualInvocation, AgentInstanceId: instance, SupportsTools: true,
                    OwnedSessionId: held.SessionId, AgentRunId: held.AgentRunId, TenantId: tenant, AgentRunGeneration: held.Claim!.Generation);
                var guard = factory.Services.GetRequiredService<TenantToolAuthorization>();
                Assert.Equal(ToolPolicyDecision.Deny, await guard.PolicyAsync(held.SessionId, new("blocked-native", ToolCatalog.SandboxExec, "{}"), admission, default));
                var denied = await Assert.ThrowsAsync<AccessDeniedException>(() => guard.PrepareAsync(held.SessionId,
                    new("blocked-persona", ToolCatalog.RequestAuthorization, """{"operation":"update_persona","persona":{"name":"Blocked","role":"Blocked","description":"Blocked","tone":"Blocked"}}"""), admission, default).AsTask());
                Assert.Equal("tenant_effect_unavailable", denied.ReasonCode);
            }
            model.Release.TrySetResult();
            var waiting = await AwaitBrowserRestartRun(runs, tenant, initialId, expectedConsent);
            TenantApprovalRequest? request = null;
            if (expectedConsent)
            {
                Assert.Equal(AgentRunStatus.WaitingForApproval, waiting.Status);
                request = (await factory.Services.GetRequiredService<ITenantApprovalStore>().FindApprovalAsync(tenant, waiting.ApprovalRequestId!.Value))!;
                Assert.Equal(ApprovalKind.Review, request.Kind); Assert.Equal(child.Owner.SessionId, request.SessionId);
                if (decision == "changed") Assert.Equal(HttpStatusCode.NoContent, (await owner.PutAsync(workspace + $"/content?path={Uri.EscapeDataString(path)}&expectedRevision={await Revision(owner, tenant)}", new ByteArrayContent("Changed source must not be substituted."u8.ToArray()))).StatusCode);
                if (restart)
                {
                    await factory.DisposeAsync(); var nextModel = new BackgroundFileModel { Review = true, Tool = model.Tool, Arguments = model.Arguments }; nextModel.Release.TrySetResult(); model = nextModel;
                    recreated = new TenantBackgroundSqliteFactory(root, model); factory = recreated; recreatedOwner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") }); owner = recreatedOwner;
                    await Login(owner, "owner@example.test", "explicit-synthetic-test-password"); runs = factory.Services.GetRequiredService<ITenantRunStore>();
                    Assert.Equal(AgentRunStatus.WaitingForApproval, (await runs.FindTenantRunAsync(tenant, initialId))!.Status);
                }
                var detail = await Json(await owner.GetAsync(api + $"/approvals/{request.RequestId}")); Assert.Equal(decision != "changed", detail.GetProperty("review").GetProperty("canDecide").GetBoolean());
                Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync(api + $"/approvals/{request.RequestId}/" + (decision == "changed" ? "cancel" : "decision"), new { decision = decision == "reject" ? "reject" : "approve", actionHash = request.Operation.ActionHash, expectedRevision = detail.GetProperty("securityRevision").GetInt64(), expectedRequestRevision = request.Revision })).StatusCode);
            }
            AgentRun final = await AwaitBrowserRestartRun(runs, tenant, initialId, false);
            if (expectedConsent)
            {
                var original = (await runs.FindTenantRunAsync(tenant, initialId))!;
                Assert.Equal(AgentRunOutcomeKind.Continued, original.Result!.OutcomeKind);
                Assert.Equal(final.AgentRunId, original.Result.ContinuationRunId);
                Assert.Equal(initialId, final.Admission.ExecutionContext!.ParentRunId);
                Assert.Equal(AgentRunOutputContract.BackgroundOutcome, final.Admission.OutputContract);
                Assert.Equal(child.Snapshot.Origin.InitialBackgroundAgentRunId, initialId);
            }
            Assert.True(final.Status == AgentRunStatus.Completed, $"Final {final.Status}: {final.Failure?.Code} {final.Failure?.Summary}\n{string.Join('\n', model.Failures)}"); Assert.Equal("File observation finished.", final.Result!.Text);
            var observations = model.ChildRequests.SelectMany(r => r.Messages).Where(m => m.Role == ModelRole.Tool).Select(m => m.Text).ToArray();
            var expected = kind is "list" or "resource-list" or "attachment-list" ? "observation.txt" : kind == "artifact" ? "Exact_background_source" : "Exact background source";
            if (decision == "pending")
            {
                Assert.DoesNotContain(observations, text => text.Contains("Exact background source", StringComparison.Ordinal) || text.Contains("observation.txt", StringComparison.Ordinal));
                Assert.Contains(observations, text => text.Contains("capability_unavailable", StringComparison.Ordinal));
            }
            else if (decision is "reject" or "changed" or "revoked")
            {
                Assert.DoesNotContain(observations, text => text.Contains("Exact background source", StringComparison.Ordinal) || text.Contains("Changed source", StringComparison.Ordinal));
                Assert.Contains(observations, text => text.Contains(decision == "reject" ? "approval_rejected" : decision == "changed" ? "approval_cancelled" : "executor_read_required", StringComparison.Ordinal));
            }
            else Assert.True(observations.Any(text => text.Contains(expected, StringComparison.Ordinal)), string.Join("\n", observations));
            if (kind == "artifact") Assert.DoesNotContain(observations, text => text.Contains("No inherited authority", StringComparison.Ordinal));
            Assert.Equal(waiting.Admission.ExecutionContext!.RootActivationId, final.Admission.ExecutionContext!.RootActivationId);
            var report = await WaitBackgroundFileReport(runs, tenant, setup.Session);
            Assert.Equal(final.AgentRunId, report.Admission.ExecutionContext!.ParentRunId);
            var history = await Json(await owner.GetAsync(api + $"/sessions/{setup.Session}/messages?limit=100")); Assert.Contains("Background work: File observation finished.", history.ToString());
            var activity = await Json(await owner.GetAsync(api + $"/agents/{instance}/activity/sessions/{child.Owner.SessionId}/background"));
            Assert.Equal(initialId, activity.GetProperty("session").GetProperty("initialRunId").GetGuid());
            Assert.Equal(final.AgentRunId, activity.GetProperty("initialRun").GetProperty("runId").GetGuid());
            Assert.Equal("File observation finished.", activity.GetProperty("initialRun").GetProperty("outcomeText").GetString());
            Assert.Equal("delivered", activity.GetProperty("completionDelivery").GetProperty("status").GetString());
            if (sqlite && kind == "home" && decision == "approve" && !restart)
                await AssertBackgroundSuccessorReadScope(factory, owner, tenant, instance, child.Owner.SessionId, initialId, final.AgentRunId);
            var audit = await factory.Services.GetRequiredService<IIamStore>().ReadAuditAsync(tenant, 200); Assert.Single(audit, a => a.Action == "agent-runs.background.report");
            if (expectedConsent) Assert.Single(audit, a => a.Action == "approvals.request");
            else Assert.DoesNotContain(audit, a => a.Action == "approvals.request");
            if (model.RepeatRefusal) Assert.Contains(observations, text => text.Contains(decision == "reject" ? "previously_rejected" : "previously_cancelled", StringComparison.Ordinal));
        }
        finally { recreatedOwner?.Dispose(); if (recreated is not null) await recreated.DisposeAsync(); SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private static async Task AssertBackgroundSuccessorReadScope(WebApplicationFactory<Program> factory, HttpClient owner, Guid tenant, Guid instance, Guid session, Guid initial, Guid successor)
    {
        var access = factory.Services.GetRequiredService<TenantAccessService>(); var identity = factory.Services.GetRequiredService<IdentityService>();
        var api = $"/api/v2/tenants/{tenant}";
        var human = (await Json(await owner.GetAsync(api + "/access/me"))).GetProperty("principalId").GetGuid();
        var account = await identity.RegisterAsync("background-reader@example.test", "Background reader", "background-reader-password");
        var memberRole = await access.SaveRoleAsync(human, tenant, null, "Background reader membership", ["members.read"], await Revision(owner, tenant));
        var invitation = await access.InviteAsync(human, tenant, account.LoginKey, memberRole.RoleId, await Revision(owner, tenant));
        var user = await access.AcceptInvitationAsync(account.AccountId, tenant, invitation.Invitation.InvitationId, invitation.Token);
        async Task Grant(string permission, string resource, Guid id)
        {
            var role = await access.SaveRoleAsync(human, tenant, null, permission + " " + id, [permission], await Revision(owner, tenant));
            await access.AssignRoleAsync(human, tenant, user.PrincipalId, role.RoleId, AccessScope.ForResource(tenant, resource, id.ToString("D")), await Revision(owner, tenant));
        }
        await Grant("agents.read", "agent", instance); await Grant("sessions.read", "session", session); await Grant("agent-runs.read", "agent-run", initial);
        using var reader = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") }); await Login(reader, account.LoginKey, "background-reader-password");
        var url = api + $"/agents/{instance}/activity/sessions/{session}/background";
        var hidden = await Json(await reader.GetAsync(url));
        Assert.Equal(initial, hidden.GetProperty("initialRun").GetProperty("runId").GetGuid());
        Assert.DoesNotContain("File observation finished.", hidden.GetRawText()); Assert.Equal(JsonValueKind.Null, hidden.GetProperty("completionDelivery").ValueKind);
        await Grant("agent-runs.read", "agent-run", successor);
        var visible = await Json(await reader.GetAsync(url)); Assert.Equal(successor, visible.GetProperty("initialRun").GetProperty("runId").GetGuid());
        Assert.Equal("File observation finished.", visible.GetProperty("initialRun").GetProperty("outcomeText").GetString());
        Assert.Equal(JsonValueKind.Null, visible.GetProperty("completionDelivery").GetProperty("targetSessionId").ValueKind);
    }
    private static async Task<AgentRun> WaitBackgroundFileReport(ITenantRunStore runs, Guid tenant, Guid session)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (true)
        {
            foreach (var id in await runs.ListTenantRunIdsAsync(tenant, session))
            {
                var run = (await runs.FindTenantRunAsync(tenant, id))!;
                if (run.Admission.OutputContract != AgentRunOutputContract.CompletionReport) continue;
                if (run.IsTerminal) { Assert.True(run.Status == AgentRunStatus.Completed, $"Report {run.Status}: {run.Failure?.Summary}"); return run; }
            }
            await Task.Delay(25, timeout.Token);
        }
    }
}
