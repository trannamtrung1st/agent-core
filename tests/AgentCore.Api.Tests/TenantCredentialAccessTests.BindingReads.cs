using System.Net;
using System.Net.Http.Json;
using AgentCore.Application.Ports;
using AgentCore.Contracts.Http;
using AgentCore.Domain.Credentials;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentCore.Api.Tests;

public sealed partial class TenantCredentialAccessTests
{
    [Theory]
    [InlineData("logout")]
    [InlineData("password")]
    [InlineData("unlink")]
    public async Task Binding_HTTP_withholds_captured_metadata_after_session_retirement_or_configuration_change(string change)
    {
        await using var factory = new BindingReadFactory();
        using var owner = Client(factory); await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
        var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid();
        var root = $"/api/v2/tenants/{tenant}";
        var credential = (await Json(await owner.PostAsJsonAsync(root + "/credentials", new TenantCredentialCreateRequest("Captured private account", "Password", "General",
            await Revision(owner, tenant), ProtectedValue: "http-private-sentinel")))).GetProperty("credential").GetProperty("credentialId").GetGuid();
        var agent = (await Json(await owner.PostAsJsonAsync(root + "/agents", new { templateId = "examiner", version = 1, expectedRevision = await Revision(owner, tenant) })))
            .GetProperty("instance").GetProperty("instanceId").GetGuid();
        var path = root + $"/agents/{agent}/credential-bindings";
        var binding = (await Json(await owner.PostAsJsonAsync(path, new TenantBindCredentialRequest(credential, "private-alias", await Revision(owner, tenant), 1, 1))))
            .GetProperty("binding").GetProperty("bindingId").GetGuid();
        Assert.Equal("private-alias", (await Json(await owner.GetAsync(path))).GetProperty("items")[0].GetProperty("reference").GetString());
        var before = await Revision(owner, tenant);
        factory.AfterSnapshot = async () =>
        {
            using var changed = change switch
            {
                "logout" => await owner.PostAsync("/api/v2/auth/logout", null),
                "password" => await owner.PostAsJsonAsync("/api/v2/account/password", new { currentPassword = "explicit-synthetic-test-password", newPassword = "replacement-synthetic-password" }),
                _ => await owner.DeleteAsync(path + $"/{binding}?expectedRevision={before}&expectedInstanceRevision=2&expectedBindingRevision=1")
            };
            Assert.True(changed.IsSuccessStatusCode, await changed.Content.ReadAsStringAsync());
        };
        using var response = await owner.GetAsync(path);
        Assert.Equal(change == "unlink" ? HttpStatusCode.Conflict : HttpStatusCode.Forbidden, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var failure = await Json(response); Safe(failure);
        Assert.Equal(change == "unlink" ? "stale_revision" : "initiator_unavailable", failure.GetProperty("reasonCode").GetString());
        Assert.False(failure.TryGetProperty("items", out _));
        Assert.DoesNotContain("Captured private account", failure.GetRawText()); Assert.DoesNotContain("private-alias", failure.GetRawText());
        if (change != "unlink")
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await owner.GetAsync(path)).StatusCode);
            await Login(owner, "owner@example.test", change == "password" ? "replacement-synthetic-password" : "explicit-synthetic-test-password");
        }
        var recovered = await Json(await owner.GetAsync(path)); Safe(recovered);
        Assert.Equal(change == "unlink" ? 0 : 1, recovered.GetProperty("total").GetInt32());
        Assert.Equal(change == "unlink" ? before + 1 : before, await Revision(owner, tenant));
    }

    private sealed class BindingReadFactory : AgentCoreApiFactory
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "tenant-binding-read-http-" + Guid.NewGuid().ToString("N"));
        public Func<Task>? AfterSnapshot;
        protected override IReadOnlyDictionary<string, string?> ExtraConfiguration => new Dictionary<string, string?> { ["Persistence:WorkspaceRoot"] = Path.Combine(root, "workspaces") };
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                var options = (PersistenceOptions)services.Single(d => d.ServiceType == typeof(PersistenceOptions)).ImplementationInstance!;
                options.CredentialProtectionKeyRoot = Path.Combine(root, "keys"); options.ArtifactRoot = Path.Combine(root, "artifacts");
                services.Replace(ServiceDescriptor.Singleton<ITenantCredentialBindingStore>(p => new InterceptedBindingReads((ITenantCredentialBindingStore)p.GetRequiredService<IIamStore>(), this)));
            });
        }
        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
    private sealed class InterceptedBindingReads(ITenantCredentialBindingStore inner, BindingReadFactory factory) : ITenantCredentialBindingStore
    {
        public async ValueTask<TenantCredentialBindingSnapshot?> ReadTenantCredentialBindingsAsync(Guid tenantId, Guid instanceId, CancellationToken ct = default)
        {
            var captured = await inner.ReadTenantCredentialBindingsAsync(tenantId, instanceId, ct);
            if (Interlocked.Exchange(ref factory.AfterSnapshot, null) is { } after) await after();
            return captured;
        }
        public ValueTask<TenantAgent?> TryBindTenantCredentialAsync(TenantSecurityState next, long expectedSecurityRevision, AgentCredentialBinding binding,
            long expectedInstanceRevision, long expectedCredentialRevision, AuthorizationAudit audit, CancellationToken ct = default, HumanSessionReference? initiator = null) =>
            inner.TryBindTenantCredentialAsync(next, expectedSecurityRevision, binding, expectedInstanceRevision, expectedCredentialRevision, audit, ct, initiator);
        public ValueTask<TenantAgent?> TryUnbindTenantCredentialAsync(TenantSecurityState next, long expectedSecurityRevision, Guid instanceId, Guid bindingId,
            long expectedInstanceRevision, long expectedBindingRevision, DateTimeOffset at, AuthorizationAudit audit, CancellationToken ct = default, HumanSessionReference? initiator = null) =>
            inner.TryUnbindTenantCredentialAsync(next, expectedSecurityRevision, instanceId, bindingId, expectedInstanceRevision, expectedBindingRevision, at, audit, ct, initiator);
    }
}
