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
    public async Task Retired_Sandbox_candidate_requires_explicit_repair_without_rewriting_immutable_source()
    {
        await using var factory = new SandboxFactory();
        var source = (await factory.Services.GetRequiredService<IAgentDefinitionStore>().GetAsync("general-assistant", 21))!;
        Assert.Contains("sandbox.run", DefinitionExecutionPolicies.Of(source).ToolList);
        var candidate = AgentDefinitionCandidate.FromDefinition(source);
        var aliases = factory.Services.GetRequiredService<ProviderAliasSet>();
        var rejected = Assert.Throws<AgentCore.Application.Sessions.AgentCoreException>(() =>
            AgentCore.Application.Admin.AgentDefinitionCandidateValidator.ValidateForPersistence(candidate with { AgentInstructions = "Explicit repair." }, aliases));
        Assert.Equal(400, rejected.StatusCode);
        var repaired = HarnessLegacyFixture.WithoutRetiredSandboxTool(candidate) with { AgentInstructions = "Explicit repair." };
        AgentCore.Application.Admin.AgentDefinitionCandidateValidator.ValidateForPersistence(repaired, aliases);
        Assert.DoesNotContain("sandbox.run", repaired.ExecutionPolicy!.ToolList); Assert.DoesNotContain("sandbox.exec", repaired.ExecutionPolicy.ToolList);
        Assert.Null(repaired.SandboxPolicy);
        Assert.Contains("sandbox.run",DefinitionExecutionPolicies.Of((await factory.Services.GetRequiredService<IAgentDefinitionStore>().GetAsync(source.Id,source.Version))!).ToolList);
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
