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
        var token = client.DefaultRequestHeaders.TryGetValues(OwnerCapabilityHeaders.Name, out var values)
            ? values.Single()
            : client.PostAsJsonAsync("/api/v1/local/owner-capability", new { }).GetAwaiter().GetResult()
                .Content.ReadFromJsonAsync<OwnerCapabilityResponse>().GetAwaiter().GetResult()!.Token;
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v2/admin/agent-instances")
        {
            Content = JsonContent.Create(new AdminCreateAgentInstanceRequest(definitionId, version ?? 1))
        };
        request.Headers.Add(OwnerCapabilityHeaders.Name, token);
        using var response = client.SendAsync(request).GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();
        return Guid.Parse(response.Content.ReadFromJsonAsync<AdminAgentInstanceResponse>().GetAwaiter().GetResult()!.InstanceId);
    }
}
