using System.Net;
using System.Net.Http.Json;
using AgentCore.Contracts.Http;

namespace AgentCore.Api.Tests;

public sealed partial class TenantCredentialAccessTests
{
    [Fact]
    public async Task Tenant_verification_HTTP_records_disabled_host_result_without_granting_compatibility_or_exposing_internal_proof()
    {
        await using var factory = new DisabledVerificationFactory(); using var owner = Client(factory);
        await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
        var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid();
        var root = $"/api/v2/tenants/{tenant}/sandbox-profiles";
        var created = await Json(await owner.PostAsJsonAsync(root, new TenantSandboxProfileSaveRequest(await Revision(owner, tenant), 0, "Verification fixture", "Synthetic", true,
            "ghcr.io/acme/tool@sha256:" + new string('a', 64))));
        var id = created.GetProperty("profile").GetProperty("id").GetString(); var path = root + "/" + id;
        var before = await Revision(owner, tenant);
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsJsonAsync(path + "/verify", new TenantSandboxVerificationRequest(before, 99))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync(path + "/verify", new { expectedRevision = before, expectedProfileRevision = 1, imageOnly = true })).StatusCode);
        Assert.Equal(before, await Revision(owner, tenant));
        Assert.Equal(System.Text.Json.JsonValueKind.Null, (await Json(await owner.GetAsync(path + "/verification"))).GetProperty("attempt").ValueKind);
        var response = await owner.PostAsJsonAsync(path + "/verify", new TenantSandboxVerificationRequest(await Revision(owner, tenant), 1));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.True(response.Headers.CacheControl?.NoStore);
        var result = await Json(response); var attempt = result.GetProperty("attempt");
        Assert.Equal("Failed", attempt.GetProperty("status").GetString()); Assert.False(attempt.GetProperty("accepted").GetBoolean()); Assert.False(attempt.GetProperty("active").GetBoolean());
        Assert.False(attempt.GetProperty("result").GetProperty("passed").GetBoolean()); Assert.Contains("disabled", attempt.GetProperty("result").GetProperty("message").GetString());
        var attemptId = attempt.GetProperty("attemptId").GetGuid();
        var exact = await Json(await owner.GetAsync(path + "/verification/" + attemptId)); Assert.Equal(attemptId, exact.GetProperty("attempt").GetProperty("attemptId").GetGuid());
        var profile = (await Json(await owner.GetAsync(path))).GetProperty("profile"); Assert.Equal(1, profile.GetProperty("revision").GetInt64());
        Assert.Equal("VerificationFailed", profile.GetProperty("availability").GetString());
        foreach (var text in new[] { result.GetRawText(), exact.GetRawText(), profile.GetRawText() })
            foreach (var field in new[] { "authenticationSessionId", "accountId", "principalId", "registrySecretRevision", "registryCredentialRevision", "hostScope", "cleanupClaim" })
                Assert.DoesNotContain(field, text, StringComparison.OrdinalIgnoreCase);
        using var anonymous = Client(factory); Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(path + "/verification")).StatusCode);
        var beta = (await Json(await owner.PostAsJsonAsync("/api/v2/system/tenants", new { name = "Other organization", slug = "verification-beta" }))).GetProperty("tenantId").GetGuid();
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync($"/api/v2/tenants/{beta}/sandbox-profiles/{id}/verification/{attemptId}")).StatusCode);
        using var legacy = RetiredOwnerHeader.CreateClient(factory);
        Assert.Equal(HttpStatusCode.NotFound, (await legacy.PostAsJsonAsync($"/api/v2/admin/sandbox-profiles/{id}/verify", new VerifySandboxProfileRequest(1))).StatusCode);
    }

    [Fact]
    public async Task Tenant_verification_HTTP_requires_exact_manage_authority_and_revocation_masks_history_before_stale_witnesses()
    {
        await using var factory = new DisabledVerificationFactory(); using var owner = Client(factory);
        await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
        var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid();
        var root = $"/api/v2/tenants/{tenant}"; var catalog = root + "/sandbox-profiles";
        var created = await Json(await owner.PostAsJsonAsync(catalog, new TenantSandboxProfileSaveRequest(await Revision(owner, tenant), 0, "Verification authority", "Synthetic", true,
            "ghcr.io/acme/tool@sha256:" + new string('a', 64))));
        var id = created.GetProperty("profile").GetProperty("id").GetString(); var path = catalog + "/" + id;
        var minimal = await Json(await owner.PostAsJsonAsync(root + "/roles", new { name = "Member", permissions = new[] { "agents.chat" }, expectedRevision = await Revision(owner, tenant) }));
        var invite = await Json(await owner.PostAsJsonAsync(root + "/members/invite", new { login = "verification-reader@example.test", roleId = minimal.GetProperty("roleId").GetGuid(), expectedRevision = await Revision(owner, tenant) }));
        using var reader = Client(factory); await Csrf(reader);
        Assert.Equal(HttpStatusCode.Created, (await reader.PostAsJsonAsync("/api/v2/auth/register", new { login = "verification-reader@example.test", displayName = "Reader", password = "strong-reader-password",
            invitation = new { tenantId = tenant, invitationId = invite.GetProperty("invitationId").GetGuid(), token = invite.GetProperty("token").GetString() } })).StatusCode);
        await Login(reader, "verification-reader@example.test", "strong-reader-password");
        var member = (await Json(await owner.GetAsync(root + "/members"))).GetProperty("items").EnumerateArray().Single(m => m.GetProperty("login").GetString() == "VERIFICATION-READER@EXAMPLE.TEST");
        var readRole = await Json(await owner.PostAsJsonAsync(root + "/roles", new { name = "Read one profile", permissions = new[] { "sandbox.read" }, expectedRevision = await Revision(owner, tenant) }));
        var grant = await Json(await owner.PostAsJsonAsync(root + "/role-assignments", new { principalId = member.GetProperty("principalId").GetGuid(), roleId = readRole.GetProperty("roleId").GetGuid(), resourceType = "sandbox-profile", resourceId = id, expectedRevision = await Revision(owner, tenant) }));
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync(path + "/verification")).StatusCode);
        var before = await Revision(owner, tenant);
        foreach (var suffix in new[] { "/verify", "/check-image" })
            Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync(path + suffix, new TenantSandboxVerificationRequest(before, 1))).StatusCode);
        Assert.Equal(before, await Revision(owner, tenant));
        var check = await Json(await owner.PostAsJsonAsync(path + "/check-image", new TenantSandboxVerificationRequest(before, 1)));
        Assert.True(check.GetProperty("attempt").GetProperty("imageOnly").GetBoolean());
        var attempt = check.GetProperty("attempt").GetProperty("attemptId").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync(path + "/verification/" + attempt)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync(root + $"/role-assignments/{grant.GetProperty("assignmentId").GetGuid()}?expectedRevision={await Revision(owner, tenant)}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync(path + "/verification/" + attempt)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync(path + "/verify", new TenantSandboxVerificationRequest(0, 0))).StatusCode);
    }
    private sealed class DisabledVerificationFactory : AgentCoreApiFactory
    {
        protected override IReadOnlyDictionary<string, string?> ExtraConfiguration => new Dictionary<string, string?> { ["OpenSandbox:Enabled"] = "false" };
    }

}
