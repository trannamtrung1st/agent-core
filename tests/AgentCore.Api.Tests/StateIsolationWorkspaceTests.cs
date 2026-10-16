using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentCore.Application.Admin;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Tools;
using AgentCore.Application.Workspaces;
using AgentCore.Domain.Conversation;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed class StateIsolationWorkspaceTests
{
    [Fact]
    public async Task Tightened_home_policy_blocks_old_run_reads_and_all_mutation_routes_while_preserving_owner_bytes()
    {
        await using var factory = new AgentCoreApiFactory();
        using var client = TestOwnerCapability.CreateOwnerClient(factory);
        var services = factory.Services;
        var instance = await services.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 31);
        var session = await services.GetRequiredService<SessionManager>().CreateForInstanceAsync(instance.InstanceId, SessionMode.Text);
        var configuration = (await services.GetRequiredService<AgentRunConfigurationResolver>().ResolveAsync(instance.InstanceId)).Configuration;
        var home = services.GetRequiredService<AgentInstanceWorkspaceService>();
        var item = await home.WriteAsync(session.SessionId, "/home/customer.txt", "text/plain", "customer-A"u8.ToArray());
        var tool = services.GetRequiredService<SessionToolExecutor>();
        var admission = new ToolExecutionAdmission(false, TriggerKind.UserTurn, AgentInstanceId: instance.InstanceId, OwnedSessionId: session.SessionId, WorkspaceCwd: "/home");
        async Task<string> Execute(string name, object args) => (await tool.ExecuteAsync(configuration.Definition, session.SessionId,
            new(Guid.NewGuid().ToString("N"), name, JsonSerializer.Serialize(args)), 16_000, admission: admission)).Text;
        var path = $"/api/v2/admin/agent-instances/{instance.InstanceId}/settings/stateIsolationPolicy";
        var section = (await client.GetFromJsonAsync<InstanceSettingsSection>(path))!;
        Assert.Equal("ReadWrite", section.Effective["homeAccess"]?.ToString());
        var readOnly = await client.PatchAsJsonAsync(path, new { expectedInstanceRevision = section.InstanceRevision, set = new { homeAccess = "ReadOnly" }, clear = Array.Empty<string>() });
        readOnly.EnsureSuccessStatusCode();
        Assert.Contains("customer-A", await Execute(ToolCatalog.WorkspaceRead, new { path = "/home/customer.txt" }));
        Assert.Contains("forbidden", (await Execute(ToolCatalog.WorkspaceWrite, new { path = "/home/new.txt", content = "no" })).ToLowerInvariant());
        Assert.Contains("forbidden", (await Execute(ToolCatalog.WorkspaceCopy, new { source = "/working/new.txt", destination = "/home/copied.txt" })).ToLowerInvariant());
        Assert.Equal("Forbidden", (await Assert.ThrowsAsync<AgentCoreException>(() => home.CommitBinaryAsync(session.SessionId, "/home/indirect.bin", "application/octet-stream", new byte[] { 1 }).AsTask())).Code);
        var updated = (await readOnly.Content.ReadFromJsonAsync<InstanceSettingsSection>())!;
        var disabled = await client.PatchAsJsonAsync(path, new { expectedInstanceRevision = updated.InstanceRevision, set = new { homeAccess = "Disabled" }, clear = Array.Empty<string>() });
        disabled.EnsureSuccessStatusCode();
        foreach (var name in new[] { ToolCatalog.WorkspaceRead, ToolCatalog.WorkspaceList, ToolCatalog.ArtifactsCreateFromWorkspace })
            Assert.Contains("forbidden", (await Execute(name, new { path = "/home/customer.txt" })).ToLowerInvariant());
        Assert.Contains("/working/note.txt", await Execute(ToolCatalog.WorkspaceWrite, new { path = "note.txt", content = "session-A" }));
        Assert.Contains("/working", await Execute(ToolCatalog.WorkspaceCwd, new { operation = "get" }));
        Assert.Contains("forbidden", (await Execute(ToolCatalog.WorkspaceCwd, new { operation = "set", path = "/home" })).ToLowerInvariant());
        Assert.Contains("forbidden", (await Execute(ToolCatalog.WorkspaceCopy, new { source = "/home/customer.txt", destination = "/working/copy.txt" })).ToLowerInvariant());
        Assert.Equal("customer-A"u8.ToArray(), (await home.ReadAsync(instance.InstanceId, item.LogicalPath)).Bytes);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PatchAsJsonAsync(path, new { expectedInstanceRevision = updated.InstanceRevision, set = new { homeAccess = "ReadWrite" }, clear = Array.Empty<string>() })).StatusCode);
    }
}
