using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Domain.Triggers;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;
public sealed partial class TenantAgentAccessTests
{
    private sealed class PresetModelCatalog : IModelCatalog
    {
        private readonly IModelCatalog inner = AgentCore.Infrastructure.Providers.ModelCatalogFactory.Synthetic();
        public bool HideDefault;
        public string DefaultKey => inner.DefaultKey;
        public ModelDescriptor Default => inner.Default;
        public IReadOnlyList<ModelDescriptor> Models => inner.Models.Where(m => !HideDefault || m.Key != DefaultKey).ToArray();
        public ModelDescriptor? Get(string key) => HideDefault && key == DefaultKey ? null : inner.Get(key);
    }
    private static JsonObject PresetCommand(JsonElement editor, string preset)
    {
        var recipe = editor.GetProperty("presets").EnumerateArray().Single(p => p.GetProperty("presetId").GetString() == preset);
        var body = JsonSerializer.SerializeToNode(new { enabled = false, name = recipe.GetProperty("name").GetString(), instructions = recipe.GetProperty("instructions").GetString(),
            presetId = preset, presetVersion = recipe.GetProperty("presetVersion").GetInt32(), requiresTools = true, requiresVision = false,
            expectedRevision = editor.GetProperty("securityRevision").GetInt64(), expectedInstanceRevision = editor.GetProperty("agent").GetProperty("instance").GetProperty("revision").GetInt64(), expectedAutomationRevision = 0 })!.AsObject();
        if (recipe.GetProperty("triggerKind").GetString() == "coreEvent")
            body["triggers"] = JsonSerializer.SerializeToNode(new[] { new { kind = "event", enabled = true, source = new { kind = "builtin", key = recipe.GetProperty("coreEventKey").GetString() },
                filterExpression = recipe.GetProperty("filterExpression").GetString(), dispatch = recipe.GetProperty("dispatch") } });
        else body["schedule"] = JsonSerializer.SerializeToNode(new { kind = "weekly", interval = 1, timeZone = "UTC", localTime = "09:00", weekdays = new[] { 1 }, maxOccurrences = 52 });
        return body;
    }
    [Theory] [InlineData(false, "review-recent-work")] [InlineData(true, "review-recent-work")]
    [InlineData(false, "consolidate-continuity")] [InlineData(true, "consolidate-continuity")]
    [InlineData(false, "improve-harness")] [InlineData(true, "improve-harness")]
    public async Task Tenant_automation_presets_create_only_owned_disabled_drafts_and_preserve_immutable_provenance_through_customization(bool sqlite, string preset)
    {
        await using var factory = new RuntimeStateFactory(sqlite); using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") });
        var (tenant, instance) = await OwnedMemoryAgent(owner, enabled: false, automation: true); var api = $"/api/v2/tenants/{tenant}/agents/{instance}/automations";
        var editor = await Json(await owner.GetAsync(api + "/editor")); Assert.Equal(3, editor.GetProperty("presets").GetArrayLength());
        var body = PresetCommand(editor, preset); var saved = await Json(await owner.PostAsJsonAsync(api, body)); var id = saved.GetProperty("automationId").GetGuid();
        Assert.Equal("disabled", saved.GetProperty("status").GetString()); Assert.Equal(preset, saved.GetProperty("presetId").GetString()); Assert.Equal(1, saved.GetProperty("presetVersion").GetInt32());
        Assert.Equal(0, (await factory.Services.GetRequiredService<ITenantRunStore>().ListTenantSessionRunsAsync(tenant, null, null, 0, 100, instanceId: instance)).Total);
        var store = factory.Services.GetRequiredService<ITenantAutomationStore>(); var retained = (await store.FindTenantAutomationAsync(tenant, id))!;
        Assert.Equal(tenant, retained.Owner.TenantId); Assert.Null(retained.Owner.ProfileIdOrNull);
        body["name"] = "Customized recipe"; body["instructions"] = "These are the actual saved instructions."; body["requiresTools"] = false;
        body.Remove("presetId"); body.Remove("presetVersion"); body["expectedRevision"] = await Revision(owner, tenant); body["expectedAutomationRevision"] = saved.GetProperty("revision").GetInt64();
        if (body["triggers"] is not null) body["triggers"] = JsonNode.Parse(saved.GetProperty("triggers").GetRawText());
        var changed = await Json(await owner.PutAsJsonAsync(api + $"/{id}", body)); Assert.Equal(preset, changed.GetProperty("presetId").GetString()); Assert.False(changed.GetProperty("requiresTools").GetBoolean());
        Assert.Equal("These are the actual saved instructions.", changed.GetProperty("instructions").GetString());
        foreach (var forged in new[] { ("review-recent-work", 9), (preset == "improve-harness" ? "consolidate-continuity" : "improve-harness", 1) })
        {
            body["presetId"] = forged.Item1; body["presetVersion"] = forged.Item2; body["expectedRevision"] = await Revision(owner, tenant); body["expectedAutomationRevision"] = changed.GetProperty("revision").GetInt64();
            Assert.Equal(HttpStatusCode.Conflict, (await owner.PutAsJsonAsync(api + $"/{id}", body)).StatusCode);
        }
        body["presetId"] = null; body["presetVersion"] = null;
        var preserved = await Json(await owner.PutAsJsonAsync(api + $"/{id}", body)); Assert.Equal(preset, preserved.GetProperty("presetId").GetString());
        if (sqlite)
        {
            var reopened = new AgentCore.Infrastructure.Security.SqliteIamStore(factory.Services.GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<AgentCore.Infrastructure.Persistence.AgentCoreDbContext>>());
            Assert.Equal(preset, (await reopened.FindTenantAutomationAsync(tenant, id))!.Provenance.PresetId);
        }
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_automation_presets_reject_unknown_or_partial_creation_provenance_without_partial_writes(bool sqlite)
    {
        await using var factory = new RuntimeStateFactory(sqlite); using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") });
        var (tenant, instance) = await OwnedMemoryAgent(owner, enabled: false, automation: true); var api = $"/api/v2/tenants/{tenant}/agents/{instance}/automations";
        var editor = await Json(await owner.GetAsync(api + "/editor")); var body = PresetCommand(editor, "consolidate-continuity"); var before = await Revision(owner, tenant);
        foreach (var (id, version) in new (string?, int?)[] { ("unknown", 1), ("consolidate-continuity", 2), ("consolidate-continuity", null), (null, 1), ("", 1), ("consolidate-continuity", 0) })
        {
            body["presetId"] = id; body["presetVersion"] = version;
            Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync(api, body)).StatusCode); Assert.Equal(before, await Revision(owner, tenant));
        }
        Assert.Equal(0, (await Json(await owner.GetAsync(api))).GetProperty("total").GetInt32());
    }
    [Theory(Timeout = 120000)] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_automation_preset_metadata_does_not_supply_activation_or_tool_authority_after_customization(bool sqlite)
    {
        var model = new ExperienceModel(); await using var factory = new RuntimeStateFactory(sqlite, model: model); using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") });
        var (tenant, instance) = await OwnedMemoryAgent(owner, enabled: false, automation: true); await MemoryExecutor(owner, tenant, instance, operations: true);
        var api = $"/api/v2/tenants/{tenant}/agents/{instance}/automations"; var editor = await Json(await owner.GetAsync(api + "/editor"));
        var body = PresetCommand(editor, "consolidate-continuity"); var saved = await Json(await owner.PostAsJsonAsync(api, body)); var id = saved.GetProperty("automationId").GetGuid();
        var runBody = new { expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1, expectedAutomationRevision = 1 };
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.PostAsJsonAsync(api + $"/{id}/run", runBody)).StatusCode);
        body["enabled"] = true; body["expectedRevision"] = await Revision(owner, tenant); body["expectedAutomationRevision"] = 1;
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsJsonAsync(api + $"/{id}", body)).StatusCode); // Weekly timing violates this Definition's schedule policy.
        body["schedule"] = JsonSerializer.SerializeToNode(new { kind = "oneShot", timeZone = "UTC", atUtc = DateTimeOffset.UtcNow.AddMinutes(30).ToString("O") });
        body["instructions"] = "Run the customized task using actual current permissions."; body["requiresTools"] = false; body["expectedRevision"] = await Revision(owner, tenant); body["expectedAutomationRevision"] = 1;
        var customized = await Json(await owner.PutAsJsonAsync(api + $"/{id}", body)); Assert.Equal("consolidate-continuity", customized.GetProperty("presetId").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.PostAsJsonAsync(api + $"/{id}/run", new { expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1, expectedAutomationRevision = customized.GetProperty("revision").GetInt64() })).StatusCode);
        Assert.Empty(model.Requests); // Several missing Agent permissions cannot be supplied by one primary grant or by recipe metadata.
        await ExperienceAutomationRights(owner, tenant, instance);
        var admitted = await Json(await owner.PostAsJsonAsync(api + $"/{id}/run", new { expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1, expectedAutomationRevision = customized.GetProperty("revision").GetInt64() }));
        await factory.Services.GetRequiredService<ITenantRunExecution>().ExecuteRunnableAsync(8); var completed = await ProfileTerminal(factory, tenant, admitted.GetProperty("runId").GetGuid());
        Assert.Equal(AgentRunStatus.Completed, completed.Status); Assert.Equal("Run the customized task using actual current permissions.", completed.Admission.TaskIntent!.Text);
        Assert.Null(completed.Owner.ProfileIdOrNull);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_automation_preset_prerequisites_observe_only_the_selected_owned_Agent_and_current_isolation_without_writing(bool sqlite)
    {
        await using var factory = new RuntimeStateFactory(sqlite); using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") });
        var (tenant, instance) = await OwnedMemoryAgent(owner, enabled: false, automation: true); var api = $"/api/v2/tenants/{tenant}";
        var revision = await ExperienceOptIn(owner, tenant, instance, true, true); var agents = factory.Services.GetRequiredService<ITenantAgentStore>();
        var agent = (await agents.FindTenantAgentAsync(tenant, instance))!;
        var second = await Json(await owner.PostAsJsonAsync(api + "/agents/from-publication", new { definitionId = agent.Instance.DefinitionId, version = agent.Instance.ActiveVersion, expectedRevision = await Revision(owner, tenant) }));
        var other = second.GetProperty("instance").GetProperty("instanceId").GetGuid(); var before = await Revision(owner, tenant);
        var selected = await Json(await owner.GetAsync(api + $"/agents/{instance}/automations/editor"));
        var separate = await Json(await owner.GetAsync(api + $"/agents/{other}/automations/editor"));
        var current = selected.GetProperty("presets").EnumerateArray().Single(p => p.GetProperty("presetId").GetString() == "consolidate-continuity");
        var independent = separate.GetProperty("presets").EnumerateArray().Single(p => p.GetProperty("presetId").GetString() == "consolidate-continuity");
        Assert.DoesNotContain("Enable eligible Experience", current.GetRawText()); Assert.DoesNotContain("Enable Allow agent consolidation", current.GetRawText());
        Assert.Contains("Enable eligible Experience", independent.GetRawText()); Assert.Contains("Enable Allow agent consolidation", independent.GetRawText());
        Assert.Equal(revision, selected.GetProperty("agent").GetProperty("instance").GetProperty("revision").GetInt64()); Assert.Equal(before, await Revision(owner, tenant));
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync($"/api/v2/tenants/{Guid.NewGuid()}/agents/{instance}/automations/editor")).StatusCode);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_automation_preset_store_rechecks_known_creation_provenance_before_writing_any_owned_row(bool sqlite)
    {
        await using var factory = new RuntimeStateFactory(sqlite); using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") });
        var (tenant, instance) = await OwnedMemoryAgent(owner, enabled: false, automation: true); var api = $"/api/v2/tenants/{tenant}/agents/{instance}/automations";
        var saved = await Json(await owner.PostAsJsonAsync(api, PresetCommand(await Json(await owner.GetAsync(api + "/editor")), "consolidate-continuity")));
        var store = factory.Services.GetRequiredService<ITenantAutomationStore>(); var current = (await store.FindTenantAutomationAsync(tenant, saved.GetProperty("automationId").GetGuid()))!;
        var security = factory.Services.GetRequiredService<IIamStore>(); var state = (await security.ReadTenantAsync(tenant))!;
        var login = (await factory.Services.GetRequiredService<IdentityService>().SignInAsync("owner@example.test", "explicit-synthetic-test-password"))!;
        var human = state.Users.Single(u => u.AccountId == login.Account.AccountId); var at = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()); var id = Guid.NewGuid();
        var forged = new Automation(id, current.Owner, AutomationStatus.Disabled, current.Instructions, current.Triggers, null, null, 0, 1, current.TriggerRevision,
            new(TriggerAuthorizationOrigin.AdminOwner, null, null, at, at, "forged-preset", 1), null, current.ModelOverrideCatalogKey, current.ModelOverrideReasoningEffort, current.RequiresVision, current.Name, current.ExecutionTarget, current.CompletionDelivery, current.RequiresTools);
        await Assert.ThrowsAsync<ArgumentException>(() => store.TrySaveTenantAutomationAsync(state with { Tenant = state.Tenant with { Revision = state.Tenant.Revision + 1 } }, state.Tenant.Revision,
            forged, 0, 1, new(login.Account.AccountId, login.Session.SessionId, human.PrincipalId), "automations.create", new(Guid.NewGuid(), tenant, human.PrincipalId, "automations.create", id.ToString("D"), "allowed", "automation_saved", at)).AsTask());
        Assert.Null(await store.FindTenantAutomationAsync(tenant, id)); Assert.Equal(state.Tenant.Revision, (await security.ReadTenantAsync(tenant))!.Tenant.Revision);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_automation_preset_disabled_drafts_can_retain_an_unavailable_saved_model_but_cannot_enable_or_select_an_unknown_replacement(bool sqlite)
    {
        var catalog = new PresetModelCatalog(); await using var factory = new RuntimeStateFactory(sqlite, catalog: catalog);
        using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") });
        var (tenant, instance) = await OwnedMemoryAgent(owner, enabled: false, automation: true); var api = $"/api/v2/tenants/{tenant}/agents/{instance}/automations";
        var body = PresetCommand(await Json(await owner.GetAsync(api + "/editor")), "consolidate-continuity"); body["modelKey"] = catalog.DefaultKey;
        var first = await Json(await owner.PostAsJsonAsync(api, body)); var id = first.GetProperty("automationId").GetGuid();
        catalog.HideDefault = true; body["name"] = "Retained disabled model"; body["expectedRevision"] = await Revision(owner, tenant); body["expectedAutomationRevision"] = 1;
        var retained = await Json(await owner.PutAsJsonAsync(api + $"/{id}", body)); Assert.Equal("disabled", retained.GetProperty("status").GetString()); Assert.Equal(catalog.DefaultKey, retained.GetProperty("modelOverrideCatalogKey").GetString());
        body["expectedRevision"] = await Revision(owner, tenant); body["expectedAutomationRevision"] = retained.GetProperty("revision").GetInt64(); body["modelKey"] = "missing-new-model";
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsJsonAsync(api + $"/{id}", body)).StatusCode);
        body["modelKey"] = catalog.DefaultKey; body["enabled"] = true; body["schedule"] = JsonSerializer.SerializeToNode(new { kind = "oneShot", timeZone = "UTC", atUtc = DateTimeOffset.UtcNow.AddMinutes(30).ToString("O") });
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsJsonAsync(api + $"/{id}", body)).StatusCode);
        catalog.HideDefault = false; Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(api + $"/{id}", body)).StatusCode);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_automation_preset_event_draft_cannot_activate_with_every_subscription_disabled(bool sqlite)
    {
        await using var factory = new RuntimeStateFactory(sqlite); using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") });
        var (tenant, instance) = await OwnedMemoryAgent(owner, enabled: false, automation: true); var api = $"/api/v2/tenants/{tenant}/agents/{instance}/automations";
        var body = PresetCommand(await Json(await owner.GetAsync(api + "/editor")), "review-recent-work"); body["triggers"]![0]!["enabled"] = false; body["enabled"] = true;
        var before = await Revision(owner, tenant); Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync(api, body)).StatusCode); Assert.Equal(before, await Revision(owner, tenant));
        body["enabled"] = false; var saved = await Json(await owner.PostAsJsonAsync(api, body)); Assert.Equal("disabled", saved.GetProperty("status").GetString());
        Assert.False(saved.GetProperty("triggers")[0].GetProperty("enabled").GetBoolean());
    }
}
