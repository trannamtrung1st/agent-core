using System.Net;
using System.Net.Http.Json;
using AgentCore.Api.Security;
using AgentCore.Application.Execution;
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
    [Theory] [InlineData(false, false, "approve")] [InlineData(true, false, "approve")]
    [InlineData(false, true, "approve")] [InlineData(true, true, "approve")]
    [InlineData(false, true, "reject")] [InlineData(true, true, "reject")]
    public Task Tenant_Artifact_verification_model_uses_independent_read_and_exact_durable_metadata_Review(bool sqlite, bool review, string decision) => ArtifactVerificationFlow(sqlite, review, decision);
    [Fact]
    public Task Tenant_Artifact_verification_pending_consent_survives_actual_SQLite_application_host_recreation() => ArtifactVerificationFlow(true, true, "approve", restart: true);
    private async Task ArtifactVerificationFlow(bool sqlite, bool review, string decision, bool restart = false)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-artifact-verification-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var proof = !sqlite && review && decision == "approve" && System.Environment.GetEnvironmentVariable("AGENTCORE_ARTIFACT_VERIFICATION_UI_PROOF") == "1";
        const string proofPath = "/private/tmp/agent-core-artifact-verification-ui";
        WebApplicationFactory<Program>? restartedFactory = null; HttpClient? restartedOwner = null;
        try
        {
            var model = new ApprovalRecordingModel { ProposedArtifact = System.Text.Json.JsonSerializer.Serialize(new {
                displayName = proof ? "Customer support quarterly operations handover metadata verification with a deliberately long exact file name for current scoped review.md" : "verified.md",
                content = "# Exact unpublished-to-reader body\nUTF-8: thử nghiệm" }), VerifyPublishedArtifact = true };
            void Configure(IServiceCollection services)
            { if (proof) ((IdentityHttpOptions)services.Single(d => d.ServiceType == typeof(IdentityHttpOptions)).ImplementationInstance!).AllowedOrigins = ["http://127.0.0.1:5218"]; }
            await using WebApplicationFactory<Program> initialFactory = sqlite ? new NativeTenantSqliteFactory(root, model, Configure) : new NativeTenantMemoryFactory(root, model, Configure);
            WebApplicationFactory<Program> factory = initialFactory;
            if (proof) factory.UseKestrel(0);
            using var initialOwner = proof ? factory.CreateClient() : factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
            HttpClient owner = initialOwner;
            await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
            var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid(); var api = $"/api/v2/tenants/{tenant}";
            var human = (await Json(await owner.GetAsync(api + "/access/me"))).GetProperty("principalId").GetGuid();
            var agent = await Json(await owner.PostAsJsonAsync(api + "/agents", new { templateId = "customer-support", version = 3, expectedRevision = await Revision(owner, tenant) }));
            var instance = agent.GetProperty("instance").GetProperty("instanceId").GetGuid(); var executor = agent.GetProperty("principalId").GetGuid();
            var session = (await Json(await owner.PostAsJsonAsync(api + "/sessions", new { agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1 }))).GetProperty("sessionId").GetGuid();
            var access = factory.Services.GetRequiredService<TenantAccessService>();
            var role = await access.SaveRoleAsync(human, tenant, null, "Publish current Session", ["sessions.read", "artifacts.create"], await Revision(owner, tenant));
            await access.AssignRoleAsync(human, tenant, executor, role.RoleId, TenantSessionRules.Scope(tenant, session), await Revision(owner, tenant));
            role = await access.SaveRoleAsync(human, tenant, null, "Independent Artifact metadata read", ["artifacts.read"], await Revision(owner, tenant));
            await access.AssignRoleAsync(human, tenant, executor, role.RoleId, AccessScope.ForTenant(tenant), await Revision(owner, tenant));
            if (review) Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(api + "/operation-policies/artifacts.read", new { mode = "always", independentReview = false, expectedRevision = await Revision(owner, tenant), expectedPolicyRevision = 0 })).StatusCode);
            var sent = await Json(await owner.PostAsJsonAsync(api + $"/sessions/{session}/messages", new { messageId = Guid.NewGuid(), text = "Publish this file, then verify its exact metadata under current policy", expectedRevision = await Revision(owner, tenant), expectedSessionRevision = 1 }));
            var sourceId = sent.GetProperty("runId").GetGuid(); var runs = factory.Services.GetRequiredService<ITenantRunStore>(); TenantApprovalRequest? saved = null;
            Guid? proofReadGrant = null; Guid? proofReviewer = null;
            if (review)
            {
                var waiting = await AwaitBrowserRestartRun(runs, tenant, sourceId, true);
                Assert.True(waiting.Status == AgentRunStatus.WaitingForApproval, $"{waiting.Status}: {waiting.Failure?.Code}: {waiting.Failure?.Summary}"); Assert.Null(waiting.Claim);
                if (restart)
                {
                    await factory.DisposeAsync();
                    model = new ApprovalRecordingModel { ProposedArtifact = model.ProposedArtifact, VerifyPublishedArtifact = true };
                    restartedFactory = new NativeTenantSqliteFactory(root, model, Configure);
                    restartedOwner = restartedFactory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
                    factory = restartedFactory; owner = restartedOwner;
                    await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
                    runs = factory.Services.GetRequiredService<ITenantRunStore>(); access = factory.Services.GetRequiredService<TenantAccessService>();
                    var restored = (await runs.FindTenantRunAsync(tenant, sourceId))!;
                    Assert.Equal(AgentRunStatus.WaitingForApproval, restored.Status); Assert.Equal(waiting.ApprovalRequestId, restored.ApprovalRequestId); Assert.Null(restored.Claim);
                }
                saved = (await factory.Services.GetRequiredService<ITenantApprovalStore>().FindApprovalAsync(tenant, waiting.ApprovalRequestId!.Value))!;
                Assert.Equal(ApprovalKind.Review, saved.Kind); Assert.Equal(TenantOperationKind.ArtifactVerification, saved.Operation.Kind);
                var detail = await Json(await owner.GetAsync(api + $"/approvals/{saved.RequestId}"));
                Assert.Equal("verify_artifact", detail.GetProperty("preparedAction").GetProperty("operation").GetString()); Assert.True(detail.GetProperty("review").GetProperty("canDecide").GetBoolean());
                Assert.DoesNotContain("Exact unpublished-to-reader body", detail.GetRawText());
                Assert.Equal("verify_artifact", Assert.Single((await Json(await owner.GetAsync(api + $"/sessions/{session}/approvals"))).GetProperty("items").EnumerateArray()).GetProperty("operation").GetString());
                if (proof)
                {
                    var identity = factory.Services.GetRequiredService<IdentityService>();
                    var account = await identity.RegisterAsync("artifact-metadata-reviewer@example.test", "Metadata reviewer", "strong-artifact-review-password");
                    var reviewerRole = await access.SaveRoleAsync(human, tenant, null, "Metadata reviewer base", ["approvals.read", "approvals.approve", "sessions.read", "agents.chat"], await Revision(owner, tenant));
                    var invite = await access.InviteAsync(human, tenant, account.LoginKey, reviewerRole.RoleId, await Revision(owner, tenant));
                    var reviewer = await access.AcceptInvitationAsync(account.AccountId, tenant, invite.Invitation.InvitationId, invite.Token); proofReviewer = reviewer.PrincipalId;
                    var fileRole = await access.SaveRoleAsync(human, tenant, null, "Read the exact reviewed file", ["artifacts.read"], await Revision(owner, tenant));
                    proofReadGrant = (await access.AssignRoleAsync(human, tenant, reviewer.PrincipalId, fileRole.RoleId, TenantArtifactRules.Scope(tenant, saved.Operation.ArtifactVerification!.ArtifactId), await Revision(owner, tenant))).AssignmentId;
                    await File.WriteAllTextAsync(proofPath + ".json", System.Text.Json.JsonSerializer.Serialize(new { baseUrl = owner.BaseAddress!.ToString(), tenant, session, requestId = saved.RequestId }));
                    using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(30));
                    while (!File.Exists(proofPath + "-decided")) await Task.Delay(100, deadline.Token);
                }
                else Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync(api + $"/approvals/{saved.RequestId}/decision", new { decision, actionHash = saved.Operation.ActionHash, expectedRevision = detail.GetProperty("securityRevision").GetInt64(), expectedRequestRevision = saved.Revision })).StatusCode);
            }
            var done = await AwaitBrowserRestartRun(runs, tenant, sourceId, false);
            Assert.True(done.Status == AgentRunStatus.Completed, $"{done.Status}: {done.Failure?.Code}: {done.Failure?.Summary}");
            var final = model.Requests.Last(); var message = final.Messages.Last(m => m.Role == ModelRole.Tool && m.Name == (review ? ToolCatalog.RequestAuthorization : ToolCatalog.ArtifactsVerify));
            using var output = System.Text.Json.JsonDocument.Parse(message.Text); var result = output.RootElement;
            if (review)
            {
                Assert.NotEqual(sourceId, done.AgentRunId); Assert.False(result.GetProperty("applied").GetBoolean());
                Assert.Equal(decision == "approve", result.GetProperty("readAuthorized").GetBoolean());
                var receipt = await factory.Services.GetRequiredService<ITenantApprovedOperationStore>().FindOperationReceiptAsync(tenant, saved!.Operation.OperationId);
                if (decision == "approve") { Assert.NotNull(receipt); Assert.Equal(done.AgentRunId, receipt.RunId); Assert.Equal(TenantArtifactVerificationRules.Reason, receipt.ReasonCode); }
                else Assert.Null(receipt);
                result = result.GetProperty("artifactMetadata");
            }
            if (!review || decision == "approve")
            {
                Assert.True(result.GetProperty("tenantArtifactRead").GetBoolean()); var id = result.GetProperty("artifactId").GetGuid();
                Assert.Equal(id, Assert.Single((await Json(await owner.GetAsync(api + $"/sessions/{session}/artifacts"))).GetProperty("items").EnumerateArray()).GetProperty("artifactId").GetGuid());
                foreach (var field in new[] { "sourceAttachmentId", "workspaceLogicalPath", "blobKey", "bytes", "content" }) Assert.False(result.TryGetProperty(field, out _));
            }
            if (saved is not null) Assert.Null(await factory.Services.GetRequiredService<ITenantApprovalStore>().FindOperationGrantAsync(tenant, saved.RequestId));
            Assert.Equal(0, factory.Services.GetRequiredService<AgentCore.Api.Http.OutboundHttpProbe>().Attempts);
            if (proof)
            {
                var recorded = await factory.Services.GetRequiredService<ITenantApprovedOperationStore>().FindApprovalDecisionAsync(tenant, saved!.RequestId);
                Assert.Equal(proofReviewer, recorded!.HumanPrincipalId);
                await File.WriteAllTextAsync(proofPath + "-completed.json", "{\"completed\":true}");
                using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(30));
                while (!File.Exists(proofPath + "-revoke")) await Task.Delay(100, deadline.Token);
                await access.RevokeAssignmentAsync(human, tenant, proofReadGrant!.Value, await Revision(owner, tenant));
                using var reader = factory.CreateClient(); await Login(reader, "artifact-metadata-reviewer@example.test", "strong-artifact-review-password");
                var unavailable = await reader.GetAsync(api + $"/approvals/{saved.RequestId}");
                Assert.Equal(HttpStatusCode.Forbidden, unavailable.StatusCode);
                Assert.Equal("approval_unavailable", (await unavailable.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("reasonCode").GetString());
                Assert.Equal(0, (await Json(await reader.GetAsync(api + "/approvals?pageSize=1"))).GetProperty("total").GetInt32());
                await File.WriteAllTextAsync(proofPath + "-revoked.json", "{\"revoked\":true}");
                while (!File.Exists(proofPath + "-release")) await Task.Delay(100, deadline.Token);
            }
        }
        finally { restartedOwner?.Dispose(); if (restartedFactory is not null) await restartedFactory.DisposeAsync(); SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
}
