using System.Net;
using System.Net.Http.Json;
using AgentCore.Api.Realtime;
using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    [Theory(Timeout = 90000)]
    [InlineData(false)] [InlineData(true)]
    public async Task Tenant_runtime_shutdown_retires_a_noncooperative_generation_and_rejects_late_publication(bool sqlite)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-runtime-drain-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var model = new CancellationModel();
        try
        {
            await using WebApplicationFactory<Program> factory = sqlite ? new TenantApprovalSqliteFactory(root, model) : new CancellationFactory(root, model);
            try
            {
                using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
                await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
                var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid();
                var api = $"/api/v2/tenants/{tenant}";
                var instance = (await Json(await owner.PostAsJsonAsync(api + "/agents", new { templateId = "examiner", version = 1, expectedRevision = await Revision(owner, tenant) }))).GetProperty("instance").GetProperty("instanceId").GetGuid();
                var session = (await Json(await owner.PostAsJsonAsync(api + "/sessions", new { agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1 }))).GetProperty("sessionId").GetGuid();
                var accepted = await owner.PostAsJsonAsync(api + $"/sessions/{session}/messages", new { messageId = Guid.NewGuid(), text = "Keep this admitted objective during shutdown", expectedRevision = await Revision(owner, tenant), expectedSessionRevision = 1 });
                Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
                var runId = (await Json(accepted)).GetProperty("runId").GetGuid();
                await model.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
                var runs = factory.Services.GetRequiredService<ITenantRunStore>();
                var before = (await runs.FindTenantRunAsync(tenant, runId))!;
                Assert.Equal(AgentRunStatus.Running, before.Status);
                var host = factory.Services.GetRequiredService<TenantSessionRuntimeHost>();
                await Task.WhenAll(host.StopAsync(), host.StopAsync()).WaitAsync(TimeSpan.FromSeconds(15));
                Assert.False(factory.Services.GetRequiredService<TenantRuntimeCapacity>().TryReserveTenantRuntime());
                Assert.False(model.Release.Task.IsCompleted); Assert.Equal(1, model.Calls);
                model.Release.TrySetResult(); await model.LateAttempted.Task.WaitAsync(TimeSpan.FromSeconds(10));
                var retained = (await runs.FindTenantRunAsync(tenant, runId))!;
                Assert.Equal(before.AgentRunId, retained.AgentRunId);
                var saved = (await factory.Services.GetRequiredService<ITenantRuntimeStore>().LoadTenantRuntimeAsync(
                    new(before.Owner, session, runId, before.Claim!.Generation)))!;
                Assert.DoesNotContain(saved.Entries, e => e.Text.Contains("LATE AFTER TENANT CANCELLATION", StringComparison.Ordinal));
                Assert.Equal(1, model.Calls);
            }
            finally { model.Release.TrySetResult(); model.InspectionRelease.TrySetResult(); }
        }
        finally { SqliteConnection.ClearAllPools(); try { Directory.Delete(root, true); } catch (IOException) { } }
    }
}
