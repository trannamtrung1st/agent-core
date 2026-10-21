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
        await using var host = new AgentCoreApiFactory().WithWebHostBuilder(builder => builder.ConfigureTestServices(services => {
            services.RemoveAll<IAgentDefinitionStore>();
            services.AddSingleton<IAgentDefinitionStore>(sp => new VisibilityDefinitionStore(sp.GetRequiredService<IBuiltInAgentDefinitionStore>()));
        }));
        using var client = TestOwnerCapability.CreateOwnerClient(host);
        var instance = await host.Services.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 31);
        var settings = host.Services.GetRequiredService<AgentInstanceSettingsService>();
        await settings.PatchAsync(instance.InstanceId, "stateIsolationPolicy", instance.Revision,
            new Dictionary<string, System.Text.Json.JsonElement> { ["homeAccess"] = System.Text.Json.JsonSerializer.SerializeToElement("Disabled") }, []);
        var service = host.Services.GetRequiredService<StateIsolationConfigurationService>();
        var persistent = await service.PresetAsync(instance.InstanceId, "Persistent");
        Assert.Empty(persistent.BlockingReasons);
        Assert.Equal("ReadWrite", persistent.Draft.Settings.Single(p => p.Section == "stateIsolationPolicy").Set["homeAccess"].GetString());
        Assert.Equal("InstanceSummary", persistent.Draft.Settings.Single(p => p.Section == "stateIsolationPolicy").Set["agentStateVisibility"].GetString());
        var applied = await service.ApplyAsync(instance.InstanceId, persistent.Expected, persistent.Draft, persistent.Fingerprint);
        Assert.Equal("instance", applied.Settings.Single(s => s.Section == "stateIsolationPolicy").Sources["homeAccess"]);
        var reset = await service.PresetAsync(instance.InstanceId, "DefinitionDefaults");
        Assert.Contains("homeAccess", reset.Draft.Settings.Single(p => p.Section == "stateIsolationPolicy").Clear);
        applied = await service.ApplyAsync(instance.InstanceId, reset.Expected, reset.Draft, reset.Fingerprint);
        Assert.Empty(applied.Settings.Single(s => s.Section == "stateIsolationPolicy").Overrides);
        Assert.Equal("definition", applied.Settings.Single(s => s.Section == "stateIsolationPolicy").Sources["homeAccess"]);
        Assert.Equal("Off", applied.Settings.Single(s => s.Section == "stateIsolationPolicy").Effective["agentStateVisibility"]);
    }

    private sealed class VisibilityDefinitionStore(IBuiltInAgentDefinitionStore inner) : IAgentDefinitionStore
    {
        public ValueTask<IReadOnlyList<AgentDefinition>> ListAsync(CancellationToken ct = default) => inner.ListAsync(ct);
        public async ValueTask<AgentDefinition?> GetAsync(string id, int? version = null, CancellationToken ct = default)
        {
            var definition = await inner.GetAsync(id, version, ct);
            return definition is null ? null : definition with { StateIsolationPolicy = new(MaxAgentStateVisibility: AgentStateVisibility.InstanceSummary) };
        }
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
}
