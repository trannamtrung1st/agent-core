using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentCore.Api.Security;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Definitions;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed class IdentitySqliteRestartTests
{
    [Fact]
    public async Task Real_identity_http_composition_keeps_cookie_and_tenant_authority_across_sqlite_host_restart()
    {
        var root = Path.Combine(Path.GetTempPath(), $"iam-http-restart-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var options = new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite($"Data Source={Path.Combine(root, "identity.db")};Foreign Keys=True").Options;
        var contexts = new ContextFactory(options);
        try
        {
            await using (var db = contexts.CreateDbContext()) await db.Database.MigrateAsync();
            var ids = new SystemIdGenerator(TimeProvider.System);
            var identity = new IdentityService(new SqliteIamStore(contexts), new AccountPasswordHasher(), new IdentityTokens(), ids, TimeProvider.System);
            var first = await identity.BootstrapAsync("restart@example.test", "Restart owner", "strong-restart-password", "Restart organization", "restart-organization");
            string cookie;
            Guid instanceId, agentPrincipalId;
            await using (var app = await StartAsync(root, contexts))
            {
                using var client = app.GetTestClient(); client.BaseAddress = new("https://localhost");
                var csrfResponse = await client.GetAsync("/api/v2/auth/csrf");
                Assert.Equal(HttpStatusCode.OK, csrfResponse.StatusCode);
                var csrfCookie = Assert.Single(csrfResponse.Headers.GetValues("Set-Cookie"), value => value.StartsWith("agentcore.csrf=", StringComparison.Ordinal)).Split(';')[0];
                var csrf = await Json(csrfResponse);
                client.DefaultRequestHeaders.Add("Cookie", csrfCookie);
                client.DefaultRequestHeaders.Add("X-AgentCore-CSRF", csrf.GetProperty("token").GetString());
                var login = await client.PostAsJsonAsync("/api/v2/auth/login", new { login = "restart@example.test", password = "strong-restart-password" });
                Assert.Equal(HttpStatusCode.OK, login.StatusCode);
                cookie = Assert.Single(login.Headers.GetValues("Set-Cookie"), value => value.StartsWith("agentcore.session=", StringComparison.Ordinal)).Split(';')[0];
                client.DefaultRequestHeaders.Remove("Cookie"); client.DefaultRequestHeaders.Add("Cookie", cookie + "; " + csrfCookie);
                var authenticatedCsrf = await Json(await client.GetAsync("/api/v2/auth/csrf"));
                client.DefaultRequestHeaders.Remove("X-AgentCore-CSRF");
                client.DefaultRequestHeaders.Add("X-AgentCore-CSRF", authenticatedCsrf.GetProperty("token").GetString());
                var members = await client.GetAsync($"/api/v2/tenants/{first.TenantId:D}/members");
                Assert.Equal(HttpStatusCode.OK, members.StatusCode);
                Assert.Equal(first.OwnerPrincipalId, (await Json(members)).GetProperty("items")[0].GetProperty("principalId").GetGuid());
                var created = await client.PostAsJsonAsync($"/api/v2/tenants/{first.TenantId:D}/agents",
                    new { templateId = "examiner", version = 1, expectedRevision = 1 });
                Assert.Equal(HttpStatusCode.Created, created.StatusCode);
                var agent = await Json(created);
                instanceId = agent.GetProperty("instance").GetProperty("instanceId").GetGuid();
                agentPrincipalId = agent.GetProperty("principalId").GetGuid();
                await app.StopAsync();
            }
            await using (var reopened = await StartAsync(root, contexts))
            {
                using var client = reopened.GetTestClient(); client.BaseAddress = new("https://localhost");
                client.DefaultRequestHeaders.Add("Cookie", cookie);
                var current = await client.GetAsync("/api/v2/account/me");
                Assert.Equal(HttpStatusCode.OK, current.StatusCode);
                Assert.Equal(first.AccountId, (await Json(current)).GetProperty("account").GetProperty("accountId").GetGuid());
                var members = await client.GetAsync($"/api/v2/tenants/{first.TenantId:D}/members");
                Assert.Equal(HttpStatusCode.OK, members.StatusCode);
                Assert.Equal(1, (await Json(members)).GetProperty("total").GetInt32());
                var agents = await client.GetAsync($"/api/v2/tenants/{first.TenantId:D}/agents");
                Assert.Equal(HttpStatusCode.OK, agents.StatusCode);
                var inventory = await Json(agents);
                Assert.Equal(1, inventory.GetProperty("total").GetInt32());
                Assert.Equal(instanceId, inventory.GetProperty("items")[0].GetProperty("instance").GetProperty("instanceId").GetGuid());
                Assert.Equal(agentPrincipalId, inventory.GetProperty("items")[0].GetProperty("principalId").GetGuid());
                Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/v2/tenants/{Guid.NewGuid():D}/members")).StatusCode);
                await reopened.StopAsync();
            }
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, recursive: true); }
    }

    // Uses the production identity registration, cookie/CSRF middleware and endpoint mappers. Other
    // runtime/worker boundaries are intentionally excluded and are not claimed verified by this test.
    private static async Task<WebApplication> StartAsync(string root, IDbContextFactory<AgentCoreDbContext> contexts)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>());
        builder.Services.AddSingleton(contexts);
        builder.Services.AddSingleton(new PersistenceOptions { Provider = "Sqlite", CredentialProtectionKeyRoot = Path.Combine(root, "keys") });
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<IIdGenerator, SystemIdGenerator>();
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "agents"))) directory = directory.Parent;
        var agentRoot = Path.Combine(directory!.FullName, "agents");
        var builtIns = new FileAgentDefinitionStore(agentRoot, SyntheticProviderAliases.Default);
        builder.Services.AddSingleton<IBuiltInAgentDefinitionStore>(builtIns);
        var content = new FileDefinitionResourceContentStore(Path.Combine(root, "resources"));
        builder.Services.AddSingleton<IDefinitionResourceContentStore>(content);
        builder.Services.AddSingleton(new AgentCore.Application.Agents.InheritedDefinitionResourceCatalog(
            new SqliteAgentDefinitionResourceAdminStore(contexts, content, new SystemIdGenerator(TimeProvider.System)),
            builtIns, new FileApprovedKnowledgeCatalog(agentRoot), content));
        builder.Services.AddAgentCoreIdentity();
        builder.Services.AddIdentityHttp(builder.Configuration);
        var app = builder.Build();
        app.UseIdentityHttp(); IdentityEndpoints.Map(app); TenantAccessEndpoints.Map(app); TenantAgentEndpoints.Map(app);
        await app.StartAsync(); return app;
    }
    private static async Task<JsonElement> Json(HttpResponseMessage response)
    { using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); return document.RootElement.Clone(); }
    private sealed class ContextFactory(DbContextOptions<AgentCoreDbContext> options) : IDbContextFactory<AgentCoreDbContext>
    { public AgentCoreDbContext CreateDbContext() => new(options); }
}
