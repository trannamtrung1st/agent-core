using System.Net;
using System.Net.Http.Json;
using AgentCore.Application.Admin;
using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Definitions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentCore.Api.Tests;

public sealed class StateIsolationConfigurationTests
{
    [Fact]
    public async Task Preset_rejects_an_owner_change_between_reading_its_recipe_and_preparing_the_exact_preview()
    {
        DefinitionReadHook? hook = null;
        await using var host = new AgentCoreApiFactory().WithWebHostBuilder(builder => builder.ConfigureTestServices(services => {
            services.RemoveAll<IAgentDefinitionStore>();
            services.AddSingleton<IAgentDefinitionStore>(sp => hook = new DefinitionReadHook(sp.GetRequiredService<IBuiltInAgentDefinitionStore>()));
        }));
        using var client = TestOwnerCapability.CreateOwnerClient(host);
        var owner = await host.Services.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 31);
        var experience = host.Services.GetRequiredService<IExperienceStore>();
        hook!.OnSecondRead = async () => { await experience.ConfigureAsync(owner.InstanceId, 0, true); };
        var service = host.Services.GetRequiredService<StateIsolationConfigurationService>();
        var rejected = await Assert.ThrowsAsync<AgentCoreException>(async () => await service.PresetAsync(owner.InstanceId, "Persistent"));
        Assert.Equal("Conflict", rejected.Code);
        Assert.True((await experience.SettingsAsync(owner.InstanceId)).Enabled);
        Assert.Equal(owner.Revision, (await service.ReadAsync(owner.InstanceId)).Revisions.Instance);
        var current = await service.PresetAsync(owner.InstanceId, "Persistent");
        Assert.True(current.Draft.ExperienceEnabled);
        Assert.Equal(1, current.Expected.Experience);
    }

    private sealed class DefinitionReadHook(IBuiltInAgentDefinitionStore inner) : IAgentDefinitionStore
    {
        private int reads;
        public Func<ValueTask>? OnSecondRead { get; set; }
        public ValueTask<IReadOnlyList<AgentDefinition>> ListAsync(CancellationToken ct = default) => inner.ListAsync(ct);
        public async ValueTask<AgentDefinition?> GetAsync(string id, int? version = null, CancellationToken ct = default)
        {
            if (OnSecondRead is { } callback && ++reads == 2) { OnSecondRead = null; await callback(); }
            return await inner.GetAsync(id, version, ct);
        }
    }

    [Fact]
    public async Task Persistent_requests_an_explicit_recipe_and_definition_defaults_restore_inheritance()
    {
        await using var host = new AgentCoreApiFactory();
        using var client = TestOwnerCapability.CreateOwnerClient(host);
        var instance = await host.Services.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 31);
        var settings = host.Services.GetRequiredService<AgentInstanceSettingsService>();
        await settings.PatchAsync(instance.InstanceId, "stateIsolationPolicy", instance.Revision,
            new Dictionary<string, System.Text.Json.JsonElement> { ["homeAccess"] = System.Text.Json.JsonSerializer.SerializeToElement("Disabled") }, []);
        var service = host.Services.GetRequiredService<StateIsolationConfigurationService>();
        var persistent = await service.PresetAsync(instance.InstanceId, "Persistent");
        Assert.Empty(persistent.BlockingReasons);
        Assert.Equal("ReadWrite", persistent.Draft.Settings.Single(p => p.Section == "stateIsolationPolicy").Set["homeAccess"].GetString());
        var applied = await service.ApplyAsync(instance.InstanceId, persistent.Expected, persistent.Draft, persistent.Fingerprint);
        Assert.Equal("instance", applied.Settings.Single(s => s.Section == "stateIsolationPolicy").Sources["homeAccess"]);
        var reset = await service.PresetAsync(instance.InstanceId, "DefinitionDefaults");
        Assert.Contains("homeAccess", reset.Draft.Settings.Single(p => p.Section == "stateIsolationPolicy").Clear);
        applied = await service.ApplyAsync(instance.InstanceId, reset.Expected, reset.Draft, reset.Fingerprint);
        Assert.Empty(applied.Settings.Single(s => s.Section == "stateIsolationPolicy").Overrides);
        Assert.Equal("definition", applied.Settings.Single(s => s.Section == "stateIsolationPolicy").Sources["homeAccess"]);
    }

    [Fact]
    public async Task Restrictive_definition_blocks_Persistent_while_defaults_reset_all_coordinated_overrides_and_preserve_opt_ins()
    {
        await using var host = new AgentCoreApiFactory().WithWebHostBuilder(builder => builder.ConfigureTestServices(services => {
            services.RemoveAll<IAgentDefinitionStore>();
            services.AddSingleton<IAgentDefinitionStore>(sp => new IsolatedDefinitionStore(sp.GetRequiredService<IBuiltInAgentDefinitionStore>()));
        }));
        using var client = TestOwnerCapability.CreateOwnerClient(host);
        var instance = await host.Services.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 31);
        var service = host.Services.GetRequiredService<StateIsolationConfigurationService>();
        var persistent = await service.PresetAsync(instance.InstanceId, "Persistent");
        Assert.NotEmpty(persistent.BlockingReasons);
        var denied = await client.PostAsJsonAsync($"/api/v2/admin/agent-instances/{instance.InstanceId}/isolation/apply", new { persistent.Expected, persistent.Draft, persistent.Fingerprint });
        Assert.Equal(HttpStatusCode.BadRequest, denied.StatusCode);
        Assert.Equal(instance.Revision, (await service.ReadAsync(instance.InstanceId)).Revisions.Instance);
        var settings = host.Services.GetRequiredService<AgentInstanceSettingsService>();
        var revision = instance.Revision;
        foreach (var (section, field, value) in new[] { ("stateIsolationPolicy", "workingCleanup", (object)"AfterTerminal"), ("memoryPolicy", "sessionMemory", (object)false), ("voice", "enabled", (object)false) })
        {
            var updated = await settings.PatchAsync(instance.InstanceId, section, revision,
                new Dictionary<string, System.Text.Json.JsonElement> { [field] = System.Text.Json.JsonSerializer.SerializeToElement(value) }, []);
            revision = updated.InstanceRevision;
        }
        var experience = host.Services.GetRequiredService<IExperienceStore>();
        await experience.ConfigureAsync(instance.InstanceId, 0, true);
        await experience.ConfigureMaintenanceAsync(instance.InstanceId, 0, true);
        var reset = await service.PresetAsync(instance.InstanceId, "DefinitionDefaults");
        Assert.Empty(reset.BlockingReasons);
        Assert.True(reset.Draft.ExperienceEnabled); Assert.True(reset.Draft.AllowMaintenance);
        var applied = await service.ApplyAsync(instance.InstanceId, reset.Expected, reset.Draft, reset.Fingerprint);
        Assert.All(applied.Settings, section => Assert.Empty(section.Overrides));
        Assert.Equal("Disabled", applied.Settings.Single(s => s.Section == "stateIsolationPolicy").Effective["homeAccess"]);
        Assert.True(applied.ExperienceEnabled); Assert.True(applied.AllowMaintenance);
    }

    private sealed class IsolatedDefinitionStore(IBuiltInAgentDefinitionStore inner) : IAgentDefinitionStore
    {
        public ValueTask<IReadOnlyList<AgentDefinition>> ListAsync(CancellationToken ct = default) => inner.ListAsync(ct);
        public async ValueTask<AgentDefinition?> GetAsync(string id, int? version = null, CancellationToken ct = default)
        {
            var definition = await inner.GetAsync(id, version, ct);
            return definition is null ? null : definition with { StateIsolationPolicy = new(HomeAccess: HomeAccess.Disabled,
                BrowserIsolation: BrowserIsolation.EphemeralSession, AgentStateVisibility: AgentStateVisibility.CurrentSession,
                ExperienceRecord: false, ExperienceRecall: false, ProfileEvidence: false, CrossSessionContinuity: false), MemoryPolicy = new(true) };
        }
    }

    [Fact]
    public async Task Preview_explains_retained_scratch_and_explicit_terminal_cleanup_without_applying_either()
    {
        await using var host = new AgentCoreApiFactory();
        using var client = TestOwnerCapability.CreateOwnerClient(host);
        var instance = await host.Services.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 31);
        var service = host.Services.GetRequiredService<StateIsolationConfigurationService>();
        var preset = await service.PresetAsync(instance.InstanceId, "Stateless");
        Assert.Contains("Working files remain after a request completes until the Session is deleted", preset.Notice);
        var cleanup = preset.Draft with { Settings = preset.Draft.Settings.Select(p => p.Section == "stateIsolationPolicy"
            ? p with { Set = p.Set.ToDictionary(v => v.Key, v => v.Key == "workingCleanup"
                ? System.Text.Json.JsonSerializer.SerializeToElement("AfterTerminal") : v.Value) } : p).ToArray() };
        var preview = await service.PreviewAsync(instance.InstanceId, cleanup);
        Assert.Contains("Working files are eligible for safe cleanup after terminal completion", preview.Notice);
        Assert.Contains(preview.Changes, c => c.Field == "workingCleanup" && c.After.GetString() == "AfterTerminal");
        Assert.Equal(instance.Revision, (await service.ReadAsync(instance.InstanceId)).Revisions.Instance);
    }

    [Fact]
    public async Task Busy_browser_preview_discloses_pending_transition_without_starting_native_work()
    {
        var browser = new BusyBrowser();
        await using var host = new AgentCoreApiFactory().WithWebHostBuilder(builder => builder.ConfigureTestServices(services => {
            services.RemoveAll<IBrowser>(); services.AddSingleton<IBrowser>(browser);
        }));
        using var client = TestOwnerCapability.CreateOwnerClient(host);
        var instance = await host.Services.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 31);
        var service = host.Services.GetRequiredService<StateIsolationConfigurationService>();
        var plan = await service.PresetAsync(instance.InstanceId, "SessionIsolated");
        Assert.Empty(plan.BlockingReasons);
        Assert.Contains("native transitions remain pending", plan.Notice);
        Assert.Equal(1, browser.Observations); Assert.Equal(0, browser.Executions);
        Assert.Equal(instance.Revision, (await host.Services.GetRequiredService<IAgentInstanceStore>().FindAsync(instance.InstanceId))!.Revision);
    }

    private sealed class BusyBrowser : IBrowser, IBrowserStateReader
    {
        public int Observations { get; private set; }
        public int Executions { get; private set; }
        public bool IsAvailable => true;
        public BrowserProviderDescriptor Provider { get; } = new("fixture", "Busy fixture", new HashSet<BrowserFeature>());
        public BrowserHostPolicy HostPolicy { get; } = new(true, true, BrowserInteractionMode.InteractiveDemo, [], ProfileMode: BrowserProfileMode.PersistentAgent);
        public ValueTask<BrowserResult> ExecuteAsync(BrowserRequest request, CancellationToken ct = default)
        { Executions++; return ValueTask.FromResult(new BrowserResult("not_expected")); }
        public ValueTask<ResourceStateObservation> ObserveAsync(Guid trustedInstanceId, CancellationToken ct = default)
        { Observations++; return ValueTask.FromResult(new ResourceStateObservation("browser", "partial", "sharedCoordinated", DateTimeOffset.UtcNow, Code: "resource_busy")); }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Preset_is_an_unsaved_exact_plan_and_atomic_apply_rejects_stale_independent_owner_revisions(bool sqlite)
    {
        var path = Path.Combine(Path.GetTempPath(), "state-isolation-atomic-" + Guid.NewGuid().ToString("N") + ".db");
        await using WebApplicationFactory<Program> host = sqlite ? new ExperienceHost(path) : new AgentCoreApiFactory();
        using var client = TestOwnerCapability.CreateOwnerClient(host);
        var owner = await host.Services.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 31);
        var experience = host.Services.GetRequiredService<IExperienceStore>();
        var learned = await experience.ConfigureAsync(owner.InstanceId, 0, true);
        var maintenance = await experience.ConfigureMaintenanceAsync(owner.InstanceId, 0, true);
        var currentSettings = new InstanceSettingsOverrides(HomeAccess: new("Disabled"));
        var invalidAudit = new AdminEventAppend(Guid.NewGuid(), DateTimeOffset.UtcNow, AdminEventActorKind.LocalOwner,
            AdminEventOperationKind.InstanceSettingsChanged, "agent.instance", owner.InstanceId.ToString("D"), owner.Revision + 1,
            owner.ActiveVersion, """{"section":"stateIsolationPolicy","set":["unknownField"],"clear":[]}""");
        await Assert.ThrowsAsync<AgentCoreException>(async () => await host.Services.GetRequiredService<IStateIsolationConfigurationStore>()
            .ApplyAsync(new(owner.InstanceId, new(owner.Revision, learned.Revision, maintenance.Revision), currentSettings,
                false, false, invalidAudit), DateTimeOffset.UtcNow));
        Assert.Equal(owner.Revision, (await host.Services.GetRequiredService<IAgentInstanceStore>().FindAsync(owner.InstanceId))!.Revision);
        Assert.True((await experience.SettingsAsync(owner.InstanceId)).Enabled);
        Assert.True((await experience.MaintenanceSettingsAsync(owner.InstanceId)).AllowAgentConsolidation);
        var root = $"/api/v2/admin/agent-instances/{owner.InstanceId}/isolation";
        using var anonymous = host.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync(root + "/preset", new { preset = "SessionIsolated" })).StatusCode);
        var response = await client.PostAsJsonAsync(root + "/preset", new { preset = "SessionIsolated" });
        response.EnsureSuccessStatusCode();
        var plan = (await response.Content.ReadFromJsonAsync<IsolationChangePlan>())!;
        Assert.Empty(plan.BlockingReasons);
        Assert.Contains(plan.Changes, c => c.Owner == "stateIsolationPolicy" && c.Field == "homeAccess" && c.After.GetString() == "Disabled");
        Assert.Contains(plan.Changes, c => c.Owner == "experience" && !c.After.GetBoolean());
        Assert.Contains(plan.Changes, c => c.Owner == "identityMaintenance" && !c.After.GetBoolean());
        Assert.Equal(owner.Revision, (await host.Services.GetRequiredService<IAgentInstanceStore>().FindAsync(owner.InstanceId))!.Revision);
        await experience.ConfigureAsync(owner.InstanceId, learned.Revision, false);
        var rejected = await client.PostAsJsonAsync(root + "/apply", new { plan.Expected, plan.Draft, plan.Fingerprint });
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
        Assert.True((await experience.MaintenanceSettingsAsync(owner.InstanceId)).AllowAgentConsolidation);
        Assert.Equal(owner.Revision, (await host.Services.GetRequiredService<IAgentInstanceStore>().FindAsync(owner.InstanceId))!.Revision);
        plan = (await (await client.PostAsJsonAsync(root + "/preset", new { preset = "SessionIsolated" })).Content.ReadFromJsonAsync<IsolationChangePlan>())!;
        var applied = await client.PostAsJsonAsync(root + "/apply", new { plan.Expected, plan.Draft, plan.Fingerprint });
        applied.EnsureSuccessStatusCode();
        var view = (await applied.Content.ReadFromJsonAsync<IsolationConfigurationView>())!;
        Assert.False(view.ExperienceEnabled); Assert.False(view.AllowMaintenance);
        Assert.Equal(owner.Revision + 1, view.Revisions.Instance);
        var state = view.Settings.Single(s => s.Section == "stateIsolationPolicy");
        Assert.Equal("Disabled", state.Effective["homeAccess"]?.ToString());
        Assert.Equal("EphemeralSession", state.Effective["browserIsolation"]?.ToString());
        Assert.Equal("Session", state.Effective["conversationContextScope"]?.ToString());
        Assert.Equal("instance", state.Sources["homeAccess"]);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(root + "/apply", new { plan.Expected, plan.Draft, plan.Fingerprint })).StatusCode);
        var instance = (await host.Services.GetRequiredService<IAgentInstanceStore>().FindAsync(owner.InstanceId))!;
        Assert.NotNull(instance.SettingsOverrides); Assert.Equal("Disabled", instance.SettingsOverrides.HomeAccess?.Value);
        var events = await host.Services.GetRequiredService<IAdminEventStore>().ListAsync(new("agent.instance", owner.InstanceId.ToString("D")));
        var audit = Assert.Single(events, e => e.Operation == AdminEventOperationKind.InstanceSettingsChanged);
        using var summary = System.Text.Json.JsonDocument.Parse(audit.SummaryJson);
        Assert.Equal(StateIsolationConfigurationService.AuditSection, summary.RootElement.GetProperty("section").GetString());
        Assert.Equal(plan.Changes.Select(c => c.Owner + "." + c.Field).Order(StringComparer.Ordinal),
            summary.RootElement.GetProperty("set").EnumerateArray().Concat(summary.RootElement.GetProperty("clear").EnumerateArray())
                .Select(v => v.GetString()).Order(StringComparer.Ordinal));
    }
}
