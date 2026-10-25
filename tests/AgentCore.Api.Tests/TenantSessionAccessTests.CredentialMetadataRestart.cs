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
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Credential_metadata_pending_review_survives_SQLite_host_recreation_and_rechecks_delivery_authority(bool revoke)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-metadata-restart-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        const string proposal = """{"operation":"inspect_credential_metadata","arguments":{}}""";
        Guid tenant, source, requestId, executor, credentialId;
        try
        {
            await using (var first = new NativeTenantSqliteFactory(root, new ApprovalRecordingModel { ProposedMetadataRead = proposal }))
            {
                using var owner = first.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") });
                await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
                tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid(); var api = $"/api/v2/tenants/{tenant}";
                var human = (await Json(await owner.GetAsync(api + "/access/me"))).GetProperty("principalId").GetGuid();
                var agent = await Json(await owner.PostAsJsonAsync(api + "/agents", new { templateId = "general-assistant", version = 32, expectedRevision = await Revision(owner, tenant) }));
                var instance = agent.GetProperty("instance").GetProperty("instanceId").GetGuid(); executor = agent.GetProperty("principalId").GetGuid();
                var credential = await first.Services.GetRequiredService<TenantCredentialService>().CreateAsync(human, tenant,
                    new("Restart metadata account", CredentialKind.Password, CredentialPurpose.General, null, ["https://login.example.test"], "restart-private-password"), await Revision(owner, tenant));
                credentialId = credential.Credential.CredentialId;
                await first.Services.GetRequiredService<TenantCredentialBindingService>().BindAsync(human, tenant, instance, credentialId, "account", await Revision(owner, tenant), 1, 1);
                var session = (await Json(await owner.PostAsJsonAsync(api + "/sessions", new { retentionKind = "saved", agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 2 }))).GetProperty("sessionId").GetGuid();
                var access = first.Services.GetRequiredService<TenantAccessService>();
                foreach (var target in new[] { ("credentials.read", TenantCredentialRules.Scope(tenant, credentialId)), ("sessions.read", TenantSessionRules.Scope(tenant, session)) })
                {
                    var role = await access.SaveRoleAsync(human, tenant, null, target.Item1, [target.Item1], await Revision(owner, tenant));
                    await access.AssignRoleAsync(human, tenant, executor, role.RoleId, target.Item2, await Revision(owner, tenant));
                }
                Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(api + "/operation-policies/credentials.read", new { mode = "always", independentReview = false,
                    expectedRevision = await Revision(owner, tenant), expectedPolicyRevision = 0 })).StatusCode);
                source = (await Json(await owner.PostAsJsonAsync(api + $"/sessions/{session}/messages", new { messageId = Guid.NewGuid(), text = "Read bound metadata after review.",
                    expectedRevision = await Revision(owner, tenant), expectedSessionRevision = 1 }))).GetProperty("runId").GetGuid();
                var pending = await AwaitBrowserRestartRun(first.Services.GetRequiredService<ITenantRunStore>(), tenant, source, true);
                Assert.Equal(AgentRunStatus.WaitingForApproval, pending.Status); requestId = pending.ApprovalRequestId!.Value;
            }
            var model = new ApprovalRecordingModel { ProposedMetadataRead = proposal }; var enabled = false;
            await using var second = new NativeTenantSqliteFactory(root, model, services => services.Replace(ServiceDescriptor.Singleton<ITenantRunExecution>(p =>
                new MetadataExecutionGate(() => Volatile.Read(ref enabled), new TenantRunExecution(p.GetRequiredService<ITenantRuntimeStore>(), p.GetRequiredService<ITenantRunStore>(),
                    p.GetRequiredService<ITenantRunDispatcher>(), p.GetRequiredService<IIdGenerator>(), p.GetRequiredService<TimeProvider>())))));
            using var client = second.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") });
            await Login(client, "owner@example.test", "explicit-synthetic-test-password"); var path = $"/api/v2/tenants/{tenant}";
            var view = await Json(await client.GetAsync(path + $"/approvals/{requestId}")); Assert.Equal("pending", view.GetProperty("status").GetString());
            Assert.True(view.GetProperty("review").GetProperty("canDecide").GetBoolean()); Assert.Empty(model.Requests);
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(path + $"/approvals/{requestId}/decision", new { decision = "approve", actionHash = view.GetProperty("preparedAction").GetProperty("actionHash").GetString(),
                expectedRevision = view.GetProperty("securityRevision").GetInt64(), expectedRequestRevision = view.GetProperty("requestRevision").GetInt64() })).StatusCode);
            if (revoke)
            {
                var state = (await second.Services.GetRequiredService<IIamStore>().ReadTenantAsync(tenant))!; var human = state.Users.Single().PrincipalId;
                var assignment = state.Assignments.Single(a => a.PrincipalId == executor && a.Scope.ResourceId == credentialId.ToString("D"));
                await second.Services.GetRequiredService<TenantAccessService>().RevokeAssignmentAsync(human, tenant, assignment.AssignmentId, state.Tenant.Revision);
            }
            Volatile.Write(ref enabled, true);
            var completed = await AwaitBrowserRestartRun(second.Services.GetRequiredService<ITenantRunStore>(), tenant, source, false);
            Assert.True(completed.Status == AgentRunStatus.Completed, $"{completed.Status}: {completed.Failure?.Code}: {completed.Failure?.Summary}");
            Assert.True(AgentRunToolCallCheckpoint.TryRead(completed.Checkpoint, out var messages));
            using var result = JsonDocument.Parse(Assert.Single(messages!, m => m.Role == ModelRole.Tool && m.Name == ToolCatalog.RequestAuthorization).Text);
            Assert.Equal(!revoke, result.RootElement.GetProperty("readAuthorized").GetBoolean());
            if (revoke) Assert.Equal(JsonValueKind.Null, result.RootElement.GetProperty("credentialMetadata").ValueKind);
            else Assert.Equal("account", Assert.Single(result.RootElement.GetProperty("credentialMetadata").GetProperty("items").EnumerateArray()).GetProperty("reference").GetString());
            Assert.DoesNotContain("restart-private-password", JsonSerializer.Serialize(model.Requests));
            Assert.Single(await second.Services.GetRequiredService<IIamStore>().ReadAuditAsync(tenant, 200), a => a.Action == TenantCredentialReadOperationRules.AuditAction);
        }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
