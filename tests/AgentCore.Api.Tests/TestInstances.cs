using System.Net.Http.Json;
using AgentCore.Contracts.Http;
using AgentCore.Application.Ports;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

internal static class TestInstances
{
    // Provision through Admin before building an instance-owned Session request.
    // A separate request header preserves the caller's authorization test state.
    public static Guid Create(HttpClient client, string definitionId, int? version)
    {
        if (TestOwnerCapability.TryGetServices(client, out var services))
            return services!.GetRequiredService<IAgentInstanceService>().CreateAsync(definitionId, version)
                .AsTask().GetAwaiter().GetResult().InstanceId;
        throw new InvalidOperationException("Instance fixtures require an explicitly registered test service provider.");
    }
}
