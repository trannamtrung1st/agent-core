using System.Text.Json;
using AgentCore.Application.Admin;
using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Definitions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentCore.Api.Tests;

public sealed class HarnessChatAuthoringTests
{


    private sealed class BrowserUnavailableFactory : AgentCoreApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IToolConfigurationGate>();
                services.AddSingleton(ToolConfigurationGates.Unconfigured);
            });
        }
    }

    [Fact]
    public void Harness_approval_shows_the_actual_semantic_operation_and_untruncated_change()
    {
        using var content = JsonDocument.Parse(JsonSerializer.Serialize(new { content = new string('x', 600) + " exact tail", expectedVersion = 21, policyRevision = 1 }));
        Assert.EndsWith("exact tail", ToolApprovalPreview.Build("harness.instructions.update", content.RootElement).Details["Change"]);
        using var tool = JsonDocument.Parse("""{"id":"http.request","enabled":false,"content":"misleading unrelated content","expectedVersion":7,"policyRevision":1}""");
        var preview = ToolApprovalPreview.Build("harness.tool.select", tool.RootElement);
        Assert.Contains("http.request", preview.Details["Change"]);
        Assert.DoesNotContain("misleading", preview.Details["Change"]);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("not json")]
    [InlineData("{\"error\":\"source_denied\",\"content\":\"not obtained\"}")]
    [InlineData("{\"finalUrl\":\"https://example.test\"}")]
    public void Failed_empty_or_non_object_results_never_create_source_receipts(string result)
    {
        Assert.Empty(HarnessChatTools.Sources(new("read", "web.fetch", """{"url":"https://example.test"}"""), result));
    }

    private static IToolConfigurationGate executorGate(IServiceProvider services) => services.GetRequiredService<IToolConfigurationGate>();
}
