using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Contracts.Http;
using AgentCore.Domain.Credentials;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentCore.Api.Tests;

public sealed partial class TenantCredentialAccessTests
{
    public static IEnumerable<object[]> CredentialCommandRetirementCases() =>
        from sqlite in new[] { false, true }
        from boundary in new[] { "logout", "password" }
        from action in new[] { "create", "update", "replace", "delete", "bind", "unbind" }
        select new object[] { sqlite, boundary, action };

    [Theory]
    [MemberData(nameof(CredentialCommandRetirementCases))]
    public async Task Credential_HTTP_commit_rechecks_original_login_and_preserves_all_owners_after_retirement(bool sqlite, string boundary, string action)
    {
        await using var factory = new CredentialAuthorityFactory(sqlite);
        using var owner = Client(factory); await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
        var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid();
        var root = $"/api/v2/tenants/{tenant}";
        var credential = (await Json(await owner.PostAsJsonAsync(root + "/credentials", new TenantCredentialCreateRequest("Original private metadata", "Password", "General",
            await Revision(owner, tenant), ProtectedValue: "http-private-sentinel")))).GetProperty("credential").GetProperty("credentialId").GetGuid();
        var agent = (await Json(await owner.PostAsJsonAsync(root + "/agents", new { templateId = "examiner", version = 1, expectedRevision = await Revision(owner, tenant) })))
            .GetProperty("instance").GetProperty("instanceId").GetGuid();
        Guid? binding = null;
        if (action == "unbind") binding = (await Json(await owner.PostAsJsonAsync(root + $"/agents/{agent}/credential-bindings",
            new TenantBindCredentialRequest(credential, "private-alias", await Revision(owner, tenant), 1, 1))))
            .GetProperty("binding").GetProperty("bindingId").GetGuid();
        var store = factory.Services.GetRequiredService<IIamStore>(); var before = (await store.ReadTenantAsync(tenant))!;
        async Task<string> Facts() => JsonSerializer.Serialize(new {
            state = await store.ReadTenantAsync(tenant),
            credential = await ((ITenantCredentialStore)store).FindTenantCredentialAsync(tenant, credential),
            bindings = await ((ITenantCredentialBindingStore)store).ReadTenantCredentialBindingsAsync(tenant, agent),
            audit = await store.ReadAuditAsync(tenant, 100)
        });
        var facts = await Facts();
        factory.BeforeCommand = () => RetireCredentialLogin(owner, boundary);
        Task<HttpResponseMessage> Invoke() => action switch {
            "create" => owner.PostAsJsonAsync(root + "/credentials", new TenantCredentialCreateRequest("New protected metadata", "Password", "General", before.Tenant.Revision, ProtectedValue: "http-replacement-sentinel")),
            "update" => owner.PatchAsJsonAsync(root + $"/credentials/{credential}", new TenantCredentialUpdateRequest("Changed private metadata", "Disabled", before.Tenant.Revision, 1)),
            "replace" => owner.PutAsJsonAsync(root + $"/credentials/{credential}/value", new TenantCredentialReplaceRequest("http-replacement-sentinel", before.Tenant.Revision, 1)),
            "delete" => owner.DeleteAsync(root + $"/credentials/{credential}?expectedRevision={before.Tenant.Revision}&expectedCredentialRevision=1"),
            "bind" => owner.PostAsJsonAsync(root + $"/agents/{agent}/credential-bindings", new TenantBindCredentialRequest(credential, "private-alias", before.Tenant.Revision, 1, 1)),
            _ => owner.DeleteAsync(root + $"/agents/{agent}/credential-bindings/{binding}?expectedRevision={before.Tenant.Revision}&expectedInstanceRevision=2&expectedBindingRevision=1")
        };
        using var refused = await Invoke();
        Assert.Null(factory.BeforeCommand);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        var error = await Json(refused); Safe(error); Assert.Equal("initiator_unavailable", error.GetProperty("reasonCode").GetString());
        Assert.Equal(facts, await Facts());
        Assert.Equal(HttpStatusCode.Unauthorized, (await owner.GetAsync(root + "/credentials")).StatusCode);
        await Login(owner, "owner@example.test", boundary == "password" ? "replacement-synthetic-password" : "explicit-synthetic-test-password");
        using var allowed = await Invoke(); Assert.True(allowed.IsSuccessStatusCode, await allowed.Content.ReadAsStringAsync());
        Assert.Equal(before.Tenant.Revision + 1, await Revision(owner, tenant));
        Assert.Equal(before.Assignments, (await store.ReadTenantAsync(tenant))!.Assignments);
    }

