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
using AgentCore.Infrastructure.Browser;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    private sealed class BackgroundNativeBrowserModel : TenantBackgroundModel
    {
        public string Url { get; set; } = "";
        public bool ReadReview { get; init; }
        public bool Independent { get; init; }
        public bool Capture { get; init; }
        public string? Transfer { get; init; }
        public Func<Task<Guid>>? SeedTransfer { get; set; }
        public Func<Task>? VerifyTransfer { get; set; }
        public override async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            if (!Independent && Origin(request) != ExecutionOrigin.ImmediateBackground)
            { await foreach (var e in base.GenerateAsync(request, ct)) yield return e; yield break; }
            Requests.Enqueue(request);
            Assert.Contains(request.Tools!, t => t.Name == ToolCatalog.WorkComplete);
            Assert.Contains(request.Tools!, t => t.Name == ToolCatalog.BrowserSnapshot);
            Assert.DoesNotContain(request.Tools!, t => t.Name is ToolCatalog.BrowserNavigate or ToolCatalog.SandboxExec);
            var schema = Assert.Single(request.Tools!, t => t.Name == ToolCatalog.RequestAuthorization).ParametersJson;
            Assert.Contains("run_browser_command", schema); Assert.DoesNotContain(ToolCatalog.BrowserFillCredential, schema); Assert.DoesNotContain("update_persona", schema);
            var navigation = request.Messages.SingleOrDefault(m => m.Role == ModelRole.Tool && m.ToolCallId == "child-native-navigate");
            var refused = navigation is not null && !navigation.Text.Contains("\"commandCompleted\":true", StringComparison.Ordinal);
            ModelToolCall? call = null;
            if (navigation is null) call = new("child-native-navigate", ToolCatalog.RequestAuthorization,
                JsonSerializer.Serialize(new { operation = "run_browser_command", command = new { tool = ToolCatalog.BrowserNavigate, arguments = new { url = Url } } }));
            else if (!refused && !request.Messages.Any(m => m.Role == ModelRole.Tool && m.ToolCallId == "child-native-observe"))
                call = ReadReview ? new("child-native-observe", ToolCatalog.RequestAuthorization,
                    JsonSerializer.Serialize(new { operation = "run_browser_command", command = new { tool = ToolCatalog.BrowserSnapshot, arguments = new { } } }))
                    : new("child-native-observe", ToolCatalog.BrowserSnapshot, "{}");
            else if (!refused && Transfer is not null && !request.Messages.Any(m => m.Role == ModelRole.Tool && m.ToolCallId == "child-native-transfer"))
            {
                var id = await SeedTransfer!();
                var arguments = Transfer == "drop" ? JsonSerializer.SerializeToElement(new { target = new { by = "testId", value = "file-drop" }, artifactId = id.ToString("D") })
                    : JsonSerializer.SerializeToElement(new { target = new { by = "label", value = "Product image" }, artifactIds = new[] { id.ToString("D") } });
                call = new("child-native-transfer", ToolCatalog.RequestAuthorization, JsonSerializer.Serialize(new { operation = "run_browser_command",
                    command = new { tool = Transfer == "drop" ? ToolCatalog.BrowserDrop : ToolCatalog.BrowserUpload, arguments } }));
            }
            else if (!refused && Capture && !request.Messages.Any(m => m.Role == ModelRole.Tool && m.ToolCallId == "child-native-capture"))
                call = new("child-native-capture", ToolCatalog.RequestAuthorization,
                    JsonSerializer.Serialize(new { operation = "run_browser_command", command = new { tool = ToolCatalog.BrowserScreenshot, arguments = new { } } }));
            if (call is null && VerifyTransfer is { } verify) { VerifyTransfer = null; await verify(); }
            call ??= new("child-native-finish", ToolCatalog.WorkComplete, JsonSerializer.Serialize(new { outcome = refused ? "NeedsAttention" : "Response",
                summary = refused ? "Native background Browser permission was refused." : "Native background Browser work finished.", attentionRequired = refused }));
            yield return new ModelToolCallEvent(call); yield return new ModelCompleted(ModelStopReason.ToolCalls);
        }
    }

    [TenantBrowserChromiumTheory] [InlineData(false, "approve")] [InlineData(true, "approve")]
    [InlineData(false, "reject")] [InlineData(true, "reject")] [InlineData(false, "cancel")] [InlineData(true, "cancel")]
    [InlineData(false, "read-review")] [InlineData(true, "read-review")] [InlineData(false, "capture")] [InlineData(true, "capture")]
    [InlineData(false, "revoked")] [InlineData(true, "revoked")] [InlineData(false, "changed")] [InlineData(true, "changed")]
    [InlineData(false, "capability")] [InlineData(true, "capability")]
    [InlineData(false, "upload")] [InlineData(true, "upload")] [InlineData(false, "drop")] [InlineData(true, "drop")]
    public Task Tenant_background_native_Browser_uses_exact_child_consent_observation_and_terminal_report(bool sqlite, string mode)
        => BackgroundNativeBrowserFlow(sqlite, mode);

    [TenantBrowserChromiumTheory] [InlineData(true)]
    public Task Tenant_background_native_Browser_pending_consent_survives_actual_SQLite_host_recreation(bool sqlite)
        => BackgroundNativeBrowserFlow(sqlite, "approve", restart: true);

    private async Task BackgroundNativeBrowserFlow(bool sqlite, string mode, bool restart = false)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-background-native-browser-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        WebApplicationFactory<Program>? reopened = null; HttpClient? reopenedOwner = null;
        try
        {
            await using var page = new LoopbackBrowserFixtureHost(NullLogger.Instance); await page.StartAsync(0, CancellationToken.None); Assert.True(page.IsAvailable);
            var model = new BackgroundNativeBrowserModel { ReadReview = mode == "read-review", Capture = mode == "capture", Transfer = mode is "upload" or "drop" ? mode : null };
            void Configure(IServiceCollection services)
            {
                var options = (BrowserOptions)services.Last(d => d.ServiceType == typeof(BrowserOptions)).ImplementationInstance!;
                options.Enabled = true; options.Headless = true; options.Channel = null; options.FixtureEnabled = false;
                options.ProfileRoot = Path.Combine(root, "browser"); options.PolicyMode = "Restricted"; options.ProfileMode = "EphemeralSession";
                options.NavigationOrigins = [page.Origin!]; options.TargetOrigins = null; options.InteractionOrigins = null; options.ResourceOrigins = [];
            }
            await using WebApplicationFactory<Program> initial = sqlite ? new NativeTenantSqliteFactory(root, model, Configure) : new NativeTenantMemoryFactory(root, model, Configure);
            var factory = initial; using var firstOwner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") }); var owner = firstOwner;
            var setup = await StartTenantBackground(factory, owner, submit: false, fileCapabilities: [ToolCatalog.BackgroundStart, ToolCatalog.BrowserNavigate, ToolCatalog.BrowserSnapshot, ToolCatalog.BrowserScreenshot, ToolCatalog.BrowserUpload, ToolCatalog.BrowserDrop], reportReads: true);
            var tenant = setup.Tenant; var api = $"/api/v2/tenants/{tenant}";
            var runs = factory.Services.GetRequiredService<ITenantRunStore>(); var sessions = factory.Services.GetRequiredService<ITenantSessionStore>();
            var parent = (await sessions.FindTenantSessionAsync(tenant, setup.Session))!;
            var access = factory.Services.GetRequiredService<TenantAccessService>(); var human = (await Json(await owner.GetAsync(api + "/access/me"))).GetProperty("principalId").GetGuid();
            var role = await access.SaveRoleAsync(human, tenant, null, "Native child Browser", ["browser.read", "browser.use", "artifacts.create", "artifacts.read"], await Revision(owner, tenant));
            var browserAssignment = await access.AssignRoleAsync(human, tenant, parent.Owner.ExecutorPrincipalId, role.RoleId, AccessScope.ForTenant(tenant), await Revision(owner, tenant));
            if (model.ReadReview) Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(api + "/operation-policies/browser.read", new { mode = "always", independentReview = false, expectedRevision = await Revision(owner, tenant), expectedPolicyRevision = 0 })).StatusCode);
            var native = Assert.IsType<NativePlaywrightBrowser>(factory.Services.GetRequiredService<IBrowser>()); model.Url = page.Origin + (model.Transfer is null ? "/browser-native.html" : "/upload");
            if (model.Transfer is not null)
            {
                var bytes = Enumerable.Range(0, 4096).Select(i => (byte)(i % 256)).ToArray(); Guid transferSession = default;
                model.SeedTransfer = async () => {
                    var child = Assert.Single((await sessions.ListTenantSessionsAsync(tenant, null, 0, 100)).Items, s => s.Owner.SessionId != setup.Session);
                    var active = (await CurrentApprovalExecution(runs, tenant, child.Snapshot.Origin.InitialBackgroundAgentRunId!.Value))!;
                    transferSession = active.SessionId;
                    var file = await factory.Services.GetRequiredService<ITenantArtifactStore>().CreateTenantAsync(new(active.Owner, active.SessionId, active.AgentRunId, active.Claim!.Generation),
                        Guid.NewGuid(), "child-original.bin", "application/octet-stream", bytes);
                    if (model.Transfer == "drop") await native.ContextFor(active.SessionId)!.Pages.Single().SetContentAsync("""
                        <div data-testid="file-drop">Drop child file</div><script>window.dropCount=0;document.querySelector('[data-testid=file-drop]').addEventListener('drop', e=>{e.preventDefault();window.dropCount++;window.droppedFile=e.dataTransfer.files[0];});</script>
                        """);
                    return file.ArtifactId;
                };
                model.VerifyTransfer = async () => {
                    var childPage = native.ContextFor(transferSession)!.Pages.Single();
                    var actual = await childPage.EvaluateAsync<byte[]>(model.Transfer == "drop"
                        ? "async () => Array.from(new Uint8Array(await window.droppedFile.arrayBuffer()))"
                        : "async () => Array.from(new Uint8Array(await document.querySelector('input[type=file]').files[0].arrayBuffer()))");
                    Assert.Equal(bytes, actual);
                    if (model.Transfer == "drop") Assert.Equal(1, await childPage.EvaluateAsync<int>("() => window.dropCount"));
                };
            }
            var submitted = await Json(await owner.PostAsJsonAsync(api + $"/sessions/{setup.Session}/messages", new { messageId = Guid.NewGuid(), text = "Perform the native Browser task in owned background work", expectedRevision = await Revision(owner, tenant), expectedSessionRevision = 1 }));
            var sourceId = submitted.GetProperty("runId").GetGuid(); Guid childId = default; AgentRun? leaf = null;
            async Task<AgentRun> AwaitChild(Func<AgentRun, bool> done)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
                while (true)
                {
                    if (childId == Guid.Empty)
                    {
                        var childSession = (await sessions.ListTenantSessionsAsync(tenant, null, 0, 100)).Items.SingleOrDefault(s => s.Snapshot.Origin.OriginatingAgentRunId == sourceId);
                        childId = childSession?.Snapshot.Origin.InitialBackgroundAgentRunId ?? Guid.Empty;
                    }
                    if (childId != Guid.Empty)
                    {
                        leaf = await CurrentApprovalExecution(runs, tenant, childId);
                        if (leaf is not null && (done(leaf) || leaf.IsTerminal)) return leaf;
                    }
                    await Task.Delay(25, timeout.Token);
                }
            }
            var waiting = await AwaitChild(r => r.Status == AgentRunStatus.WaitingForApproval);
            Assert.True(waiting.Status == AgentRunStatus.WaitingForApproval, $"{waiting.Status}: {waiting.Failure?.Code} {waiting.Failure?.Summary}; effects={waiting.SideEffect.Disposition}; latest tools={string.Join(",", model.Requests.LastOrDefault()?.Tools?.Select(t => t.Name) ?? [])}");
            var scope = new TenantBrowserOwner(tenant, parent.Owner.AgentInstanceId, parent.Owner.ExecutorPrincipalId, waiting.SessionId);
            Assert.Equal(0, (await native.ObserveTenantSessionAsync(scope)).LiveContexts);
            var approvals = factory.Services.GetRequiredService<ITenantApprovalStore>(); var first = (await approvals.FindApprovalAsync(tenant, waiting.ApprovalRequestId!.Value))!;
            Assert.NotEqual(setup.Session, first.SessionId); Assert.Equal(waiting.SessionId, first.SessionId); Assert.Equal(ApprovalKind.Review, first.Kind);
            if (restart)
            {
                await initial.DisposeAsync(); reopened = new NativeTenantSqliteFactory(root, model, Configure); factory = reopened;
                reopenedOwner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") }); owner = reopenedOwner;
                await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
                runs = factory.Services.GetRequiredService<ITenantRunStore>(); sessions = factory.Services.GetRequiredService<ITenantSessionStore>(); approvals = factory.Services.GetRequiredService<ITenantApprovalStore>();
                native = Assert.IsType<NativePlaywrightBrowser>(factory.Services.GetRequiredService<IBrowser>());
                Assert.Equal(first.Operation, (await approvals.FindApprovalAsync(tenant, first.RequestId))!.Operation);
            }
            var lostCapability = mode == "capability";
            if (lostCapability)
            {
                var agent = (await factory.Services.GetRequiredService<ITenantAgentStore>().FindTenantAgentAsync(tenant, parent.Owner.AgentInstanceId))!;
                var definitions = factory.Services.GetRequiredService<TenantDefinitionService>();
                var draft = await definitions.ForkAsync(human, tenant, Guid.Parse(agent.Instance.DefinitionId), 1, await Revision(owner, tenant));
                var environment = draft.Candidate.ExecutionPolicy!;
                draft = await definitions.UpdateAsync(human, tenant, draft.DraftId, draft.Candidate with { ExecutionPolicy = environment with {
                    Capabilities = environment.Capabilities! with { ResolvedCapabilities = environment.Capabilities.ResolvedCapabilities.Where(t => t != ToolCatalog.BackgroundStart).ToArray() },
                    Projection = new(environment.Projection!.AlwaysCapabilities.Where(t => t != ToolCatalog.BackgroundStart).ToArray()) } }, await Revision(owner, tenant), draft.Revision);
                var publication = await definitions.PublishAsync(human, tenant, draft.DraftId, await Revision(owner, tenant), draft.Revision);
                Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(api + $"/agents/{agent.Instance.InstanceId}/active-version", new {
                    version = publication.Version, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1 })).StatusCode);
                var denied = await Assert.ThrowsAsync<AccessDeniedException>(() => factory.Services.GetRequiredService<TenantBrowserOperationTarget>()
                    .PrepareAsync(waiting, ToolCatalog.BrowserNavigate, JsonSerializer.Serialize(new { url = model.Url }), default, registerCold: true).AsTask());
                Assert.Equal("browser_policy_changed", denied.ReasonCode);
                mode = "cancel";
            }
            if (mode is "revoked" or "changed")
            {
                if (mode == "revoked") await access.RevokeAssignmentAsync(human, tenant, browserAssignment.AssignmentId, await Revision(owner, tenant));
                else Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(api + $"/agents/{parent.Owner.AgentInstanceId}/persona", new {
                    expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1,
                    persona = new { name = "Changed native task", role = "Assistant", description = "Changed exact witness", tone = "Clear" }, expectedPersonaRevision = 1 })).StatusCode);
                var stale = await Json(await owner.GetAsync(api + $"/approvals/{first.RequestId}"));
                Assert.False(stale.GetProperty("review").GetProperty("canDecide").GetBoolean()); Assert.True(stale.GetProperty("review").GetProperty("canCancel").GetBoolean());
                mode = "cancel";
            }
            var decided = new HashSet<Guid>();
            while (waiting.Status == AgentRunStatus.WaitingForApproval)
            {
                var request = (await approvals.FindApprovalAsync(tenant, waiting.ApprovalRequestId!.Value))!;
                Assert.True(decided.Add(request.RequestId));
                var detail = await Json(await owner.GetAsync(api + $"/approvals/{request.RequestId}")); Assert.True(detail.GetProperty("review").GetProperty(mode == "cancel" ? "canCancel" : "canDecide").GetBoolean(), detail.GetRawText());
                var decision = decided.Count == 1 && mode is "reject" or "cancel" ? mode : "approve";
                Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync(api + $"/approvals/{request.RequestId}/" + (decision == "cancel" ? "cancel" : "decision"),
                    new { decision, actionHash = request.Operation.ActionHash, expectedRevision = await Revision(owner, tenant), expectedRequestRevision = request.Revision })).StatusCode);
                var old = request.RequestId;
                waiting = await AwaitChild(r => r.Status == AgentRunStatus.WaitingForApproval && r.ApprovalRequestId != old);
            }
            if (lostCapability)
            {
                Assert.True(waiting.IsTerminal); Assert.NotEqual(AgentRunStatus.Running, waiting.Status);
                Assert.Equal(0, (await native.ObserveTenantSessionAsync(scope)).LiveContexts);
                Assert.Null(await factory.Services.GetRequiredService<ITenantApprovedOperationStore>().FindOperationReceiptAsync(tenant, first.Operation.OperationId));
                using var refusedDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(25)); CompletionInboxItem? refused;
                do { refused = await runs.FindTenantCompletionAsync(waiting.Owner, waiting.AgentRunId); if (refused?.Status != CompletionInboxStatus.Skipped) await Task.Delay(25, refusedDeadline.Token); } while (refused?.Status != CompletionInboxStatus.Skipped);
                Assert.Null(refused.ReportActivationId); return;
            }
            Assert.Equal(AgentRunStatus.Completed, waiting.Status); Assert.NotEqual(childId, waiting.AgentRunId);
            Assert.Equal(waiting.AgentRunId, (await runs.FindTenantCompletionAsync(waiting.Owner, waiting.AgentRunId))!.ChildAgentRunId);
            Assert.True(AgentRunToolCallCheckpoint.TryRead(waiting.Checkpoint, out var messages));
            var navigation = Assert.Single(messages!, m => m.ToolCallId == "child-native-navigate");
            using var result = JsonDocument.Parse(navigation.Text); Assert.Equal(mode is not ("reject" or "cancel"), result.RootElement.GetProperty("commandCompleted").GetBoolean());
            Assert.False(result.RootElement.GetProperty("replayAllowed").GetBoolean());
            if (mode is not ("reject" or "cancel"))
            {
                var observed = Assert.Single(messages!, m => m.ToolCallId == "child-native-observe"); Assert.Contains("untrustedBrowserContent", observed.Text);
                Assert.Contains(model.Requests, r => r.Messages.Any(m => m.ToolCallId == observed.ToolCallId && m.Text == observed.Text));
            }
            Assert.Equal(mode is "reject" or "cancel" ? 1 : model.ReadReview || model.Capture || model.Transfer is not null ? 2 : 1, decided.Count);
            Assert.Equal(0, (await native.ObserveTenantSessionAsync(new(tenant, parent.Owner.AgentInstanceId, parent.Owner.ExecutorPrincipalId, setup.Session))).LiveContexts);
            using var reportTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(35)); CompletionInboxItem? item;
            do { item = await runs.FindTenantCompletionAsync(waiting.Owner, waiting.AgentRunId); if (item?.Status != CompletionInboxStatus.Delivered) await Task.Delay(25, reportTimeout.Token); } while (item?.Status != CompletionInboxStatus.Delivered);
            var history = await Json(await owner.GetAsync(api + $"/sessions/{setup.Session}/messages?limit=100")); Assert.Contains("Background work: Native background Browser", history.ToString());
            var files = await factory.Services.GetRequiredService<ITenantArtifactStore>().ListTenantAsync(tenant, waiting.SessionId, null, null, 100);
            Assert.Equal(model.Capture || model.Transfer is not null ? 1 : 0, files.Items.Count);
            Assert.Equal(0, factory.Services.GetRequiredService<AgentCore.Api.Http.OutboundHttpProbe>().Attempts);
        }
        finally { reopenedOwner?.Dispose(); if (reopened is not null) await reopened.DisposeAsync(); SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
