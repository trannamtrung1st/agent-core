using AgentCore.Application.Ports;
using AgentCore.Infrastructure;
using AgentCore.Infrastructure.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentCore.Infrastructure.Tests;

public sealed class DiagnosticIdSourceTests
{
    [Fact]
    public void Diagnostic_ids_do_not_dequeue_the_business_id_generator()
    {
        var businessId = Guid.Parse("019944af-00d7-7000-8000-0000000000b1");
        var sessionId = Guid.Parse("019944af-00d7-7000-8000-0000000000a1");
        var business = new DeterministicIdGenerator([businessId], [sessionId]);
        var diagnostics = new SystemDiagnosticIdSource();

        var first = diagnostics.NewId();
        var second = diagnostics.NewId();

        Assert.NotEqual(Guid.Empty, first);
        Assert.NotEqual(first, second);
        Assert.NotEqual(businessId, first);
        Assert.NotEqual(businessId, second);
        Assert.Equal(businessId, business.NewId());
        Assert.Equal(sessionId, business.NewSessionId());
    }

    [Fact]
    public void Infrastructure_registers_the_diagnostic_source_beside_the_id_generator()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddAgentCoreInfrastructure(FindAgents(), "Synthetic");
        using var provider = services.BuildServiceProvider();

        var diagnostics = provider.GetRequiredService<IDiagnosticIdSource>();
        var business = provider.GetRequiredService<IIdGenerator>();
        Assert.IsType<SystemDiagnosticIdSource>(diagnostics);
        Assert.IsType<SystemIdGenerator>(business);
        Assert.NotSame(diagnostics, business);
    }

    private static string FindAgents()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "AgentCore.sln")))
            {
                return Path.Combine(dir.FullName, "agents");
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Repository root was not found.");
    }
}
