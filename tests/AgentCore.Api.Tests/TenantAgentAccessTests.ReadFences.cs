using System.Net;
using System.Net.Http.Json;
using AgentCore.Application.Ports;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;
using Microsoft.AspNetCore.Hosting;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentCore.Api.Tests;

public sealed partial class TenantAgentAccessTests
{
    public static IEnumerable<object[]> AgentReadLogoutCases =>
        from read in new[] { "state-isolation", "settings", "execution-budgets", "self-management", "effective-config", "skills", "resources", "skills/editor", "resources/editor", "skills/builtin:configuration-assistance.v1/editor", "editor", "configuration-controls", "versions", "active-version/preview?version=1", "agent", "inventory", "templates" }
        from signInAgain in new[] { false, true }
        select new object[] { read, signInAgain };

    [Theory]
    [MemberData(nameof(AgentReadLogoutCases))]
    public async Task Agent_read_does_not_release_captured_data_after_original_session_logout(string read, bool signInAgain)
    {
        await using var factory = new AgentReadFenceFactory();
        using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
        var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid();
        var agent = await Json(await owner.PostAsJsonAsync($"/api/v2/tenants/{tenant}/agents", new { templateId = "examiner", version = 1, expectedRevision = await Revision(owner, tenant) }));
        var id = agent.GetProperty("instance").GetProperty("instanceId").GetGuid();
        factory.CaptureAt = read switch { "agent" => "agent", "inventory" => "inventory", "templates" or "versions" => "templates", _ => "definition" };
        factory.AfterCapture = async () =>
        {
            Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsync("/api/v2/auth/logout", null)).StatusCode);
            if (signInAgain) await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
        };
        var path = read switch { "agent" => $"agents/{id}", "inventory" => "agents", "templates" => "agent-templates", _ => $"agents/{id}/{read}" };
        var response = await owner.GetAsync($"/api/v2/tenants/{tenant}/{path}");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("initiator_unavailable", (await Json(response)).GetProperty("reasonCode").GetString());
        Assert.Null(factory.AfterCapture);
        if (signInAgain) Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync($"/api/v2/tenants/{tenant}/{path}")).StatusCode);
    }

    [Theory]
    [InlineData("agent")]
    [InlineData("inventory")]
    [InlineData("templates")]
    public async Task Directory_reads_reject_a_capture_when_tenant_security_changes_and_recover_explicitly(string read)
    {
        await using var factory = new AgentReadFenceFactory();
        using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
        var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid(); var prefix = $"/api/v2/tenants/{tenant}";
        var agent = await Json(await owner.PostAsJsonAsync(prefix + "/agents", new { templateId = "examiner", version = 1, expectedRevision = await Revision(owner, tenant) }));
        var id = agent.GetProperty("instance").GetProperty("instanceId").GetGuid();
        factory.CaptureAt = read; factory.AfterCapture = async () => Assert.Equal(HttpStatusCode.Created,
            (await owner.PostAsJsonAsync(prefix + "/roles", new { name = "Concurrent inventory role", permissions = new[] { "agents.read" }, expectedRevision = await Revision(owner, tenant) })).StatusCode);
        var path = prefix + "/" + (read switch { "agent" => $"agents/{id}", "inventory" => "agents", _ => "agent-templates" });
        var result = await owner.GetAsync(path);
        Assert.Equal(HttpStatusCode.Conflict, result.StatusCode); Assert.Equal("stale_revision", (await Json(result)).GetProperty("reasonCode").GetString()); Assert.Null(factory.AfterCapture);
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync(path)).StatusCode);
    }

    [Theory]
    [InlineData("logout")]
    [InlineData("relogin")]
    [InlineData("security")]
    public async Task Resource_download_fences_original_human_and_security_after_actual_byte_IO(string change)
    {
        await using var factory = new AgentReadFenceFactory();
        using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
        var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid(); var prefix = $"/api/v2/tenants/{tenant}";
        var agent = await Json(await owner.PostAsJsonAsync(prefix + "/agents", new { templateId = "examiner", version = 1, expectedRevision = await Revision(owner, tenant) }));
        var id = agent.GetProperty("instance").GetProperty("instanceId").GetGuid(); var root = prefix + $"/agents/{id}";
        var bytes = System.Text.Encoding.UTF8.GetBytes("Original session protected download sentinel.");
        var saved = await Json(await owner.PostAsJsonAsync(root + "/resources", new { expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1,
            input = new { logicalPath = "protected.txt", kind = 2, mediaType = "text/plain", content = bytes } }));
        var path = root + "/resources/" + Uri.EscapeDataString(saved.GetProperty("resource").GetProperty("key").GetString()!) + "/content";
        factory.CaptureAt = "bytes"; factory.AfterCapture = async () =>
        {
            if (change == "security") Assert.Equal(HttpStatusCode.Created, (await owner.PostAsJsonAsync(prefix + "/roles", new { name = "Concurrent harmless role", permissions = new[] { "agents.read" }, expectedRevision = await Revision(owner, tenant) })).StatusCode);
            else
            {
                Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsync("/api/v2/auth/logout", null)).StatusCode);
                if (change == "relogin") await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
            }
        };
        var result = await owner.GetAsync(path);
        Assert.Equal(change == "security" ? HttpStatusCode.Conflict : HttpStatusCode.Forbidden, result.StatusCode);
        Assert.DoesNotContain("protected download sentinel", await result.Content.ReadAsStringAsync()); Assert.Null(factory.AfterCapture);
        if (change != "logout") Assert.Equal(bytes, await owner.GetByteArrayAsync(path));
    }

    private sealed class AgentReadFenceFactory(IReadOnlyDictionary<string, string?>? settings = null, string? sqliteRoot = null) : AgentCoreApiFactory
    {
        protected override IReadOnlyDictionary<string, string?> ExtraConfiguration => settings ?? new Dictionary<string, string?>();
        public Func<Task>? AfterCapture;
        public int ContentReads;
        public string CaptureAt = "definition";
        public async Task Captured(string kind)
        {
            if (CaptureAt == kind && Interlocked.Exchange(ref AfterCapture, null) is { } action) await action();
        }
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                if (sqliteRoot is not null)
                {
                    Directory.CreateDirectory(sqliteRoot);
                    var connection = $"Data Source={Path.Combine(sqliteRoot, "state.db")};Foreign Keys=True;Default Timeout=10";
                    services.AddDbContextFactory<AgentCoreDbContext>(options => options.UseSqlite(connection));
                    TestSqliteIdentity.Configure(services, connection, Path.Combine(sqliteRoot, "keys"));
                }
                var descriptor = services.Single(d => d.ServiceType == typeof(IBuiltInAgentDefinitionStore));
                services.Replace(ServiceDescriptor.Singleton<IBuiltInAgentDefinitionStore>(provider => new AgentReadDefinitions(
                    Resolve<IBuiltInAgentDefinitionStore>(descriptor, provider), this)));
                var content = services.Single(d => d.ServiceType == typeof(IDefinitionResourceContentStore));
                services.Replace(ServiceDescriptor.Singleton<IDefinitionResourceContentStore>(provider => new AgentReadContent(Resolve<IDefinitionResourceContentStore>(content, provider), this)));
                var office = services.Single(d => d.ServiceType == typeof(IOfficeProcessor));
                services.Replace(ServiceDescriptor.Singleton<IOfficeProcessor>(provider => new AgentReadOffice(Resolve<IOfficeProcessor>(office, provider), this)));
                services.Replace(ServiceDescriptor.Singleton<ITenantAgentStore>(provider => new AgentReadStore((ITenantAgentStore)provider.GetRequiredService<IIamStore>(), this)));
            });
        }
        private static T Resolve<T>(ServiceDescriptor descriptor, IServiceProvider provider) =>
            (T)(descriptor.ImplementationInstance ?? descriptor.ImplementationFactory?.Invoke(provider) ?? ActivatorUtilities.CreateInstance(provider, descriptor.ImplementationType!));
    }
    private sealed class AgentReadContent(IDefinitionResourceContentStore inner, AgentReadFenceFactory factory) : IDefinitionResourceContentStore, ITenantResourceSourceContentStore
    {
        public ValueTask<byte[]?> ReadVerifiedResourceSourceAsync(string hash, long expectedByteSize, CancellationToken ct = default) =>
            ((ITenantResourceSourceContentStore)inner).ReadVerifiedResourceSourceAsync(hash, expectedByteSize, ct);
        public ValueTask StoreVerifiedAsync(string hash, byte[] content, CancellationToken ct = default) => inner.StoreVerifiedAsync(hash, content, ct);
        public async ValueTask<byte[]?> ReadAsync(string hash, CancellationToken ct = default)
        { factory.ContentReads++; var bytes = await inner.ReadAsync(hash, ct); await factory.Captured("bytes"); return bytes; }
    }
    private sealed class AgentReadOffice(IOfficeProcessor inner, AgentReadFenceFactory factory) : IOfficeProcessor
    {
        public OfficeProcessedFile CreateDocument(OfficeDocumentSpec spec, CancellationToken ct) => inner.CreateDocument(spec, ct);
        public OfficeProcessedFile CreateWorkbook(OfficeWorkbookSpec spec, CancellationToken ct) => inner.CreateWorkbook(spec, ct);
        public OfficeDocumentInspection InspectDocument(ReadOnlyMemory<byte> bytes, int offset, int limit, CancellationToken ct)
        { var result = inner.InspectDocument(bytes, offset, limit, ct); factory.Captured("office").GetAwaiter().GetResult(); return result; }
        public OfficeWorkbookInspection PreviewWorkbook(ReadOnlyMemory<byte> bytes, string? sheet, string? range, CancellationToken ct)
        { var result = inner.PreviewWorkbook(bytes, sheet, range, ct); factory.Captured("office").GetAwaiter().GetResult(); return result; }
        public OfficeProcessedFile EditDocument(ReadOnlyMemory<byte> bytes, IReadOnlyList<OfficeDocumentEdit> operations, CancellationToken ct) => inner.EditDocument(bytes, operations, ct);
        public OfficeWorkbookInspection InspectWorkbook(ReadOnlyMemory<byte> bytes, string? sheet, string? range, CancellationToken ct) => inner.InspectWorkbook(bytes, sheet, range, ct);
        public OfficeSparseWorkbookInspection ReadWorkbookContent(ReadOnlyMemory<byte> bytes, string? sheet, string? cursor, CancellationToken ct) => inner.ReadWorkbookContent(bytes, sheet, cursor, ct);
        public OfficeProcessedFile EditWorkbook(ReadOnlyMemory<byte> bytes, IReadOnlyList<OfficeSpreadsheetEdit> operations, CancellationToken ct) => inner.EditWorkbook(bytes, operations, ct);
    }
    private sealed class AgentReadDefinitions(IBuiltInAgentDefinitionStore inner, AgentReadFenceFactory factory) : IBuiltInAgentDefinitionStore
    {
        public async ValueTask<IReadOnlyList<AgentDefinition>> ListAsync(CancellationToken ct = default)
        {
            var result = await inner.ListAsync(ct); await factory.Captured("templates"); return result;
        }
        public async ValueTask<AgentDefinition?> GetAsync(string id, int? version = null, CancellationToken ct = default)
        {
            var captured = await inner.GetAsync(id, version, ct);
            await factory.Captured("definition");
            return captured;
        }
    }
    private sealed class AgentReadStore(ITenantAgentStore inner, AgentReadFenceFactory factory) : ITenantAgentStore
    {
        public ValueTask<IReadOnlyList<Guid>> ListAssistantTenantPageAsync(int page, int limit, CancellationToken ct = default) => inner.ListAssistantTenantPageAsync(page, limit, ct);
        public ValueTask<AgentCore.Domain.Security.AuthorizationAudit?> FindAgentOperationAsync(Guid tenant, Guid operation, CancellationToken ct = default) => inner.FindAgentOperationAsync(tenant, operation, ct);
        public ValueTask<bool> TryProvisionGeneralAssistantAsync(AgentCore.Domain.Security.TenantSecurityState next, long expected, AgentCore.Domain.Definitions.AgentInstance instance, IReadOnlyList<AgentCore.Domain.Definitions.SkillSpec> skills, AgentCore.Domain.Security.AuthorizationAudit audit, CancellationToken ct = default) => inner.TryProvisionGeneralAssistantAsync(next, expected, instance, skills, audit, ct);
        public ValueTask<TenantAgent?> TryAdoptGeneralAssistantAsync(AgentCore.Domain.Security.TenantSecurityState next, long expected, TenantAgentConfigurationChange change, DateTimeOffset at, AgentCore.Domain.Security.AuthorizationAudit audit, CancellationToken ct = default) => inner.TryAdoptGeneralAssistantAsync(next, expected, change, at, audit, ct);

        public async ValueTask<TenantAgent?> FindTenantAgentAsync(Guid tenant, Guid id, CancellationToken ct = default)
        { var result = await inner.FindTenantAgentAsync(tenant, id, ct); await factory.Captured("agent"); return result; }
        public async ValueTask<TenantAgentPage> ListTenantAgentsAsync(Guid tenant, IReadOnlyList<Guid>? allowed, int page, int pageSize, CancellationToken ct = default, TenantAgentChatFilter? chat = null)
        { var result = await inner.ListTenantAgentsAsync(tenant, allowed, page, pageSize, ct, chat); await factory.Captured("inventory"); return result; }
        public async ValueTask<TenantAgentConfigurationSnapshot?> ReadTenantAgentConfigurationAsync(Guid tenant, Guid id, CancellationToken ct = default)
        { var snapshot = await inner.ReadTenantAgentConfigurationAsync(tenant, id, ct); await factory.Captured("configuration-snapshot"); return snapshot; }
        public async ValueTask<TenantAgent?> TryChangeTenantAgentConfigurationAsync(TenantSecurityState next, long revision, TenantAgentConfigurationChange change, DateTimeOffset at, AuthorizationAudit audit, CancellationToken ct = default)
        { await factory.Captured("configuration-commit"); return await inner.TryChangeTenantAgentConfigurationAsync(next, revision, change, at, audit, ct); }
        public async ValueTask<bool> TryCreateTenantAgentAsync(TenantSecurityState next, long revision, AgentInstance instance, IReadOnlyList<SkillSpec> skills, AuthorizationAudit audit, CancellationToken ct = default, HumanSessionReference? initiator = null)
        { await factory.Captured("creation-commit"); return await inner.TryCreateTenantAgentAsync(next, revision, instance, skills, audit, ct, initiator); }
        public async ValueTask<TenantAgent?> TrySetTenantAgentLifecycleAsync(TenantSecurityState next, long revision, Guid id, long instanceRevision, AgentInstanceLifecycle lifecycle, DateTimeOffset at, AuthorizationAudit audit, CancellationToken ct = default, HumanSessionReference? initiator = null)
        { await factory.Captured("lifecycle-commit"); return await inner.TrySetTenantAgentLifecycleAsync(next, revision, id, instanceRevision, lifecycle, at, audit, ct, initiator); }
    }
}
