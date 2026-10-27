using AgentCore.Infrastructure.Workspaces;
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
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    private sealed class BackgroundFileEffectModel : TenantBackgroundModel
    {
        public bool Review { get; init; }
        public bool RepeatRefusal { get; init; }
        public string WriteTool { get; set; } = ToolCatalog.WorkspaceWrite;
        public string WriteArguments { get; set; } = """{"path":"/home/background-effect.txt","content":"Exact background file: thử nghiệm."}""";
        public string PublishTool { get; set; } = ToolCatalog.ArtifactsCreateFromWorkspace;
        public string PublishArguments { get; set; } = """{"path":"/home/background-effect.txt","displayName":"background-result.txt","contentType":"text/plain"}""";
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentQueue<ModelRequest> ChildRequests { get; } = new();
        public override async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            if (Origin(request) != ExecutionOrigin.DelegatedTask && request.Messages.Any(m => m.Role == ModelRole.User && m.Text.Contains("Keep the queued file available", StringComparison.Ordinal)))
            { yield return new ModelSemanticResponseReady(new("Queued file noted.", new(ModelSpeechMode.None, null), [])); yield return new ModelCompleted(ModelStopReason.Completed); yield break; }
            if (Origin(request) != ExecutionOrigin.DelegatedTask)
            { await foreach (var item in base.GenerateAsync(request, ct)) yield return item; yield break; }
            ChildRequests.Enqueue(request); Entered.TrySetResult(); await Release.Task.WaitAsync(ct);
            Assert.Contains(request.Tools!, t => t.Name == ToolCatalog.WorkComplete);
            Assert.Equal(new[] { "tasks.delegate" }, request.Tools!
                .Where(t => t.Name is "tasks.delegate" or ToolCatalog.SandboxExec or ToolCatalog.BrowserNavigate).Select(t => t.Name));
            var schema = Assert.Single(request.Tools!, t => t.Name == ToolCatalog.RequestAuthorization).ParametersJson;
            Assert.DoesNotContain("update_persona", schema); Assert.DoesNotContain(ToolCatalog.BrowserFillCredential, schema);
            var refused = request.Messages.Where(m => m.Role == ModelRole.Tool && m.ToolCallId?.StartsWith("background-file-stage-", StringComparison.Ordinal) == true)
                .FirstOrDefault(m => !m.Text.Contains("\"applied\":true", StringComparison.Ordinal));
            if (refused is not null && (!RepeatRefusal || request.Messages.Any(m => m.Role == ModelRole.Tool && m.ToolCallId == "background-file-refusal-repeat")))
            {
                yield return new ModelToolCallEvent(new("background-file-effect-complete", ToolCatalog.WorkComplete,
                    """{"outcome":"NeedsAttention","summary":"Background file work needs attention.","attentionRequired":true}"""));
                yield return new ModelCompleted(ModelStopReason.ToolCalls); yield break;
            }
            for (var step = 0; step < 2; step++)
            {
                var id = "background-file-stage-" + step;
                if (refused is null && request.Messages.Any(m => m.Role == ModelRole.Tool && m.ToolCallId == id)) continue;
                if (refused is not null) id = "background-file-refusal-repeat";
                var tool = step == 0 ? WriteTool : PublishTool; var arguments = step == 0 ? WriteArguments : PublishArguments;
                Assert.Contains(request.Tools!, t => t.Name == tool);
                if (Review)
                {
                    arguments = step == 0 ? JsonSerializer.Serialize(new { operation = "mutate_workspace", mutation = new { tool, arguments = JsonSerializer.Deserialize<JsonElement>(arguments) } })
                        : JsonSerializer.Serialize(new { operation = PublishTool == ToolCatalog.ArtifactsCreateFromWorkspace ? "publish_workspace_artifact" : "publish_artifact", artifact = JsonSerializer.Deserialize<JsonElement>(arguments) });
                    tool = ToolCatalog.RequestAuthorization;
                }
                yield return new ModelToolCallEvent(new(id, tool, arguments)); yield return new ModelCompleted(ModelStopReason.ToolCalls); yield break;
            }
            yield return new ModelToolCallEvent(new("background-file-effect-complete", ToolCatalog.WorkComplete,
                """{"outcome":"Response","summary":"Background file work finished.","attentionRequired":false}"""));
            yield return new ModelCompleted(ModelStopReason.ToolCalls);
        }
    }

    [Theory(Timeout = 120000)] [InlineData(false, "allowed")] [InlineData(true, "allowed")]
    [InlineData(false, "review")] [InlineData(true, "review")]
    [InlineData(false, "grant")] [InlineData(true, "grant")]
    [InlineData(false, "reject")] [InlineData(true, "reject")]
    [InlineData(false, "reject-repeat")] [InlineData(true, "reject-repeat")]
    [InlineData(false, "missing")] [InlineData(true, "missing")]
    [InlineData(false, "changed")] [InlineData(true, "changed")]
    [InlineData(false, "revoked")] [InlineData(true, "revoked")]
    [InlineData(false, "unknown")] [InlineData(true, "unknown")]
    public Task Tenant_background_file_effects_mutate_publish_and_report_through_existing_owners(bool sqlite, string mode) => BackgroundFileEffectFlow(sqlite, mode);

    [Theory(Timeout = 120000)] [InlineData(false, "patch", "allowed")] [InlineData(true, "patch", "review")]
    [InlineData(false, "batch", "review")] [InlineData(true, "batch", "review")] [InlineData(true, "batch", "required")]
    [InlineData(false, "docx", "allowed")] [InlineData(true, "docx", "review")]
    [InlineData(false, "xlsx", "review")] [InlineData(true, "xlsx", "allowed")]
    [InlineData(false, "attachment", "review")] [InlineData(true, "attachment", "allowed")]
    [InlineData(false, "resource", "allowed")] [InlineData(true, "resource", "review")]
    public Task Tenant_background_file_effect_families_use_actual_owned_sources_and_independent_primary_operations(bool sqlite, string kind, string mode) => BackgroundFileEffectFlow(sqlite, mode, kind);

    [Theory(Timeout = 120000)] [InlineData(false, "allowed")] [InlineData(true, "review")]
    public Task Tenant_background_inline_publication_uses_explicitly_declared_capability_and_exact_review(bool sqlite, string mode) => BackgroundFileEffectFlow(sqlite, mode, "inline");

    [Theory(Timeout = 120000)] [InlineData(false)] [InlineData(true)]
    public Task Tenant_background_file_current_HomeAccess_restricts_already_admitted_effects(bool sqlite) => BackgroundFileEffectFlow(sqlite, "home-disabled");

    [Theory(Timeout = 120000)] [InlineData(false, "allowed")] [InlineData(true, "allowed")] [InlineData(false, "review")] [InlineData(true, "review")]
    public Task Tenant_background_file_effect_requester_keeps_parent_scope_without_inheriting_private_child_access(bool sqlite, string mode) => BackgroundFileEffectFlow(sqlite, mode, scoped: true);

    [Theory(Timeout = 120000)] [InlineData("text")] [InlineData("docx")] [InlineData("resource")]
    public Task Tenant_background_file_effect_Review_survives_actual_SQLite_host_recreation_without_replaying_mutation(string kind) => BackgroundFileEffectFlow(true, "review", kind, restart: true);

    private async Task BackgroundFileEffectFlow(bool sqlite, string mode, string kind = "text", bool restart = false, bool scoped = false)
    {
        const string body = "Exact background file: thử nghiệm.";
        var root = Path.Combine(Path.GetTempPath(), "tenant-background-file-effect-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        WebApplicationFactory<Program>? recreated = null; HttpClient? reopenedOwner = null;
        try
        {
            var reviewed = mode is not ("allowed" or "missing" or "unknown" or "required" or "home-disabled"); var writes = 0;
            var model = new BackgroundFileEffectModel { Review = reviewed, RepeatRefusal = mode == "reject-repeat" };
            void Configure(IServiceCollection services)
            {
                services.AddSingleton<ILoggerProvider>(new BackgroundFailureLogs(model));
                if (mode == "unknown") services.Replace(ServiceDescriptor.Singleton<ITenantWorkspaceMutationStore>(p =>
                    new LostWorkspaceAcknowledgement((ITenantWorkspaceMutationStore)p.GetRequiredService<FileSpaceWorkspaceStore>(), () => Interlocked.Increment(ref writes))));
            }
            await using WebApplicationFactory<Program> initial = sqlite ? new NativeTenantSqliteFactory(root, model, Configure) : new NativeTenantMemoryFactory(root, model, Configure);
            var factory = initial;
            using var firstOwner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") }); var owner = firstOwner;
            var setup = await StartTenantBackground(factory, owner, submit: false, fileCapabilities: kind == "inline" ? new[] { "tasks.delegate", ToolCatalog.WorkspaceWrite, ToolCatalog.ArtifactsCreate } : null, reportReads: true); var tenant = setup.Tenant; var api = $"/api/v2/tenants/{tenant}";
            var sessions = factory.Services.GetRequiredService<ITenantSessionStore>(); var runs = factory.Services.GetRequiredService<ITenantRunStore>();
            var parent = (await sessions.FindTenantSessionAsync(tenant, setup.Session))!;
            var instance = parent.Owner.AgentInstanceId; var executor = parent.Owner.ExecutorPrincipalId;
            var human = (await Json(await owner.GetAsync(api + "/access/me"))).GetProperty("principalId").GetGuid();
            var access = factory.Services.GetRequiredService<TenantAccessService>();
            var permissions = new List<string> { "agents.read", "sessions.read", "workspaces.read", "artifacts.read", "attachments.read", "resources.read", "workspaces.delete" };
            if (mode is not ("grant" or "missing")) permissions.AddRange(["workspaces.write", "artifacts.create"]);
            var role = await access.SaveRoleAsync(human, tenant, null, "Background file effects", permissions, await Revision(owner, tenant));
            var assignment = await access.AssignRoleAsync(human, tenant, executor, role.RoleId, AccessScope.ForTenant(tenant), await Revision(owner, tenant));
            if (reviewed && mode != "grant" || scoped)
                foreach (var permission in new[] { "workspaces.write", "artifacts.create" })
                    Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(api + "/operation-policies/" + permission, new { mode = reviewed ? "always" : "whenRequesterLacksPermission", independentReview = false, expectedRevision = await Revision(owner, tenant), expectedPolicyRevision = 0 })).StatusCode);
            if (scoped)
                foreach (var permission in new[] { "sessions.read", "workspaces.read" })
                    Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(api + "/operation-policies/" + permission, new { mode = "none", independentReview = false, expectedRevision = await Revision(owner, tenant), expectedPolicyRevision = 0 })).StatusCode);
            var path = kind == "inline" ? "/working/background-effect.txt" : kind == "xlsx" ? "/working/background-effect.xlsx" : "/home/background-effect." + (kind == "docx" ? "docx" : "txt");
            model.WriteArguments = JsonSerializer.Serialize(new { path, content = body });
            if (kind == "docx") { model.WriteTool = ToolCatalog.OfficeDocumentCreate; model.WriteArguments = JsonSerializer.Serialize(new { path, title = "Owned background file", sections = new[] { new { heading = "Observed", paragraphs = new[] { body } } } }); }
            if (kind == "xlsx") { model.WriteTool = ToolCatalog.OfficeSpreadsheetCreate; model.WriteArguments = JsonSerializer.Serialize(new { path, sheets = new[] { new { name = "試験", rows = new[] { new[] { new { type = "text", text = body } } } } } }); }
            if (kind == "patch") { model.WriteTool = ToolCatalog.WorkspacePatch; model.WriteArguments = JsonSerializer.Serialize(new { path, edits = new[] { new { oldText = "Private original", newText = body } } }); }
            if (kind == "batch") { path = "/working/owned/result.txt"; model.WriteTool = ToolCatalog.WorkspaceBatch; model.WriteArguments = JsonSerializer.Serialize(new { operations = new object[] { new { op = "mkdir", path = "/working/owned" }, new { op = "copy", source = "/working/source.txt", destination = "/working/owned/copy.txt" }, new { op = "move", source = "/working/owned/copy.txt", destination = path }, new { op = "delete", path = "/working/source.txt" } } }); }
            if (kind == "resource")
            {
                var saved = await Json(await owner.PostAsJsonAsync(api + $"/agents/{instance}/resources", new { expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = (await factory.Services.GetRequiredService<ITenantAgentStore>().FindTenantAgentAsync(tenant, instance))!.Instance.Revision,
                    input = new { logicalPath = "background-source.txt", kind = AgentCore.Domain.Definitions.AgentDefinitionResourceKind.Reference, mediaType = "text/plain", content = Encoding.UTF8.GetBytes(body) } }));
                path = saved.GetProperty("resource").GetProperty("virtualPath").GetString()!;
            }
            model.PublishArguments = JsonSerializer.Serialize(new { path, displayName = "background-result." + (kind is "docx" or "xlsx" ? kind : "txt") });
            if (kind == "inline") { model.PublishTool = ToolCatalog.ArtifactsCreate; model.PublishArguments = JsonSerializer.Serialize(new { displayName = "background-inline.txt", contentType = "text/plain", content = body }); }
            using var member = scoped ? factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") }) : null;
            Guid requester = human;
            if (scoped)
            {
                var identity = factory.Services.GetRequiredService<IdentityService>(); var account = await identity.RegisterAsync("background-requester@example.test", "Scoped requester", "background-requester-password");
                var directory = await access.SaveRoleAsync(human, tenant, null, "Requester directory", ["members.read"], await Revision(owner, tenant));
                var invite = await access.InviteAsync(human, tenant, account.LoginKey, directory.RoleId, await Revision(owner, tenant));
                requester = (await access.AcceptInvitationAsync(account.AccountId, tenant, invite.Invitation.InvitationId, invite.Token)).PrincipalId;
                var intentRole = await access.SaveRoleAsync(human, tenant, null, "Independent creation and primary file operations", ["sessions.create", "agent-runs.create", "tasks.delegate", "spaces.read", "workspaces.write", "artifacts.create"], await Revision(owner, tenant));
                await access.AssignRoleAsync(human, tenant, requester, intentRole.RoleId, AccessScope.ForTenant(tenant), await Revision(owner, tenant));
                foreach (var grant in new[] { ("sessions.read", TenantSessionRules.Scope(tenant, setup.Session)), ("agents.chat", AccessScope.ForResource(tenant, "agent", instance.ToString("D"))) })
                { var scopedRole = await access.SaveRoleAsync(human, tenant, null, grant.Item1, [grant.Item1], await Revision(owner, tenant)); await access.AssignRoleAsync(human, tenant, requester, scopedRole.RoleId, grant.Item2, await Revision(owner, tenant)); }
                await Login(member!, account.LoginKey, "background-requester-password");
            }
            var source = (await Json(await (member ?? owner).PostAsJsonAsync(api + $"/sessions/{setup.Session}/messages", new { messageId = Guid.NewGuid(), text = "Prepare and publish the owned background file", expectedRevision = await Revision(owner, tenant), expectedSessionRevision = 1 }))).GetProperty("runId").GetGuid();
            await model.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            var child = (await sessions.ListTenantSessionsAsync(tenant, null, 0, 100)).Items.Single(s => s.Snapshot.Origin.OriginatingAgentRunId == source);
            var initialId = child.Snapshot.Origin.InitialBackgroundAgentRunId!.Value;
            var workerSpace = (await factory.Services.GetRequiredService<ITenantSpaceStore>().FindPrivateSpaceAsync(tenant, instance))!;
            var workspace = api + (kind is "xlsx" or "batch" ? $"/sessions/{child.Owner.SessionId}/workspace" : $"/spaces/{workerSpace.SpaceId}/workspace");
            if (kind is "patch" or "batch")
                Assert.Equal(HttpStatusCode.NoContent, (await owner.PutAsync(workspace + $"/content?path={Uri.EscapeDataString(kind == "patch" ? path : "/working/source.txt")}&expectedRevision={await Revision(owner, tenant)}", new ByteArrayContent(Encoding.UTF8.GetBytes(kind == "patch" ? "Private original" : body)))).StatusCode);
            if (kind == "attachment")
            {
                Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync(api + $"/sessions/{child.Owner.SessionId}/continue-in-chat", new { expectedRevision = await Revision(owner, tenant), expectedSessionRevision = child.Snapshot.Revision })).StatusCode);
                var snapshot = (await sessions.FindTenantSessionAsync(tenant, child.Owner.SessionId))!.Snapshot;
                using var upload = new MultipartFormDataContent(); var bytes = new ByteArrayContent(Encoding.UTF8.GetBytes(body)); bytes.Headers.ContentType = new("text/plain"); upload.Add(bytes, "file", "background-source.txt");
                var attachment = (await Json(await owner.PostAsync(api + $"/sessions/{child.Owner.SessionId}/attachments?uploadId={Guid.NewGuid()}&expectedRevision={await Revision(owner, tenant)}&expectedSessionRevision={snapshot.Revision}", upload))).GetProperty("attachmentId").GetGuid();
                Assert.Equal(HttpStatusCode.Accepted, (await owner.PostAsJsonAsync(api + $"/sessions/{child.Owner.SessionId}/messages", new { messageId = Guid.NewGuid(), text = "Keep the queued file available", attachmentIds = new[] { attachment }, expectedRevision = await Revision(owner, tenant), expectedSessionRevision = snapshot.Revision })).StatusCode);
                model.PublishArguments = JsonSerializer.Serialize(new { path = "/attachments/" + attachment.ToString("D"), displayName = "background-original.txt" });
            }
            if (mode == "home-disabled")
                Assert.Equal(HttpStatusCode.OK, (await owner.PatchAsJsonAsync(api + $"/agents/{instance}/settings/stateIsolationPolicy", new { expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = (await factory.Services.GetRequiredService<ITenantAgentStore>().FindTenantAgentAsync(tenant, instance))!.Instance.Revision,
                    set = new { homeAccess = "Disabled" }, clear = Array.Empty<string>() })).StatusCode);
            model.Release.TrySetResult();
            if (reviewed) for (var step = 0; step < 2; step++)
            {
                var waiting = await AwaitBrowserRestartRun(runs, tenant, initialId, true);
                Assert.True(waiting.Status == AgentRunStatus.WaitingForApproval, $"Step {step} {waiting.Status}: {waiting.Failure} {string.Join('\n', model.Failures)} {string.Join('\n', model.ChildRequests.SelectMany(r => r.Messages).Where(m => m.Role == ModelRole.Tool).Select(m => m.Text))}");
                var request = (await factory.Services.GetRequiredService<ITenantApprovalStore>().FindApprovalAsync(tenant, waiting.ApprovalRequestId!.Value))!;
                Assert.Equal(mode == "grant" ? ApprovalKind.ExceptionalGrant : ApprovalKind.Review, request.Kind); Assert.Equal(child.Owner.SessionId, request.SessionId);
                Assert.Equal((await runs.FindTenantRunAsync(tenant, source))!.Admission.ExecutionContext!.RootActivationId, request.RootActivationId);
                if (restart && step == 1)
                {
                    await factory.DisposeAsync(); var next = new BackgroundFileEffectModel { Review = true, WriteTool = model.WriteTool, WriteArguments = model.WriteArguments, PublishTool = model.PublishTool, PublishArguments = model.PublishArguments }; next.Release.TrySetResult(); model = next;
                    recreated = new NativeTenantSqliteFactory(root, next); factory = recreated; reopenedOwner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") }); owner = reopenedOwner;
                    await Login(owner, "owner@example.test", "explicit-synthetic-test-password"); runs = factory.Services.GetRequiredService<ITenantRunStore>();
                }
                if (step == 1 && mode == "changed") Assert.Equal(HttpStatusCode.NoContent, (await owner.PutAsync(workspace + $"/content?path={Uri.EscapeDataString(path)}&expectedRevision={await Revision(owner, tenant)}", new ByteArrayContent("Changed source must never be substituted."u8.ToArray()))).StatusCode);
                if (step == 1 && mode == "revoked") Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync(api + $"/role-assignments/{assignment.AssignmentId}?expectedRevision={await Revision(owner, tenant)}")).StatusCode);
                var detail = await Json(await owner.GetAsync(api + $"/approvals/{request.RequestId}"));
                var cancel = step == 1 && mode is "changed" or "revoked";
                if (cancel)
                {
                    Assert.False(detail.GetProperty("review").GetProperty("canDecide").GetBoolean());
                    Assert.False((await owner.PostAsJsonAsync(api + $"/approvals/{request.RequestId}/decision", new { decision = "approve", actionHash = request.Operation.ActionHash, expectedRevision = detail.GetProperty("securityRevision").GetInt64(), expectedRequestRevision = request.Revision })).IsSuccessStatusCode);
                    detail = await Json(await owner.GetAsync(api + $"/approvals/{request.RequestId}"));
                }
                Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync(api + $"/approvals/{request.RequestId}/" + (cancel ? "cancel" : "decision"), new { decision = mode.StartsWith("reject", StringComparison.Ordinal) ? "reject" : "approve", actionHash = request.Operation.ActionHash, expectedRevision = detail.GetProperty("securityRevision").GetInt64(), expectedRequestRevision = request.Revision })).StatusCode);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                while ((await CurrentApprovalExecution(runs, tenant, initialId)) is { } next && next.ApprovalRequestId == request.RequestId && !next.IsTerminal) await Task.Delay(25, timeout.Token);
                if (mode.StartsWith("reject", StringComparison.Ordinal)) break;
            }
            var final = await AwaitBrowserRestartRun(runs, tenant, initialId, false);
            var refused = mode is "reject" or "reject-repeat" or "missing" or "changed" or "revoked" or "unknown" or "required" or "home-disabled";
            Assert.True(final.Status == AgentRunStatus.Completed || mode is "revoked" or "unknown" && final.IsTerminal, $"{final.Status}: {final.Failure} {string.Join('\n', model.Failures)}");
            var report = await WaitBackgroundFileReport(runs, tenant, setup.Session); Assert.Equal(final.AgentRunId, report.Admission.ExecutionContext!.ParentRunId);
            Assert.Equal(requester, final.Admission.ExecutionContext!.OriginalInitiatorPrincipalId);
            if (final.Status == AgentRunStatus.Completed) Assert.Equal(refused ? "Background file work needs attention." : "Background file work finished.", final.Result!.Text);
            var artifacts = await factory.Services.GetRequiredService<ITenantArtifactStore>().ListTenantAsync(tenant, child.Owner.SessionId, null, null, 100);
            Assert.Equal(refused ? 0 : 1, artifacts.Items.Count);
            if (!refused)
            {
                var artifact = Assert.Single(artifacts.Items); var bytes = await owner.GetByteArrayAsync(api + $"/sessions/{child.Owner.SessionId}/artifacts/{artifact.ArtifactId}/content");
                if (kind == "docx") Assert.Contains(body, factory.Services.GetRequiredService<IOfficeProcessor>().InspectDocument(bytes, 0, 20, default).Elements.Select(e => e.Text));
                else if (kind == "xlsx") Assert.Contains(factory.Services.GetRequiredService<IOfficeProcessor>().InspectWorkbook(bytes, "試験", "A1:A1", default).Rows.SelectMany(r => r), c => c.Value == body);
                else Assert.Equal(body, Encoding.UTF8.GetString(bytes));
                Assert.Equal(final.AgentRunId, artifact.AgentRunId);
                if (kind is "patch" or "batch" or "docx" or "xlsx")
                    Assert.Equal(await owner.GetByteArrayAsync(workspace + "/content?path=" + Uri.EscapeDataString(path)), bytes);
            }
            if (kind == "batch")
            {
                if (refused) Assert.Equal(body, await owner.GetStringAsync(workspace + "/content?path=" + Uri.EscapeDataString("/working/source.txt")));
                else Assert.False((await owner.GetAsync(workspace + "/content?path=" + Uri.EscapeDataString("/working/source.txt"))).IsSuccessStatusCode);
            }
            if (scoped)
            {
                Assert.False((await member!.GetAsync(api + $"/sessions/{child.Owner.SessionId}/conversation")).IsSuccessStatusCode);
                Assert.False((await member.GetAsync(api + $"/sessions/{child.Owner.SessionId}/artifacts")).IsSuccessStatusCode);
            }
            if (mode == "unknown") Assert.Equal(1, writes);
            if (mode == "reject-repeat") Assert.Contains(model.ChildRequests.SelectMany(r => r.Messages), m => m.Role == ModelRole.Tool && m.Text.Contains("previously_rejected", StringComparison.Ordinal));
            var audit = await factory.Services.GetRequiredService<IIamStore>().ReadAuditAsync(tenant, 200);
            Assert.Single(audit, a => a.Action == "tasks.report");
            Assert.Equal(reviewed ? mode.StartsWith("reject", StringComparison.Ordinal) ? 1 : 2 : 0, audit.Count(a => a.Action == "approvals.request"));
            Assert.Equal(0, factory.Services.GetRequiredService<AgentCore.Api.Http.OutboundHttpProbe>().Attempts);
        }
        finally { reopenedOwner?.Dispose(); if (recreated is not null) await recreated.DisposeAsync(); SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
