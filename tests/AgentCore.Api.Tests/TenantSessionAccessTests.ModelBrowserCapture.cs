using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection.Extensions;
using AgentCore.Api.Security;
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

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    [TenantBrowserChromiumTheory] [InlineData(false, false, false)] [InlineData(true, false, false)] [InlineData(false, true, false)] [InlineData(true, true, false)]
    [InlineData(false, false, true)] [InlineData(true, false, true)]
    public async Task Tenant_Browser_model_capture_HTTP_publishes_owned_image_and_rechecks_read_before_each_generation(bool sqlite, bool revoke, bool denyPublication)
    {
        var proof = !sqlite && !revoke && Environment.GetEnvironmentVariable("AGENTCORE_BROWSER_FILES_UI_PROOF") == (denyPublication ? "unavailable" : "published");
        var proofPath = "/private/tmp/agent-core-browser-files-ui-" + (denyPublication ? "unavailable" : "published");
        if (proof) foreach (var suffix in new[] { ".json", "-decision", "-completed", "-release" }) File.Delete(proofPath + suffix);
        var race = new BrowserPublicationRace();
        var root = Path.Combine(Path.GetTempPath(), "tenant-model-capture-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var model = new ApprovalRecordingModel();
            await using WebApplicationFactory<Program> baseFactory = sqlite ? new TenantApprovalSqliteFactory(root, model) : new ApprovalExecutionFactory();
            if (baseFactory is ApprovalExecutionFactory memory) model = memory.Model;
            await using var factory = baseFactory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => {
                if (proof) NativeTenantIdentity(services, root);
                if (proof) ((IdentityHttpOptions)services.Single(d => d.ServiceType == typeof(IdentityHttpOptions)).ImplementationInstance!).AllowedOrigins = ["http://127.0.0.1:5234"];
                var options = (BrowserOptions)services.Single(d => d.ServiceType == typeof(BrowserOptions)).ImplementationInstance!;
                options.Enabled = true; options.Headless = true; options.Channel = null; options.FixtureEnabled = true; options.FixturePort = 0;
                options.ProfileRoot = Path.Combine(root, "browser"); options.PolicyMode = "Restricted"; options.ProfileMode = "EphemeralSession";
                options.NavigationOrigins = ["http://127.0.0.1:5091"]; options.TargetOrigins = null; options.InteractionOrigins = null; options.ResourceOrigins = [];
                if (denyPublication) services.Replace(ServiceDescriptor.Singleton<TenantBrowserEffectService>(p => new(p.GetRequiredService<IIamStore>(), p.GetRequiredService<ITenantRunStore>(),
                    p.GetRequiredService<ITenantApprovalStore>(), p.GetRequiredService<ITenantApprovedOperationStore>(), p.GetRequiredService<TenantBrowserOperationTarget>(),
                    p.GetRequiredService<IamAuthorizer>(), new PublicationRaceBrowser(p.GetRequiredService<IBrowser>(), race), p.GetRequiredService<TenantOperationAuthorizer>(),
                    p.GetRequiredService<IIdGenerator>(), p.GetRequiredService<TimeProvider>())));
            }));
            if (proof) factory.UseKestrel(0);
            using var owner = proof ? factory.CreateClient() : factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
            await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
            var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid(); var api = $"/api/v2/tenants/{tenant}";
            var human = (await Json(await owner.GetAsync(api + "/access/me"))).GetProperty("principalId").GetGuid();
            var agent = await Json(await owner.PostAsJsonAsync(api + "/agents", new { templateId = "general-assistant", version = 36, expectedRevision = await Revision(owner, tenant) }));
            var instance = agent.GetProperty("instance").GetProperty("instanceId").GetGuid(); var executor = agent.GetProperty("principalId").GetGuid();
            var session = await Json(await owner.PostAsJsonAsync(api + "/sessions", new { retentionKind = "saved", agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1, offeringId = "dca30000-0000-4000-8000-000000000003" }));
            var sessionId = session.GetProperty("sessionId").GetGuid(); var access = factory.Services.GetRequiredService<TenantAccessService>();
            var role = await access.SaveRoleAsync(human, tenant, null, "Capture browser", ["browser.read", "browser.use"], await Revision(owner, tenant));
            await access.AssignRoleAsync(human, tenant, executor, role.RoleId, AccessScope.ForResource(tenant, "agent", instance.ToString("D")), await Revision(owner, tenant));
            role = await access.SaveRoleAsync(human, tenant, null, "Publish in Session", ["sessions.read", "artifacts.create"], await Revision(owner, tenant));
            var creation = await access.AssignRoleAsync(human, tenant, executor, role.RoleId, TenantSessionRules.Scope(tenant, sessionId), await Revision(owner, tenant));
            if (denyPublication) race.AfterCapture = async () => { await access.RevokeAssignmentAsync(human, tenant, creation.AssignmentId, await Revision(owner, tenant)); };
            role = await access.SaveRoleAsync(human, tenant, null, "Independent captured image read", ["artifacts.read"], await Revision(owner, tenant));
            var read = await access.AssignRoleAsync(human, tenant, executor, role.RoleId, AccessScope.ForTenant(tenant), await Revision(owner, tenant));
            var native = Assert.IsType<NativePlaywrightBrowser>(factory.Services.GetRequiredService<IBrowser>());
            var scope = new TenantBrowserOwner(tenant, instance, executor, sessionId); Assert.Null(await native.BindTenantIsolationAsync(scope, BrowserProfileMode.EphemeralSession));
            var navigation = new BrowserNavigate(native.Fixture.Origin + "/browser-native.html"); var target = await native.PrepareTenantCommandAsync(scope, navigation);
            Assert.Null((await native.ExecuteAsync(new(sessionId, navigation) { TenantOwner = scope, TenantWitness = target.Witness })).ErrorCode);
            model.ProposedBrowserCommand = """{"operation":"run_browser_command","command":{"tool":"browser.screenshot","arguments":{}}}""";
            var revoked = 0;
            if (revoke) model.BeforeGenerate = async (request, _) => {
                if (!request.Messages.Any(m => m.Parts?.OfType<ModelImageContent>().Any() == true) || Interlocked.Exchange(ref revoked, 1) != 0) return;
                await access.RevokeAssignmentAsync(human, tenant, read.AssignmentId, await Revision(owner, tenant));
                model.ProposedBrowserRead = new("after-capture-read", ToolCatalog.BrowserSnapshot, "{}");
            };
            var submitted = await Json(await owner.PostAsJsonAsync(api + $"/sessions/{sessionId}/messages", new { messageId = Guid.NewGuid(), text = "Capture the approved local Browser image", expectedRevision = await Revision(owner, tenant), expectedSessionRevision = 1 }));
            var runId = submitted.GetProperty("runId").GetGuid(); if (baseFactory is ApprovalExecutionFactory enabled) Volatile.Write(ref enabled.Enabled, true);
            var runs = factory.Services.GetRequiredService<ITenantRunStore>(); AgentRun? waiting = null;
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
                while (!timeout.IsCancellationRequested)
                { waiting = await runs.FindTenantRunAsync(tenant, runId); if (waiting?.Status == AgentRunStatus.WaitingForApproval || waiting?.IsTerminal == true) break; await Task.Delay(25, timeout.Token); }
            Assert.NotNull(waiting); Assert.True(waiting.Status == AgentRunStatus.WaitingForApproval, $"{waiting.Status}: {waiting.Failure?.Code}: {waiting.Failure?.Summary}");
            var request = (await factory.Services.GetRequiredService<ITenantApprovalStore>().FindApprovalAsync(tenant, waiting.ApprovalRequestId!.Value))!;
            if (proof)
            {
                await File.WriteAllTextAsync(proofPath + ".json", JsonSerializer.Serialize(new { baseUrl = owner.BaseAddress!.ToString(), tenant, sessionId, requestId = request.RequestId, root }));
                await WaitForBrowserFilesUi(proofPath + "-decision");
            }
            else
                Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync(api + $"/approvals/{request.RequestId}/decision", new { decision = "approve", actionHash = request.Operation.ActionHash, expectedRevision = await Revision(owner, tenant), expectedRequestRevision = request.Revision })).StatusCode);
            AgentRun? completed = null;
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35)))
                while (!timeout.IsCancellationRequested)
                { completed = await CurrentApprovalExecution(runs, tenant, runId); if (completed?.IsTerminal == true) break; await Task.Delay(25, timeout.Token); }
            Assert.NotNull(completed); Assert.True(completed.Status == AgentRunStatus.Completed, $"{completed.Status}: {completed.Failure?.Code}: {completed.Failure?.Summary}");
            var receipt = (await factory.Services.GetRequiredService<ITenantApprovedOperationStore>().FindOperationReceiptAsync(tenant, request.Operation.OperationId))!;
            var publication = TenantBrowserArtifactRules.Read(receipt.BrowserArtifactsJson!);
            Assert.Equal(TenantOperationOutcome.Completed, receipt.Outcome);
            if (denyPublication)
            {
                Assert.Equal("unavailable", publication.Status); Assert.Equal("artifact_publication_unavailable", publication.ReasonCode); Assert.Empty(publication.Files);
                Assert.DoesNotContain(model.Requests, r => r.Messages.Any(m => m.Parts?.OfType<ModelImageContent>().Any() == true));
            }
            else
            {
                Assert.Contains(model.Requests, r => r.Messages.Any(m => m.Parts?.OfType<ModelImageContent>().Any() == true));
                if (revoke) { Assert.Equal(1, revoked); Assert.DoesNotContain(model.Requests.Last().Messages, m => m.Parts?.OfType<ModelImageContent>().Any() == true); }
            }
            if (proof) { await File.WriteAllTextAsync(proofPath + "-completed", "completed"); await WaitForBrowserFilesUi(proofPath + "-release"); }
            Assert.DoesNotContain("base64", completed.Checkpoint!.PayloadJson, StringComparison.OrdinalIgnoreCase);
            var details = await Json(await owner.GetAsync(api + $"/approvals/{request.RequestId}"));
            Assert.Equal(denyPublication ? "unavailable" : "published", details.GetProperty("operationResult").GetProperty("browserArtifacts").GetProperty("status").GetString());
            if (denyPublication) { Assert.Empty((await factory.Services.GetRequiredService<ITenantArtifactStore>().ListTenantAsync(tenant, sessionId, null, null, 100)).Items); return; }
            var file = Assert.Single(publication.Files);
            using var download = await owner.GetAsync(api + $"/sessions/{sessionId}/artifacts/{file.ArtifactId}/content"); Assert.Equal(HttpStatusCode.OK, download.StatusCode);
            Assert.True((await download.Content.ReadAsByteArrayAsync()).AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }));
            Assert.Single((await factory.Services.GetRequiredService<ITenantArtifactStore>().ListTenantAsync(tenant, sessionId, null, null, 100)).Items);
        }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private static async Task WaitForBrowserFilesUi(string path)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(18));
        while (!File.Exists(path)) await Task.Delay(100, deadline.Token);
    }
    private sealed class BrowserPublicationRace { public Func<Task>? AfterCapture; }
    private sealed class PublicationRaceBrowser(IBrowser native, BrowserPublicationRace race) : IBrowser
    {
        public BrowserProviderDescriptor Provider => native.Provider;
        public BrowserHostPolicy HostPolicy => native.HostPolicy;
        public bool IsAvailable => native.IsAvailable;
        public async ValueTask<BrowserResult> ExecuteAsync(BrowserRequest request, CancellationToken ct = default)
        {
            var result = await native.ExecuteAsync(request, ct);
            if (request.Command is BrowserScreenshot && Interlocked.Exchange(ref race.AfterCapture, null) is { } after) await after();
            return result;
        }
    }
}
