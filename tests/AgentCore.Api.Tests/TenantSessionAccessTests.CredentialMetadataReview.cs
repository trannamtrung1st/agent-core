using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Credentials;
using AgentCore.Domain.Security;
using AgentCore.Api.Http;
using AgentCore.Api.Security;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    private sealed class MetadataExecutionGate(Func<bool> enabled, ITenantRunExecution inner) : ITenantRunExecution
    {
        public ValueTask<int> ExecuteRunnableAsync(int limit, CancellationToken ct = default) =>
            enabled() ? inner.ExecuteRunnableAsync(limit, ct) : ValueTask.FromResult(0);
    }
    [Theory] [InlineData(false, "approve")] [InlineData(true, "approve")] [InlineData(false, "reject")] [InlineData(true, "reject")]
    [InlineData(false, "cancel")] [InlineData(true, "cancel")] [InlineData(false, "revoke")] [InlineData(true, "revoke")]
    public async Task Credential_metadata_model_waits_for_exact_HTTP_review_and_receives_only_its_scoped_page(bool sqlite, string choice)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-metadata-model-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        const string name = "Metadata account <script>not markup</script> — shared team login";
        const string secret = "metadata-must-never-resolve-this-private-password";
        var proof = !sqlite && choice == "approve" && Environment.GetEnvironmentVariable("AGENTCORE_METADATA_UI_PROOF") == "1";
        try
        {
            var model = new ApprovalRecordingModel { ProposedMetadataRead = """{"operation":"inspect_credential_metadata","arguments":{"limit":1}}""" };
            var enabled = true;
            void Configure(IServiceCollection services)
            {
                if (proof) ((IdentityHttpOptions)services.Single(d => d.ServiceType == typeof(IdentityHttpOptions)).ImplementationInstance!).AllowedOrigins =
                    [Environment.GetEnvironmentVariable("AGENTCORE_METADATA_UI_ORIGIN") ?? "http://127.0.0.1:5214"];
                services.Replace(ServiceDescriptor.Singleton<ITenantRunExecution>(p =>
                    new MetadataExecutionGate(() => Volatile.Read(ref enabled), new TenantRunExecution(p.GetRequiredService<ITenantRuntimeStore>(), p.GetRequiredService<ITenantRunStore>(),
                        p.GetRequiredService<ITenantRunDispatcher>(), p.GetRequiredService<IIdGenerator>(), p.GetRequiredService<TimeProvider>()))));
            }
            await using WebApplicationFactory<Program> factory = sqlite ? new NativeTenantSqliteFactory(root, model, Configure) : new NativeTenantMemoryFactory(root, model, Configure);
            if (proof) factory.UseKestrel(0);
            using var owner = proof ? factory.CreateClient()
                : factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
            await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
            var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid(); var api = $"/api/v2/tenants/{tenant}";
            var human = (await Json(await owner.GetAsync(api + "/access/me"))).GetProperty("principalId").GetGuid();
            var agent = await Json(await owner.PostAsJsonAsync(api + "/agents", new { templateId = "general-assistant", version = 36, expectedRevision = await Revision(owner, tenant) }));
            var instance = agent.GetProperty("instance").GetProperty("instanceId").GetGuid(); var executor = agent.GetProperty("principalId").GetGuid();
            var credential = await factory.Services.GetRequiredService<TenantCredentialService>().CreateAsync(human, tenant,
                new(name, CredentialKind.Password, CredentialPurpose.General, null, ["https://login.example.test"], secret), await Revision(owner, tenant));
            await factory.Services.GetRequiredService<TenantCredentialBindingService>().BindAsync(human, tenant, instance, credential.Credential.CredentialId, "account",
                await Revision(owner, tenant), 1, 1);
            var session = (await Json(await owner.PostAsJsonAsync(api + "/sessions", new { retentionKind = "saved", agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 2 })))
                .GetProperty("sessionId").GetGuid();
            var access = factory.Services.GetRequiredService<TenantAccessService>();
            var role = await access.SaveRoleAsync(human, tenant, null, "Read this Credential metadata", ["credentials.read"], await Revision(owner, tenant));
            var assignment = await access.AssignRoleAsync(human, tenant, executor, role.RoleId, TenantCredentialRules.Scope(tenant, credential.Credential.CredentialId), await Revision(owner, tenant));
            role = await access.SaveRoleAsync(human, tenant, null, "Read this Session", ["sessions.read"], await Revision(owner, tenant));
            await access.AssignRoleAsync(human, tenant, executor, role.RoleId, TenantSessionRules.Scope(tenant, session), await Revision(owner, tenant));
            Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(api + "/operation-policies/credentials.read", new { mode = "always", independentReview = false,
                expectedRevision = await Revision(owner, tenant), expectedPolicyRevision = 0 })).StatusCode);
            var source = (await Json(await owner.PostAsJsonAsync(api + $"/sessions/{session}/messages", new { messageId = Guid.NewGuid(), text = "Read the first page of currently bound Credential metadata after review.",
                expectedRevision = await Revision(owner, tenant), expectedSessionRevision = 1 }))).GetProperty("runId").GetGuid();
            var runs = factory.Services.GetRequiredService<ITenantRunStore>(); var waiting = await AwaitBrowserRestartRun(runs, tenant, source, true);
            Assert.True(waiting.Status == AgentRunStatus.WaitingForApproval, $"{waiting.Status}: {waiting.Failure?.Code}: {waiting.Failure?.Summary}");
            Volatile.Write(ref enabled, false);
            var request = (await factory.Services.GetRequiredService<ITenantApprovalStore>().FindApprovalAsync(tenant, waiting.ApprovalRequestId!.Value))!;
            Assert.Equal(TenantOperationKind.CredentialRead, request.Operation.Kind); Assert.Equal(ApprovalKind.Review, request.Kind); Assert.Equal("credentials.read", request.Permission);
            Assert.DoesNotContain("Metadata account", JsonSerializer.Serialize(model.Requests));
            var view = await Json(await owner.GetAsync(api + $"/approvals/{request.RequestId}")); Assert.True(view.GetProperty("review").GetProperty("canDecide").GetBoolean());
            Assert.Equal("inspect_credential_metadata", view.GetProperty("preparedAction").GetProperty("operation").GetString());
            Assert.DoesNotContain(secret, view.GetRawText()); Assert.DoesNotContain("ProtectedPayload", view.GetRawText());
            if (proof)
            {
                await File.WriteAllTextAsync("/private/tmp/agent-core-metadata-ui-proof-20261018.json", JsonSerializer.Serialize(new { baseUrl = owner.BaseAddress!.ToString(), tenant, session, requestId = request.RequestId }));
                Volatile.Write(ref enabled, true);
                using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(8));
                while ((await factory.Services.GetRequiredService<ITenantApprovalStore>().FindApprovalAsync(tenant, request.RequestId, deadline.Token))!.Status == TenantApprovalStatus.Pending)
                    await Task.Delay(100, deadline.Token);
            }
            else
            {
                var response = choice == "cancel" ? await owner.PostAsJsonAsync(api + $"/approvals/{request.RequestId}/cancel", new { actionHash = request.Operation.ActionHash,
                    expectedRevision = view.GetProperty("securityRevision").GetInt64(), expectedRequestRevision = request.Revision })
                    : await owner.PostAsJsonAsync(api + $"/approvals/{request.RequestId}/decision", new { decision = choice == "reject" ? "reject" : "approve", actionHash = request.Operation.ActionHash,
                        expectedRevision = view.GetProperty("securityRevision").GetInt64(), expectedRequestRevision = request.Revision });
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                if (choice == "revoke") await access.RevokeAssignmentAsync(human, tenant, assignment.AssignmentId, await Revision(owner, tenant));
                Volatile.Write(ref enabled, true);
            }
            var completed = await AwaitBrowserRestartRun(runs, tenant, source, false);
            Assert.True(completed.Status == AgentRunStatus.Completed, $"{completed.Status}: {completed.Failure?.Code}: {completed.Failure?.Summary}");
            Assert.True(AgentRunToolCallCheckpoint.TryRead(completed.Checkpoint, out var messages));
            var result = Assert.Single(messages!, m => m.Role == ModelRole.Tool && m.Name == ToolCatalog.RequestAuthorization).Text;
            using var output = JsonDocument.Parse(result); Assert.False(output.RootElement.GetProperty("applied").GetBoolean());
            Assert.Equal(choice == "approve", output.RootElement.GetProperty("readAuthorized").GetBoolean());
            if (choice == "approve") Assert.Equal(name, Assert.Single(output.RootElement.GetProperty("credentialMetadata").GetProperty("items").EnumerateArray()).GetProperty("displayName").GetString());
            else Assert.Equal(JsonValueKind.Null, output.RootElement.GetProperty("credentialMetadata").ValueKind);
            Assert.Contains(model.Requests, r => r.Messages.Any(m => m.Role == ModelRole.Tool && m.Text == result));
            Assert.DoesNotContain(secret, JsonSerializer.Serialize(model.Requests)); Assert.DoesNotContain(secret, JsonSerializer.Serialize(new { completed.Checkpoint, completed.Admission }));
            var receipt = await factory.Services.GetRequiredService<ITenantApprovedOperationStore>().FindOperationReceiptAsync(tenant, request.Operation.OperationId);
            if (choice is "reject" or "cancel") Assert.Null(receipt);
            else { Assert.NotNull(receipt); Assert.Equal(choice == "approve" ? TenantOperationOutcome.Completed : TenantOperationOutcome.Denied, receipt.Outcome); }
            Assert.Null(await factory.Services.GetRequiredService<ITenantApprovalStore>().FindOperationGrantAsync(tenant, request.RequestId));
            Assert.False((await factory.Services.GetRequiredService<IamAuthorizer>().CheckTenantAsync((await factory.Services.GetRequiredService<IIamStore>().ReadTenantAsync(tenant))!, executor,
                PermissionCatalog.Get("credentials.use"), TenantCredentialRules.Scope(tenant, credential.Credential.CredentialId), CancellationToken.None)).Allowed);
            Assert.Equal(0, factory.Services.GetRequiredService<OutboundHttpProbe>().Attempts);
            if (proof)
            {
                await File.WriteAllTextAsync("/private/tmp/agent-core-metadata-ui-proof-completed-20261018.json", JsonSerializer.Serialize(new { tenant, session, requestId = request.RequestId, completed = true }));
                using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(8));
                while (!File.Exists("/private/tmp/agent-core-metadata-ui-proof-release-20261018")) await Task.Delay(100, deadline.Token);
            }
        }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
