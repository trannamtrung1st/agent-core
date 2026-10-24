using System.Net;
using System.Net.Http.Json;
using AgentCore.Application.Ports;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    public static IEnumerable<object[]> UserProfileFinalReadAuthorityCases() =>
        from sqlite in new[] { false, true }
        from action in new[] { "read", "update-result" }
        from boundary in new[] { "logout", "relogin", "password", "security" }
        select new object[] { sqlite, action, boundary };

    [Theory]
    [MemberData(nameof(UserProfileFinalReadAuthorityCases))]
    public async Task User_Profile_HTTP_withholds_private_values_after_the_last_comparison_read_loses_original_authority(bool sqlite, string action, string boundary)
    {
        await using var factory = new ProfileFinalReadAuthorityFactory(sqlite); using var owner = Client(factory);
        await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
        var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid();
        var api = $"/api/v2/tenants/{tenant}"; var path = api + "/me/profile";
        var empty = await Json(await owner.GetAsync(path));
        var before = await Json(await owner.PatchAsJsonAsync(path, new { expectedRevision = empty.GetProperty("securityRevision").GetInt64(), expectedProfileRevision = 0,
            values = new { preferredName = "Private Profile retirement sentinel" } }));
        factory.ReadsRemaining = action == "read" ? 2 : 4;
        factory.AfterRead = async () => {
            if (boundary == "security") {
                Assert.Equal(HttpStatusCode.Created, (await owner.PostAsJsonAsync(api + "/roles", new { name = "Concurrent Profile reader role", permissions = new[] { "sessions.read" }, expectedRevision = await Revision(owner, tenant) })).StatusCode);
            } else if (boundary == "password") {
                Assert.True((await owner.PostAsJsonAsync("/api/v2/account/password", new { currentPassword = "explicit-synthetic-test-password", newPassword = "replacement-synthetic-password" })).IsSuccessStatusCode);
            } else {
                Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsync("/api/v2/auth/logout", null)).StatusCode);
                if (boundary == "relogin") await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
            }
        };
        using var refused = action == "read" ? await owner.GetAsync(path)
            : await owner.PatchAsJsonAsync(path, new { expectedRevision = before.GetProperty("securityRevision").GetInt64(), expectedProfileRevision = 1,
                values = new { preferredName = "Private Profile applied-once sentinel" } });
        Assert.Null(factory.AfterRead); Assert.Equal(0, factory.ReadsRemaining);
        Assert.Equal(boundary == "security" ? HttpStatusCode.Conflict : HttpStatusCode.Forbidden, refused.StatusCode);
        var body = await Json(refused); Assert.Equal(boundary == "security" ? "profile_changed" : "initiator_unavailable", body.GetProperty("reasonCode").GetString());
        Assert.False(body.TryGetProperty("profile", out _)); Assert.DoesNotContain("sentinel", body.GetRawText());
        if (boundary is "logout" or "password") await Login(owner, "owner@example.test", boundary == "password" ? "replacement-synthetic-password" : "explicit-synthetic-test-password");
        var factual = await Json(await owner.GetAsync(path)); var profile = factual.GetProperty("profile");
        Assert.Equal(action == "read" ? 1 : 2, profile.GetProperty("revision").GetInt64());
        Assert.Equal(action == "read" ? "Private Profile retirement sentinel" : "Private Profile applied-once sentinel", profile.GetProperty("preferences").GetProperty("preferredName").GetProperty("value").GetString());
    }

    private sealed class ProfileFinalReadAuthorityFactory(bool sqlite) : AgentCoreApiFactory
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "tenant-profile-final-read-" + Guid.NewGuid().ToString("N"));
        public int ReadsRemaining; public Func<Task>? AfterRead;
        protected override IReadOnlyDictionary<string, string?> ExtraConfiguration => new Dictionary<string, string?> { ["Persistence:WorkspaceRoot"] = Path.Combine(root, "workspaces") };
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services => {
                var options = (PersistenceOptions)services.Single(d => d.ServiceType == typeof(PersistenceOptions)).ImplementationInstance!;
                options.CredentialProtectionKeyRoot = Path.Combine(root, "keys"); options.ArtifactRoot = Path.Combine(root, "artifacts");
                if (sqlite) { Directory.CreateDirectory(root); var connection = $"Data Source={Path.Combine(root, "state.db")};Foreign Keys=True;Default Timeout=10";
                    services.AddDbContextFactory<AgentCoreDbContext>(o => o.UseSqlite(connection)); TestSqliteIdentity.Configure(services, connection, Path.Combine(root, "keys")); }
                services.Replace(ServiceDescriptor.Singleton<ITenantUserProfileStore>(p => new ProfileFinalReadAuthorityStore((ITenantUserProfileStore)p.GetRequiredService<IIamStore>(), this)));
            });
        }
        public override async ValueTask DisposeAsync() { await base.DisposeAsync(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private sealed class ProfileFinalReadAuthorityStore(ITenantUserProfileStore inner, ProfileFinalReadAuthorityFactory factory) : ITenantUserProfileStore
    {
        public async ValueTask<TenantUserProfileView> ReadOwnTenantUserProfileAsync(Guid tenant, HumanSessionReference human, CancellationToken ct = default)
        {
            var value = await inner.ReadOwnTenantUserProfileAsync(tenant, human, ct);
            if (Volatile.Read(ref factory.ReadsRemaining) > 0 && Interlocked.Decrement(ref factory.ReadsRemaining) == 0
                && Interlocked.Exchange(ref factory.AfterRead, null) is { } hook) await hook();
            return value;
        }
        public ValueTask<TenantUserProfileView?> TrySaveOwnTenantUserProfileAsync(TenantUserProfile profile, HumanSessionReference human, long expectedSecurityRevision, long expectedProfileRevision, AuthorizationAudit audit, CancellationToken ct = default)
            => inner.TrySaveOwnTenantUserProfileAsync(profile, human, expectedSecurityRevision, expectedProfileRevision, audit, ct);
    }
}
