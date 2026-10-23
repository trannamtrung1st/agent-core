using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AgentCore.Application.Context;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;
public sealed partial class TenantAgentAccessTests
{
    private sealed class ExperienceModel : ILanguageModel
    {
        public ModelCapabilities Capabilities => new(true, true, true);
        public ConcurrentQueue<ModelRequest> Requests { get; } = new();
        public Guid Source; public bool Hold; public bool Followup; public bool AllowMaintenance;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Enqueue(request);
            if (Source != Guid.Empty)
            {
                if (!AllowMaintenance) Assert.DoesNotContain(request.Tools ?? [], t => t.Name == TenantExperienceToolContract.Consolidate);
                var inspected = request.Messages.LastOrDefault(m => m.Role == ModelRole.Tool && m.Name == TenantExperienceToolContract.Source);
                if (inspected is null) { Assert.Contains(request.Tools!, t => t.Name == TenantExperienceToolContract.Source && t.ParametersJson.Contains("sourceKind", StringComparison.Ordinal)); yield return new ModelToolCallEvent(new("inspect-experience", TenantExperienceToolContract.Source, JsonSerializer.Serialize(new { sourceKind = "AgentRun", sourceId = Source }))); yield return new ModelCompleted(ModelStopReason.ToolCalls); yield break; }
                using var json = JsonDocument.Parse(inspected.Text); Assert.True(json.RootElement.GetProperty("tenantExperienceSourceRead").GetBoolean());
                var recorded = request.Messages.LastOrDefault(m => m.Role == ModelRole.Tool && m.Name == TenantExperienceToolContract.Record);
                if (recorded is null)
                {
                    Assert.Contains(request.Tools!, t => t.Name == TenantExperienceToolContract.Record);
                    yield return new ModelToolCallEvent(new("record-experience", TenantExperienceToolContract.Record, JsonSerializer.Serialize(new { sourceKind = "AgentRun", sourceId = Source, throughCursor = json.RootElement.GetProperty("throughCursor").GetInt64(),
                        goal = "PRIVATE_EXPERIENCE_EVIDENCE", attempts = new[] { "Inspected actual retained source" }, decisions = Array.Empty<string>(), outcomes = new[] { "Observed bounded source" }, corrections = Array.Empty<string>(), unresolved = Array.Empty<string>(), difficulties = Array.Empty<string>(), lessons = new[] { "Keep evidence grouping explicit" } })));
                    yield return new ModelCompleted(ModelStopReason.ToolCalls); yield break;
                }
                using var effect = JsonDocument.Parse(recorded.Text); Assert.True(effect.RootElement.GetProperty("applied").GetBoolean());
            }
            if (!Hold && Source == Guid.Empty && !request.Messages.Any(m => m.Role == ModelRole.Tool && m.Name == ToolCatalog.AgentStateInspect))
            {
                yield return new ModelToolCallEvent(new("substantive-source-observation", ToolCatalog.AgentStateInspect, "{}"));
                yield return new ModelCompleted(ModelStopReason.ToolCalls); yield break;
            }
            if (Hold)
            {
                Entered.TrySetResult(); await Release.Task.WaitAsync(ct);
                if (Followup) { yield return new ModelToolCallEvent(new("after-experience", ToolCatalog.AgentStateInspect, "{}")); yield return new ModelCompleted(ModelStopReason.ToolCalls); yield break; }
            }
            if (request.Tools?.Any(t => t.Name == ToolCatalog.WorkComplete) == true)
            {
                yield return new ModelToolCallEvent(new("complete-manual-review", ToolCatalog.WorkComplete, JsonSerializer.Serialize(new { outcome = "Response", summary = "Experience recording completed.", attentionRequired = false })));
                yield return new ModelCompleted(ModelStopReason.ToolCalls); yield break;
            }
            yield return new ModelSemanticResponseReady(new(Hold ? "Captured Experience answer." : Source == Guid.Empty ? "Experience seed or recall completed." : "Experience recording completed.", new(ModelSpeechMode.None, null), []));
            yield return new ModelCompleted(ModelStopReason.Completed);
        }
    }
    private static async Task<long> ExperienceOptIn(HttpClient owner, Guid tenant, Guid agent, bool enabled, bool maintenance = false)
    {
        var root = $"/api/v2/tenants/{tenant}/agents/{agent}/state-isolation";
        var preview = await Json(await owner.PostAsJsonAsync(root + "/preview", new { settings = Array.Empty<object>(), experienceEnabled = enabled, allowMaintenance = maintenance }));
        Assert.Equal(HttpStatusCode.OK, (await ApplyIsolation(owner, root, preview)).StatusCode);
        return (await Json(await owner.GetAsync(root))).GetProperty("configuration").GetProperty("revisions").GetProperty("instance").GetInt64();
    }
    private static async Task<(Guid Tenant, Guid Agent, long Revision, Guid Source, Guid Fact)> ExperienceRecorded(RuntimeStateFactory factory, HttpClient owner, ExperienceModel model)
    {
        var (tenant, agent) = await OwnedMemoryAgent(owner, enabled: false); await MemoryExecutor(owner, tenant, agent, operations: true); var revision = await ExperienceOptIn(owner, tenant, agent, true);
        var source = await MemoryRequest(factory, owner, tenant, agent, instanceRevision: revision); model.Source = source.Run.AgentRunId;
        (Guid Session, AgentRun Run) recorded;
        try { recorded = await MemoryRequest(factory, owner, tenant, agent, instanceRevision: revision); }
        catch (Xunit.Sdk.XunitException error) { throw new Xunit.Sdk.XunitException(error.Message + "\nSynthetic model trace: " + JsonSerializer.Serialize(model.Requests.Select(r => new { tools = r.Tools?.Select(t => t.Name), results = r.Messages.Where(m => m.Role == ModelRole.Tool).Select(m => new { m.Name, m.Text }) }))); }
        Assert.Equal(AgentRunStatus.Completed, recorded.Run.Status); model.Source = Guid.Empty;
        var page = await Json(await owner.GetAsync($"/api/v2/tenants/{tenant}/agents/{agent}/experience?includeHistorical=true"));
        var item = Assert.Single(page.GetProperty("items").EnumerateArray()); Assert.Equal("PRIVATE_EXPERIENCE_EVIDENCE", item.GetProperty("content").GetProperty("goal").GetString());
        Assert.Equal("AgentUser", item.GetProperty("scope").GetString()); Assert.Equal(source.Run.AgentRunId, item.GetProperty("sourceId").GetGuid());
        Assert.False(item.TryGetProperty("accountId", out _)); Assert.False(item.TryGetProperty("sourceWitnessJson", out _));
        return (tenant, agent, revision, source.Session, item.GetProperty("experienceId").GetGuid());
    }
    [Theory(Timeout = 120000)] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_experience_actual_HTTP_model_and_human_retention_remain_separate_from_Memory_and_source_history(bool sqlite)
    {
        var model = new ExperienceModel(); await using var factory = new RuntimeStateFactory(sqlite, model: model); using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") });
        var application = typeof(AgentCore.Application.Security.TenantSessionService).Assembly;
        foreach (var retired in new[] { "Experience.ExperienceService", "Triggers.AdminAutomationAuthoringService", "Triggers.AutomationPresetCatalog", "Events.UnifiedEventCatalog" })
            Assert.Null(application.GetType("AgentCore.Application." + retired));
        var value = await ExperienceRecorded(factory, owner, model); var api = $"/api/v2/tenants/{value.Tenant}/agents/{value.Agent}/experience";
        await MemoryRequest(factory, owner, value.Tenant, value.Agent, instanceRevision: value.Revision);
        Assert.Contains(model.Requests.Last().Context!.Sections, s => s.Id == ContextSectionId.Experience && s.Message.Text.Contains("PRIVATE_EXPERIENCE_EVIDENCE", StringComparison.Ordinal));
        Assert.DoesNotContain(model.Requests.Last().Context!.Sections, s => s.Id == ContextSectionId.Memory);
        var page = await Json(await owner.GetAsync(api)); var item = Assert.Single(page.GetProperty("items").EnumerateArray());
        var stale = new { expectedRevision = page.GetProperty("securityRevision").GetInt64(), experienceId = value.Fact, expectedExperienceRevision = 99, visibility = "Suppressed" };
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsJsonAsync(api + "/visibility", stale)).StatusCode);
        await Json(await owner.PostAsJsonAsync(api + "/visibility", new { stale.expectedRevision, stale.experienceId, expectedExperienceRevision = item.GetProperty("revision").GetInt64(), stale.visibility }));
        var revision = await ExperienceOptIn(owner, value.Tenant, value.Agent, false); page = await Json(await owner.GetAsync(api)); Assert.Equal("Suppressed", Assert.Single(page.GetProperty("items").EnumerateArray()).GetProperty("visibility").GetString());
        await MemoryRequest(factory, owner, value.Tenant, value.Agent, instanceRevision: revision); Assert.DoesNotContain(model.Requests.Last().Context!.Sections, s => s.Id == ContextSectionId.Experience);
        page = await Json(await owner.GetAsync(api));
        await Json(await owner.PostAsJsonAsync(api + "/reset", new { expectedRevision = page.GetProperty("securityRevision").GetInt64(), expectedRetainedCount = 1 }));
        page = await Json(await owner.GetAsync(api + "?includeHistorical=true")); var deleted = Assert.Single(page.GetProperty("items").EnumerateArray()); Assert.Equal("Deleted", deleted.GetProperty("visibility").GetString()); Assert.Equal(JsonValueKind.Null, deleted.GetProperty("content").ValueKind); Assert.False(deleted.GetProperty("canManage").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync(api + "/" + value.Fact)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync($"/api/v2/tenants/{value.Tenant}/sessions/{value.Source}/messages")).StatusCode);
        Assert.Equal(0, (await Json(await owner.GetAsync($"/api/v2/tenants/{value.Tenant}/agents/{value.Agent}/memory?scope=identityUser"))).GetProperty("total").GetInt32());
    }
    [Theory(Timeout = 120000)] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task Tenant_experience_current_suppression_blocks_held_output_or_a_further_provider_call(bool sqlite, bool nextProvider)
    {
        var model = new ExperienceModel(); await using var factory = new RuntimeStateFactory(sqlite, model: model); using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") }); var value = await ExperienceRecorded(factory, owner, model);
        model.Hold = true; model.Followup = nextProvider; var before = model.Requests.Count;
        var submitted = await SubmitProfileRequest(owner, value.Tenant, value.Agent, value.Revision); await factory.Services.GetRequiredService<ITenantRunExecution>().ExecuteRunnableAsync(8); await model.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        try
        {
            Assert.Contains(model.Requests.Last().Context!.Sections, s => s.Id == ContextSectionId.Experience && s.Message.Text.Contains("PRIVATE_EXPERIENCE_EVIDENCE", StringComparison.Ordinal));
            var active = (await factory.Services.GetRequiredService<ITenantRunStore>().FindTenantRunAsync(value.Tenant, submitted.Run))!; Assert.Single(TenantExperienceReferences.Facts(active.Checkpoint));
            var api = $"/api/v2/tenants/{value.Tenant}/agents/{value.Agent}/experience"; var page = await Json(await owner.GetAsync(api)); var item = Assert.Single(page.GetProperty("items").EnumerateArray());
            await Json(await owner.PostAsJsonAsync(api + "/visibility", new { expectedRevision = page.GetProperty("securityRevision").GetInt64(), experienceId = value.Fact, expectedExperienceRevision = item.GetProperty("revision").GetInt64(), visibility = "Suppressed" }));
            model.Release.TrySetResult(); var terminal = await ProfileTerminal(factory, value.Tenant, submitted.Run); Assert.NotEqual(AgentRunStatus.Completed, terminal.Status); Assert.Equal(1, model.Requests.Count - before);
            var history = await Json(await owner.GetAsync($"/api/v2/tenants/{value.Tenant}/sessions/{submitted.Session}/messages")); Assert.DoesNotContain("Captured Experience answer.", history.GetRawText());
        }
        finally { model.Release.TrySetResult(); }
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_experience_human_HTTP_rejects_forged_owner_unknown_duplicate_fields_and_foreign_sources(bool sqlite)
    {
        await using var factory = new RuntimeStateFactory(sqlite); using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") }); var (tenant, agent) = await OwnedMemoryAgent(owner); var api = $"/api/v2/tenants/{tenant}/agents/{agent}/experience";
        foreach (var query in new[] { "?owner=other", "?page=0", "?page=1&page=2", "?includeHistorical=yes", "?includeHistorical=true&includeHistorical=false" }) Assert.Equal(HttpStatusCode.BadRequest, (await owner.GetAsync(api + query)).StatusCode);
        foreach (var body in new[] { "{}", "{\"expectedRevision\":1,\"expectedRetainedCount\":1,\"principalId\":\"other\"}", "{\"expectedRevision\":1,\"expectedRevision\":2,\"expectedRetainedCount\":1}" }) Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsync(api + "/reset", new StringContent(body, System.Text.Encoding.UTF8, "application/json"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync($"/api/v2/tenants/{Guid.NewGuid()}/agents/{agent}/experience")).StatusCode); Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync($"/api/v2/tenants/{tenant}/agents/{Guid.NewGuid()}/experience")).StatusCode);
    }
}
