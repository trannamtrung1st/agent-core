using System.Net;
using System.Net.Http.Json;
using AgentCore.Api.Http;
using Microsoft.AspNetCore.Builder;
using AgentCore.Application.Identity;
using AgentCore.Application.Ports;
using AgentCore.Contracts.Http;
using AgentCore.Domain.Definitions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed class AdminApiTests : IClassFixture<AdminSecretSentinelApiFactory>
{
    private readonly AdminSecretSentinelApiFactory _factory;

    public AdminApiTests(AdminSecretSentinelApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Admin_definitions_require_owner_capability()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/v2/admin/definitions");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Admin_definitions_reject_invalid_owner_capability()
    {
        var client = OwnerClient("invalid-owner-token");
        var response = await client.GetAsync("/api/v2/admin/definitions");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Admin_definitions_reject_non_trusted_remote_caller()
    {
        await using var factory = new RemoteCallerApiFactory();
        var client = TestOwnerCapability.CreateOwnerClient(factory);
        var response = await client.GetAsync("/api/v2/admin/definitions");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Admin_definitions_succeed_for_trusted_owner()
    {
        var client = OwnerClient();
        var response = await client.GetAsync("/api/v2/admin/definitions");
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<AdminDefinitionInventoryResponse>();
        Assert.NotNull(payload);
        Assert.Contains(payload!.Items, item => item.DefinitionId == "examiner");
    }

    [Fact]
    public async Task Admin_effective_config_resolves_exact_instance_state()
    {
        var client = OwnerClient();
        await using var scope = _factory.Services.CreateAsyncScope();
        var instances = scope.ServiceProvider.GetRequiredService<IAgentInstanceService>();
        var definitions = scope.ServiceProvider.GetRequiredService<IAgentDefinitionStore>();
        var definition = await definitions.GetAsync("examiner", 1);
        Assert.NotNull(definition);
        var managed = await instances.CreateAsync("examiner", 1);
        Assert.False(managed.Compatibility);
        var compatibility = await instances.ResolveCompatibilityAsync(definition!);

        var managedConfig = await client.GetFromJsonAsync<AdminEffectiveConfigurationResponse>(
            $"/api/v2/admin/instances/{managed.InstanceId:D}/effective-config");
        Assert.NotNull(managedConfig);
        Assert.Equal("builtIn", managedConfig!.DefinitionSource);
        Assert.Equal("examiner", managedConfig.DefinitionId);
        Assert.Equal(1, managedConfig.DefinitionVersion);
        Assert.False(managedConfig.Compatibility);
        Assert.Equal(managed.Persona.Name, managedConfig.Persona.Name);

        var compatibilityConfig = await client.GetFromJsonAsync<AdminEffectiveConfigurationResponse>(
            $"/api/v2/admin/instances/{compatibility.InstanceId:D}/effective-config");
        Assert.NotNull(compatibilityConfig);
        Assert.True(compatibilityConfig!.Compatibility);
    }

    [Fact]
    public async Task Admin_effective_config_returns_not_found_for_broken_definition_association()
    {
        var client = OwnerClient();
        await using var scope = _factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IAgentInstanceStore>();
        var ids = scope.ServiceProvider.GetRequiredService<IIdGenerator>();
        var now = DateTimeOffset.UtcNow;
        var broken = new AgentInstance(
            ids.NewId(),
            "examiner",
            9_999,
            new AgentIdentity("Broken", "role", "desc", "tone"),
            AgentInstanceLifecycle.Active,
            now,
            now,
            Compatibility: false);
        await store.InsertAsync(broken);

        var response = await client.GetAsync($"/api/v2/admin/instances/{broken.InstanceId:D}/effective-config");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Admin_responses_do_not_leak_secret_sentinels()
    {
        var client = OwnerClient();
        await using var scope = _factory.Services.CreateAsyncScope();
        var instances = scope.ServiceProvider.GetRequiredService<IAgentInstanceService>();
        var managed = await instances.CreateAsync("examiner", 1);
        var ownerToken = TestOwnerCapability.Token(_factory.Services);

        var definitions = await client.GetAsync("/api/v2/admin/definitions");
        var inventory = await client.GetAsync("/api/v2/admin/instances");
        var effective = await client.GetAsync($"/api/v2/admin/instances/{managed.InstanceId:D}/effective-config");
        definitions.EnsureSuccessStatusCode();
        inventory.EnsureSuccessStatusCode();
        effective.EnsureSuccessStatusCode();

        foreach (var response in new[] { definitions, inventory, effective })
        {
            var json = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain("OPENROUTER_SECRET_SENTINEL", json, StringComparison.Ordinal);
            Assert.DoesNotContain("OWNER_CAPABILITY_SENTINEL", json, StringComparison.Ordinal);
            Assert.DoesNotContain(ownerToken, json, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task User_catalog_agents_remain_unprotected_read()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/v1/agents");
        response.EnsureSuccessStatusCode();
    }

    private HttpClient OwnerClient(string? token = null)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            OwnerCapabilityHeaders.Name,
            token ?? TestOwnerCapability.Token(_factory.Services));
        return client;
    }
}

public sealed class AdminSecretSentinelApiFactory : AgentCoreApiFactory
{
    protected override IReadOnlyDictionary<string, string?> ExtraConfiguration =>
        new Dictionary<string, string?>
        {
            ["OPENROUTER_API_KEY"] = "OPENROUTER_SECRET_SENTINEL",
            ["Providers:LanguageModels:primary-llm:ApiKey"] = "OPENROUTER_SECRET_SENTINEL"
        };
}

public sealed class RemoteCallerApiFactory : AgentCoreApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
        {
            services.AddSingleton<Microsoft.AspNetCore.Hosting.IStartupFilter, RemoteCallerStartupFilter>();
        });
    }
}

internal sealed class RemoteCallerStartupFilter : Microsoft.AspNetCore.Hosting.IStartupFilter
{
    public Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> Configure(
        Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> next) =>
        app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                context.Connection.RemoteIpAddress = IPAddress.Parse("8.8.8.8");
                await nextMiddleware().ConfigureAwait(false);
            });
            next(app);
        };
}
