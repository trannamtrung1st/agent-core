using System.Net;
using System.Net.Http.Json;
using AgentCore.Contracts.Http;
using AgentCore.Domain.Conversation;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AgentCore.Api.Tests;

public sealed class ProfileApiTests : IClassFixture<AgentCoreApiFactory>
{
    private readonly AgentCoreApiFactory _factory;

    public ProfileApiTests(AgentCoreApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Get_and_patch_require_owner_capability()
    {
        var client = _factory.CreateClient();
        var unauthorizedGet = await client.GetAsync("/api/v2/profile");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorizedGet.StatusCode);

        var unauthorizedPatch = await client.PatchAsJsonAsync(
            "/api/v2/profile",
            new PatchUserProfileRequest(1, new Dictionary<string, string?> { ["language"] = "en" }));
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorizedPatch.StatusCode);
    }

    [Fact]
    public async Task Get_returns_typed_seed_and_patch_stamps_userSet()
    {
        var client = TestOwnerCapability.CreateOwnerClient(_factory);
        var profile = await client.GetFromJsonAsync<UserProfileResponse>("/api/v2/profile");
        Assert.NotNull(profile);
        Assert.Equal("en", profile!.Values["language"].Value);
        Assert.Equal("applicationProfile", profile.Values["language"].Source);

        var patched = await client.PatchAsJsonAsync(
            "/api/v2/profile",
            new PatchUserProfileRequest(
                profile.Revision,
                new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["preferredName"] = "Sam",
                    ["locale"] = "en-US"
                }));
        patched.EnsureSuccessStatusCode();
        var body = await patched.Content.ReadFromJsonAsync<UserProfileResponse>();
        Assert.NotNull(body);
        Assert.Equal("userSet", body!.Values["preferredName"].Source);
        Assert.Equal("Sam", body.Values["preferredName"].Value);
        Assert.Equal(profile.Revision + 1, body.Revision);
    }

    [Fact]
    public async Task Patch_rejects_unknown_keys_stale_revision_and_invalid_values()
    {
        var client = TestOwnerCapability.CreateOwnerClient(_factory);
        var profile = await client.GetFromJsonAsync<UserProfileResponse>("/api/v2/profile");
        Assert.NotNull(profile);

        var unknown = await client.PatchAsJsonAsync(
            "/api/v2/profile",
            new PatchUserProfileRequest(
                profile!.Revision,
                new Dictionary<string, string?>(StringComparer.Ordinal) { ["nickname"] = "x" }));
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);

        var stale = await client.PatchAsJsonAsync(
            "/api/v2/profile",
            new PatchUserProfileRequest(
                profile.Revision + 99,
                new Dictionary<string, string?>(StringComparer.Ordinal) { ["language"] = "en" }));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

        var invalid = await client.PatchAsJsonAsync(
            "/api/v2/profile",
            new PatchUserProfileRequest(
                profile.Revision,
                new Dictionary<string, string?>(StringComparer.Ordinal) { ["timeZone"] = "not-valid" }));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [Fact]
    public async Task Patch_null_removes_value()
    {
        var client = TestOwnerCapability.CreateOwnerClient(_factory);
        var profile = await client.GetFromJsonAsync<UserProfileResponse>("/api/v2/profile");
        Assert.NotNull(profile);

        var patched = await client.PatchAsJsonAsync(
            "/api/v2/profile",
            new PatchUserProfileRequest(
                profile!.Revision,
                new Dictionary<string, string?>(StringComparer.Ordinal) { ["preferredName"] = "Sam" }));
        patched.EnsureSuccessStatusCode();
        var withName = await patched.Content.ReadFromJsonAsync<UserProfileResponse>();

        var removed = await client.PatchAsJsonAsync(
            "/api/v2/profile",
            new PatchUserProfileRequest(
                withName!.Revision,
                new Dictionary<string, string?>(StringComparer.Ordinal) { ["preferredName"] = null }));
        removed.EnsureSuccessStatusCode();
        var afterRemove = await removed.Content.ReadFromJsonAsync<UserProfileResponse>();
        Assert.False(afterRemove!.Values.ContainsKey("preferredName"));
    }
}
