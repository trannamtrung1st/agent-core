using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentCore.Api.Http;
using Microsoft.AspNetCore.Builder;
using AgentCore.Application.Identity;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
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
    public async Task Admin_tools_lists_registered_tool_names()
    {
        var client = OwnerClient();
        var response = await client.GetAsync("/api/v2/admin/tools");
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<AdminToolRegistryResponse>();
        Assert.NotNull(payload);
        Assert.Contains(payload!.ToolNames, name => name == ToolCatalog.WorkspaceRead);
        Assert.Equal(payload.ToolNames.OrderBy(name => name, StringComparer.Ordinal), payload.ToolNames);
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
    public async Task Admin_can_archive_managed_instance_and_block_new_session()
    {
        var client = OwnerClient();
        var create = await client.PostAsJsonAsync(
            "/api/v2/admin/agent-instances",
            new AdminCreateAgentInstanceRequest("examiner", 1));
        create.EnsureSuccessStatusCode();
        var instance = await create.Content.ReadFromJsonAsync<AdminAgentInstanceResponse>();
        Assert.NotNull(instance);

        var archive = await client.PatchAsJsonAsync(
            $"/api/v2/admin/agent-instances/{instance!.InstanceId}/lifecycle",
            new AdminUpdateAgentInstanceLifecycleRequest(instance.Revision, "Archived"));
        archive.EnsureSuccessStatusCode();
        var archived = await archive.Content.ReadFromJsonAsync<AdminAgentInstanceResponse>();
        Assert.NotNull(archived);
        Assert.Equal("Archived", archived!.Lifecycle);

        var session = await client.PostAsJsonAsync(
            "/api/v2/sessions",
            new CreateSessionRequest(null, null, "text", AgentInstanceId: Guid.Parse(instance.InstanceId)));
        Assert.Equal(HttpStatusCode.BadRequest, session.StatusCode);
    }

    [Fact]
    public async Task Admin_can_update_managed_instance_persona_with_expected_revisions()
    {
        var client = OwnerClient();
        var create = await client.PostAsJsonAsync(
            "/api/v2/admin/agent-instances",
            new AdminCreateAgentInstanceRequest("examiner", 1));
        create.EnsureSuccessStatusCode();
        var instance = await create.Content.ReadFromJsonAsync<AdminAgentInstanceResponse>();
        Assert.NotNull(instance);

        var update = await client.PatchAsJsonAsync(
            $"/api/v2/admin/agent-instances/{instance!.InstanceId}/persona",
            new AdminUpdateAgentInstancePersonaRequest(
                instance.Revision,
                instance.PersonaRevision,
                instance.DefinitionId,
                "Guide",
                "Helps operators.",
                "Calm"));
        update.EnsureSuccessStatusCode();
        var updated = await update.Content.ReadFromJsonAsync<AdminAgentInstanceResponse>();
        Assert.NotNull(updated);
        Assert.Equal(2, updated!.PersonaRevision);
        Assert.Equal(2, updated.Revision);
    }

    [Fact]
    public async Task Admin_persona_update_rejects_unknown_properties()
    {
        var client = OwnerClient();
        var create = await client.PostAsJsonAsync(
            "/api/v2/admin/agent-instances",
            new AdminCreateAgentInstanceRequest("examiner", 1));
        create.EnsureSuccessStatusCode();
        var instance = await create.Content.ReadFromJsonAsync<AdminAgentInstanceResponse>();
        Assert.NotNull(instance);
        using var content = new StringContent(
            $$"""
            {
              "expectedRevision": {{instance!.Revision}},
              "expectedPersonaRevision": {{instance.PersonaRevision}},
              "name": "Examiner",
              "role": "Guide",
              "description": "Helps.",
              "tone": "Calm",
              "instanceId": "injected"
            }
            """,
            System.Text.Encoding.UTF8,
            "application/json");
        var response = await client.PatchAsync(
            $"/api/v2/admin/agent-instances/{instance.InstanceId}/persona",
            content);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Admin_persona_update_rejects_secret_sentinel_and_oversized_fields()
    {
        var client = OwnerClient();
        var create = await client.PostAsJsonAsync(
            "/api/v2/admin/agent-instances",
            new AdminCreateAgentInstanceRequest("examiner", 1));
        create.EnsureSuccessStatusCode();
        var instance = await create.Content.ReadFromJsonAsync<AdminAgentInstanceResponse>();
        Assert.NotNull(instance);

        var secret = await client.PatchAsJsonAsync(
            $"/api/v2/admin/agent-instances/{instance!.InstanceId}/persona",
            new AdminUpdateAgentInstancePersonaRequest(
                instance.Revision,
                instance.PersonaRevision,
                instance.DefinitionId,
                "Guide",
                "Helps.",
                "OPENROUTER_API_KEY"));
        Assert.Equal(HttpStatusCode.BadRequest, secret.StatusCode);

        var oversized = await client.PatchAsJsonAsync(
            $"/api/v2/admin/agent-instances/{instance.InstanceId}/persona",
            new AdminUpdateAgentInstancePersonaRequest(
                instance.Revision,
                instance.PersonaRevision,
                new string('n', 257),
                "Guide",
                "Helps.",
                "Calm"));
        Assert.Equal(HttpStatusCode.BadRequest, oversized.StatusCode);
    }

    [Fact]
    public async Task Admin_mutations_reject_stale_expected_revision_after_concurrent_change()
    {
        var client = OwnerClient();
        var create = await client.PostAsJsonAsync(
            "/api/v2/admin/agent-instances",
            new AdminCreateAgentInstanceRequest("examiner", 1));
        create.EnsureSuccessStatusCode();
        var instance = await create.Content.ReadFromJsonAsync<AdminAgentInstanceResponse>();
        Assert.NotNull(instance);
        var staleRevision = instance!.Revision;

        var persona = await client.PatchAsJsonAsync(
            $"/api/v2/admin/agent-instances/{instance.InstanceId}/persona",
            new AdminUpdateAgentInstancePersonaRequest(
                instance.Revision,
                instance.PersonaRevision,
                "Examiner",
                "Guide",
                "Helps.",
                "Edited"));
        persona.EnsureSuccessStatusCode();

        var staleLifecycle = await client.PatchAsJsonAsync(
            $"/api/v2/admin/agent-instances/{instance.InstanceId}/lifecycle",
            new AdminUpdateAgentInstanceLifecycleRequest(staleRevision, "Archived"));
        Assert.Equal(HttpStatusCode.Conflict, staleLifecycle.StatusCode);

        var staleVersion = await client.PatchAsJsonAsync(
            $"/api/v2/admin/agent-instances/{instance.InstanceId}/active-version",
            new AdminReassociateAgentInstanceVersionRequest(staleRevision, 1));
        Assert.Equal(HttpStatusCode.Conflict, staleVersion.StatusCode);
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
    public async Task Admin_definition_draft_create_returns_created_status()
    {
        var client = OwnerClient();
        var candidate = SampleDraftCandidate("p7b-create-status");
        var response = await client.PostAsJsonAsync(
            "/api/v2/admin/definition-drafts",
            new AdminCreateDefinitionDraftRequest(
                "p7b-create-status",
                JsonSerializer.SerializeToElement(candidate, JsonOptions())));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Admin_definition_draft_fork_returns_created_status()
    {
        var client = OwnerClient();
        var response = await client.PostAsJsonAsync(
            "/api/v2/admin/definition-drafts/fork",
            new AdminForkDefinitionDraftRequest("examiner", 1, "ForkBuiltIn"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
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
    public async Task Admin_definition_draft_resource_upload_bind_and_publish_snapshot()
    {
        var client = OwnerClient();
        const string definitionId = "p7c-resource-api";
        var create = await client.PostAsJsonAsync(
            "/api/v2/admin/definition-drafts",
            new AdminCreateDefinitionDraftRequest(
                definitionId,
                JsonSerializer.SerializeToElement(SampleDraftCandidate(definitionId), JsonOptions())));
        create.EnsureSuccessStatusCode();
        var draft = await create.Content.ReadFromJsonAsync<AdminDefinitionDraftResponse>();
        Assert.NotNull(draft);

        var contentBytes = "policy text for runtime"u8.ToArray();
        using var contentRequest = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v2/admin/definition-drafts/{draft!.DraftId}/resources/content")
        {
            Content = new ByteArrayContent(contentBytes)
        };
        contentRequest.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
        var storedResponse = await client.SendAsync(contentRequest);
        storedResponse.EnsureSuccessStatusCode();
        var stored = await storedResponse.Content.ReadFromJsonAsync<AdminDefinitionResourceContentStoredResponse>();
        Assert.NotNull(stored);

        var bind = await client.PutAsJsonAsync(
            $"/api/v2/admin/definition-drafts/{draft.DraftId}/resources",
            new AdminUpsertDefinitionDraftResourceRequest(
                draft.Revision,
                null,
                "knowledge/policy.md",
                "Knowledge",
                stored!.MediaType,
                stored.ContentSha256,
                stored.ByteLength));
        bind.EnsureSuccessStatusCode();
        var bound = await bind.Content.ReadFromJsonAsync<AdminDefinitionDraftResourceResponse>();
        Assert.NotNull(bound);

        var list = await client.GetFromJsonAsync<AdminDefinitionDraftResourceListResponse>(
            $"/api/v2/admin/definition-drafts/{draft.DraftId}/resources");
        Assert.NotNull(list);
        Assert.Single(list!.Items);
        Assert.Equal("knowledge/policy.md", list.Items[0].LogicalPath);

        var boundDraft = await client.GetFromJsonAsync<AdminDefinitionDraftResponse>(
            $"/api/v2/admin/definition-drafts/{draft.DraftId}");
        Assert.NotNull(boundDraft);
        var publish = await client.PostAsJsonAsync(
            $"/api/v2/admin/definition-drafts/{boundDraft!.DraftId}/publish",
            new AdminPublishDefinitionDraftRequest(boundDraft.Revision));
        publish.EnsureSuccessStatusCode();
        var publication = await publish.Content.ReadFromJsonAsync<AdminDefinitionPublicationSummaryResponse>();
        Assert.NotNull(publication);

        var publicationResources = await client.GetFromJsonAsync<AdminDefinitionPublicationResourceListResponse>(
            $"/api/v2/admin/definitions/{definitionId}/publications/{publication!.Version}/resources");
        Assert.NotNull(publicationResources);
        Assert.Single(publicationResources!.Items);
        Assert.Equal(stored.ContentSha256, publicationResources.Items[0].ContentSha256);
    }

    [Fact]
    public async Task Admin_resource_content_rejects_oversized_declared_length()
    {
        var client = OwnerClient();
        var draft = await CreateIsolatedDraftAsync(client, "p7c-oversize-declared");
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v2/admin/definition-drafts/{draft.DraftId}/resources/content")
        {
            Content = new ByteArrayContent([0x01])
        };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
        request.Content.Headers.ContentLength = AgentResourceLimits.MaxItemBytes + 1;
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Admin_resource_content_rejects_oversized_chunked_body()
    {
        var client = OwnerClient();
        var draft = await CreateIsolatedDraftAsync(client, "p7c-oversize-chunked");
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v2/admin/definition-drafts/{draft.DraftId}/resources/content")
        {
            Content = new StreamContent(new OversizeResourceStream(AgentResourceLimits.MaxItemBytes + 1))
        };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
        request.Content.Headers.ContentLength = null;
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Admin_resource_bind_rejects_invalid_resource_id_and_kind()
    {
        var client = OwnerClient();
        var draft = await CreateIsolatedDraftAsync(client, "p7c-bind-validation");
        var stored = await UploadDraftResourceContentAsync(client, draft.DraftId, "ok"u8.ToArray());

        var invalidId = await client.PutAsJsonAsync(
            $"/api/v2/admin/definition-drafts/{draft.DraftId}/resources",
            new AdminUpsertDefinitionDraftResourceRequest(
                draft.Revision,
                "not-a-guid",
                "refs/a.txt",
                "Reference",
                stored.MediaType,
                stored.ContentSha256,
                stored.ByteLength));
        Assert.Equal(HttpStatusCode.BadRequest, invalidId.StatusCode);

        var invalidKind = await client.PutAsJsonAsync(
            $"/api/v2/admin/definition-drafts/{draft.DraftId}/resources",
            new AdminUpsertDefinitionDraftResourceRequest(
                draft.Revision,
                null,
                "refs/a.txt",
                "999",
                stored.MediaType,
                stored.ContentSha256,
                stored.ByteLength));
        Assert.Equal(HttpStatusCode.BadRequest, invalidKind.StatusCode);

        var numericKind = await client.PutAsJsonAsync(
            $"/api/v2/admin/definition-drafts/{draft.DraftId}/resources",
            new AdminUpsertDefinitionDraftResourceRequest(
                draft.Revision,
                null,
                "refs/a.txt",
                "1",
                stored.MediaType,
                stored.ContentSha256,
                stored.ByteLength));
        Assert.Equal(HttpStatusCode.BadRequest, numericKind.StatusCode);

        var combinedKind = await client.PutAsJsonAsync(
            $"/api/v2/admin/definition-drafts/{draft.DraftId}/resources",
            new AdminUpsertDefinitionDraftResourceRequest(
                draft.Revision,
                null,
                "refs/a.txt",
                "Knowledge, Reference",
                stored.MediaType,
                stored.ContentSha256,
                stored.ByteLength));
        Assert.Equal(HttpStatusCode.BadRequest, combinedKind.StatusCode);
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

    [Fact]
    public async Task Managed_instance_session_reads_publication_resource_under_agent()
    {
        var client = OwnerClient();
        const string definitionId = "p7c-runtime-resource";
        var draft = await CreateIsolatedDraftAsync(client, definitionId);
        var contentBytes = "runtime-visible policy"u8.ToArray();
        var stored = await UploadDraftResourceContentAsync(client, draft.DraftId, contentBytes);
        var bind = await client.PutAsJsonAsync(
            $"/api/v2/admin/definition-drafts/{draft.DraftId}/resources",
            new AdminUpsertDefinitionDraftResourceRequest(
                draft.Revision,
                null,
                "knowledge/policy.md",
                "Knowledge",
                stored.MediaType,
                stored.ContentSha256,
                stored.ByteLength));
        bind.EnsureSuccessStatusCode();
        var boundDraft = await client.GetFromJsonAsync<AdminDefinitionDraftResponse>(
            $"/api/v2/admin/definition-drafts/{draft.DraftId}");
        Assert.NotNull(boundDraft);
        var publish = await client.PostAsJsonAsync(
            $"/api/v2/admin/definition-drafts/{boundDraft!.DraftId}/publish",
            new AdminPublishDefinitionDraftRequest(boundDraft.Revision));
        publish.EnsureSuccessStatusCode();
        var publication = await publish.Content.ReadFromJsonAsync<AdminDefinitionPublicationSummaryResponse>();
        Assert.NotNull(publication);

        var createInstance = await client.PostAsJsonAsync(
            "/api/v2/admin/agent-instances",
            new AdminCreateAgentInstanceRequest(definitionId, publication!.Version));
        createInstance.EnsureSuccessStatusCode();
        var instance = await createInstance.Content.ReadFromJsonAsync<AdminAgentInstanceResponse>();
        Assert.NotNull(instance);
        Assert.False(instance!.Compatibility);
        Assert.Equal(publication.Version, instance.ActiveVersion);

        var session = await client.PostAsJsonAsync(
            "/api/v2/sessions",
            new CreateSessionRequest(null, null, "text", AgentInstanceId: Guid.Parse(instance.InstanceId)));
        session.EnsureSuccessStatusCode();
        var view = await session.Content.ReadFromJsonAsync<SessionViewResponse>();
        Assert.NotNull(view);
        Assert.Equal(definitionId, view!.AgentId);
        Assert.Equal(publication.Version, view.AgentVersion);

        var listed = await client.GetFromJsonAsync<WorkspaceNodeResponse[]>(
            $"/api/v2/sessions/{view.SessionId}/workspace?prefix=/agent");
        Assert.NotNull(listed);
        Assert.Contains(listed!, node => node.LogicalPath == "/agent/resources");

        var bytes = await client.GetByteArrayAsync(
            $"/api/v2/sessions/{view.SessionId}/workspace/content?path=/agent/resources/knowledge/policy.md");
        Assert.Equal("runtime-visible policy", System.Text.Encoding.UTF8.GetString(bytes));

        var both = await client.PostAsJsonAsync(
            "/api/v2/sessions",
            new CreateSessionRequest("examiner", 1, "text", AgentInstanceId: Guid.Parse(instance.InstanceId)));
        Assert.Equal(HttpStatusCode.BadRequest, both.StatusCode);
    }

    private static async Task<AdminDefinitionDraftResponse> CreateIsolatedDraftAsync(
        HttpClient client,
        string definitionId)
    {
        var create = await client.PostAsJsonAsync(
            "/api/v2/admin/definition-drafts",
            new AdminCreateDefinitionDraftRequest(
                definitionId,
                JsonSerializer.SerializeToElement(SampleDraftCandidate(definitionId), JsonOptions())));
        create.EnsureSuccessStatusCode();
        return (await create.Content.ReadFromJsonAsync<AdminDefinitionDraftResponse>(JsonOptions()))!;
    }

    private static async Task<AdminDefinitionResourceContentStoredResponse> UploadDraftResourceContentAsync(
        HttpClient client,
        string draftId,
        byte[] bytes)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v2/admin/definition-drafts/{draftId}/resources/content")
        {
            Content = new ByteArrayContent(bytes)
        };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AdminDefinitionResourceContentStoredResponse>(JsonOptions()))!;
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

internal sealed class OversizeResourceStream(long totalBytes) : Stream
{
    private long _remaining = totalBytes;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_remaining <= 0)
        {
            return 0;
        }

        var toRead = (int)Math.Min(count, _remaining);
        Array.Fill(buffer, (byte)'x', offset, toRead);
        _remaining -= toRead;
        return toRead;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        return Task.FromResult(Read(buffer, offset, count));
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
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
