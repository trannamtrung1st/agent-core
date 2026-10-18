using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentCore.Api.Security;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    // Hold consent expiry steady during interactive proof; worker timers still use real time.
    private sealed class ArtifactProofClock : TimeProvider
    {
        private readonly DateTimeOffset at = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => at;
    }

    [Theory] [InlineData(false, true, "approve")] [InlineData(true, true, "approve")]
    [InlineData(false, false, "approve")] [InlineData(true, false, "approve")]
    [InlineData(false, true, "reject")] [InlineData(true, true, "reject")]
    [InlineData(false, false, "cancel")] [InlineData(true, false, "cancel")]
    public async Task Artifact_model_HTTP_exact_review_or_grant_publishes_one_saved_file_through_the_actual_successor_Run(bool sqlite, bool review, string choice)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-artifact-review-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var proof = !sqlite && choice == "approve" && Environment.GetEnvironmentVariable("AGENTCORE_ARTIFACT_UI_PROOF") == "1";
        var proofPath = "/private/tmp/agent-core-artifact-review-ui-" + (review ? "review" : "grant");
        try
        {
            var content = "# Exact proposed file\n<script>alert('literal')</script>\nUTF-8: thử nghiệm" + (proof ? "\n" + string.Join("\n", Enumerable.Range(1, 35).Select(i => $"{i}: Exact saved content for the operations team — literal UTF-8 thử nghiệm.")) + "\n" + new string('x', 600) : "");
            var model = new ApprovalRecordingModel { ProposedArtifactAuthorization = JsonSerializer.Serialize(new { operation = "publish_artifact", artifact = new { displayName = "reviewed-report.md", contentType = "text/markdown", content } }) };
            var enabled = true;
            void Configure(IServiceCollection services)
            {
                if (proof) services.Replace(ServiceDescriptor.Singleton<TimeProvider>(new ArtifactProofClock()));
                if (proof) ((IdentityHttpOptions)services.Single(d => d.ServiceType == typeof(IdentityHttpOptions)).ImplementationInstance!).AllowedOrigins = ["http://127.0.0.1:5216"];
                services.Replace(ServiceDescriptor.Singleton<ITenantRunExecution>(p => new MetadataExecutionGate(() => Volatile.Read(ref enabled),
                new TenantRunExecution(p.GetRequiredService<ITenantRuntimeStore>(), p.GetRequiredService<ITenantRunStore>(), p.GetRequiredService<ITenantRunDispatcher>(), p.GetRequiredService<IIdGenerator>(), p.GetRequiredService<TimeProvider>()))));
            }
            await using WebApplicationFactory<Program> factory = sqlite ? new NativeTenantSqliteFactory(root, model, Configure) : new NativeTenantMemoryFactory(root, model, Configure);
            if (proof) factory.UseKestrel(0);
            using var owner = proof ? factory.CreateClient() : factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
            await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
            var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid(); var api = $"/api/v2/tenants/{tenant}";
            var human = (await Json(await owner.GetAsync(api + "/access/me"))).GetProperty("principalId").GetGuid();
            var agent = await Json(await owner.PostAsJsonAsync(api + "/agents", new { templateId = "customer-support", version = 3, expectedRevision = await Revision(owner, tenant) }));
            var instance = agent.GetProperty("instance").GetProperty("instanceId").GetGuid(); var executor = agent.GetProperty("principalId").GetGuid();
            var session = (await Json(await owner.PostAsJsonAsync(api + "/sessions", new { agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1 }))).GetProperty("sessionId").GetGuid();
            var access = factory.Services.GetRequiredService<TenantAccessService>();
            var role = await access.SaveRoleAsync(human, tenant, null, "Artifact creation in this Session", review ? ["sessions.read", "artifacts.create"] : ["sessions.read"], await Revision(owner, tenant));
            await access.AssignRoleAsync(human, tenant, executor, role.RoleId, TenantSessionRules.Scope(tenant, session), await Revision(owner, tenant));
            if (review) Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(api + "/operation-policies/artifacts.create", new { mode = "always", independentReview = false, expectedRevision = await Revision(owner, tenant), expectedPolicyRevision = 0 })).StatusCode);
            var accepted = await owner.PostAsJsonAsync(api + $"/sessions/{session}/messages", new { messageId = Guid.NewGuid(), text = "Publish this exact file after review", expectedRevision = await Revision(owner, tenant), expectedSessionRevision = 1 });
            Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode); var source = (await Json(accepted)).GetProperty("runId").GetGuid();
            var runs = factory.Services.GetRequiredService<ITenantRunStore>(); var waiting = await AwaitBrowserRestartRun(runs, tenant, source, true);
            Assert.True(waiting.Status == AgentRunStatus.WaitingForApproval, $"{waiting.Status}: {waiting.Failure?.Code}: {waiting.Failure?.Summary}");
            Volatile.Write(ref enabled, false);
            var request = (await factory.Services.GetRequiredService<ITenantApprovalStore>().FindApprovalAsync(tenant, waiting.ApprovalRequestId!.Value))!;
            Assert.Equal(review ? ApprovalKind.Review : ApprovalKind.ExceptionalGrant, request.Kind);
            var detail = await Json(await owner.GetAsync(api + $"/approvals/{request.RequestId}")); Assert.True(detail.GetProperty("review").GetProperty("canDecide").GetBoolean());
            Assert.Equal("publish_artifact", detail.GetProperty("preparedAction").GetProperty("operation").GetString());
            var pendingFeed = Assert.Single((await Json(await owner.GetAsync(api + $"/sessions/{session}/approvals?pageSize=1"))).GetProperty("items").EnumerateArray());
            Assert.Equal("publish_artifact", pendingFeed.GetProperty("operation").GetString()); Assert.True(pendingFeed.GetProperty("waiting").GetBoolean());
            Assert.False(pendingFeed.TryGetProperty("artifactContent", out _)); Assert.False(pendingFeed.TryGetProperty("preparedAction", out _));
            Assert.Equal(content, detail.GetProperty("review").GetProperty("changes").EnumerateArray().Single(c => c.GetProperty("field").GetString() == "artifactContent").GetProperty("after").GetString());
            var action = choice == "cancel" ? $"/approvals/{request.RequestId}/cancel" : $"/approvals/{request.RequestId}/decision";
            if (proof)
            {
                await File.WriteAllTextAsync(proofPath + ".json", JsonSerializer.Serialize(new { baseUrl = owner.BaseAddress!.ToString(), tenant, session, requestId = request.RequestId }));
                using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(30));
                while ((await factory.Services.GetRequiredService<ITenantApprovalStore>().FindApprovalAsync(tenant, request.RequestId, deadline.Token))!.Status == TenantApprovalStatus.Pending) await Task.Delay(100, deadline.Token);
            }
            else
            Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync(api + action, new { decision = choice, actionHash = request.Operation.ActionHash, expectedRevision = detail.GetProperty("securityRevision").GetInt64(), expectedRequestRevision = request.Revision })).StatusCode);
            Volatile.Write(ref enabled, true); var done = await AwaitBrowserRestartRun(runs, tenant, source, false);
            Assert.True(done.Status == AgentRunStatus.Completed, $"{done.Status}: {done.Failure?.Code}: {done.Failure?.Summary}"); Assert.NotEqual(source, done.AgentRunId);
            var terminalFeed = Assert.Single((await Json(await owner.GetAsync(api + $"/sessions/{session}/approvals?pageSize=1"))).GetProperty("items").EnumerateArray());
            Assert.Equal(choice == "approve" ? "approved" : choice == "reject" ? "rejected" : "cancelled", terminalFeed.GetProperty("status").GetString());
            Assert.Equal(choice == "approve" ? "completed" : null, terminalFeed.GetProperty("outcome").GetString());
            var effects = factory.Services.GetRequiredService<ITenantApprovedOperationStore>(); var receipt = await effects.FindOperationReceiptAsync(tenant, request.Operation.OperationId);
            var files = (await Json(await owner.GetAsync(api + $"/sessions/{session}/artifacts"))).GetProperty("items").EnumerateArray().ToArray();
            if (choice == "approve")
            {
                Assert.NotNull(receipt); Assert.Equal(done.AgentRunId, receipt.RunId); Assert.Equal(TenantOperationOutcome.Completed, receipt.Outcome);
                var id = Assert.Single(files).GetProperty("artifactId").GetGuid(); Assert.Equal(request.Operation.OperationId, id);
                Assert.Equal(content, await owner.GetStringAsync(api + $"/sessions/{session}/artifacts/{id}/content"));
            }
            else { Assert.Null(receipt); Assert.Empty(files); }
            var grant = await factory.Services.GetRequiredService<ITenantApprovalStore>().FindOperationGrantAsync(tenant, request.RequestId);
            if (choice == "approve" && !review) Assert.Equal(OperationGrantStatus.Consumed, grant!.Status); else Assert.Null(grant);
            Assert.Equal(review, (await factory.Services.GetRequiredService<IamAuthorizer>().CheckAsync(executor, "artifacts.create", TenantSessionRules.Scope(tenant, session))).Allowed);
            Assert.False((await factory.Services.GetRequiredService<IamAuthorizer>().CheckAsync(executor, "artifacts.read", AccessScope.ForTenant(tenant))).Allowed);
            Assert.Equal(0, factory.Services.GetRequiredService<AgentCore.Api.Http.OutboundHttpProbe>().Attempts);
            if (proof)
            {
                await File.WriteAllTextAsync(proofPath + "-completed.json", JsonSerializer.Serialize(new { completed = true, requestId = request.RequestId }));
                using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(30));
                while (!File.Exists(proofPath + "-release")) await Task.Delay(100, deadline.Token);
            }
        }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
