using System.Net;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Application.Workspaces;
using AgentCore.Domain.Conversation;
using AgentCore.Infrastructure.Workspaces;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    [Theory(Timeout = 90000)]
    [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task Tenant_working_cleanup_deleted_parent_waits_actual_causal_child_and_completion_accounting(bool sqlite, bool report)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-working-child-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var model = new IsolatedChildModel(report);
        await using WebApplicationFactory<Program> factory = sqlite ? new TenantBackgroundSqliteFactory(root, model, holdCompletion: true) : new TenantBackgroundMemoryFactory(root, model, holdCompletion: true);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") });
        try
        {
            var (tenant, session, source) = await StartTenantBackground(factory, client, independentTask: true, fileCapabilities: ["tasks.delegate"], reportReads: report, requestIsolated: true);
            await model.ChildEntered.Task.WaitAsync(TimeSpan.FromSeconds(20));
            var runs = factory.Services.GetRequiredService<ITenantRunStore>(); var sessions = factory.Services.GetRequiredService<ITenantSessionStore>();
            for (var i = 0; i < 400 && ((await runs.FindTenantRunAsync(tenant, source))?.IsTerminal != true || (await sessions.FindTenantSessionAsync(tenant, session))!.Snapshot.Status is SessionStatus.Attached or SessionStatus.Ending); i++) await Task.Delay(25);
            Assert.True((await runs.FindTenantRunAsync(tenant, source))!.IsTerminal);
            var parent = (await sessions.FindTenantSessionAsync(tenant, session))!;
            var child = (await sessions.ListTenantSessionsAsync(tenant, null, 0, 100)).Items.Single(s => s.Snapshot.Origin.OriginatingSessionId == session);
            var path = AgentWorkspacePhysicalPaths.WorkingDirectory(Path.Combine(root, "workspaces"), parent.Owner.AgentInstanceId, session); Directory.CreateDirectory(path); await File.WriteAllTextAsync(Path.Combine(path, "private.txt"), "parent");
            var childPath = AgentWorkspacePhysicalPaths.WorkingDirectory(Path.Combine(root, "workspaces"), child.Owner.AgentInstanceId, child.Owner.SessionId); Directory.CreateDirectory(childPath); await File.WriteAllTextAsync(Path.Combine(childPath, "private.txt"), "child");
            Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/v2/tenants/{tenant}/sessions/{session}?expectedRevision={await Revision(client, tenant)}&expectedSessionRevision={parent.Snapshot.Revision}")).StatusCode);
            var store = factory.Services.GetRequiredService<ITenantWorkingCleanupStore>(); var cleanup = factory.Services.GetRequiredService<TenantWorkingCleanupService>();
            Assert.Null(await store.ReadTenantWorkingCleanupAsync(parent.Owner)); Assert.False(await cleanup.CleanupAsync(parent.Owner)); Assert.True(Directory.Exists(path));
            var generatedBeforeRetirement = model.Requests.Count;
            model.ReleaseChild.TrySetResult();
            for (var i = 0; i < 400 && (await runs.FindTenantRunAsync(tenant, child.Snapshot.Origin.InitialBackgroundAgentRunId!.Value))?.IsTerminal != true; i++) await Task.Delay(25);
            var completed = (await runs.FindTenantRunAsync(tenant, child.Snapshot.Origin.InitialBackgroundAgentRunId!.Value))!; Assert.Equal(AgentRunStatus.Failed, completed.Status);
            Assert.Equal(AgentRunSideEffectDisposition.None, completed.SideEffect.Disposition);
            Assert.Equal(generatedBeforeRetirement + 1, model.Requests.Count); // The already-entered held call records itself on release; no subsequent generation is authorized.
            Assert.Single(model.Requests, r => TenantBackgroundModel.Origin(r) == ExecutionOrigin.DelegatedTask);
            if (report)
            {
                for (var i = 0; i < 200 && await runs.FindTenantCompletionAsync(completed.Owner, completed.AgentRunId) is null; i++) await Task.Delay(25);
                Assert.Null(await store.ReadTenantWorkingCleanupAsync(parent.Owner)); Assert.False(await cleanup.CleanupAsync(parent.Owner));
                var requests = model.Requests.Count;
                await factory.Services.GetRequiredService<TenantBackgroundCompletionService>().AdmitPendingAsync(8, CancellationToken.None);
                Assert.Equal(CompletionInboxStatus.Skipped, (await runs.FindTenantCompletionAsync(completed.Owner, completed.AgentRunId))!.Status); Assert.Equal(requests, model.Requests.Count);
            }
            Assert.True(await cleanup.CleanupAsync(parent.Owner)); Assert.False(Directory.Exists(path));
            Assert.Equal("child", await File.ReadAllTextAsync(Path.Combine(childPath, "private.txt")));
            var failure = Assert.Single(model.Failures);
            // Current Harness preparation can observe the deleted lineage before the later
            // projection guard. Both are authority refusals; diagnostic sinks omit their messages.
            Assert.True(failure.Contains("AgentCore.Application.Security.AccessDeniedException HResult=", StringComparison.Ordinal)
                || failure.Contains("AgentCore.Application.Sessions.AgentCoreException HResult=", StringComparison.Ordinal), failure);
            Assert.DoesNotContain("Tenant execution authority is unavailable.", failure);
        }
        finally { model.ReleaseChild.TrySetResult(); }
    }
}
