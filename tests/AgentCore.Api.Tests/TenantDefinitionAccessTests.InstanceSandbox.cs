using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Ports;
using AgentCore.Contracts.Http;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentCore.Api.Tests;

public sealed partial class TenantDefinitionAccessTests
{
    [Fact]
    public async Task Owned_instance_sandbox_HTTP_selects_verified_tenant_profile_and_resets_sparse_setup_without_execution()
    {
        await using var factory = new SandboxSetupFactory();
        using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
        var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid(); var root = $"/api/v2/tenants/{tenant}";
        var profile = await Json(await owner.PostAsJsonAsync(root + "/sandbox-profiles", new TenantSandboxProfileSaveRequest(await Revision(owner, tenant), 0, "Owned setup", "Synthetic", true,
            "ghcr.io/acme/tool@sha256:" + new string('a', 64))));
        var profileId = profile.GetProperty("profile").GetProperty("id").GetString();
        var draft = await Json(await owner.PostAsJsonAsync(root + "/agent-definitions", new { name = "Sandbox policy", expectedRevision = await Revision(owner, tenant) }));
        var definitionId = draft.GetProperty("definitionId").GetString(); var draftId = draft.GetProperty("draftId").GetGuid(); var candidate = JsonNode.Parse(draft.GetProperty("candidate").GetRawText())!;
        candidate["sandboxPolicy"] = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(new { defaultProfileId = profileId, allowedProfileIds = new[] { profileId } }));
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(root + $"/definition-drafts/{draftId}", new { candidate, expectedRevision = await Revision(owner, tenant), expectedDraftRevision = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync(root + $"/definition-drafts/{draftId}/publish", new { expectedRevision = await Revision(owner, tenant), expectedDraftRevision = 2 })).StatusCode);
        var created = await Json(await owner.PostAsJsonAsync(root + "/agents/from-publication", new { definitionId, version = 1, expectedRevision = await Revision(owner, tenant) }));
        var id = created.GetProperty("instance").GetProperty("instanceId").GetGuid(); var path = root + $"/agents/{id}";
        var response = await owner.GetAsync(path + "/sandbox"); Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.True(response.Headers.CacheControl?.NoStore);
        var view = await Json(response); Assert.Equal("NeedsVerification", view.GetProperty("profiles")[0].GetProperty("availability").GetString());
        var before = await Revision(owner, tenant);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PatchAsJsonAsync(path + "/settings/sandboxPolicy", new { expectedRevision = before, expectedInstanceRevision = 1, set = new { profileId }, clear = Array.Empty<string>() })).StatusCode);
        Assert.Equal(before, await Revision(owner, tenant));
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync(root + $"/sandbox-profiles/{profileId}/verify", new TenantSandboxVerificationRequest(before, 1))).StatusCode);
        view = await Json(await owner.GetAsync(path + "/sandbox")); Assert.True(view.GetProperty("completeCatalog").GetBoolean()); Assert.True(view.GetProperty("canManage").GetBoolean());
        Assert.Equal("Eligible", view.GetProperty("profiles")[0].GetProperty("availability").GetString());
        var body = new { expectedRevision = view.GetProperty("securityRevision").GetInt64(), expectedInstanceRevision = 1, set = new { profileId, cpu = .5, initializationScript = "echo saved-only" }, clear = Array.Empty<string>() };
        var saved = await owner.PatchAsJsonAsync(path + "/settings/sandboxPolicy", body); Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PatchAsJsonAsync(path + "/settings/sandboxPolicy", body)).StatusCode);
        view = await Json(await owner.GetAsync(path + "/sandbox")); var section = view.GetProperty("section");
        Assert.Equal(.5d, section.GetProperty("effective").GetProperty("cpu").GetDouble()); Assert.Equal("echo saved-only", section.GetProperty("effective").GetProperty("initializationScript").GetString());
        Assert.Equal("instance", section.GetProperty("sources").GetProperty("cpu").GetString());
        Assert.Equal(HttpStatusCode.OK, (await owner.PatchAsJsonAsync(path + "/settings/sandboxPolicy", new { expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 2, set = new { }, clear = new[] { "profileId", "cpu", "initializationScript" } })).StatusCode);
        view = await Json(await owner.GetAsync(path + "/sandbox")); Assert.Empty(view.GetProperty("section").GetProperty("overrides").EnumerateObject());
        Assert.Equal(1d, view.GetProperty("section").GetProperty("effective").GetProperty("cpu").GetDouble());
        Assert.Equal(1, factory.Services.GetRequiredService<SetupVerification>().Calls); // Saving/resetting never verifies or runs a script again.
        using var anonymous = factory.CreateClient(); Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(path + "/sandbox")).StatusCode);
        using var legacy = TestOwnerCapability.CreateOwnerClient(factory); Assert.Equal(HttpStatusCode.Unauthorized, (await legacy.GetAsync(path + "/sandbox")).StatusCode);
        var beta = (await Json(await owner.PostAsJsonAsync("/api/v2/system/tenants", new { name = "Other setup", slug = "other-setup" }))).GetProperty("tenantId").GetGuid();
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync($"/api/v2/tenants/{beta}/agents/{id}/sandbox")).StatusCode);
    }
    private sealed class SetupVerification(ISandboxVerificationHost host) : ITenantSandboxVerifier
    {
        public int Calls { get; private set; }
        public string HostScope => host.Scope;
        public ValueTask<SandboxVerificationOutcome> VerifyAsync(SandboxVerificationAttempt attempt, SandboxProfile profile, SandboxImageAuthorization proof, CancellationToken ct = default)
        {
            Calls++;
            return ValueTask.FromResult(new SandboxVerificationOutcome(new(profile.Id, profile.Revision, DateTimeOffset.UtcNow, true, "Synthetic setup prerequisite",
                ContractVersion: SandboxProfileVerification.CurrentContractVersion, TenantId: attempt.TenantId, IncludesInitialization: !attempt.ImageOnly, HostScope: host.Scope), true));
        }
        public ValueTask<bool> CleanupAsync(SandboxVerificationCleanup claim, CancellationToken ct = default) => ValueTask.FromResult(true);
    }
    private sealed class SetupReady : ISandboxReadiness
    { public bool IsAvailable => true; public string State => "Ready"; public string SafeMessage => "Synthetic fixture"; }
    private sealed class SandboxSetupFactory : AgentCoreApiFactory
    {
        protected override IReadOnlyDictionary<string, string?> ExtraConfiguration => new Dictionary<string, string?> { ["OpenSandbox:Enabled"] = "false" };
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<SetupVerification>();
                services.Replace(ServiceDescriptor.Singleton<ITenantSandboxVerifier>(p => p.GetRequiredService<SetupVerification>()));
                services.Replace(ServiceDescriptor.Singleton<ISandboxReadiness, SetupReady>());
            });
        }
    }
}
