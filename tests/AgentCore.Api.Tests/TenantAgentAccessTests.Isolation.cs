using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed partial class TenantAgentAccessTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Tenant_isolation_coordinates_all_owners_presets_resets_and_retains_other_voice_fields(bool sqlite)
    {
        await using var factory = new IsolationFactory(sqlite);
        using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") });
        var (tenant, id, root) = await IsolationAgent(owner);
        var before = await Json(await owner.GetAsync(root));
        Assert.Equal("Active", before.GetProperty("agent").GetProperty("instance").GetProperty("lifecycle").GetString());
        var optIn = new { settings = Array.Empty<object>(), experienceEnabled = true, allowMaintenance = true };
        var preview = await Json(await owner.PostAsJsonAsync(root + "/preview", optIn));
        Assert.Equal(HttpStatusCode.OK, (await ApplyIsolation(owner, root, preview)).StatusCode);
        var enabled = await Json(await owner.GetAsync(root)); var config = enabled.GetProperty("configuration");
        Assert.True(config.GetProperty("experienceEnabled").GetBoolean()); Assert.True(config.GetProperty("allowMaintenance").GetBoolean());
        Assert.Equal(2, config.GetProperty("revisions").GetProperty("instance").GetInt64());
        Assert.Equal(1, config.GetProperty("revisions").GetProperty("experience").GetInt64()); Assert.Equal(1, config.GetProperty("revisions").GetProperty("maintenance").GetInt64());
        Assert.Equal(before.GetProperty("securityRevision").GetInt64() + 1, enabled.GetProperty("securityRevision").GetInt64());
        var voice = config.GetProperty("settings").EnumerateArray().Single(x => x.GetProperty("section").GetString() == "voice").GetProperty("effective").GetRawText();
        preview = await Json(await owner.PostAsync(root + "/presets/Stateless", null));
        Assert.Empty(preview.GetProperty("plan").GetProperty("blockingReasons").EnumerateArray());
        Assert.Equal(HttpStatusCode.OK, (await ApplyIsolation(owner, root, preview)).StatusCode);
        var isolated = await Json(await owner.GetAsync(root)); config = isolated.GetProperty("configuration");
        Assert.False(config.GetProperty("experienceEnabled").GetBoolean()); Assert.False(config.GetProperty("allowMaintenance").GetBoolean());
        var policy = config.GetProperty("settings").EnumerateArray().Single(x => x.GetProperty("section").GetString() == "stateIsolationPolicy");
        Assert.Equal("CurrentRequest", policy.GetProperty("effective").GetProperty("conversationContextScope").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await ApplyIsolation(owner, root, preview)).StatusCode);
        preview = await Json(await owner.PostAsync(root + "/presets/DefinitionDefaults", null));
        Assert.Equal(HttpStatusCode.OK, (await ApplyIsolation(owner, root, preview)).StatusCode);
        config = (await Json(await owner.GetAsync(root))).GetProperty("configuration");
        Assert.False(config.GetProperty("experienceEnabled").GetBoolean()); Assert.False(config.GetProperty("allowMaintenance").GetBoolean());
        foreach (var section in config.GetProperty("settings").EnumerateArray()) Assert.Empty(section.GetProperty("overrides").EnumerateObject());
        Assert.Equal(voice, config.GetProperty("settings").EnumerateArray().Single(x => x.GetProperty("section").GetString() == "voice").GetProperty("effective").GetRawText());
        var audit = await factory.Services.GetRequiredService<IIamStore>().ReadAuditAsync(tenant, 100);
        Assert.Equal(3, audit.Count(x => x.Action == "agents.state-isolation.update"));
        Assert.All(audit.Where(x => x.Action == "agents.state-isolation.update"), x => Assert.Equal(id.ToString("D"), x.Target));
        Assert.Equal("NotObserved", config.GetProperty("sandboxEnforcementState").GetString());
        if (sqlite)
        {
            var expected = (await Json(await owner.GetAsync(root))).GetRawText();
            await factory.DisposeAsync();
            await using var reopened = new IsolationFactory(true, factory.Root);
            using var again = reopened.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") });
            await Login(again, "owner@example.test", "explicit-synthetic-test-password");
            Assert.Equal(expected, (await Json(await again.GetAsync(root))).GetRawText());
        }
    }

    [Theory]
    [InlineData(false, "experience")]
    [InlineData(false, "maintenance")]
    [InlineData(false, "instance")]
    [InlineData(true, "experience")]
    [InlineData(true, "maintenance")]
    [InlineData(true, "instance")]
    public async Task Tenant_isolation_any_stale_owner_refuses_entire_transaction(bool sqlite, string changed)
    {
        await using var factory = new IsolationFactory(sqlite);
        using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") });
        var (tenant, id, root) = await IsolationAgent(owner);
        var preview = await Json(await owner.PostAsync(root + "/presets/Stateless", null));
        if (changed == "instance")
        {
            var value = await Json(await owner.GetAsync(root)); var instance = value.GetProperty("agent").GetProperty("instance");
            Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(root.Replace("/state-isolation", "/persona"), new {
                expectedRevision = value.GetProperty("securityRevision").GetInt64(), expectedInstanceRevision = instance.GetProperty("revision").GetInt64(),
                expectedPersonaRevision = instance.GetProperty("personaRevision").GetInt64(), persona = new { name = "Concurrent persona", role = "Guide", description = "Preserve this update", tone = "Clear" } })).StatusCode);
        }
        else if (!sqlite)
        {
            var experience = factory.Services.GetRequiredService<IExperienceStore>();
            if (changed == "experience") await experience.ConfigureAsync(id, 0, true);
            else await experience.ConfigureMaintenanceAsync(id, 0, true);
        }
        else
        {
            await using var db = await factory.Services.GetRequiredService<IDbContextFactory<AgentCoreDbContext>>().CreateDbContextAsync();
            if (changed == "experience") db.ExperienceSettings.Add(new() { AgentInstanceId = id.ToString("D"), Revision = 1, Enabled = true });
            else db.IdentityMaintenanceSettings.Add(new() { AgentInstanceId = id.ToString("D"), Revision = 1, AllowAgentConsolidation = true });
            await db.SaveChangesAsync();
        }
        var before = await Json(await owner.GetAsync(root));
        var audits = await factory.Services.GetRequiredService<IIamStore>().ReadAuditAsync(tenant, 100);
        Assert.Equal(HttpStatusCode.Conflict, (await ApplyIsolation(owner, root, preview)).StatusCode);
        Assert.Equal(before.GetRawText(), (await Json(await owner.GetAsync(root))).GetRawText());
        Assert.Equal(audits.Count, (await factory.Services.GetRequiredService<IIamStore>().ReadAuditAsync(tenant, 100)).Count);
        preview = await Json(await owner.PostAsync(root + "/presets/SessionIsolated", null));
        Assert.Equal(HttpStatusCode.OK, (await ApplyIsolation(owner, root, preview)).StatusCode);
    }

    [Fact]
    public async Task Tenant_isolation_rejects_foreign_ownership_forged_preview_and_invalid_coordinated_fields()
    {
        await using var factory = new IsolationFactory(false);
        using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") });
        var (tenant, _, root) = await IsolationAgent(owner);
        var beta = (await Json(await owner.PostAsJsonAsync("/api/v2/system/tenants", new { name = "Isolation foreign tenant", slug = "isolation-foreign" }))).GetProperty("tenantId").GetGuid();
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync(root.Replace(tenant.ToString(), beta.ToString()))).StatusCode);
        var plan = await Json(await owner.PostAsync(root + "/presets/Stateless", null));
        Assert.Equal(HttpStatusCode.Conflict, (await ApplyIsolation(owner, root, plan, "forged")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync(root + "/preview", new { settings = new[] { new { section = "capabilities", set = new { selected = new[] { "browser.click" } }, clear = Array.Empty<string>() } }, experienceEnabled = false, allowMaintenance = false })).StatusCode);
        var malformed = await owner.PostAsJsonAsync(root + "/preview", new { settings = new[] { new { section = "voice", set = (object?)null, clear = Array.Empty<string>() } }, experienceEnabled = false, allowMaintenance = false });
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        owner.DefaultRequestHeaders.Remove("X-AgentCore-CSRF");
        Assert.Equal(HttpStatusCode.BadRequest, (await ApplyIsolation(owner, root, plan)).StatusCode);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Tenant_isolation_atomic_commit_refuses_metadata_changed_after_final_preview(bool sqlite, bool maintenance)
    {
        var temporary = Path.Combine(Path.GetTempPath(), "tenant-isolation-race-" + Guid.NewGuid().ToString("N"));
        await using var factory = new AgentReadFenceFactory(sqliteRoot: sqlite ? temporary : null);
        using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") });
        var (tenant, id, root) = await IsolationAgent(owner);
        var preview = await Json(await owner.PostAsync(root + "/presets/Stateless", null));
        var before = await Json(await owner.GetAsync(root));
        factory.CaptureAt = "configuration-commit";
        factory.AfterCapture = async () =>
        {
            if (!sqlite)
            {
                var store = factory.Services.GetRequiredService<IExperienceStore>();
                if (maintenance) await store.ConfigureMaintenanceAsync(id, 0, true); else await store.ConfigureAsync(id, 0, true);
            }
            else
            {
                await using var db = await factory.Services.GetRequiredService<IDbContextFactory<AgentCoreDbContext>>().CreateDbContextAsync();
                if (maintenance) db.IdentityMaintenanceSettings.Add(new() { AgentInstanceId = id.ToString("D"), Revision = 1, AllowAgentConsolidation = true });
                else db.ExperienceSettings.Add(new() { AgentInstanceId = id.ToString("D"), Revision = 1, Enabled = true });
                await db.SaveChangesAsync();
            }
        };
        Assert.Equal(HttpStatusCode.Conflict, (await ApplyIsolation(owner, root, preview)).StatusCode); Assert.Null(factory.AfterCapture);
        var after = await Json(await owner.GetAsync(root));
        Assert.Equal(before.GetProperty("agent").GetRawText(), after.GetProperty("agent").GetRawText());
        Assert.Equal(before.GetProperty("securityRevision").GetInt64(), after.GetProperty("securityRevision").GetInt64());
        Assert.Equal(before.GetProperty("configuration").GetProperty("settings").GetRawText(), after.GetProperty("configuration").GetProperty("settings").GetRawText());
        Assert.True(after.GetProperty("configuration").GetProperty(maintenance ? "allowMaintenance" : "experienceEnabled").GetBoolean());
        Assert.DoesNotContain(await factory.Services.GetRequiredService<IIamStore>().ReadAuditAsync(tenant, 100), item => item.Action == "agents.state-isolation.update");
    }

    private static async Task<(Guid Tenant, Guid Id, string Root)> IsolationAgent(HttpClient owner)
    {
        await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
        var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid();
        var created = await Json(await owner.PostAsJsonAsync($"/api/v2/tenants/{tenant}/agents", new { templateId = "examiner", version = 1, expectedRevision = await Revision(owner, tenant) }));
        var id = created.GetProperty("instance").GetProperty("instanceId").GetGuid(); return (tenant, id, $"/api/v2/tenants/{tenant}/agents/{id}/state-isolation");
    }
    private static Task<HttpResponseMessage> ApplyIsolation(HttpClient owner, string root, JsonElement preview, string? fingerprint = null) => owner.PostAsJsonAsync(root + "/apply", new {
        expectedRevision = preview.GetProperty("securityRevision").GetInt64(), expected = preview.GetProperty("plan").GetProperty("expected"),
        draft = preview.GetProperty("plan").GetProperty("draft"), fingerprint = fingerprint ?? preview.GetProperty("plan").GetProperty("fingerprint").GetString() });
    private sealed class IsolationFactory(bool sqlite, string? root = null) : AgentCoreApiFactory
    {
        public string Root { get; } = root ?? Path.Combine(Path.GetTempPath(), "tenant-isolation-" + Guid.NewGuid().ToString("N"));
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            if (!sqlite) return;
            Directory.CreateDirectory(Root);
            builder.ConfigureTestServices(services => {
                var connection = $"Data Source={Path.Combine(Root, "state.db")};Foreign Keys=True;Default Timeout=10";
                services.AddDbContextFactory<AgentCoreDbContext>(options => options.UseSqlite(connection));
                TestSqliteIdentity.Configure(services, connection, Path.Combine(Root, "keys"));
            });
        }
    }
}
