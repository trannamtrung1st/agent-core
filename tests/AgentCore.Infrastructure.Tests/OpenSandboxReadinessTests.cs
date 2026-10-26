using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Sandbox;
using AgentCore.Infrastructure.Tools;
using AgentCore.Application.Tools;

namespace AgentCore.Infrastructure.Tests;

public sealed class OpenSandboxReadinessTests
{
    [Fact]
    public async Task Authenticated_host_readiness_does_not_depend_on_the_retired_global_profile_catalog()
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-sandbox-readiness-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders();
            await using var host = builder.Build(); host.Urls.Add("http://127.0.0.1:0");
            host.Run(async context => { Assert.Equal("fixture-key", context.Request.Headers["OPEN-SANDBOX-API-KEY"]); await context.Response.WriteAsJsonAsync(new { items = Array.Empty<object>() }); });
            await host.StartAsync();
            var options = new OpenSandboxOptions { Enabled = true, Endpoint = host.Urls.Single(), ApiKey = "fixture-key",
                CanonicalWorkspaceRoot = root, WorkspaceDockerHostRoot = root };
            var readiness = new OpenSandboxReadiness(options);
            await readiness.RefreshAsync(default);
            Assert.Equal("Ready", readiness.State); Assert.True(readiness.IsAvailable);
            Assert.True(new ToolConfigurationGate(null, null, sandbox: readiness).IsConfigured(ToolCatalog.SandboxExec));
            options.Enabled = false; await readiness.RefreshAsync(default); Assert.False(readiness.IsAvailable);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Verified_dotnet_profile_with_missing_mount_configuration_is_not_execution_ready_but_inspection_is_available()
    {
        var store = new InMemorySandboxStore();
        var profile = new SandboxProfile("dotnet",1,".NET Workbench","",true,"sha256:"+new string('a',64));
        await store.SaveProfileAsync(profile,0);
        await store.SaveVerificationAsync(new(profile.Id,1,DateTimeOffset.UtcNow,true,"Current-contract fixture",ContractVersion:SandboxProfileVerification.CurrentContractVersion));
        var options = new OpenSandboxOptions { Enabled=true,AllowLocalImageIds=true,Endpoint="http://127.0.0.1:1",ApiKey="fixture" };
        var readiness = new OpenSandboxReadiness(options);
        await readiness.RefreshAsync(default);
        Assert.Equal("MountIncompatible",readiness.State);Assert.False(readiness.IsAvailable);Assert.Contains("mount configuration",readiness.SafeMessage);
        Assert.DoesNotContain("127.0.0.1",readiness.SafeMessage);Assert.DoesNotContain("fixture",readiness.SafeMessage);
        var gate = new ToolConfigurationGate(null,null,sandbox:readiness);
        Assert.True(gate.IsExecutionConfigured(ToolCatalog.SandboxStatus));Assert.True(gate.IsExecutionConfigured(ToolCatalog.SandboxProcessStatus));
        Assert.True(gate.IsConfigured(ToolCatalog.SandboxProfiles));Assert.False(gate.IsExecutionConfigured(ToolCatalog.SandboxExec));
    }
}
