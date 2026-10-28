using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Security;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace AgentCore.Api.Tests;

public sealed partial class TenantAgentAccessTests
{
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Managed_setup_failure_preserves_created_tenants_and_explicit_owner_retry_recovers_without_restart(bool sqlite)
    {
        SetupFailureStore? fault = null; var logs = new AssistantSetupLogs();
        void Configure(IServiceCollection services)
        {
            services.Replace(ServiceDescriptor.Singleton<ILogger<ManagedGeneralAssistantService>>(logs));
            services.Replace(ServiceDescriptor.Singleton<ITenantAgentStore>(provider => {
                var proxy = DispatchProxy.Create<ITenantAgentStore, SetupFailureStore>();
                fault = (SetupFailureStore)(object)proxy; fault.Inner = (ITenantAgentStore)provider.GetRequiredService<IIamStore>(); return proxy;
            }));
        }
        await using var factory = new RuntimeStateFactory(sqlite, configure: Configure, nativeDefinitions: true);
        using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") });
        await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
        var original = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid();
        var initial = await Json(await owner.GetAsync($"/api/v2/tenants/{original}/chat/agents"));
        Assert.Equal("setup_pending", initial.GetProperty("emptyReason").GetString()); Assert.True(initial.GetProperty("canManageSetup").GetBoolean());
        var created = await owner.PostAsJsonAsync("/api/v2/system/tenants", new { name = "Pending setup", slug = "pending-setup" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var tenant = (await Json(created)).GetProperty("tenantId").GetGuid(); var root = $"/api/v2/tenants/{tenant}";
        var security = factory.Services.GetRequiredService<IIamStore>();
        var system = await security.ReadSystemAsync(); var systemHuman = system.Principals.Single(p => p.Kind == PrincipalKind.User);
        var legacyEntry = await factory.Services.GetRequiredService<TenantAccessService>().CreateTenantAsync(systemHuman.PrincipalId, systemHuman.AccountId!.Value, "Other pending", "other-pending");
        Assert.NotEqual(tenant, legacyEntry.Tenant.TenantId); Assert.Empty(legacyEntry.SystemIdentities);
        Assert.NotNull(await security.ReadTenantAsync(tenant));
        var attempts = fault!.Attempts;
        for (var i = 0; i < 3; i++)
            Assert.Equal("setup_pending", (await Json(await owner.GetAsync(root + "/chat/agents"))).GetProperty("emptyReason").GetString());
        Assert.Equal(attempts, fault.Attempts); // Discovery is read-only; no polling repair loop.
        var pending = await Json(await owner.PostAsync(root + "/general-assistant/reconcile", null));
        Assert.Equal("setup_pending", pending.GetProperty("status").GetString()); Assert.Equal(attempts + 1, fault.Attempts);
        Assert.All(logs.Messages, message => {
            Assert.Contains("reason unexpected_failure", message); Assert.Contains("System.InvalidOperationException", message);
            Assert.DoesNotContain("sensitive-provider-body", message); Assert.DoesNotContain("secret-test-token", message);
        });
        Assert.NotEmpty(logs.Messages);
        // Existing ordinary agents must not conceal missing default setup or its retry path.
        Assert.Equal(HttpStatusCode.Created, (await owner.PostAsJsonAsync(root + "/agents", new { templateId = "examiner", version = 1, expectedRevision = await Revision(owner, tenant) })).StatusCode);
        var ordinary = await Json(await owner.GetAsync(root + "/chat/agents"));
        Assert.Equal(1, ordinary.GetProperty("total").GetInt32()); Assert.True(ordinary.GetProperty("assistantSetupPending").GetBoolean());
        fault.Fail = false;
        Assert.Equal("ready", (await Json(await owner.PostAsync(root + "/general-assistant/reconcile", null))).GetProperty("status").GetString());
        var recovered = await Json(await owner.GetAsync(root + "/chat/agents")); Assert.False(recovered.GetProperty("assistantSetupPending").GetBoolean());
        var agent = recovered.GetProperty("items")[0];
        Assert.Equal("General Assistant", agent.GetProperty("name").GetString()); Assert.True(agent.GetProperty("systemManaged").GetBoolean());
        Assert.Equal("ready", (await Json(await owner.PostAsync(root + "/general-assistant/reconcile", null))).GetProperty("status").GetString());
        Assert.Single((await security.ReadTenantAsync(tenant))!.SystemIdentities, i => i.ManagedKind == ManagedGeneralAssistant.Kind);
        Assert.Single(await security.ReadAuditAsync(tenant, 100), a => a.Action == ManagedGeneralAssistant.ProvisionAction);
        // Bootstrap's incomplete setup can recover independently in the same running process.
        Assert.Equal("ready", (await Json(await owner.PostAsync($"/api/v2/tenants/{original}/general-assistant/reconcile", null))).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Managed_template_cutover_rejects_historical_create_and_fork_without_changing_exact_version_reads()
    {
        await using var factory = new AgentCoreApiFactory();
        using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") });
        await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
        var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid();
        var root = $"/api/v2/tenants/{tenant}"; var before = await Revision(owner, tenant);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.PostAsJsonAsync(root + "/agents", new { templateId = "general-assistant", version = 35, expectedRevision = before })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.PostAsJsonAsync(root + "/agent-definitions/from-template", new { templateId = "general-assistant", version = 35, expectedRevision = before })).StatusCode);
        Assert.Equal(before, await Revision(owner, tenant));
        var definitions = factory.Services.GetRequiredService<IBuiltInAgentDefinitionStore>();
        Assert.Equal(35, (await definitions.GetAsync("general-assistant", 35))!.Version);
        Assert.Equal(HttpStatusCode.Created, (await owner.PostAsJsonAsync(root + "/agents", new { templateId = "general-assistant", version = 36, expectedRevision = before })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await owner.PostAsJsonAsync(root + "/agent-definitions/from-template", new { templateId = "general-assistant", version = 36, expectedRevision = await Revision(owner, tenant) })).StatusCode);
    }

    public class SetupFailureStore : DispatchProxy
    {
        public ITenantAgentStore Inner = null!; public bool Fail = true; public int Attempts;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == nameof(ITenantAgentStore.TryProvisionGeneralAssistantAsync))
            { Interlocked.Increment(ref Attempts); if (Fail) throw new InvalidOperationException("sensitive-provider-body secret-test-token"); }
            return method.Invoke(Inner, args);
        }
    }
    private sealed class AssistantSetupLogs : ILogger<ManagedGeneralAssistantService>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, error));
    }
}
