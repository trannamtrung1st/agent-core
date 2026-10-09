using System.Net;
using System.Net.Http.Json;
using AgentCore.Contracts.Http;

namespace AgentCore.Api.Tests;

public sealed class CredentialApiTests : IClassFixture<AgentCoreApiFactory>
{
    private readonly AgentCoreApiFactory _factory;
    public CredentialApiTests(AgentCoreApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Owner_shared_resource_journey_has_no_value_readback_and_enforces_lifecycle()
    {
        using var client = TestOwnerCapability.CreateOwnerClient(_factory);
        var a = TestInstances.Create(client, "secretary", 8);
        var b = TestInstances.Create(client, "secretary", 8);
        var aPath = $"/api/v2/admin/agent-instances/{a}/credential-bindings";
        var bPath = $"/api/v2/admin/agent-instances/{b}/credential-bindings";
        const string initial = "api-private-value-9475";
        var created = await client.PostAsJsonAsync("/api/v2/admin/credentials", new AdminCreateCredentialRequest("Shared store", "Password", new Dictionary<string,string> { ["username"] = "owner@example.test" }, ["https://store.example.test"], initial));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        AssertSafe(await created.Content.ReadAsStringAsync(), initial);
        var credential = (await created.Content.ReadFromJsonAsync<AdminCredentialResponse>())!;
        var path = $"/api/v2/admin/credentials/{credential.CredentialId}";
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await client.GetAsync(path + "/value")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v2/admin/agent-instances/{a}/connection")).StatusCode);
        var bindA = await client.PostAsJsonAsync(aPath, new AdminBindCredentialRequest(credential.CredentialId, "PRIMARY", 1));
        Assert.Equal(HttpStatusCode.Created, bindA.StatusCode);
        var grantA = (await bindA.Content.ReadFromJsonAsync<AdminCredentialBindingResponse>())!;
        var bindB = await client.PostAsJsonAsync(bPath, new AdminBindCredentialRequest(credential.CredentialId, "shared", 1));
        Assert.Equal(HttpStatusCode.Created, bindB.StatusCode);
        Assert.Contains("primary", await client.GetStringAsync(aPath));
        Assert.DoesNotContain("shared\"", await client.GetStringAsync(aPath));
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(aPath, new AdminBindCredentialRequest(credential.CredentialId, "second", 1))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync(bPath + $"/{grantA.BindingId}?expectedRevision=1&expectedInstanceRevision=1")).StatusCode);
        var replaced = await client.PutAsJsonAsync(path + "/value", new AdminReplaceCredentialRequest(1, "replacement-private-9386"));
        Assert.True(replaced.IsSuccessStatusCode);
        AssertSafe(await replaced.Content.ReadAsStringAsync(), initial, "replacement-private-9386");
        credential = (await replaced.Content.ReadFromJsonAsync<AdminCredentialResponse>())!;
        Assert.Equal(2, credential.BindingCount);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync(path + "/value", new AdminReplaceCredentialRequest(1, "stale"))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.DeleteAsync(path + "?expectedRevision=2")).StatusCode);
        var disabled = await client.PatchAsJsonAsync(path, new AdminUpdateCredentialRequest(2, credential.DisplayName, "Disabled", credential.Metadata, credential.AllowedOrigins));
        Assert.True(disabled.IsSuccessStatusCode);
        Assert.Contains("Disabled", await client.GetStringAsync(aPath));
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"/api/v2/admin/agent-instances/{a}/browser-profile/reset", new AdminResetBrowserProfileRequest(1, false))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync($"/api/v2/admin/agent-instances/{a}/browser-profile/reset", new AdminResetBrowserProfileRequest(1, true))).StatusCode);
        Assert.Contains("primary", await client.GetStringAsync(aPath));
        var archived = await client.PatchAsJsonAsync($"/api/v2/admin/agent-instances/{a}/lifecycle", new AdminUpdateAgentInstanceLifecycleRequest(1, "Archived"));
        Assert.True(archived.IsSuccessStatusCode);
        Assert.True((await client.GetAsync(aPath)).IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.DeleteAsync(aPath + $"/{grantA.BindingId}?expectedRevision=1&expectedInstanceRevision=2")).StatusCode);
        using var deletion = new HttpRequestMessage(HttpMethod.Delete, $"/api/v2/admin/agent-instances/{a}") { Content = JsonContent.Create(new { expectedRevision = 2 }) };
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(deletion)).StatusCode);
        credential = (await client.GetFromJsonAsync<AdminCredentialResponse>(path))!;
        Assert.Equal(1, credential.BindingCount);
        Assert.Contains("shared", await client.GetStringAsync(bPath));
        var grantB = (await bindB.Content.ReadFromJsonAsync<AdminCredentialBindingResponse>())!;
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(bPath + $"/{grantB.BindingId}?expectedRevision=1&expectedInstanceRevision=1")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(path + "?expectedRevision=3")).StatusCode);
    }

    [Fact]
    public async Task Every_surface_requires_owner_and_invalid_kind_origin_and_empty_values_fail_safely()
    {
        using var anonymous = _factory.CreateClient();
        var id = Guid.NewGuid();
        foreach (var (method, path) in new[] { (HttpMethod.Get, "/credentials"), (HttpMethod.Post, "/credentials"), (HttpMethod.Get, $"/credentials/{id}"), (HttpMethod.Patch, $"/credentials/{id}"), (HttpMethod.Put, $"/credentials/{id}/value"), (HttpMethod.Delete, $"/credentials/{id}?expectedRevision=1"), (HttpMethod.Get, $"/agent-instances/{id}/credential-bindings"), (HttpMethod.Post, $"/agent-instances/{id}/credential-bindings"), (HttpMethod.Delete, $"/agent-instances/{id}/credential-bindings/{id}?expectedRevision=1&expectedInstanceRevision=1"), (HttpMethod.Post, $"/agent-instances/{id}/browser-profile/reset") })
        {
            using var request = new HttpRequestMessage(method, "/api/v2/admin" + path) { Content = JsonContent.Create(new { }) };
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.SendAsync(request)).StatusCode);
        }
        using var client = TestOwnerCapability.CreateOwnerClient(_factory);
        foreach (var input in new[] { new AdminCreateCredentialRequest("Invalid", "Custom", null, [], "private"), new AdminCreateCredentialRequest("Invalid", "0", null, [], "private"), new AdminCreateCredentialRequest("Invalid", "Password", null, ["https://store.test/path"], "private"), new AdminCreateCredentialRequest("Invalid", "Password", null, [], "") })
        {
            var response = await client.PostAsJsonAsync("/api/v2/admin/credentials", input);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            AssertSafe(await response.Content.ReadAsStringAsync(), "private");
        }
    }
    private static void AssertSafe(string json, params string[] values)
    {
        foreach (var value in values) Assert.DoesNotContain(value, json);
        Assert.DoesNotContain("protectedPayload", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("protectionVersion", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("protectedValue", json, StringComparison.OrdinalIgnoreCase);
    }
}
