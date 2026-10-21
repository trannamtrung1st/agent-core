using AgentCore.Domain.Conversation;
using System.Net;
using System.Net.Http.Json;
using AgentCore.Contracts.Http;
using AgentCore.Infrastructure.Sandbox;
using AgentCore.Application.Ports;
using AgentCore.Domain.Definitions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Configuration;

namespace AgentCore.Api.Tests;

public sealed class SandboxApiTests
{

    private sealed class ReadySandbox : ISandboxReadiness
    { public bool IsAvailable=>true;public string State=>"Ready";public string SafeMessage=>"Deterministic fixture"; }

    [Fact]
    public async Task Historical_drafts_require_explicit_owner_repair_and_never_restore_retired_execution()
    {
        await using var factory = new SandboxFactory();
        var lifecycle = factory.Services.GetRequiredService<AgentCore.Application.Admin.AgentDefinitionLifecycleService>();
        var source = (await factory.Services.GetRequiredService<IAgentDefinitionStore>().GetAsync("general-assistant", 21))!;
        Assert.Contains("sandbox.run", RoleEnvironments.Of(source).ToolList);
        var draft = await lifecycle.ForkDraftAsync(source.Id, source.Version, DefinitionDraftSourceKind.ForkBuiltIn);
        var rejected = await Assert.ThrowsAsync<AgentCore.Application.Sessions.AgentCoreException>(()=>lifecycle.UpdateDraftAsync(
            draft.DraftId,draft.Revision,draft.Candidate with {AgentInstructions="Explicit owner edit."}).AsTask());
        Assert.Equal(400,rejected.StatusCode);
        var repaired = await lifecycle.UpdateDraftAsync(draft.DraftId,draft.Revision,
            HarnessLegacyFixture.WithoutRetiredSandboxTool(draft.Candidate) with {AgentInstructions="Explicit owner edit."});
        Assert.Equal(draft.Revision+1,repaired.Revision);
        Assert.DoesNotContain("sandbox.run",repaired.Candidate.Environment!.ToolList);
        Assert.DoesNotContain("sandbox.exec",repaired.Candidate.Environment.ToolList);
        Assert.Null(repaired.Candidate.SandboxPolicy);
        Assert.Contains("sandbox.run",RoleEnvironments.Of((await factory.Services.GetRequiredService<IAgentDefinitionStore>().GetAsync(source.Id,source.Version))!).ToolList);
    }

    private sealed class SandboxFactory(OpenSandboxOptions? options = null) : AgentCoreApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(s => { s.RemoveAll<OpenSandboxOptions>(); s.AddSingleton(options ?? new OpenSandboxOptions { AllowLocalImageIds=true }); });
        }
    }

    private sealed class UnconfiguredSandboxFactory : AgentCoreApiFactory
    {
        protected override IReadOnlyDictionary<string, string?> ExtraConfiguration =>
            new Dictionary<string, string?>
            {
                ["OpenSandbox:ApiKey"] = ""
            };
    }
}
