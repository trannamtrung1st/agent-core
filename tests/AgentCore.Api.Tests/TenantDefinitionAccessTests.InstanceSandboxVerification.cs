using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using AgentCore.Contracts.Http;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AgentCore.Api.Tests;

public sealed partial class TenantDefinitionAccessTests
{
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Owned_instance_sandbox_verification_HTTP_fences_exact_setup_and_keeps_profile_and_sibling_history_separate(bool disabled)
    {
        await using AgentCoreApiFactory factory = disabled ? new DisabledInstanceVerificationFactory() : new SandboxSetupFactory();
        using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
        var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid(); var root = $"/api/v2/tenants/{tenant}";
        var profile = await Json(await owner.PostAsJsonAsync(root + "/sandbox-profiles", new TenantSandboxProfileSaveRequest(await Revision(owner, tenant), 0, "Verify setup", "Synthetic", true,
            "ghcr.io/acme/tool@sha256:" + new string('a', 64), "echo reusable")));
        var profileId = profile.GetProperty("profile").GetProperty("id").GetString()!;
        var draft = await Json(await owner.PostAsJsonAsync(root + "/agent-definitions", new { name = "Verified setup", expectedRevision = await Revision(owner, tenant) }));
        var definitionId = draft.GetProperty("definitionId").GetString(); var draftId = draft.GetProperty("draftId").GetGuid(); var candidate = JsonNode.Parse(draft.GetProperty("candidate").GetRawText())!;
        candidate["sandboxPolicy"] = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(new { defaultProfileId = profileId, allowedProfileIds = new[] { profileId } }));
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(root + $"/definition-drafts/{draftId}", new { candidate, expectedRevision = await Revision(owner, tenant), expectedDraftRevision = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync(root + $"/definition-drafts/{draftId}/publish", new { expectedRevision = await Revision(owner, tenant), expectedDraftRevision = 2 })).StatusCode);
        var created = await Json(await owner.PostAsJsonAsync(root + "/agents/from-publication", new { definitionId, version = 1, expectedRevision = await Revision(owner, tenant) }));
        var id = created.GetProperty("instance").GetProperty("instanceId").GetGuid(); var path = root + $"/agents/{id}/sandbox";
        var other = await Json(await owner.PostAsJsonAsync(root + "/agents/from-publication", new { definitionId, version = 1, expectedRevision = await Revision(owner, tenant) }));
        var sibling = other.GetProperty("instance").GetProperty("instanceId").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await owner.PatchAsJsonAsync(root + $"/agents/{id}/settings/sandboxPolicy", new { expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1,
            set = new { cpu = .5, initializationScript = "echo saved-instance" }, clear = Array.Empty<string>() })).StatusCode);
        var before = await Revision(owner, tenant);
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsJsonAsync(path + "/verify", new TenantSandboxInstanceVerificationRequest(before, 99, profileId, 1))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync(path + "/verify", new { expectedRevision = before, expectedInstanceRevision = 2, profileId, expectedProfileRevision = 1, instanceId = sibling })).StatusCode);
        Assert.Equal(before, await Revision(owner, tenant));
        var response = await owner.PostAsJsonAsync(path + "/verify", new TenantSandboxInstanceVerificationRequest(before, 2, profileId, 1));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.True(response.Headers.CacheControl?.NoStore);
        var view = await Json(response); var attempt = view.GetProperty("attempt"); var attemptId = attempt.GetProperty("attemptId").GetGuid();
        Assert.Equal(id, attempt.GetProperty("instanceId").GetGuid()); Assert.Equal(2, attempt.GetProperty("instanceRevision").GetInt64());
        Assert.Equal(disabled ? "Failed" : "Succeeded", attempt.GetProperty("status").GetString()); Assert.Equal(!disabled, attempt.GetProperty("accepted").GetBoolean());
        Assert.Equal(disabled ? "VerificationFailed" : "Verified", view.GetProperty("status").GetString()); Assert.True(view.GetProperty("canVerify").GetBoolean());
        Assert.Equal(2, attempt.GetProperty("result").GetProperty("instanceRevision").GetInt64());
        if (disabled) Assert.Contains("disabled", attempt.GetProperty("result").GetProperty("message").GetString());
        var exact = await Json(await owner.GetAsync(path + $"/verification/{attemptId}?profileId={profileId}"));
        Assert.Equal(attemptId, exact.GetProperty("attempt").GetProperty("attemptId").GetGuid());
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync(root + $"/agents/{sibling}/sandbox/verification/{attemptId}?profileId={profileId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync(root + $"/sandbox-profiles/{profileId}/verification/{attemptId}")).StatusCode);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, (await Json(await owner.GetAsync(root + $"/sandbox-profiles/{profileId}/verification"))).GetProperty("attempt").ValueKind);
        Assert.Equal("NeedsVerification", (await Json(await owner.GetAsync(root + $"/sandbox-profiles/{profileId}"))).GetProperty("profile").GetProperty("availability").GetString());
        foreach (var field in new[] { "accountId", "authenticationSessionId", "principalId", "registrySecretRevision", "registryCredentialRevision", "hostScope", "cleanupClaim" })
            Assert.DoesNotContain(field, exact.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.OK, (await owner.PatchAsJsonAsync(root + $"/agents/{id}/settings/sandboxPolicy", new { expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 2,
            set = new { cpu = .4 }, clear = Array.Empty<string>() })).StatusCode);
        var stale = await Json(await owner.GetAsync(path + $"/verification?profileId={profileId}"));
        Assert.Equal("NeedsVerification", stale.GetProperty("status").GetString()); Assert.Equal(System.Text.Json.JsonValueKind.Null, stale.GetProperty("observation").ValueKind);
        Assert.Equal(3, stale.GetProperty("currentInstanceRevision").GetInt64());
        using var anonymous = factory.CreateClient(); Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(path + $"/verification?profileId={profileId}")).StatusCode);
        using var legacy = RetiredOwnerHeader.CreateClient(factory); Assert.Equal(HttpStatusCode.Unauthorized, (await legacy.GetAsync(path + $"/verification?profileId={profileId}")).StatusCode);
        var beta = (await Json(await owner.PostAsJsonAsync("/api/v2/system/tenants", new { name = "Other verification", slug = "other-verification" }))).GetProperty("tenantId").GetGuid();
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync($"/api/v2/tenants/{beta}/agents/{id}/sandbox/verification/{attemptId}?profileId={profileId}")).StatusCode);
    }
    private sealed class DisabledInstanceVerificationFactory : AgentCoreApiFactory
    {
        protected override IReadOnlyDictionary<string, string?> ExtraConfiguration => new Dictionary<string, string?> { ["OpenSandbox:Enabled"] = "false" };
    }
}
