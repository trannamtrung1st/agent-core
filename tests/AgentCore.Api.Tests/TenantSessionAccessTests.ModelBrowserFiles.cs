using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
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
    [TenantBrowserChromiumTheory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task Tenant_Browser_model_HTTP_upload_and_Artifact_drop_deliver_original_binary_once_under_exact_source_consent(bool sqlite, bool drop)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-model-files-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var model = new ApprovalRecordingModel();
            await using WebApplicationFactory<Program> baseFactory = sqlite ? new TenantApprovalSqliteFactory(root, model) : new ApprovalExecutionFactory();
            if (baseFactory is ApprovalExecutionFactory memory) model = memory.Model;
            await using var factory = baseFactory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => {
                var options = (BrowserOptions)services.Single(d => d.ServiceType == typeof(BrowserOptions)).ImplementationInstance!;
                options.Enabled = true; options.Headless = true; options.Channel = null; options.FixtureEnabled = true; options.FixturePort = 0;
                options.ProfileRoot = Path.Combine(root, "browser"); options.PolicyMode = "Restricted"; options.ProfileMode = "EphemeralSession";
                options.NavigationOrigins = ["http://127.0.0.1:5091"]; options.TargetOrigins = null; options.InteractionOrigins = null; options.ResourceOrigins = [];
            }));
            using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
            await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
            var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid(); var api = $"/api/v2/tenants/{tenant}";
            var human = (await Json(await owner.GetAsync(api + "/access/me"))).GetProperty("principalId").GetGuid();
            var agent = await Json(await owner.PostAsJsonAsync(api + "/agents", new { templateId = "general-assistant", version = 36, expectedRevision = await Revision(owner, tenant) }));
            var instance = agent.GetProperty("instance").GetProperty("instanceId").GetGuid(); var executor = agent.GetProperty("principalId").GetGuid();
            var session = await Json(await owner.PostAsJsonAsync(api + "/sessions", new { retentionKind = "saved", agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1, offeringId = "dca30000-0000-4000-8000-000000000003" }));
            var sessionId = session.GetProperty("sessionId").GetGuid(); var access = factory.Services.GetRequiredService<TenantAccessService>();
            var role = await access.SaveRoleAsync(human, tenant, null, "Transfer browser", ["browser.read", "browser.use"], await Revision(owner, tenant));
            await access.AssignRoleAsync(human, tenant, executor, role.RoleId, AccessScope.ForResource(tenant, "agent", instance.ToString("D")), await Revision(owner, tenant));
            role = await access.SaveRoleAsync(human, tenant, null, "Independent file source", ["sessions.read", "artifacts.create", "artifacts.read"], await Revision(owner, tenant));
            await access.AssignRoleAsync(human, tenant, executor, role.RoleId, AccessScope.ForTenant(tenant), await Revision(owner, tenant));
            var native = Assert.IsType<NativePlaywrightBrowser>(factory.Services.GetRequiredService<IBrowser>());
            var scope = new TenantBrowserOwner(tenant, instance, executor, sessionId); Assert.Null(await native.BindTenantIsolationAsync(scope, BrowserProfileMode.EphemeralSession));
            var navigation = new BrowserNavigate(native.Fixture.Origin + "/upload"); var target = await native.PrepareTenantCommandAsync(scope, navigation);
            Assert.Null((await native.ExecuteAsync(new(sessionId, navigation) { TenantOwner = scope, TenantWitness = target.Witness })).ErrorCode);
            var page = native.ContextFor(sessionId)!.Pages.Single();
            if (drop) await page.SetContentAsync("""
                <div data-testid="file-drop">Drop exact file</div><output id="chosen"></output>
                <script>window.dropCount=0;document.querySelector('[data-testid=file-drop]').addEventListener('drop', e=>{
                    e.preventDefault();window.dropCount++;window.droppedFile=e.dataTransfer.files[0];document.querySelector('#chosen').textContent=window.droppedFile.name;
                });</script>
                """);
            var bytes = Enumerable.Range(0, 65536).Select(n => (byte)(n % 256)).ToArray();
            var runs = factory.Services.GetRequiredService<ITenantRunStore>(); var sourceReady = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
            ArtifactRecord? file = null; var seeded = 0;
            model.BeforeGenerate = async (_, ct) => {
                if (Interlocked.Exchange(ref seeded, 1) != 0) return;
                var id = await sourceReady.Task.WaitAsync(ct); var run = (await runs.FindTenantRunAsync(tenant, id, ct))!;
                // Test-owned source creation uses the real executing claim and canonical Artifact store; no product debug endpoint.
                file = await factory.Services.GetRequiredService<ITenantArtifactStore>().CreateTenantAsync(new(run.Owner, sessionId, run.AgentRunId, run.Claim!.Generation),
                    Guid.NewGuid(), "original-binary.bin", "application/octet-stream", bytes, ct);
                var arguments = drop ? JsonSerializer.SerializeToElement(new { target = new { by = "testId", value = "file-drop" }, artifactId = file.ArtifactId.ToString("D") })
                    : JsonSerializer.SerializeToElement(new { target = new { by = "label", value = "Product image" }, artifactIds = new[] { file.ArtifactId.ToString("D") } });
                model.ProposedBrowserCommand = JsonSerializer.Serialize(new { operation = "run_browser_command", command = new { tool = drop ? "browser.drop" : "browser.upload", arguments } });
            };
            var submitted = await Json(await owner.PostAsJsonAsync(api + $"/sessions/{sessionId}/messages", new { messageId = Guid.NewGuid(), text = "Transfer the exact saved file once", expectedRevision = await Revision(owner, tenant), expectedSessionRevision = 1 }));
            var runId = submitted.GetProperty("runId").GetGuid(); sourceReady.SetResult(runId); if (baseFactory is ApprovalExecutionFactory enabled) Volatile.Write(ref enabled.Enabled, true);
            var waiting = await AwaitBrowserRestartRun(runs, tenant, runId, true); Assert.Equal(AgentRunStatus.WaitingForApproval, waiting.Status); Assert.NotNull(file);
            var request = (await factory.Services.GetRequiredService<ITenantApprovalStore>().FindApprovalAsync(tenant, waiting.ApprovalRequestId!.Value))!;
            Assert.Equal(file.ArtifactId, Assert.Single(TenantBrowserUploadRules.Sources(request.Operation.BrowserCommand!)).ArtifactId);
            var view = await Json(await owner.GetAsync(api + $"/approvals/{request.RequestId}"));
            Assert.Contains(file.Sha256Hex, view.GetRawText()); Assert.DoesNotContain(Convert.ToBase64String(bytes), view.GetRawText());
            Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync(api + $"/approvals/{request.RequestId}/decision", new { decision = "approve", actionHash = request.Operation.ActionHash, expectedRevision = await Revision(owner, tenant), expectedRequestRevision = request.Revision })).StatusCode);
            var completed = await AwaitBrowserRestartRun(runs, tenant, runId, false);
            Assert.True(completed.Status == AgentRunStatus.Completed, $"{completed.Status}: {completed.Failure?.Code}: {completed.Failure?.Summary}");
            var receipt = (await factory.Services.GetRequiredService<ITenantApprovedOperationStore>().FindOperationReceiptAsync(tenant, request.Operation.OperationId))!;
            Assert.Equal(AgentCore.Domain.Security.TenantOperationOutcome.Completed, receipt.Outcome);
            var actual = await page.EvaluateAsync<int[]>(drop ? "async()=>Array.from(new Uint8Array(await window.droppedFile.arrayBuffer()))"
                : "async()=>Array.from(new Uint8Array(await document.querySelector('input[type=file]').files[0].arrayBuffer()))");
            Assert.Equal(bytes, actual.Select(n => (byte)n).ToArray()); Assert.Equal(file.DisplayName, await page.Locator("#chosen").InnerTextAsync());
            Assert.DoesNotContain(Convert.ToBase64String(bytes), completed.Checkpoint!.PayloadJson);
            if (drop) Assert.Equal(1, await page.EvaluateAsync<int>("()=>window.dropCount"));
            var repeated = await Json(await owner.GetAsync(api + $"/approvals/{request.RequestId}"));
            Assert.Equal("completed", repeated.GetProperty("operationResult").GetProperty("outcome").GetString());
            if (drop) Assert.Equal(1, await page.EvaluateAsync<int>("()=>window.dropCount"));
        }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
