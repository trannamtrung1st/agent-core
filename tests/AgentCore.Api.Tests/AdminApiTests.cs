using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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

    [Fact]
    public async Task Admin_definition_draft_create_rejects_invalid_candidate_with_validation_status()
    {
        var client = OwnerClient();
        var candidate = SampleDraftCandidate("demo-agent") with { SchemaVersion = 2 };
        var response = await client.PostAsJsonAsync(
            "/api/v2/admin/definition-drafts",
            new AdminCreateDefinitionDraftRequest("demo-agent", JsonSerializer.SerializeToElement(candidate, JsonOptions())));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Admin_definition_draft_create_rejects_null_identity_without_server_error()
    {
        var client = OwnerClient();
        using var content = new StringContent(
            """
            {
              "definitionId": "demo-agent",
              "candidate": {
                "schemaVersion": 1,
                "definitionId": "demo-agent",
                "identity": null,
                "goals": ["Help"],
                "systemInstructions": "You are a demo agent.",
                "behaviorPolicy": { "interruptionStyle": "acknowledgeThenContinue", "acknowledgeInterruption": true, "avoidUnsupportedClaims": true },
                "conversationPolicy": { "responseLength": "concise", "askOneQuestionAtATime": true, "language": "en", "maxOutputTokens": 512 },
                "initiativePolicy": { "enabled": false, "silenceThresholdMs": 30000, "cooldownMs": 60000, "maxPerSilencePeriod": 1, "triggers": [] },
                "voice": { "enabled": false, "voiceId": "alloy", "speakingRate": 1.0 },
                "providerPreferences": { "languageModel": "primary-llm", "interruptionClassifier": "heuristic" },
                "metadata": {}
              }
            }
            """,
            System.Text.Encoding.UTF8,
            "application/json");
        var response = await client.PostAsync("/api/v2/admin/definition-drafts", content);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Admin_definition_draft_create_rejects_secret_in_goals()
    {
        var client = OwnerClient();
        var candidate = SampleDraftCandidate("demo-agent") with { Goals = ["OPENROUTER_API_KEY"] };
        var response = await client.PostAsJsonAsync(
            "/api/v2/admin/definition-drafts",
            new AdminCreateDefinitionDraftRequest("demo-agent", JsonSerializer.SerializeToElement(candidate, JsonOptions())));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Admin_definition_draft_create_allows_benign_hyphenated_goal_text()
    {
        var client = OwnerClient();
        var candidate = SampleDraftCandidate("demo-agent") with { Goals = ["Stay risk-aware and concise."] };
        var response = await client.PostAsJsonAsync(
            "/api/v2/admin/definition-drafts",
            new AdminCreateDefinitionDraftRequest("demo-agent", JsonSerializer.SerializeToElement(candidate, JsonOptions())));
        response.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Admin_definition_draft_create_rejects_openrouter_shaped_key_in_goals()
    {
        var client = OwnerClient();
        var candidate = SampleDraftCandidate("demo-agent") with
        {
            Goals = ["Use key sk-or-v1-00000000000000000000000000000000 for routing."]
        };
        var response = await client.PostAsJsonAsync(
            "/api/v2/admin/definition-drafts",
            new AdminCreateDefinitionDraftRequest("demo-agent", JsonSerializer.SerializeToElement(candidate, JsonOptions())));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Admin_definition_draft_create_rejects_openai_project_key_in_system_instructions()
    {
        var client = OwnerClient();
        var candidate = SampleDraftCandidate("demo-agent") with
        {
            SystemInstructions = "Bearer sk-proj-00000000000000000000000000000000"
        };
        var response = await client.PostAsJsonAsync(
            "/api/v2/admin/definition-drafts",
            new AdminCreateDefinitionDraftRequest("demo-agent", JsonSerializer.SerializeToElement(candidate, JsonOptions())));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Admin_definition_draft_publish_rejects_unknown_model_catalog_key()
    {
        var client = OwnerClient();
        var fork = await client.PostAsJsonAsync(
            "/api/v2/admin/definition-drafts/fork",
            new AdminForkDefinitionDraftRequest("examiner", 1, "ForkBuiltIn"));
        fork.EnsureSuccessStatusCode();
        var draft = await fork.Content.ReadFromJsonAsync<AdminDefinitionDraftResponse>();
        Assert.NotNull(draft);
        var candidate = draft!.Candidate.Deserialize<AgentDefinitionCandidate>(JsonOptions())!;
        candidate = candidate with { ModelDefaults = new AgentModelDefaults("missing-catalog-key", null) };
        var update = await client.PutAsJsonAsync(
            $"/api/v2/admin/definition-drafts/{draft.DraftId}",
            new AdminUpdateDefinitionDraftRequest(1, JsonSerializer.SerializeToElement(candidate, JsonOptions())));
        update.EnsureSuccessStatusCode();
        var updated = await update.Content.ReadFromJsonAsync<AdminDefinitionDraftResponse>();
        var publish = await client.PostAsJsonAsync(
            $"/api/v2/admin/definition-drafts/{updated!.DraftId}/publish",
            new AdminPublishDefinitionDraftRequest(updated.Revision));
        Assert.Equal(HttpStatusCode.BadRequest, publish.StatusCode);
    }

    [Fact]
    public async Task Admin_definition_draft_publish_rejects_unknown_tool_name()
    {
        var client = OwnerClient();
        var fork = await client.PostAsJsonAsync(
            "/api/v2/admin/definition-drafts/fork",
            new AdminForkDefinitionDraftRequest("examiner", 1, "ForkBuiltIn"));
        fork.EnsureSuccessStatusCode();
        var draft = await fork.Content.ReadFromJsonAsync<AdminDefinitionDraftResponse>();
        var candidate = draft!.Candidate.Deserialize<AgentDefinitionCandidate>(JsonOptions())!;
        var environment = candidate.Environment ?? RoleEnvironment.Empty;
        candidate = candidate with
        {
            Environment = environment with
            {
                ToolAllowlist = environment.ToolList.Concat(["not.a.registered.tool"]).ToArray()
            }
        };
        var update = await client.PutAsJsonAsync(
            $"/api/v2/admin/definition-drafts/{draft!.DraftId}",
            new AdminUpdateDefinitionDraftRequest(1, JsonSerializer.SerializeToElement(candidate, JsonOptions())));
        update.EnsureSuccessStatusCode();
        var updated = await update.Content.ReadFromJsonAsync<AdminDefinitionDraftResponse>();
        var publish = await client.PostAsJsonAsync(
            $"/api/v2/admin/definition-drafts/{updated!.DraftId}/publish",
            new AdminPublishDefinitionDraftRequest(updated.Revision));
        Assert.Equal(HttpStatusCode.BadRequest, publish.StatusCode);
    }

    [Fact]
    public async Task Admin_publication_deprecate_rejects_builtin_version()
    {
        var client = OwnerClient();
        var response = await client.PostAsJsonAsync(
            "/api/v2/admin/definitions/examiner/publications/1/deprecate",
            new AdminDeprecateDefinitionPublicationRequest(1));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Admin_publication_deprecate_marks_durable_version_deprecated()
    {
        var client = OwnerClient();
        const string definitionId = "p7b-deprecate-isolated";
        var candidate = SampleDraftCandidate(definitionId);
        var create = await client.PostAsJsonAsync(
            "/api/v2/admin/definition-drafts",
            new AdminCreateDefinitionDraftRequest(
                definitionId,
                JsonSerializer.SerializeToElement(candidate, JsonOptions())));
        create.EnsureSuccessStatusCode();
        var draft = await create.Content.ReadFromJsonAsync<AdminDefinitionDraftResponse>();
        Assert.NotNull(draft);
        var publish = await client.PostAsJsonAsync(
            $"/api/v2/admin/definition-drafts/{draft!.DraftId}/publish",
            new AdminPublishDefinitionDraftRequest(draft.Revision));
        publish.EnsureSuccessStatusCode();
        var publication = await publish.Content.ReadFromJsonAsync<AdminDefinitionPublicationSummaryResponse>();
        Assert.NotNull(publication);

        var deprecate = await client.PostAsJsonAsync(
            $"/api/v2/admin/definitions/{definitionId}/publications/{publication!.Version}/deprecate",
            new AdminDeprecateDefinitionPublicationRequest(publication.MetadataRevision));
        deprecate.EnsureSuccessStatusCode();
        var deprecated = await deprecate.Content.ReadFromJsonAsync<AdminDefinitionPublicationSummaryResponse>();
        Assert.NotNull(deprecated);
        Assert.Equal("Deprecated", deprecated!.Status);

        await using var scope = _factory.Services.CreateAsyncScope();
        var definitions = scope.ServiceProvider.GetRequiredService<IAgentDefinitionStore>();
        var exact = await definitions.GetAsync(definitionId, publication.Version);
        var latest = await definitions.GetAsync(definitionId);
        Assert.NotNull(exact);
        Assert.Null(latest);
        Assert.Equal("You are a demo agent.", exact!.SystemInstructions);
    }

    [Fact]
    public async Task Admin_definitions_inventory_marks_durable_publication_source()
    {
        var client = OwnerClient();
        var fork = await client.PostAsJsonAsync(
            "/api/v2/admin/definition-drafts/fork",
            new AdminForkDefinitionDraftRequest("examiner", 1, "ForkBuiltIn"));
        fork.EnsureSuccessStatusCode();
        var draft = await fork.Content.ReadFromJsonAsync<AdminDefinitionDraftResponse>();
        var candidate = draft!.Candidate.Deserialize<AgentDefinitionCandidate>(JsonOptions())!;
        candidate = candidate with { SystemInstructions = candidate.SystemInstructions + "\nDurable inventory marker." };
        var update = await client.PutAsJsonAsync(
            $"/api/v2/admin/definition-drafts/{draft!.DraftId}",
            new AdminUpdateDefinitionDraftRequest(1, JsonSerializer.SerializeToElement(candidate, JsonOptions())));
        update.EnsureSuccessStatusCode();
        var updated = await update.Content.ReadFromJsonAsync<AdminDefinitionDraftResponse>();
        var publish = await client.PostAsJsonAsync(
            $"/api/v2/admin/definition-drafts/{updated!.DraftId}/publish",
            new AdminPublishDefinitionDraftRequest(updated.Revision));
        publish.EnsureSuccessStatusCode();
        var publication = await publish.Content.ReadFromJsonAsync<AdminDefinitionPublicationSummaryResponse>();
        Assert.NotNull(publication);

        var inventory = await client.GetFromJsonAsync<AdminDefinitionInventoryResponse>("/api/v2/admin/definitions");
        Assert.NotNull(inventory);
        var durable = inventory!.Items.Single(item =>
            item.DefinitionId == "examiner" && item.Version == publication.Version);
        Assert.Equal("durable", durable.Source, StringComparer.OrdinalIgnoreCase);
        Assert.Equal("published", durable.Status, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Admin_definition_draft_fork_publish_assigns_next_version()
    {
        var client = OwnerClient();
        var fork = await client.PostAsJsonAsync(
            "/api/v2/admin/definition-drafts/fork",
            new AdminForkDefinitionDraftRequest("examiner", 1, "ForkBuiltIn"));
        fork.EnsureSuccessStatusCode();
        var draft = await fork.Content.ReadFromJsonAsync<AdminDefinitionDraftResponse>();
        Assert.NotNull(draft);
        Assert.Equal(1, draft!.Revision);

        var candidate = draft.Candidate.Deserialize<AgentDefinitionCandidate>(JsonOptions());
        Assert.NotNull(candidate);
        candidate = candidate! with { SystemInstructions = candidate.SystemInstructions + "\nAdmin durable edit." };
        var update = await client.PutAsJsonAsync(
            $"/api/v2/admin/definition-drafts/{draft.DraftId}",
            new AdminUpdateDefinitionDraftRequest(1, JsonSerializer.SerializeToElement(candidate, JsonOptions())));
        update.EnsureSuccessStatusCode();
        var updated = await update.Content.ReadFromJsonAsync<AdminDefinitionDraftResponse>();
        Assert.NotNull(updated);
        Assert.Equal(2, updated!.Revision);

        var publish = await client.PostAsJsonAsync(
            $"/api/v2/admin/definition-drafts/{draft.DraftId}/publish",
            new AdminPublishDefinitionDraftRequest(2));
        publish.EnsureSuccessStatusCode();
        var publication = await publish.Content.ReadFromJsonAsync<AdminDefinitionPublicationSummaryResponse>();
        Assert.NotNull(publication);
        Assert.True(publication!.Version > 1);

        var publications = await client.GetFromJsonAsync<AdminDefinitionPublicationListResponse>(
            "/api/v2/admin/definitions/examiner/publications");
        Assert.NotNull(publications);
        Assert.Contains(publications!.Items, item => item.Version == publication.Version);

        await using var scope = _factory.Services.CreateAsyncScope();
        var definitions = scope.ServiceProvider.GetRequiredService<IAgentDefinitionStore>();
        var resolved = await definitions.GetAsync("examiner", publication.Version);
        Assert.NotNull(resolved);
        Assert.Contains("Admin durable edit.", resolved!.SystemInstructions, StringComparison.Ordinal);
    }

    private static JsonSerializerOptions JsonOptions() =>
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };

    private static AgentDefinitionCandidate SampleDraftCandidate(string definitionId) =>
        new(
            1,
            definitionId,
            new AgentIdentity("Demo", "Guide", "Helps with demos.", "Calm"),
            ["Help the user"],
            "You are a demo agent.",
            new BehaviorPolicy("acknowledgeThenContinue", true, true),
            new ConversationPolicy("concise", true, "en", 512),
            new InitiativePolicy(false, 30_000, 60_000, 1, []),
            new VoiceConfiguration(false, "alloy", 1.0),
            new ProviderPreferences("primary-llm", null, null),
            new Dictionary<string, string>());

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
