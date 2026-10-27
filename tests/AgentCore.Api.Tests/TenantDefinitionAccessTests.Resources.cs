using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AgentCore.Api.Tests;

public sealed partial class TenantDefinitionAccessTests
{
    [Fact]
    public async Task Owned_resource_upload_evaluate_publish_and_read_survive_fork_without_hash_or_tenant_bypass()
    {
        await using var factory = new AgentCoreApiFactory();
        using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
        var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid();
        var draft = await Json(await owner.PostAsJsonAsync($"/api/v2/tenants/{tenant}/agent-definitions", new { name = "Resource evaluator", expectedRevision = await Revision(owner, tenant) }));
        var id = draft.GetProperty("definitionId").GetString(); var draftId = draft.GetProperty("draftId").GetGuid(); var path = $"/api/v2/tenants/{tenant}/definition-drafts/{draftId}";
        var firstBytes = Encoding.UTF8.GetBytes("Private first reference."); var secondBytes = Encoding.UTF8.GetBytes("Private second reference.");
        var boundResponse = await owner.PostAsJsonAsync(path + "/resources/batch", new { expectedRevision = await Revision(owner, tenant), expectedDraftRevision = 1,
            items = new[] { new { logicalPath = "first.txt", kind = 2, mediaType = "text/plain", content = firstBytes }, new { logicalPath = "second.txt", kind = 2, mediaType = "text/plain", content = secondBytes } } });
        Assert.Equal(HttpStatusCode.OK, boundResponse.StatusCode); var bound = await Json(boundResponse);
        Assert.Equal(2, bound.GetProperty("revision").GetInt64());
        var first = bound.GetProperty("resources").EnumerateArray().Single(r => r.GetProperty("logicalPath").GetString() == "first.txt");
        var second = bound.GetProperty("resources").EnumerateArray().Single(r => r.GetProperty("logicalPath").GetString() == "second.txt");
        var firstId = first.GetProperty("resourceId").GetGuid(); var secondId = second.GetProperty("resourceId").GetGuid();
        Assert.Equal(firstBytes, await owner.GetByteArrayAsync(path + $"/resources/{firstId}/content"));
        var scenarioResponse = await owner.PutAsJsonAsync(path + "/evaluation-suite", DefinitionScenario(await Revision(owner, tenant), 2));
        Assert.Equal(HttpStatusCode.OK, scenarioResponse.StatusCode);
        var missingEvidence = await owner.PostAsJsonAsync(path + "/publish", new { expectedRevision = await Revision(owner, tenant), expectedDraftRevision = 3 });
        Assert.Equal(HttpStatusCode.BadRequest, missingEvidence.StatusCode); Assert.Equal("evaluation_required", (await Json(missingEvidence)).GetProperty("reasonCode").GetString());
        var runResponse = await owner.PostAsJsonAsync($"/api/v2/tenants/{tenant}/evaluation-attempts", DefinitionEvaluationRequest(await Revision(owner, tenant), 3, draftId));
        Assert.Equal(HttpStatusCode.OK, runResponse.StatusCode); var run = await Json(runResponse);
        Assert.Equal(0, run.GetProperty("outcome").GetInt32());
        Assert.True(run.GetProperty("commitSequence").GetInt64() > 0);
        var beforeRecovery = await Revision(owner, tenant);
        var recovered = await Json(await owner.GetAsync($"/api/v2/tenants/{tenant}/evaluation-attempts/recovery?dedupKey=" + Uri.EscapeDataString(run.GetProperty("dedupKey").GetString()!)));
        Assert.Equal(run.GetProperty("attemptId").GetGuid(), recovered.GetProperty("attempt").GetProperty("attemptId").GetGuid());
        Assert.Equal(JsonValueKind.Null, (await Json(await owner.GetAsync($"/api/v2/tenants/{tenant}/evaluation-attempts/recovery?dedupKey=never-submitted"))).GetProperty("attempt").ValueKind);
        Assert.Equal(beforeRecovery, await Revision(owner, tenant));
        var renamed = await owner.PostAsJsonAsync(path + "/resources/batch", new { expectedRevision = await Revision(owner, tenant), expectedDraftRevision = 3,
            items = new[] { new { resourceId = firstId, logicalPath = "second.txt", kind = 2, mediaType = "text/plain" }, new { resourceId = secondId, logicalPath = "first.txt", kind = 2, mediaType = "text/plain" } } });
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        var staleEvidence = await owner.PostAsJsonAsync(path + "/publish", new { expectedRevision = await Revision(owner, tenant), expectedDraftRevision = 4 });
        Assert.Equal(HttpStatusCode.BadRequest, staleEvidence.StatusCode); Assert.Equal("evaluation_stale", (await Json(staleEvidence)).GetProperty("reasonCode").GetString());
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync($"/api/v2/tenants/{tenant}/evaluation-attempts", DefinitionEvaluationRequest(await Revision(owner, tenant), 4, draftId))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync(path + "/publish", new { expectedRevision = await Revision(owner, tenant), expectedDraftRevision = 4 })).StatusCode);
        var published = $"/api/v2/tenants/{tenant}/agent-definitions/{id}/publications/1/resources";
        Assert.Equal(firstBytes, await owner.GetByteArrayAsync(published + $"/{firstId}/content"));
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync(path + "/state")).StatusCode);
        var fork = await Json(await owner.PostAsJsonAsync($"/api/v2/tenants/{tenant}/agent-definitions/{id}/drafts", new { version = 1, expectedRevision = await Revision(owner, tenant) }));
        var forkId = fork.GetProperty("draftId").GetGuid(); var forkPath = $"/api/v2/tenants/{tenant}/definition-drafts/{forkId}";
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync(forkPath + "/resources/batch", new { expectedRevision = await Revision(owner, tenant), expectedDraftRevision = 1,
            items = new[] { new { resourceId = firstId, logicalPath = "second.txt", kind = 2, mediaType = "text/plain", content = Encoding.UTF8.GetBytes("Revised fork bytes.") } } })).StatusCode);
        Assert.Equal(firstBytes, await owner.GetByteArrayAsync(published + $"/{firstId}/content"));
        Assert.Equal(HttpStatusCode.OK, (await owner.DeleteAsync(forkPath + $"/resources/{secondId}?expectedRevision={await Revision(owner, tenant)}&expectedDraftRevision=2")).StatusCode);
        var other = (await Json(await owner.PostAsJsonAsync("/api/v2/system/tenants", new { name = "Other resources", slug = "resource-other" }))).GetProperty("tenantId").GetGuid();
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync($"/api/v2/tenants/{other}/definition-drafts/{forkId}/resources/{firstId}/content")).StatusCode);
        var otherDraft = await Json(await owner.PostAsJsonAsync($"/api/v2/tenants/{other}/agent-definitions", new { name = "Other resource draft", expectedRevision = await Revision(owner, other) }));
        var otherPath = $"/api/v2/tenants/{other}/definition-drafts/{otherDraft.GetProperty("draftId").GetGuid()}";
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.PostAsJsonAsync(otherPath + "/resources/batch", new { expectedRevision = await Revision(owner, other), expectedDraftRevision = 1,
            items = new[] { new { resourceId = firstId, logicalPath = "borrowed.txt", kind = 2, mediaType = "text/plain" } } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync(otherPath + "/resources/batch", new { expectedRevision = await Revision(owner, other), expectedDraftRevision = 1,
            items = new[] { new { logicalPath = "hash-only.txt", kind = 2, mediaType = "text/plain", contentSha256 = first.GetProperty("contentSha256").GetString() } } })).StatusCode);
        Assert.Empty((await Json(await owner.GetAsync(otherPath + "/resources"))).GetProperty("items").EnumerateArray());
        var invalidPath = await owner.PostAsJsonAsync(otherPath + "/resources/batch", new { expectedRevision = await Revision(owner, other), expectedDraftRevision = 1,
            items = new[] { new { logicalPath = "../escape.txt", kind = 2, mediaType = "text/plain", content = new byte[] { 1 } } } });
        Assert.Equal(HttpStatusCode.BadRequest, invalidPath.StatusCode); Assert.Equal("invalid_request", (await Json(invalidPath)).GetProperty("reasonCode").GetString());
        var duplicate = await owner.PostAsJsonAsync(otherPath + "/resources/batch", new { expectedRevision = await Revision(owner, other), expectedDraftRevision = 1,
            items = new[] { new { logicalPath = "duplicate.txt", kind = 2, mediaType = "text/plain", content = new byte[] { 1 } }, new { logicalPath = "duplicate.txt", kind = 2, mediaType = "text/plain", content = new byte[] { 2 } } } });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode); Assert.Equal("resource_conflict", (await Json(duplicate)).GetProperty("reasonCode").GetString());
        var roles = await Json(await owner.GetAsync($"/api/v2/tenants/{tenant}/roles"));
        var readerRole = roles.GetProperty("items").EnumerateArray().Single(r => r.GetProperty("key").GetString() == "reader").GetProperty("roleId").GetGuid();
        var invite = await Json(await owner.PostAsJsonAsync($"/api/v2/tenants/{tenant}/members/invite", new { login = "resource-reader@example.test", roleId = readerRole, expectedRevision = await Revision(owner, tenant) }));
        using var reader = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        await Csrf(reader);
        Assert.Equal(HttpStatusCode.Created, (await reader.PostAsJsonAsync("/api/v2/auth/register", new { login = "resource-reader@example.test", displayName = "Resource reader", password = "strong-resource-password",
            invitation = new { tenantId = tenant, invitationId = invite.GetProperty("invitationId").GetGuid(), token = invite.GetProperty("token").GetString() } })).StatusCode);
        await Login(reader, "resource-reader@example.test", "strong-resource-password");
        Assert.Equal(firstBytes, await reader.GetByteArrayAsync(published + $"/{firstId}/content"));
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.DeleteAsync(forkPath + $"/resources/{firstId}?expectedRevision={await Revision(owner, tenant)}&expectedDraftRevision=3")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync($"/api/v2/tenants/{tenant}/evaluation-attempts", DefinitionEvaluationRequest(await Revision(owner, tenant), 3, forkId))).StatusCode);
    }
}
