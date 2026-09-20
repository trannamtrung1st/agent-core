using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AgentCore.Contracts.Http;

namespace AgentCore.Api.Tests;

public sealed class SessionModelApiTests : IClassFixture<AgentCoreApiFactory>
{
    private readonly AgentCoreApiFactory _factory;

    public SessionModelApiTests(AgentCoreApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Catalog_exposes_safe_descriptors_without_secrets()
    {
        var client = TestOwnerCapability.CreateOwnerClient(_factory);
        var catalog = await client.GetFromJsonAsync<ModelCatalogResponse>("/api/v2/models");
        Assert.Equal("scripted-alpha", catalog!.DefaultKey);
        Assert.Contains(catalog.Models, model => model.Key == "scripted-alpha" && model.Reasoning && !model.StructuredOutput);
        Assert.Contains(catalog.Models, model => model.Key == "scripted-beta" && !model.Reasoning && model.StructuredOutput);
        var json = await (await client.GetAsync("/api/v2/models")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("apiKey", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Authorization", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("baseUrl", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("providerAlias", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Create_without_model_uses_the_system_default()
    {
        var client = TestOwnerCapability.CreateOwnerClient(_factory);
        var created = await client.PostAsJsonAsync("/api/v2/sessions", new CreateSessionRequest("examiner", 1, "text"));
        created.EnsureSuccessStatusCode();
        var view = await created.Content.ReadFromJsonAsync<SessionViewResponse>();
        Assert.Equal("scripted-alpha", view!.Model!.CatalogKey);
        Assert.Equal("Scripted Alpha", view.Model.DisplayName);
        Assert.Equal("systemDefault", view.Model.SelectionSource);
        Assert.Equal("medium", view.Model.ReasoningEffort);
        Assert.Equal("scripted-alpha", view.Model.ModelId);
    }

    [Fact]
    public async Task Create_with_explicit_model_persists_it()
    {
        var client = TestOwnerCapability.CreateOwnerClient(_factory);
        var created = await client.PostAsJsonAsync(
            "/api/v2/sessions",
            new CreateSessionRequest("examiner", 1, "text", Model: new SessionModelChoiceRequest("scripted-beta")));
        created.EnsureSuccessStatusCode();
        var view = await created.Content.ReadFromJsonAsync<SessionViewResponse>();
        Assert.Equal("scripted-beta", view!.Model!.CatalogKey);
        Assert.Equal("user", view.Model.SelectionSource);
        Assert.Null(view.Model.ReasoningEffort);
    }

    [Fact]
    public async Task Host_create_can_provide_model_selection()
    {
        var client = TestOwnerCapability.CreateOwnerClient(_factory);
        var created = await client.PostAsJsonAsync(
            "/api/v2/host/sessions",
            new HostCreateSessionRequest(
                "examiner",
                1,
                "text",
                Model: new SessionModelChoiceRequest("scripted-alpha", "high")));
        created.EnsureSuccessStatusCode();
        var host = await created.Content.ReadFromJsonAsync<HostSessionViewResponse>();
        Assert.Equal("scripted-alpha", host!.Model!.CatalogKey);
        Assert.Equal("host", host.Model.SelectionSource);
        Assert.Equal("high", host.Model.ReasoningEffort);
    }

    [Fact]
    public async Task Browser_body_cannot_supply_provider_routing()
    {
        var client = TestOwnerCapability.CreateOwnerClient(_factory);
        var created = await client.PostAsync(
            "/api/v2/sessions",
            new StringContent(
                """
                {"agentId":"examiner","agentVersion":1,"mode":"text","model":{"key":"scripted-alpha","providerAlias":"evil","modelId":"secret-model","apiKey":"sk-test","baseUrl":"https://evil.example"}}
                """,
                Encoding.UTF8,
                "application/json"));
        created.EnsureSuccessStatusCode();
        var json = await created.Content.ReadAsStringAsync();
        var view = JsonSerializer.Deserialize<SessionViewResponse>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.Equal("scripted-alpha", view!.Model!.CatalogKey);
        Assert.Equal("scripted-alpha", view.Model.ModelId);
        Assert.DoesNotContain("sk-test", json, StringComparison.Ordinal);
        Assert.DoesNotContain("evil.example", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Session_model_mutation_persists_catalog_choice()
    {
        var client = TestOwnerCapability.CreateOwnerClient(_factory);
        var created = await client.PostAsJsonAsync("/api/v2/sessions", new CreateSessionRequest("examiner", 1, "text"));
        var view = await created.Content.ReadFromJsonAsync<SessionViewResponse>();
        var changed = await client.PostAsJsonAsync(
            $"/api/v2/sessions/{view!.SessionId}/model",
            new SetSessionModelRequest("scripted-beta"));
        changed.EnsureSuccessStatusCode();
        var next = await changed.Content.ReadFromJsonAsync<SessionViewResponse>();
        Assert.Equal("scripted-beta", next!.Model!.CatalogKey);
        Assert.Equal("user", next.Model.SelectionSource);
        var loaded = await client.GetFromJsonAsync<SessionViewResponse>($"/api/v2/sessions/{view.SessionId}");
        Assert.Equal("scripted-beta", loaded!.Model!.CatalogKey);
    }

    [Fact]
    public async Task Ended_session_cannot_change_model()
    {
        var client = TestOwnerCapability.CreateOwnerClient(_factory);
        var created = await client.PostAsJsonAsync("/api/v2/sessions", new CreateSessionRequest("examiner", 1, "text"));
        var view = await created.Content.ReadFromJsonAsync<SessionViewResponse>();
        var ended = await client.DeleteAsync($"/api/v1/sessions/{view!.SessionId}");
        ended.EnsureSuccessStatusCode();
        var changed = await client.PostAsJsonAsync(
            $"/api/v2/sessions/{view.SessionId}/model",
            new SetSessionModelRequest("scripted-beta"));
        Assert.Equal(HttpStatusCode.BadRequest, changed.StatusCode);
        var loaded = await client.GetFromJsonAsync<SessionViewResponse>($"/api/v2/sessions/{view.SessionId}");
        Assert.Equal("scripted-alpha", loaded!.Model!.CatalogKey);
    }

    [Fact]
    public async Task Catalog_requires_owner_capability()
    {
        var client = _factory.CreateClient();
        var denied = await client.GetAsync("/api/v2/models");
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
    }
}
