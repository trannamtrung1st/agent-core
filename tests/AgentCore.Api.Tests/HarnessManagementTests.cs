using System.Net;
using System.Net.Http.Json;
using AgentCore.Application.Admin;
using AgentCore.Contracts.Http;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed class HarnessManagementTests
{
    [Fact]
    public async Task Owner_governance_enables_scopes_and_freezes_without_a_preparation_workflow()
    {
        await using var factory = new AgentCoreApiFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(OwnerCapabilityHeaders.Name, TestOwnerCapability.Token(factory.Services));
        var instance = await factory.Services.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 21);
        var url = $"/api/v2/admin/agent-instances/{instance.InstanceId}/harness";
        var configured = await client.PutAsJsonAsync(url + "/policy", new HarnessPolicyRequest(instance.Revision, "Managed", ["KnowledgeResources"], [], []));
        configured.EnsureSuccessStatusCode();
        var review = (await configured.Content.ReadFromJsonAsync<HarnessReviewResponse>())!;
        Assert.Equal("Managed", review.Policy.Mode);
        Assert.Empty(review.Policy.Sources);
        Assert.Null(review.Preparation);
        var frozen = await client.PutAsJsonAsync(url + "/policy", new HarnessPolicyRequest(review.InstanceRevision, "Disabled", [], [], [], Frozen: true));
        frozen.EnsureSuccessStatusCode();
        review = (await frozen.Content.ReadFromJsonAsync<HarnessReviewResponse>())!;
        Assert.True(review.Policy.Frozen);
        Assert.Equal(21, review.ActiveVersion);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync(url + "/continue", new HarnessRunRequest(Guid.NewGuid().ToString()))).StatusCode);
    }
}