    public static IEnumerable<object[]> CredentialReadRetirementCases() =>
        from sqlite in new[] { false, true }
        from boundary in new[] { "logout", "password" }
        from action in new[] { "list", "read", "access" }
        select new object[] { sqlite, boundary, action };

    [Theory]
    [MemberData(nameof(CredentialReadRetirementCases))]
    public async Task Credential_HTTP_read_withholds_captured_metadata_after_original_login_retirement(bool sqlite, string boundary, string action)
    {
        await using var factory = new CredentialAuthorityFactory(sqlite);
        using var owner = Client(factory); await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
        var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid();
        var root = $"/api/v2/tenants/{tenant}/credentials";
        var credential = (await Json(await owner.PostAsJsonAsync(root, new TenantCredentialCreateRequest("Captured private metadata", "Password", "General",
            await Revision(owner, tenant), ProtectedValue: "http-private-sentinel")))).GetProperty("credential").GetProperty("credentialId").GetGuid();
        var path = action == "list" ? root : root + $"/{credential}" + (action == "access" ? "/access" : "");
        var before = await Revision(owner, tenant);
        factory.AfterCredentialRead = () => RetireCredentialLogin(owner, boundary);
        using var refused = await owner.GetAsync(path); Assert.Null(factory.AfterCredentialRead);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        var error = await Json(refused); Safe(error); Assert.Equal("initiator_unavailable", error.GetProperty("reasonCode").GetString());
        Assert.DoesNotContain("Captured private metadata", error.GetRawText()); Assert.False(error.TryGetProperty("permissions", out _)); Assert.False(error.TryGetProperty("items", out _));
        await Login(owner, "owner@example.test", boundary == "password" ? "replacement-synthetic-password" : "explicit-synthetic-test-password");
        using var allowed = await owner.GetAsync(path); Assert.Equal(HttpStatusCode.OK, allowed.StatusCode); Safe(await Json(allowed));
        Assert.Equal(before, await Revision(owner, tenant));
    }
    private static async Task RetireCredentialLogin(HttpClient owner, string boundary)
    {
        using var retired = boundary == "logout" ? await owner.PostAsync("/api/v2/auth/logout", null)
            : await owner.PostAsJsonAsync("/api/v2/account/password", new { currentPassword = "explicit-synthetic-test-password", newPassword = "replacement-synthetic-password" });
        Assert.True(retired.IsSuccessStatusCode, await retired.Content.ReadAsStringAsync());
    }
    private sealed class CredentialAuthorityFactory(bool sqlite) : AgentCoreApiFactory
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "tenant-credential-authority-" + Guid.NewGuid().ToString("N"));
        public Func<Task>? BeforeCommand;
        public Func<Task>? AfterCredentialRead;
        protected override IReadOnlyDictionary<string, string?> ExtraConfiguration => new Dictionary<string, string?> { ["Persistence:WorkspaceRoot"] = Path.Combine(root, "workspaces") };
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services => {
                var options = (PersistenceOptions)services.Single(d => d.ServiceType == typeof(PersistenceOptions)).ImplementationInstance!;
                options.CredentialProtectionKeyRoot = Path.Combine(root, "keys"); options.ArtifactRoot = Path.Combine(root, "artifacts");
                if (sqlite) {
                    Directory.CreateDirectory(root); var connection = $"Data Source={Path.Combine(root, "state.db")};Foreign Keys=True;Default Timeout=10";
                    services.AddDbContextFactory<AgentCoreDbContext>(o => o.UseSqlite(connection)); TestSqliteIdentity.Configure(services, connection, Path.Combine(root, "keys"));
                }
                services.Replace(ServiceDescriptor.Singleton<ITenantCredentialStore>(p => new CredentialAuthorityStore((ITenantCredentialStore)p.GetRequiredService<IIamStore>(), (ITenantCredentialBindingStore)p.GetRequiredService<IIamStore>(), this)));
                services.Replace(ServiceDescriptor.Singleton<ITenantCredentialBindingStore>(p => new CredentialAuthorityStore((ITenantCredentialStore)p.GetRequiredService<IIamStore>(), (ITenantCredentialBindingStore)p.GetRequiredService<IIamStore>(), this)));
            });
        }
        public override async ValueTask DisposeAsync() { await base.DisposeAsync(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private sealed class CredentialAuthorityStore(ITenantCredentialStore credentials, ITenantCredentialBindingStore bindings, CredentialAuthorityFactory factory)
        : ITenantCredentialStore, ITenantCredentialBindingStore
    {
        private async ValueTask Before() { if (Interlocked.Exchange(ref factory.BeforeCommand, null) is { } hook) await hook(); }
        private async ValueTask After() { if (Interlocked.Exchange(ref factory.AfterCredentialRead, null) is { } hook) await hook(); }
        public ValueTask<IReadOnlyDictionary<Guid, DateTimeOffset>> ReadTenantCredentialRotationsAsync(Guid tenant, IReadOnlyList<Guid> ids, CancellationToken ct = default)
            => credentials.ReadTenantCredentialRotationsAsync(tenant, ids, ct);
        public async ValueTask<TenantCredential?> FindTenantCredentialAsync(Guid tenant, Guid id, CancellationToken ct = default)
        { var value = await credentials.FindTenantCredentialAsync(tenant, id, ct); await After(); return value; }
        public async ValueTask<TenantCredentialPage> ListTenantCredentialsAsync(Guid tenant, IReadOnlyList<Guid>? ids, int page, int size, CancellationToken ct = default)
        { var value = await credentials.ListTenantCredentialsAsync(tenant, ids, page, size, ct); await After(); return value; }
        public async ValueTask<bool> TrySaveTenantCredentialAsync(TenantSecurityState next, long expectedSecurityRevision, TenantCredentialRoot owner, Credential value,
            long expectedRevision, long expectedSecretRevision, AuthorizationAudit audit, CancellationToken ct = default, HumanSessionReference? initiator = null)
        { await Before(); return await credentials.TrySaveTenantCredentialAsync(next, expectedSecurityRevision, owner, value, expectedRevision, expectedSecretRevision, audit, ct, initiator); }
        public async ValueTask<bool> TryDeleteTenantCredentialAsync(TenantSecurityState next, long expectedSecurityRevision, Guid credentialId, long expectedRevision,
            long expectedSecretRevision, DateTimeOffset at, AuthorizationAudit audit, CancellationToken ct = default, HumanSessionReference? initiator = null)
        { await Before(); return await credentials.TryDeleteTenantCredentialAsync(next, expectedSecurityRevision, credentialId, expectedRevision, expectedSecretRevision, at, audit, ct, initiator); }
        public ValueTask<TenantCredentialBindingSnapshot?> ReadTenantCredentialBindingsAsync(Guid tenantId, Guid instanceId, CancellationToken ct = default) => bindings.ReadTenantCredentialBindingsAsync(tenantId, instanceId, ct);
        public async ValueTask<TenantAgent?> TryBindTenantCredentialAsync(TenantSecurityState next, long expectedSecurityRevision, AgentCredentialBinding binding,
            long expectedInstanceRevision, long expectedCredentialRevision, AuthorizationAudit audit, CancellationToken ct = default, HumanSessionReference? initiator = null)
        { await Before(); return await bindings.TryBindTenantCredentialAsync(next, expectedSecurityRevision, binding, expectedInstanceRevision, expectedCredentialRevision, audit, ct, initiator); }
        public async ValueTask<TenantAgent?> TryUnbindTenantCredentialAsync(TenantSecurityState next, long expectedSecurityRevision, Guid instanceId, Guid bindingId,
            long expectedInstanceRevision, long expectedBindingRevision, DateTimeOffset at, AuthorizationAudit audit, CancellationToken ct = default, HumanSessionReference? initiator = null)
        { await Before(); return await bindings.TryUnbindTenantCredentialAsync(next, expectedSecurityRevision, instanceId, bindingId, expectedInstanceRevision, expectedBindingRevision, at, audit, ct, initiator); }
    }
}
