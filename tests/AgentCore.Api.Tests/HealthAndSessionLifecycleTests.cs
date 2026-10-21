using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Xml.Linq;
using AgentCore.Api.Http;
using AgentCore.Contracts.Http;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed class HealthAndSessionLifecycleTests : IClassFixture<AgentCoreApiFactory>
{
    private readonly AgentCoreApiFactory _factory;

    public HealthAndSessionLifecycleTests(AgentCoreApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Health_is_synthetic_and_performs_no_outbound_http()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/health");
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<HealthResponse>();
        Assert.Equal("healthy", body!.Status);
        Assert.Equal("Synthetic", body.Profile);
        Assert.Equal(1, body.ProtocolVersion);
        Assert.Equal(typeof(Program).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .Cast<System.Reflection.AssemblyInformationalVersionAttribute>().Single().InformationalVersion, body.BuildVersion);
        Assert.Equal(0, _factory.Services.GetRequiredService<OutboundHttpProbe>().Attempts);
    }

    [Fact]
    public void Project_reference_graph_matches_docs_11()
    {
        var root = FindRepoRoot();
        Assert.DoesNotContain("Microsoft.AspNetCore", File.ReadAllText(Path.Combine(root, "src/AgentCore.Domain/AgentCore.Domain.csproj")));
        Assert.DoesNotContain("ProjectReference Include=\"..\\AgentCore.Infrastructure", File.ReadAllText(Path.Combine(root, "src/AgentCore.Application/AgentCore.Application.csproj")));
        Assert.DoesNotContain("ProjectReference Include=\"..\\AgentCore.Contracts", File.ReadAllText(Path.Combine(root, "src/AgentCore.Application/AgentCore.Application.csproj")));
        Assert.DoesNotContain("Contracts", File.ReadAllText(Path.Combine(root, "src/AgentCore.Infrastructure/AgentCore.Infrastructure.csproj")));
        Assert.Contains("AgentCore.Domain", File.ReadAllText(Path.Combine(root, "src/AgentCore.Application/AgentCore.Application.csproj")));
        Assert.Contains("AgentCore.Application", File.ReadAllText(Path.Combine(root, "src/AgentCore.Infrastructure/AgentCore.Infrastructure.csproj")));
        Assert.Contains("AgentCore.Contracts", File.ReadAllText(Path.Combine(root, "src/AgentCore.Api/AgentCore.Api.csproj")));

        var domainRefs = PackageOrProjectRefs(Path.Combine(root, "src/AgentCore.Domain/AgentCore.Domain.csproj"));
        Assert.Empty(domainRefs);
    }

    [Fact]
    public void Frontend_and_contracts_do_not_embed_provider_credentials()
    {
        var root = FindRepoRoot();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "web"), "*", SearchOption.AllDirectories)
                     .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                                    && !path.Contains($"{Path.DirectorySeparatorChar}dist{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
        {
            if (new FileInfo(file).Length > 2_000_000)
            {
                continue;
            }

            var text = File.ReadAllText(file);
            Assert.DoesNotContain("OPENROUTER_API_KEY", text, StringComparison.Ordinal);
            Assert.DoesNotContain("OPENAI_API_KEY", text, StringComparison.Ordinal);
        }

        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src/AgentCore.Contracts"), "*.cs", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("OPENROUTER_API_KEY", text, StringComparison.Ordinal);
            Assert.DoesNotContain("ApiKey", text, StringComparison.Ordinal);
        }
    }

    private static IReadOnlyList<string> PackageOrProjectRefs(string path)
    {
        var document = XDocument.Load(path);
        return document.Descendants()
            .Where(element => element.Name.LocalName is "PackageReference" or "ProjectReference")
            .Select(element => element.Attribute("Include")?.Value ?? string.Empty)
            .Where(value => value.Length > 0)
            .ToArray();
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "AgentCore.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException();
    }
}
