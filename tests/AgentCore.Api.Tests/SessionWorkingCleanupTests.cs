using System.Net.Http.Json;
using AgentCore.Application.Admin;
using AgentCore.Application.Events;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Workspaces;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.TestHost;

namespace AgentCore.Api.Tests;

public sealed class SessionWorkingCleanupTests
{
    [Fact]
    public async Task Cleanup_waits_for_session_terminal_and_preserves_sibling_home_artifacts_and_run_receipts()
    {
        await using var host = new AgentCoreApiFactory().WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            // This fixture owns and dispatches its SessionRuntime directly. A second
            // dispatcher must not claim the same work through another SessionHost runtime.
            foreach (var service in services.Where(s => s.ImplementationType == typeof(AgentCore.Api.AgentRunHostedService)).ToArray())
                services.Remove(service);
        }));
        using var client = TestOwnerCapability.CreateOwnerClient(host);
        var services = host.Services;
        var instance = await services.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 31);
        var root = $"/api/v2/admin/agent-instances/{instance.InstanceId}/settings/stateIsolationPolicy";
        (await client.PatchAsJsonAsync(root, new { expectedInstanceRevision = instance.Revision, set = new { workingCleanup = "AfterTerminal" }, clear = Array.Empty<string>() })).EnsureSuccessStatusCode();
        var manager = services.GetRequiredService<SessionManager>();
        var a = await manager.CreateForInstanceAsync(instance.InstanceId, SessionMode.Text);
        var b = await manager.CreateForInstanceAsync(instance.InstanceId, SessionMode.Text);
        var workspace = services.GetRequiredService<ISessionWorkspace>();
        await workspace.EnsureAsync(a.SessionId, a.Definition); await workspace.EnsureAsync(b.SessionId, b.Definition);
        await workspace.WriteAsync(a.SessionId, "/working/customer.txt", "A"u8.ToArray());
        await workspace.WriteAsync(b.SessionId, "/working/customer.txt", "B"u8.ToArray());
        var artifacts = services.GetRequiredService<IArtifactStore>();
        var artifact = await artifacts.CreateAsync(a.SessionId, "retained.txt", "text/plain", "retained"u8.ToArray(), null, "/working/customer.txt");
        var home = services.GetRequiredService<AgentInstanceWorkspaceService>();
        var homeItem = await home.WriteAsync(a.SessionId, "/home/retained.txt", "text/plain", "home"u8.ToArray());
        var cleanup = services.GetRequiredService<SessionWorkingCleanupService>();
        await using var runtime = services.GetRequiredService<SessionRuntimeFactory>().Create(a, new SilentOutput());
        await runtime.AttachAsync();
        Assert.True(await runtime.SubmitPersistedUserTextAsync("Hello", Guid.NewGuid()));
        await runtime.WaitUntilIdleAsync();
        Assert.False(await cleanup.CleanupAsync(a.SessionId));
        Assert.Equal("A"u8.ToArray(), (await workspace.ReadAsync(a.SessionId, a.Definition, "/working/customer.txt")).Bytes);
        Assert.True(await runtime.RequestLifecycleTransitionAsync(SessionLifecycleStatus.Paused, LifecycleTransitionSource.User, "manual"));
        Assert.False(await cleanup.CleanupAsync(a.SessionId));
        var sandboxes = services.GetRequiredService<ISandboxStore>();
        var now = DateTimeOffset.UtcNow;
        var lease = new SandboxLease(a.SessionId, instance.InstanceId, Guid.NewGuid(), 1,
            new SandboxProfile("fixture", 1, "Fixture", "", true, "sha256:" + new string('a', 64)),
            SandboxLeaseState.NeedsReconciliation, "task-owned-unknown", now, now.AddMinutes(30));
        await sandboxes.SaveLeaseAsync(lease, 0);
        Assert.True(await runtime.RequestLifecycleTransitionAsync(SessionLifecycleStatus.Completed, LifecycleTransitionSource.User, "completed"));
        await runtime.WaitUntilIdleAsync();
        Assert.False(await cleanup.CleanupAsync(a.SessionId));
        Assert.Equal("A"u8.ToArray(), (await workspace.ReadAsync(a.SessionId, a.Definition, "/working/customer.txt")).Bytes);
        await sandboxes.SaveLeaseAsync(lease with { Revision = 2, State = SandboxLeaseState.Terminated }, 1);
        await cleanup.RecoverAsync();
        Assert.True(await cleanup.CleanupAsync(a.SessionId));
        Assert.Equal("NotFound", (await Assert.ThrowsAsync<AgentCoreException>(() => workspace.ListAsync(a.SessionId, a.Definition, "/working").AsTask())).Code);
        Assert.Equal("B"u8.ToArray(), (await workspace.ReadAsync(b.SessionId, b.Definition, "/working/customer.txt")).Bytes);
        Assert.Equal("home"u8.ToArray(), (await home.ReadAsync(instance.InstanceId, homeItem.LogicalPath)).Bytes);
        Assert.NotNull(await artifacts.GetAsync(a.SessionId, artifact.ArtifactId));
        var durable = (await services.GetRequiredService<IMemoryStore>().LoadAsync(a.SessionId))!;
        Assert.Contains(durable.Entries, e => e.Role == ConversationRole.Assistant && e.Status == EntryStatus.Completed);
        Assert.Single(await services.GetRequiredService<IAgentRunStore>().ListForSessionAsync(new(instance.InstanceId, durable.ProfileId!.Value), a.SessionId));
    }

    private sealed class SilentOutput : ISessionOutput
    {
        public ValueTask PublishAsync(SessionOutput output, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}
