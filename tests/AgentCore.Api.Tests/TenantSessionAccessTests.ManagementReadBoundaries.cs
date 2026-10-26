using System.Net;
using System.Net.Http.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentCore.Api.Tests;
public sealed partial class TenantSessionAccessTests
{
    private sealed class ManagementReadRace
    {
        public Func<Task>? AfterObservation;
        public int Applied;
    }
    private sealed class ManagementReadHook(ITenantManagementStore inner, ManagementReadRace race) : ITenantManagementStore
    {
        public ValueTask<TenantResourcePinRetention?> ReadRetainedResourcePinAsync(TenantRunReference reference, AgentCore.Domain.Definitions.EffectiveAgentResource resource, CancellationToken ct = default) =>
            inner.ReadRetainedResourcePinAsync(reference, resource, ct);
        public ValueTask<TenantManagementResourceContext> ReadTenantManagementResourceContextAsync(TenantRunReference reference, Guid? generation, string tool, string arguments, Guid? inspectionHuman = null, CancellationToken ct = default) =>
            inner.ReadTenantManagementResourceContextAsync(reference, generation, tool, arguments, inspectionHuman, ct);
        public ValueTask<TenantOperationReceipt?> TryCommitManagementCommandAsync(TenantSecurityState next, long expectedSecurityRevision, TenantManagementCommandCommit commit, AuthorizationAudit audit, CancellationToken ct = default)
            => inner.TryCommitManagementCommandAsync(next, expectedSecurityRevision, commit, audit, ct);
        public ValueTask RequireTenantManagementSkillSourcesAsync(TenantRunReference reference, Guid? generation, string tool, string arguments, Guid? inspectionHuman = null, CancellationToken ct = default)
            => inner.RequireTenantManagementSkillSourcesAsync(reference, generation, tool, arguments, inspectionHuman, ct);
        public ValueTask<TenantManagementSource> ReadTenantManagementSourceAsync(TenantRunReference reference, Guid? generation, Guid? inspectionHuman = null, CancellationToken ct = default)
            => inner.ReadTenantManagementSourceAsync(reference, generation, inspectionHuman, ct);
        public async ValueTask<TenantManagementReadObservation> ObserveTenantManagementReadAsync(TenantRunReference reference, Guid? generation, string tool, string arguments,
            PreparedManagementRead? expected = null, Guid? inspectionHuman = null, CancellationToken ct = default)
        {
            var result = await inner.ObserveTenantManagementReadAsync(reference, generation, tool, arguments, expected, inspectionHuman, ct);
            if (generation is not null && expected is not null && inspectionHuman is null && Interlocked.Exchange(ref race.AfterObservation, null) is { } change)
            { await change(); Interlocked.Increment(ref race.Applied); }
            return result;
        }
        public ValueTask RequireTenantManagementReadSourcesAsync(TenantRunReference reference, IReadOnlyList<TenantManagementReadReference> sources, Guid? generation, CancellationToken ct = default)
            => inner.RequireTenantManagementReadSourcesAsync(reference, sources, generation, ct);
    }
    [Theory]
    [InlineData(false, "external_preference")] [InlineData(true, "external_preference")]
    [InlineData(false, "read_revoked")] [InlineData(true, "read_revoked")]
    [InlineData(false, "freeze")] [InlineData(true, "freeze")]
    public async Task Management_read_checkpoint_rejects_changed_or_revoked_sources_before_model_reentry(bool sqlite, string boundary)
    {
        var root = Path.Combine(Path.GetTempPath(), "management-read-race-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var model = new ManagementReadModel("settings.inspect", "conversationPolicy"); var race = new ManagementReadRace();
            await using WebApplicationFactory<Program> factory = sqlite ? new TenantApprovalSqliteFactory(root, model) : new ApprovalExecutionFactory();
            await using var host = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => {
                s.Replace(ServiceDescriptor.Singleton<ILanguageModel>(model));
                s.Replace(ServiceDescriptor.Singleton<ITenantManagementStore>(p => new ManagementReadHook((ITenantManagementStore)p.GetRequiredService<IIamStore>(), race)));
            }));
            using var owner = host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
            var (tenant, instance) = await SetupManagementRead(host, owner, false); var api = $"/api/v2/tenants/{tenant}";
            race.AfterObservation = async () => {
                if (boundary == "external_preference")
                    Assert.Equal(HttpStatusCode.OK, (await owner.PatchAsJsonAsync(api + $"/agents/{instance}/settings/conversationPolicy", new {
                        expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 2, set = new { language = "fr-FR" }, clear = Array.Empty<string>()
                    })).StatusCode);
                else if (boundary == "freeze")
                    Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync(api + $"/agents/{instance}/self-management/freeze", new {
                        expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 2
                    })).StatusCode);
                else
                {
                    var state = (await host.Services.GetRequiredService<IIamStore>().ReadTenantAsync(tenant))!;
                    var role = state.Roles.Single(r => r.Name == "Exact management reader");
                    await host.Services.GetRequiredService<TenantAccessService>().SaveRoleAsync(Assert.Single(state.Users).PrincipalId, tenant, role.RoleId, role.Name,
                        ["agents.update"], state.Tenant.Revision);
                }
            };
            var created = await Json(await owner.PostAsJsonAsync(api + "/sessions", new { retentionKind = "saved", agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 2 })); var session = created.GetProperty("sessionId").GetGuid();
            var submitted = await owner.PostAsJsonAsync(api + $"/sessions/{session}/messages", new { messageId = Guid.NewGuid(), text = "Inspect the exact settings before a source boundary changes.", expectedRevision = await Revision(owner, tenant), expectedSessionRevision = 1 });
            Assert.Equal(HttpStatusCode.Accepted, submitted.StatusCode); var runId = (await Json(submitted)).GetProperty("runId").GetGuid();
            if (factory is ApprovalExecutionFactory memory) Volatile.Write(ref memory.Enabled, true);
            var final = await AwaitManagementRun(host.Services.GetRequiredService<ITenantRunStore>(), tenant, runId);
            Assert.Equal(1, race.Applied); Assert.Equal(AgentRunStatus.Failed, final.Status);
            Assert.Equal(2, model.Requests.Count);
            Assert.DoesNotContain(model.Requests.SelectMany(r => r.Messages).Where(m => m.Role == ModelRole.Tool), m => m.Text.Contains("\"tenantManagementRead\":true"));
            Assert.Empty(TenantManagementReadReferences.Read(final.Checkpoint));
            Assert.Empty((await host.Services.GetRequiredService<ITenantApprovalStore>().ListApprovalsAsync(tenant, null, null, new(0,100))).Items);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
    [Theory] [InlineData(false, "changed")] [InlineData(true, "changed")] [InlineData(false, "read_revoked")] [InlineData(true, "read_revoked")] [InlineData(false, "freeze")] [InlineData(true, "freeze")]
    public async Task Management_read_changed_consent_withholds_substituted_values_and_preserves_cancellation(bool sqlite, string boundary)
    {
        var root = Path.Combine(Path.GetTempPath(), "management-consent-change-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var model = new ManagementReadModel("settings.inspect", "conversationPolicy");
            await using WebApplicationFactory<Program> factory = sqlite ? new TenantApprovalSqliteFactory(root, model) : new ApprovalExecutionFactory();
            await using var host = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.Replace(ServiceDescriptor.Singleton<ILanguageModel>(model))));
            using var owner = host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
            var (tenant, instance) = await SetupManagementRead(host, owner, false); var api = $"/api/v2/tenants/{tenant}";
            Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(api + "/operation-policies/agents.read", new { mode = "always", independentReview = false, expectedRevision = await Revision(owner, tenant), expectedPolicyRevision = 0 })).StatusCode);
            var created = await Json(await owner.PostAsJsonAsync(api + "/sessions", new { retentionKind = "saved", agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 2 })); var session = created.GetProperty("sessionId").GetGuid();
            var response = await owner.PostAsJsonAsync(api + $"/sessions/{session}/messages", new { messageId = Guid.NewGuid(), text = "Request an exact settings observation.", expectedRevision = await Revision(owner, tenant), expectedSessionRevision = 1 });
            var runId = (await Json(response)).GetProperty("runId").GetGuid(); if (factory is ApprovalExecutionFactory memory) Volatile.Write(ref memory.Enabled, true);
            var pending = await AwaitManagementRun(host.Services.GetRequiredService<ITenantRunStore>(), tenant, runId, true); Assert.Equal(AgentRunStatus.WaitingForApproval, pending.Status);
            var approvals = host.Services.GetRequiredService<ITenantApprovalStore>(); var request = (await approvals.FindApprovalAsync(tenant, pending.ApprovalRequestId!.Value))!;
            if (boundary == "changed")
                Assert.Equal(HttpStatusCode.OK, (await owner.PatchAsJsonAsync(api + $"/agents/{instance}/settings/conversationPolicy", new {
                    expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 2, set = new { language = "fr-FR" }, clear = Array.Empty<string>()
                })).StatusCode);
            else if (boundary == "freeze")
                Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync(api + $"/agents/{instance}/self-management/freeze", new {
                    expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 2
                })).StatusCode);
            else
            {
                var state = (await host.Services.GetRequiredService<IIamStore>().ReadTenantAsync(tenant))!; var role = state.Roles.Single(r => r.Name == "Exact management reader");
                await host.Services.GetRequiredService<TenantAccessService>().SaveRoleAsync(Assert.Single(state.Users).PrincipalId, tenant, role.RoleId, role.Name, ["agents.update"], state.Tenant.Revision);
            }
            var detail = await owner.GetAsync(api + $"/approvals/{request.RequestId}"); Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
            var view = await Json(detail); Assert.False(view.GetProperty("review").GetProperty("canDecide").GetBoolean()); Assert.True(view.GetProperty("review").GetProperty("canCancel").GetBoolean());
            Assert.DoesNotContain("fr-FR", view.GetProperty("review").GetRawText());
            var failed = await owner.PostAsJsonAsync(api + $"/approvals/{request.RequestId}/decision", new { decision = "approve", actionHash = request.Operation.ActionHash, expectedRevision = await Revision(owner, tenant), expectedRequestRevision = 1 });
            Assert.False(failed.IsSuccessStatusCode);
            Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync(api + $"/approvals/{request.RequestId}/cancel", new { actionHash = request.Operation.ActionHash, expectedRevision = await Revision(owner, tenant), expectedRequestRevision = 1 })).StatusCode);
            Assert.Equal(TenantApprovalStatus.Cancelled, (await approvals.FindApprovalAsync(tenant, request.RequestId))!.Status);
            Assert.Null(await approvals.FindOperationGrantAsync(tenant, request.RequestId));
            Assert.Null(await host.Services.GetRequiredService<ITenantApprovedOperationStore>().FindOperationReceiptAsync(tenant, request.Operation.OperationId));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
}
