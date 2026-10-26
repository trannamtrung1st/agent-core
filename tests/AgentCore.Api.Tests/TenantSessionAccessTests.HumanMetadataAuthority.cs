using System.Net;
using System.Net.Http.Json;
using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
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
    public static IEnumerable<object[]> SessionMetadataAuthorityCases() =>
        from sqlite in new[] { false, true }
        from action in new[] { "list", "read" }
        from boundary in new[] { "logout", "relogin", "password", "security" }
        select new object[] { sqlite, action, boundary };

    [Theory]
    [MemberData(nameof(SessionMetadataAuthorityCases))]
    public async Task Session_metadata_HTTP_rechecks_original_login_and_security_after_storage_IO(bool sqlite, string action, string boundary)
    {
        await using var factory = new SessionMetadataAuthorityFactory(sqlite); using var owner = Client(factory);
        await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
        var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid();
        var api = $"/api/v2/tenants/{tenant}";
        var agent = await Json(await owner.PostAsJsonAsync(api + "/agents", new { templateId = "examiner", version = 1, expectedRevision = await Revision(owner, tenant) }));
        var instance = agent.GetProperty("instance").GetProperty("instanceId").GetGuid();
        var created = await Json(await owner.PostAsJsonAsync(api + "/sessions", new { retentionKind = "saved", agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1 }));
        var session = created.GetProperty("sessionId").GetGuid();
        Assert.True((await owner.PatchAsJsonAsync(api + $"/sessions/{session}", new { title = "Private metadata retirement sentinel", expectedRevision = await Revision(owner, tenant), expectedSessionRevision = 1 })).IsSuccessStatusCode);
        var path = api + "/sessions" + (action == "read" ? $"/{session}" : "");
        factory.AfterRead = async () => {
            if (boundary == "security") {
                Assert.Equal(HttpStatusCode.Created, (await owner.PostAsJsonAsync(api + "/roles", new { name = "Concurrent metadata reader role", permissions = new[] { "sessions.read" }, expectedRevision = await Revision(owner, tenant) })).StatusCode);
            } else if (boundary == "password") {
                Assert.True((await owner.PostAsJsonAsync("/api/v2/account/password", new { currentPassword = "explicit-synthetic-test-password", newPassword = "replacement-synthetic-password" })).IsSuccessStatusCode);
            } else {
                Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsync("/api/v2/auth/logout", null)).StatusCode);
                if (boundary == "relogin") await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
            }
        };
        using var refused = await owner.GetAsync(path); Assert.Null(factory.AfterRead);
        Assert.Equal(boundary == "security" ? HttpStatusCode.Conflict : HttpStatusCode.Forbidden, refused.StatusCode);
        var body = await Json(refused); Assert.Equal(boundary == "security" ? "stale_revision" : "initiator_unavailable", body.GetProperty("reasonCode").GetString());
        Assert.DoesNotContain("Private metadata retirement sentinel", body.GetRawText());
        Assert.False(body.TryGetProperty("items", out _)); Assert.False(body.TryGetProperty("sessionId", out _));
        if (boundary is "logout" or "password") await Login(owner, "owner@example.test", boundary == "password" ? "replacement-synthetic-password" : "explicit-synthetic-test-password");
        using var fresh = await owner.GetAsync(path); Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
        var value = await Json(fresh);
        if (action == "read") { Assert.Equal(session, value.GetProperty("sessionId").GetGuid()); Assert.Equal("Private metadata retirement sentinel", value.GetProperty("title").GetString()); }
        else { Assert.Equal(1, value.GetProperty("total").GetInt32()); var item = Assert.Single(value.GetProperty("items").EnumerateArray()); Assert.Equal(session, item.GetProperty("sessionId").GetGuid()); Assert.Equal("Private metadata retirement sentinel", item.GetProperty("title").GetString()); }
    }

    private sealed class SessionMetadataAuthorityFactory(bool sqlite) : AgentCoreApiFactory
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "tenant-session-metadata-authority-" + Guid.NewGuid().ToString("N"));
        public Func<Task>? AfterRead;
        protected override IReadOnlyDictionary<string, string?> ExtraConfiguration => new Dictionary<string, string?> { ["Persistence:WorkspaceRoot"] = Path.Combine(root, "workspaces") };
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services => {
                var options = (PersistenceOptions)services.Single(d => d.ServiceType == typeof(PersistenceOptions)).ImplementationInstance!;
                options.CredentialProtectionKeyRoot = Path.Combine(root, "keys"); options.ArtifactRoot = Path.Combine(root, "artifacts");
                if (sqlite) { Directory.CreateDirectory(root); var connection = $"Data Source={Path.Combine(root, "state.db")};Foreign Keys=True;Default Timeout=10";
                    services.AddDbContextFactory<AgentCoreDbContext>(o => o.UseSqlite(connection)); TestSqliteIdentity.Configure(services, connection, Path.Combine(root, "keys")); }
                services.Replace(ServiceDescriptor.Singleton<ITenantSessionStore>(p => new SessionMetadataAuthorityStore((ITenantSessionStore)p.GetRequiredService<IIamStore>(), this)));
            });
        }
        public override async ValueTask DisposeAsync() { await base.DisposeAsync(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private sealed class SessionMetadataAuthorityStore(ITenantSessionStore inner, SessionMetadataAuthorityFactory factory) : ITenantSessionStore
    {
        public ValueTask<IReadOnlyList<TenantSessionRetentionTarget>> ListDueSessionsAsync(TenantSessionRetentionTarget? after, int limit, CancellationToken ct = default) => inner.ListDueSessionsAsync(after, limit, ct);
        public ValueTask<TenantSessionRetentionTarget?> FindExpiredSessionAsync(Guid tenant, Guid session, CancellationToken ct = default) => inner.FindExpiredSessionAsync(tenant, session, ct);
        public ValueTask<bool> RetireDueSessionAsync(TenantSessionRetentionTarget target, Guid auditId, CancellationToken ct = default) => inner.RetireDueSessionAsync(target, auditId, ct);

        private async ValueTask After() { if (Interlocked.Exchange(ref factory.AfterRead, null) is { } hook) await hook(); }
        public async ValueTask<TenantSession?> FindTenantSessionAsync(Guid tenantId, Guid sessionId, CancellationToken ct = default)
        { var value = await inner.FindTenantSessionAsync(tenantId, sessionId, ct); await After(); return value; }
        public async ValueTask<TenantSessionPage> ListTenantSessionsAsync(Guid tenantId, IReadOnlyList<Guid>? allowedIds, int page, int pageSize, CancellationToken ct = default, bool includeArchived = true, SessionSurface? surface = null, Guid? instanceId = null, bool recentFirst = false)
        { var value = await inner.ListTenantSessionsAsync(tenantId, allowedIds, page, pageSize, ct, includeArchived, surface, instanceId, recentFirst); await After(); return value; }
        public ValueTask<TenantSession?> TryChangeTenantSessionAsync(TenantSecurityState next, long expectedSecurityRevision, TenantSessionChange change, TenantSessionInitiator initiator, DateTimeOffset at, AuthorizationAudit audit, CancellationToken ct = default) => inner.TryChangeTenantSessionAsync(next, expectedSecurityRevision, change, initiator, at, audit, ct);
        public ValueTask<bool> TryCreateTenantSessionAsync(TenantSecurityState next, long expectedSecurityRevision, TenantSessionRoot owner, SessionSnapshot snapshot, TenantSessionInitiator initiator, long expectedInstanceRevision, AuthorizationAudit audit, CancellationToken ct = default) => inner.TryCreateTenantSessionAsync(next, expectedSecurityRevision, owner, snapshot, initiator, expectedInstanceRevision, audit, ct);
    }
}
