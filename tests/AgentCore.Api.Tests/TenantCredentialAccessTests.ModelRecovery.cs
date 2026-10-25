using System.Net;
using System.Net.Http.Json;
using AgentCore.Contracts.Http;

namespace AgentCore.Api.Tests;

public sealed partial class TenantCredentialAccessTests
{
    [Fact]
    public async Task DynamicModels_HTTP_test_only_member_cannot_discover_or_restore_omitted_manual_default()
    {
        await using var factory = new AgentCoreApiFactory(); using var owner = Client(factory);
        await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
        var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid();
        var root = $"/api/v2/tenants/{tenant}";
        var key = await Json(await owner.PostAsJsonAsync(root + "/credentials", new TenantCredentialCreateRequest("Inference", "ApiKey", "ModelInference",
            await Revision(owner, tenant), ProtectedValue: "synthetic-model-key")));
        var connection = await Json(await owner.PostAsJsonAsync(root + "/model-connections", new ModelConnectionRequest("OpenAI", "Provider", null,
            key.GetProperty("credential").GetProperty("credentialId").GetGuid(), false, true, await Revision(owner, tenant), 0)));
        var connectionId = connection.GetProperty("connectionId").GetGuid();
        var capabilities = new ModelCapabilityInput(true, false, "NativeDeveloper", "NativeStructuredOutput", true, true, [], null);
        var request = new ModelOfferingRequest(connectionId, null, "manual-unlisted-model", "Manual model", "Responses", capabilities, true, false, await Revision(owner, tenant), 0);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync(root + "/models", request with { RestoreAvailability = true })).StatusCode);
        var offering = await Json(await owner.PostAsJsonAsync(root + "/models", request)); var offeringId = offering.GetProperty("offeringId").GetGuid();
        await Json(await owner.PutAsJsonAsync(root + "/models/default", new TenantModelDefaultRequest(offeringId, null, await Revision(owner, tenant), 0)));
        var role = await Json(await owner.PostAsJsonAsync(root + "/roles", new { name = "Metadata tester", permissions = new[] { "models.read", "model-connections.read", "model-connections.test" }, expectedRevision = await Revision(owner, tenant) }));
        var invite = await Json(await owner.PostAsJsonAsync(root + "/members/invite", new { login = "model-tester@example.test", roleId = role.GetProperty("roleId").GetGuid(), expectedRevision = await Revision(owner, tenant) }));
        using var tester = Client(factory); await Csrf(tester);
        Assert.Equal(HttpStatusCode.Created, (await tester.PostAsJsonAsync("/api/v2/auth/register", new { login = "model-tester@example.test", displayName = "Tester", password = "strong-tester-password",
            invitation = new { tenantId = tenant, invitationId = invite.GetProperty("invitationId").GetGuid(), token = invite.GetProperty("token").GetString() } })).StatusCode);
        await Login(tester, "model-tester@example.test", "strong-tester-password");
        var view = await Json(await tester.GetAsync(root + "/models")); var access = view.GetProperty("access");
        Assert.Contains(access.GetProperty("testConnectionIds").EnumerateArray(), id => id.GetGuid() == connectionId);
        Assert.False(access.GetProperty("canCreateOffering").GetBoolean()); Assert.False(access.GetProperty("canManageDefault").GetBoolean());
        var result = await Json(await tester.PostAsJsonAsync(root + $"/model-connections/{connectionId}/test", new ModelMetadataTestRequest(await Revision(owner, tenant), 1)));
        Assert.True(result.GetProperty("metadataAvailable").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await tester.PostAsJsonAsync(root + $"/model-connections/{connectionId}/discover", new ModelMetadataTestRequest(await Revision(owner, tenant), 1))).StatusCode);
        await Json(await owner.PostAsJsonAsync(root + $"/model-connections/{connectionId}/discover", new ModelMetadataTestRequest(await Revision(owner, tenant), 1)));
        view = await Json(await owner.GetAsync(root + "/models"));
        Assert.Equal("NotReturned", view.GetProperty("offerings").EnumerateArray().Single(o => o.GetProperty("offeringId").GetGuid() == offeringId).GetProperty("observation").GetString());
        var restore = request with { ExpectedRevision = await Revision(owner, tenant), ExpectedOfferingRevision = 1, RestoreAvailability = true };
        Assert.Equal(HttpStatusCode.Forbidden, (await tester.PatchAsJsonAsync(root + $"/models/{offeringId}", restore)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PatchAsJsonAsync(root + $"/models/{offeringId}", restore with { ExpectedRevision = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PatchAsJsonAsync(root + $"/models/{offeringId}", restore with { Enabled = false })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PatchAsJsonAsync(root + $"/models/{offeringId}", restore with { Retired = true })).StatusCode);
        var restored = await Json(await owner.PatchAsJsonAsync(root + $"/models/{offeringId}", restore));
        Assert.Equal("Unknown", restored.GetProperty("observation").GetString()); Assert.Equal(2, restored.GetProperty("revision").GetInt64());
        Assert.Equal(offeringId, restored.GetProperty("offeringId").GetGuid());
        view = await Json(await owner.GetAsync(root + "/models"));
        Assert.Equal(offeringId, view.GetProperty("settings").GetProperty("defaultOfferingId").GetGuid());
        Assert.True(view.GetProperty("availability").EnumerateArray().Single(a => a.GetProperty("offeringId").GetGuid() == offeringId).GetProperty("available").GetBoolean());
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PatchAsJsonAsync(root + $"/models/{offeringId}", restore with { ExpectedRevision = await Revision(owner, tenant), ExpectedOfferingRevision = 2 })).StatusCode);
    }
}
