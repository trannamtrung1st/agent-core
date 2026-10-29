using System.Net;
using System.Net.Http.Json;
using AgentCore.Contracts.Http;

namespace AgentCore.Api.Tests;

public sealed partial class TenantCredentialAccessTests
{
    [Fact]
    public async Task DynamicModels_HTTP_setup_preserves_explicit_default_and_safe_tenant_configuration()
    {
        await using var factory = new AgentCoreApiFactory(); using var owner = Client(factory);
        await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
        var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid();
        var root = $"/api/v2/tenants/{tenant}";
        using var anonymous = Client(factory);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(root + "/models")).StatusCode);
        var empty = await Json(await owner.GetAsync(root + "/models"));
        Assert.Empty(empty.GetProperty("connections").EnumerateArray()); Assert.Empty(empty.GetProperty("offerings").EnumerateArray());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, empty.GetProperty("settings").GetProperty("defaultOfferingId").ValueKind);
        Assert.Equal(7, empty.GetProperty("presets").GetArrayLength());
        Assert.True(empty.GetProperty("access").GetProperty("canCreateConnection").GetBoolean());
        Assert.True(empty.GetProperty("access").GetProperty("canManageDefault").GetBoolean());
        var created = await Json(await owner.PostAsJsonAsync(root + "/credentials", new TenantCredentialCreateRequest("Model key", "ApiKey", "ModelInference",
            await Revision(owner, tenant), ProtectedValue: "http-private-sentinel")));
        var credential = created.GetProperty("credential").GetProperty("credentialId").GetGuid();
        Assert.Equal(System.Text.Json.JsonValueKind.Null, created.GetProperty("lastRotatedAt").ValueKind);
        var connectionResponse = await owner.PostAsJsonAsync(root + "/model-connections", new ModelConnectionRequest("OpenAI", "Team API", null, credential, false, true, await Revision(owner, tenant), 0));
        Assert.Equal(HttpStatusCode.Created, connectionResponse.StatusCode);
        var connection = await Json(connectionResponse); var connectionId = connection.GetProperty("connectionId").GetGuid();
        Assert.Equal("https://api.openai.com/v1/", connection.GetProperty("endpoint").GetString());
        Safe(connection);
        Assert.Equal(connectionId, (await Json(await owner.GetAsync(root + $"/model-connections/{connectionId}"))).GetProperty("connectionId").GetGuid());
        var preset = empty.GetProperty("presets").EnumerateArray().First(p => p.GetProperty("kind").GetString() == "OpenAI");
        var capability = new ModelCapabilityInput(true, true, "NativeDeveloper", "NativeStructuredOutput", true, true, ["low", "medium", "high"], "medium");
        var offeringResponse = await owner.PostAsJsonAsync(root + "/models", new ModelOfferingRequest(connectionId, preset.GetProperty("presetId").GetGuid(),
            preset.GetProperty("externalModelId").GetString()!, "Team model", "Responses", capability, true, false, await Revision(owner, tenant), 0));
        Assert.Equal(HttpStatusCode.Created, offeringResponse.StatusCode);
        var offering = await Json(offeringResponse); var offeringId = offering.GetProperty("offeringId").GetGuid();
        Assert.Equal(offeringId, (await Json(await owner.GetAsync(root + $"/models/{offeringId}"))).GetProperty("offeringId").GetGuid());
        var saved = await owner.PutAsJsonAsync(root + "/models/default", new TenantModelDefaultRequest(offeringId, "medium", await Revision(owner, tenant), 0));
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var reload = await Json(await owner.GetAsync(root + "/models"));
        Assert.Equal(offeringId, reload.GetProperty("settings").GetProperty("defaultOfferingId").GetGuid()); Safe(reload);
        Assert.True(reload.GetProperty("availability")[0].GetProperty("available").GetBoolean());
        Assert.Equal("available", reload.GetProperty("availability")[0].GetProperty("reason").GetString());
        Assert.Equal(connectionId, reload.GetProperty("access").GetProperty("testConnectionIds")[0].GetGuid());
        var tested = await Json(await owner.PostAsJsonAsync(root + $"/model-connections/{connectionId}/test", new ModelMetadataTestRequest(await Revision(owner, tenant), 1)));
        Assert.True(tested.GetProperty("metadataAvailable").GetBoolean()); Assert.Equal(0, tested.GetProperty("modelsObserved").GetInt32());
        Assert.Equal("synthetic_metadata_available", tested.GetProperty("result").GetString());
        var refreshed = await Json(await owner.PostAsJsonAsync(root + $"/model-connections/{connectionId}/discover", new ModelMetadataTestRequest(await Revision(owner, tenant), 1)));
        Assert.Equal(2, refreshed.GetProperty("modelsObserved").GetInt32());
        var discovered = await Json(await owner.GetAsync(root + "/models"));
        Assert.Equal(offeringId, discovered.GetProperty("settings").GetProperty("defaultOfferingId").GetGuid());
        Assert.False(discovered.GetProperty("offerings").EnumerateArray().Single(o => o.GetProperty("offeringId").GetGuid() != offeringId).GetProperty("enabled").GetBoolean());
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PutAsJsonAsync(root + "/models/default", new TenantModelDefaultRequest(offeringId, "low", await Revision(owner, tenant), 0))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync(root + "/model-connections", new { kind = "OpenAI", label = "Implicit flags", expectedRevision = await Revision(owner, tenant), expectedConnectionRevision = 0 })).StatusCode);
        var beta = (await Json(await owner.PostAsJsonAsync("/api/v2/system/tenants", new { name = "Beta", slug = "beta" }))).GetProperty("tenantId").GetGuid();
        var foreign = await owner.PostAsJsonAsync($"/api/v2/tenants/{beta}/model-connections", new ModelConnectionRequest("OpenAI", "Guessed", null, credential, false, true, await Revision(owner, beta), 0));
        Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync($"/api/v2/tenants/{beta}/model-connections/{connectionId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync($"/api/v2/tenants/{beta}/models/{offeringId}")).StatusCode);
        var betaModels = await Json(await owner.GetAsync($"/api/v2/tenants/{beta}/models"));
        Assert.Empty(betaModels.GetProperty("connections").EnumerateArray()); Assert.Empty(betaModels.GetProperty("offerings").EnumerateArray());
        var agent = await Json(await owner.PostAsJsonAsync(root + "/agents", new { templateId = "examiner", version = 1, expectedRevision = await Revision(owner, tenant) }));
        var instance = agent.GetProperty("instance").GetProperty("instanceId").GetGuid();
        var instanceRevision = agent.GetProperty("instance").GetProperty("revision").GetInt64();
        var selectable = await Json(await owner.GetAsync(root + "/chat/models?instanceId=" + instance.ToString("D")));
        Assert.Equal(offeringId.ToString("D"), selectable.GetProperty("defaultOfferingId").GetString());
        Assert.Equal("medium", selectable.GetProperty("defaultReasoningEffort").GetString());
        var choice = selectable.GetProperty("models").EnumerateArray().Single(m => m.GetProperty("offeringId").GetString() == offeringId.ToString("D"));
        Assert.Equal(connectionId.ToString("D"), choice.GetProperty("connectionId").GetString());
        Assert.Equal("Team API", choice.GetProperty("connectionLabel").GetString());
        Assert.False(selectable.TryGetProperty("defaultKey", out _)); Assert.False(choice.TryGetProperty("key", out _)); Assert.False(choice.TryGetProperty("providerAlias", out _));
        foreach (var retired in new[] { "default", "primary-llm", "scripted-alpha" })
            Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync(root + "/sessions", new { agentInstanceId = instance,
                expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = instanceRevision, offeringId = retired })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync(root + "/sessions", new { agentInstanceId = instance,
            expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = instanceRevision, modelKey = "default" })).StatusCode);
        var session = await Json(await owner.PostAsJsonAsync(root + "/sessions", new { agentInstanceId = instance,
            expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = instanceRevision, retentionKind = "saved", modelPreference = new { mode = "fixed", offeringId, reasoning = new { mode = "fixed", effort = "high" } } }));
        var savedSelection = (await Json(await owner.GetAsync(root + "/sessions/" + session.GetProperty("sessionId").GetString() + "/conversation"))).GetProperty("model");
        Assert.Equal(offeringId.ToString("D"), savedSelection.GetProperty("offeringId").GetString());
        Assert.Equal(connectionId.ToString("D"), savedSelection.GetProperty("connectionId").GetString());
        Assert.Equal("high", savedSelection.GetProperty("reasoningEffort").GetString()); Safe(savedSelection);
        // Chat discovery uses the same sparse Instance/Definition model precedence as admission.
        Assert.Equal("tenantDefault", selectable.GetProperty("defaultSource").GetString());
        var configuredAgent = await Json(await owner.PatchAsJsonAsync(root + $"/agents/{instance}/settings/modelDefaults", new {
            expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = instanceRevision,
            set = new { preference = new { mode = "fixed", offeringId = offeringId.ToString("D"), reasoning = new { mode = "fixed", effort = "high" } } }, clear = Array.Empty<string>() }));
        instanceRevision = configuredAgent.GetProperty("instance").GetProperty("revision").GetInt64();
        var inheritedCatalog = await Json(await owner.GetAsync(root + "/chat/models?instanceId=" + instance.ToString("D")));
        Assert.Equal(offeringId.ToString("D"), inheritedCatalog.GetProperty("defaultOfferingId").GetString());
        Assert.Equal("high", inheritedCatalog.GetProperty("defaultReasoningEffort").GetString());
        Assert.Equal("agentDefault", inheritedCatalog.GetProperty("defaultSource").GetString());
        var inheritedSession = await Json(await owner.PostAsJsonAsync(root + "/sessions", new { agentInstanceId = instance,
            expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = instanceRevision, retentionKind = "saved" }));
        var inheritedModel = (await Json(await owner.GetAsync(root + "/sessions/" + inheritedSession.GetProperty("sessionId").GetString() + "/conversation"))).GetProperty("model");
        Assert.Equal("agentDefault", inheritedModel.GetProperty("selectionSource").GetString());
        Assert.Equal("high", inheritedModel.GetProperty("reasoningEffort").GetString());
        var explicitSession = await Json(await owner.PostAsJsonAsync(root + "/sessions", new { agentInstanceId = instance,
            expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = instanceRevision, retentionKind = "saved", modelPreference = new { mode = "fixed", offeringId } }));
        var explicitModel = (await Json(await owner.GetAsync(root + "/sessions/" + explicitSession.GetProperty("sessionId").GetString() + "/conversation"))).GetProperty("model");
        Assert.Equal("user", explicitModel.GetProperty("selectionSource").GetString());
        Assert.Equal("medium", explicitModel.GetProperty("reasoningEffort").GetString());
        var explicitId = explicitSession.GetProperty("sessionId").GetGuid();
        var currentSession = await Json(await owner.GetAsync(root + $"/sessions/{explicitId}"));
        var switchWitness = new TenantSessionModelRequest(await Revision(owner, tenant), currentSession.GetProperty("revision").GetInt64(),
            new("fixed", "dca30000-0000-4000-8000-000000000002"));
        var switched = await Json(await owner.PutAsJsonAsync(root + $"/sessions/{explicitId}/model", switchWitness));
        Assert.Equal("dca30000-0000-4000-8000-000000000002", switched.GetProperty("model").GetProperty("offeringId").GetString());
        Assert.Equal("user", switched.GetProperty("model").GetProperty("selectionSource").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PutAsJsonAsync(root + $"/sessions/{explicitId}/model", switchWitness)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.PutAsJsonAsync($"/api/v2/tenants/{beta}/sessions/{explicitId}/model", switchWitness)).StatusCode);
        var inheritedAgain = await Json(await owner.PutAsJsonAsync(root + $"/sessions/{explicitId}/model", new TenantSessionModelRequest(
            await Revision(owner, tenant), switched.GetProperty("revision").GetInt64())));
        Assert.Equal("agentDefault", inheritedAgain.GetProperty("model").GetProperty("selectionSource").GetString());
        Assert.Equal("high", inheritedAgain.GetProperty("model").GetProperty("reasoningEffort").GetString());
        var paused = await Json(await owner.PostAsJsonAsync(root + $"/sessions/{explicitId}/pause", new {
            expectedRevision = await Revision(owner, tenant), expectedSessionRevision = inheritedAgain.GetProperty("revision").GetInt64() }));
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PutAsJsonAsync(root + $"/sessions/{explicitId}/model", new TenantSessionModelRequest(
            await Revision(owner, tenant), paused.GetProperty("revision").GetInt64(), new("fixed", offeringId.ToString("D"))))).StatusCode);
        var defaultReferences = await Json(await owner.GetAsync(root + $"/models/{offeringId}/references?kind=default&pageSize=1"));
        Assert.Equal(1, defaultReferences.GetProperty("total").GetInt32());
        Assert.Equal(offeringId, defaultReferences.GetProperty("items")[0].GetProperty("offeringId").GetGuid());
        var instanceReferences = await Json(await owner.GetAsync(root + $"/model-connections/{connectionId}/references?kind=instances"));
        var directory = await Json(await owner.GetAsync(root + "/chat/agents"));
        var managed = Assert.Single(directory.GetProperty("items").EnumerateArray(), item => item.GetProperty("systemManaged").GetBoolean())
            .GetProperty("instanceId").GetGuid();
        Assert.Equal(2, instanceReferences.GetProperty("total").GetInt32());
        Assert.Equal(new[] { instance, managed }.Order(), instanceReferences.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("resourceId").GetGuid()).Order());
        Safe(instanceReferences);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.GetAsync(root + $"/models/{offeringId}/references?kind=unsupported")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.GetAsync(root + $"/models/{offeringId}/references?kind=instances&page=-1")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync($"/api/v2/tenants/{beta}/models/{offeringId}/references?kind=instances")).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await owner.DeleteAsync(root + $"/models/{offeringId}")).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await owner.DeleteAsync(root + $"/model-connections/{connectionId}")).StatusCode);
        var presets = await Json(await owner.GetAsync("/api/v2/system/model-presets")); Assert.Equal(7, presets.GetArrayLength()); Safe(presets);
        var systemPreset = presets.EnumerateArray().Single(p => p.GetProperty("presetId").GetGuid() == preset.GetProperty("presetId").GetGuid());
        Assert.Equal(1, systemPreset.GetProperty("offeringReferences").GetInt32());
        var systemPresetRead = await Json(await owner.GetAsync("/api/v2/system/model-presets/" + preset.GetProperty("presetId").GetString()));
        Assert.Equal(1, systemPresetRead.GetProperty("offeringReferences").GetInt32());
        Assert.All(discovered.GetProperty("presets").EnumerateArray(), p => Assert.True(!p.TryGetProperty("offeringReferences", out var count) || count.ValueKind == System.Text.Json.JsonValueKind.Null));
        var rotated = await Json(await owner.PutAsJsonAsync(root + $"/credentials/{credential}/value", new TenantCredentialReplaceRequest("replacement-http-private-sentinel", await Revision(owner, tenant), 1)));
        var rotationTime = rotated.GetProperty("lastRotatedAt").GetDateTimeOffset().ToUnixTimeMilliseconds();
        await Json(await owner.PatchAsJsonAsync(root + $"/credentials/{credential}", new TenantCredentialUpdateRequest("Renamed model key", "Active", await Revision(owner, tenant), 2)));
        var keyRead = await Json(await owner.GetAsync(root + $"/credentials/{credential}"));
        Assert.Equal(rotationTime, keyRead.GetProperty("lastRotatedAt").GetDateTimeOffset().ToUnixTimeMilliseconds());
        Assert.DoesNotContain("replacement-http-private-sentinel", keyRead.GetRawText());
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync($"/api/v2/tenants/{beta}/credentials/{credential}")).StatusCode);

    }
}
