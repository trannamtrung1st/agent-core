using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Context;
using AgentCore.Application.Execution;
using AgentCore.Application.Memory;
using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Memory;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed partial class TenantAgentAccessTests
{
    private sealed class LearnedMemoryModel : ILanguageModel
    {
        public ModelCapabilities Capabilities => new(true, true, true);
        public ConcurrentQueue<ModelRequest> Requests { get; } = new();
        public ConcurrentQueue<IReadOnlyList<MemoryProposal>> Turns { get; } = new();
        public bool Hold; public bool Followup;
        public Func<ModelRequest, Task>? BeforeResponse;
        public TaskCompletionSource Entered { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Enqueue(request); Entered.TrySetResult(); if (Hold) await Release.Task.WaitAsync(ct);
            if (BeforeResponse is { } before) await before(request);
            if (Followup && !request.Messages.Any(m => m.Role == ModelRole.Tool && m.Name == "agent.state.inspect"))
            {
                yield return new ModelToolCallEvent(new("memory-observation", "agent.state.inspect", "{}")); yield return new ModelCompleted(ModelStopReason.ToolCalls); yield break;
            }
            Turns.TryDequeue(out var proposals);
            yield return new ModelSemanticResponseReady(new("The current request is complete.", new(ModelSpeechMode.None, null), [], proposals));
            yield return new ModelCompleted(ModelStopReason.Completed);
        }
    }
    private static MemoryProposal Learn(string value = "PRIVATE_CUSTOMER_EVIDENCE", MemoryProposalOperation operation = MemoryProposalOperation.Upsert,
        string subject = "Evidence workflow") => new(operation, MemoryKind.Preference, subject, value, MemoryScopeHint.User, MemoryProposalSource.UserExplicit);
    private static async Task<(Guid Tenant, Guid Agent)> OwnedMemoryAgent(HttpClient owner, Guid? existingTenant = null, bool enabled = true, bool automation = false, bool events = false, bool automationReads = false, bool automationMutations = false)
    {
        Guid tenant;
        if (existingTenant is { } selected) tenant = selected;
        else { await Login(owner, "owner@example.test", "explicit-synthetic-test-password"); tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid(); }
        var api = $"/api/v2/tenants/{tenant}";
        var draft = await Json(await owner.PostAsJsonAsync(api + "/agent-definitions", new { name = "Owned learned memory", expectedRevision = await Revision(owner, tenant) }));
        var candidate = JsonNode.Parse(draft.GetProperty("candidate").GetRawText())!;
        candidate["memoryPolicy"] = JsonSerializer.SerializeToNode(new MemoryPolicy(enabled, enabled, enabled, enabled, enabled), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        if (automation) candidate["triggerPolicy"] = JsonSerializer.SerializeToNode(new TriggerPolicy(true, true, true, false, false, false, 20, 30, 1, events ? ["schedule", "coreEvent"] : ["schedule"]), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        candidate["stateIsolationPolicy"] = JsonSerializer.SerializeToNode(new StateIsolationPolicy(AgentStateVisibility: AgentStateVisibility.InstanceSummary,
            MaxAgentStateVisibility: AgentStateVisibility.InstanceSummary), new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } });
        candidate["environment"] = JsonSerializer.SerializeToNode(new { harness = Array.Empty<string>(), knowledgeSources = Array.Empty<string>(), toolAllowlist = Array.Empty<string>(), capabilities = new { mode = "Selected", resolvedCapabilities = automationMutations ? new[] { "agent.state.inspect", "automation.inspect", "automation.list", "automation.create", "automation.update", "automation.enable", "automation.disable", "automation.delete", "automation.run" } : automationReads ? new[] { "agent.state.inspect", "automation.inspect", "automation.list" } : new[] { "agent.state.inspect" } },
            projection = new { alwaysCapabilities = automationMutations ? new[] { "agent.state.inspect", "automation.inspect", "automation.list", "automation.create", "automation.update", "automation.enable", "automation.disable", "automation.delete", "automation.run" } : automationReads ? new[] { "agent.state.inspect", "automation.inspect", "automation.list" } : new[] { "agent.state.inspect" } } });
        var id = draft.GetProperty("draftId").GetGuid();
        var changed = await Json(await owner.PutAsJsonAsync(api + $"/definition-drafts/{id}", new { candidate, expectedRevision = await Revision(owner, tenant), expectedDraftRevision = 1 }));
        Assert.True(changed.TryGetProperty("revision", out _), changed.GetRawText());
        await Json(await owner.PostAsJsonAsync(api + $"/definition-drafts/{id}/publish", new { expectedRevision = await Revision(owner, tenant), expectedDraftRevision = changed.GetProperty("revision").GetInt64() }));
        var agent = await Json(await owner.PostAsJsonAsync(api + "/agents/from-publication", new { definitionId = draft.GetProperty("definitionId").GetString(), version = 1, expectedRevision = await Revision(owner, tenant) }));
        return (tenant, agent.GetProperty("instance").GetProperty("instanceId").GetGuid());
    }
    private static async Task MemoryExecutor(HttpClient owner, Guid tenant, Guid instance, bool read = true, bool manage = true, bool operations = false, bool runRead = true)
    {
        var api = $"/api/v2/tenants/{tenant}"; var actor = (await Json(await owner.GetAsync(api + $"/agents/{instance}"))).GetProperty("principalId").GetGuid();
        var permissions = new List<string> { "agents.chat", "sessions.read" }; if (runRead) permissions.Add("agent-runs.read"); if (operations) permissions.Add("agents.read"); if (read) permissions.Add("memory.read"); if (manage) permissions.Add("memory.manage");
        var role = await Json(await owner.PostAsJsonAsync(api + "/roles", new { name = "Memory executor " + instance.ToString("N"), permissions, expectedRevision = await Revision(owner, tenant) }));
        await Json(await owner.PostAsJsonAsync(api + "/role-assignments", new { principalId = actor, roleId = role.GetProperty("roleId").GetGuid(), expectedRevision = await Revision(owner, tenant) }));
    }
    private static async Task<(Guid Session, AgentRun Run)> MemoryRequest(WebApplicationFactory<Program> factory, HttpClient owner, Guid tenant, Guid instance, Guid? session = null, long instanceRevision = 1)
    {
        var submitted = await SubmitProfileRequest(owner, tenant, instance, instanceRevision, session);
        await factory.Services.GetRequiredService<ITenantRunExecution>().ExecuteRunnableAsync(8);
        var run = await ProfileTerminal(factory, tenant, submitted.Run); Assert.True(run.Status == AgentRunStatus.Completed, $"{run.Status}: {run.Failure?.Code} {run.Failure?.Summary}");
        return (submitted.Session, run);
    }
    [Theory(Timeout = 120000)] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_learned_memory_actual_HTTP_model_and_receipts_use_owned_scopes_then_private_Evidence_on_the_next_Run(bool sqlite)
    {
        var model = new LearnedMemoryModel(); model.Turns.Enqueue([Learn()]); await using var factory = new RuntimeStateFactory(sqlite, model: model);
        using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") });
        var (tenant, instance) = await OwnedMemoryAgent(owner); await MemoryExecutor(owner, tenant, instance);
        var first = await MemoryRequest(factory, owner, tenant, instance); Assert.DoesNotContain(Assert.Single(model.Requests).Context!.Sections, s => s.Id == ContextSectionId.Memory);
        var history = await Json(await owner.GetAsync($"/api/v2/tenants/{tenant}/sessions/{first.Session}/messages"));
        var receipt = history.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("role").GetString() == "assistant").GetProperty("memoryReceipts")[0];
        Assert.Equal("stored", receipt.GetProperty("outcome").GetString()); Assert.Equal(3, receipt.GetProperty("scopes").GetArrayLength()); Assert.DoesNotContain("owner", receipt.GetRawText());
        await MemoryRequest(factory, owner, tenant, instance, first.Session);
        var context = model.Requests.Last().Context!; var memory = Assert.Single(context.Sections, s => s.Id == ContextSectionId.Memory);
        Assert.Equal(ContextLayer.Evidence, memory.Layer); Assert.Contains("PRIVATE_CUSTOMER_EVIDENCE", memory.Message.Text);
        Assert.DoesNotContain(context.Sections, s => s.Layer != ContextLayer.Evidence && s.Message.Text.Contains("PRIVATE_CUSTOMER_EVIDENCE", StringComparison.Ordinal));
        Assert.Contains(context.Sections, s => s.Id == ContextSectionId.MemoryReceipts && s.Message.Text.Contains("Evidence workflow", StringComparison.Ordinal));
        var separate = await MemoryRequest(factory, owner, tenant, instance); Assert.Contains("identityUser", Assert.Single(model.Requests.Last().Context!.Sections, s => s.Id == ContextSectionId.Memory).Message.Text);
        Assert.NotEmpty(AgentRunToolCallCheckpoint.ReadExecutionState(separate.Run.Checkpoint).TenantMemorySources!);
    }
    [Theory(Timeout = 120000)] [InlineData(false, "read")] [InlineData(true, "read")] [InlineData(false, "manage")] [InlineData(true, "manage")]
    [InlineData(false, "review")] [InlineData(true, "review")] [InlineData(false, "off")] [InlineData(true, "off")]
    public async Task Tenant_learned_memory_optional_learning_and_recall_respect_independent_raw_permissions_and_policy_without_implicit_Review(bool sqlite, string mode)
    {
        var model = new LearnedMemoryModel(); model.Turns.Enqueue([Learn()]); await using var factory = new RuntimeStateFactory(sqlite, model: model);
        using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") });
        var (tenant, instance) = await OwnedMemoryAgent(owner, enabled: mode != "off"); await MemoryExecutor(owner, tenant, instance, read: mode != "read", manage: mode != "manage");
        if (mode == "review") Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync($"/api/v2/tenants/{tenant}/operation-policies/memory.manage", new {
            mode = "always", independentReview = false, expectedRevision = await Revision(owner, tenant), expectedPolicyRevision = 0 })).StatusCode);
        var first = await MemoryRequest(factory, owner, tenant, instance);
        var history = await Json(await owner.GetAsync($"/api/v2/tenants/{tenant}/sessions/{first.Session}/messages"));
        var receipt = history.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("role").GetString() == "assistant").GetProperty("memoryReceipts")[0];
        Assert.Equal(mode == "read" ? "stored" : "unavailable", receipt.GetProperty("outcome").GetString());
        await MemoryRequest(factory, owner, tenant, instance, first.Session); Assert.DoesNotContain(model.Requests.Last().Context!.Sections, s => s.Id == ContextSectionId.Memory);
        if (mode is "read" or "off") Assert.DoesNotContain(model.Requests.Last().Context!.Sections, s => s.Id == ContextSectionId.MemoryReceipts);
        else Assert.Contains(model.Requests.Last().Context!.Sections, s => s.Id == ContextSectionId.MemoryReceipts && s.Message.Text.Contains("unavailable", StringComparison.Ordinal));
        Assert.Empty((await factory.Services.GetRequiredService<ITenantApprovalStore>().ListApprovalsAsync(tenant, null, null, new(0, 100))).Items);
    }
    [Theory(Timeout = 120000)] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_learned_memory_other_requester_in_the_same_conversation_and_the_same_Account_in_another_tenant_have_no_private_fallback(bool sqlite)
    {
        var model = new LearnedMemoryModel(); model.Turns.Enqueue([Learn("ALPHA_PRIVATE_MEMORY")]); await using var factory = new RuntimeStateFactory(sqlite, model: model);
        using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") });
        var (tenant, instance) = await OwnedMemoryAgent(owner); await MemoryExecutor(owner, tenant, instance); var first = await MemoryRequest(factory, owner, tenant, instance);
        var api = $"/api/v2/tenants/{tenant}";
        var role = await Json(await owner.PostAsJsonAsync(api + "/roles", new { name = "Peer conversation and memory", permissions = new[] { "agents.chat", "sessions.read", "memory.read", "memory.manage" }, expectedRevision = await Revision(owner, tenant) }));
        var invite = await Json(await owner.PostAsJsonAsync(api + "/members/invite", new { login = "memory-customer@example.test", roleId = role.GetProperty("roleId").GetGuid(), expectedRevision = await Revision(owner, tenant) }));
        using var member = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") }); await Csrf(member);
        Assert.Equal(HttpStatusCode.Created, (await member.PostAsJsonAsync("/api/v2/auth/register", new { login = "memory-customer@example.test", displayName = "Customer", password = "strong-memory-customer-password",
            invitation = new { tenantId = tenant, invitationId = invite.GetProperty("invitationId").GetGuid(), token = invite.GetProperty("token").GetString() } })).StatusCode);
        await Login(member, "memory-customer@example.test", "strong-memory-customer-password");
        var peerHistory = await Json(await member.GetAsync(api + $"/sessions/{first.Session}/messages"));
        Assert.DoesNotContain(peerHistory.GetProperty("items").EnumerateArray(), i => i.TryGetProperty("memoryReceipts", out var receipts) && receipts.ValueKind == JsonValueKind.Array && receipts.GetArrayLength() > 0);
        await MemoryRequest(factory, member, tenant, instance, first.Session); Assert.DoesNotContain(model.Requests.Last().Context!.Sections, s => s.Id is ContextSectionId.Memory or ContextSectionId.MemoryReceipts);
        var beta = (await Json(await owner.PostAsJsonAsync("/api/v2/system/tenants", new { name = "Memory Beta", slug = "memory-beta" }))).GetProperty("tenantId").GetGuid();
        var (_, betaAgent) = await OwnedMemoryAgent(owner, beta); await MemoryExecutor(owner, beta, betaAgent); await MemoryRequest(factory, owner, beta, betaAgent);
        Assert.DoesNotContain(model.Requests.Last().Context!.Sections, s => s.Id is ContextSectionId.Memory or ContextSectionId.MemoryReceipts);
        Assert.DoesNotContain("ALPHA_PRIVATE_MEMORY", string.Join('\n', model.Requests.Last().Messages.Select(m => m.Text)));
    }
    [Theory(Timeout = 120000)] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task Tenant_learned_memory_live_exclusion_refreshes_followups_and_admitted_exclusion_survives_later_broadening(bool sqlite, bool admittedOff)
    {
        var model = new LearnedMemoryModel(); model.Turns.Enqueue([Learn()]); await using var factory = new RuntimeStateFactory(sqlite, model: model);
        using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") });
        var (tenant, instance) = await OwnedMemoryAgent(owner); await MemoryExecutor(owner, tenant, instance); await MemoryRequest(factory, owner, tenant, instance);
        async Task Policy(bool enabled, long revision) => Assert.Equal(HttpStatusCode.OK, (await owner.PatchAsJsonAsync($"/api/v2/tenants/{tenant}/agents/{instance}/settings/memoryPolicy", new {
            expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = revision,
            set = new { sessionMemory = enabled, identityUserPromotion = enabled, identityUserRetrieval = enabled, userPromotion = enabled, userRetrieval = enabled }, clear = Array.Empty<string>() })).StatusCode);
        if (admittedOff) await Policy(false, 1);
        var changed = false; model.Followup = true;
        model.BeforeResponse = async _ => { if (changed) return; changed = true; await Policy(admittedOff, admittedOff ? 2 : 1); };
        await MemoryRequest(factory, owner, tenant, instance, instanceRevision: admittedOff ? 2 : 1);
        var followups = model.Requests.Skip(1).ToArray(); Assert.Equal(2, followups.Length);
        if (admittedOff) Assert.All(followups, r => Assert.DoesNotContain(r.Context!.Sections, s => s.Id == ContextSectionId.Memory));
        else Assert.Contains(followups[0].Context!.Sections, s => s.Id == ContextSectionId.Memory);
        Assert.DoesNotContain(followups[1].Context!.Sections, s => s.Id is ContextSectionId.Memory or ContextSectionId.MemoryReceipts);
        Assert.Single(followups[1].Messages, m => m.Role == ModelRole.Tool && m.Name == "agent.state.inspect");
        Assert.Single(followups[1].Context!.Sections, s => s.Id == ContextSectionId.ExecutionFacts);
        model.Followup = false; model.BeforeResponse = null;
        if (!admittedOff) await Policy(true, 2);
        await MemoryRequest(factory, owner, tenant, instance, instanceRevision: 3);
        Assert.Contains(model.Requests.Last().Context!.Sections, s => s.Id == ContextSectionId.Memory && s.Message.Text.Contains("PRIVATE_CUSTOMER_EVIDENCE", StringComparison.Ordinal));
    }
    [Theory(Timeout = 120000)] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_learned_memory_current_read_policy_refuses_a_captured_model_result_without_publishing_it(bool sqlite)
    {
        var model = new LearnedMemoryModel(); model.Turns.Enqueue([Learn()]); await using var factory = new RuntimeStateFactory(sqlite, model: model);
        using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") });
        var (tenant, instance) = await OwnedMemoryAgent(owner); await MemoryExecutor(owner, tenant, instance); await MemoryRequest(factory, owner, tenant, instance);
        model.Hold = true; model.Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var submitted = await SubmitProfileRequest(owner, tenant, instance); await factory.Services.GetRequiredService<ITenantRunExecution>().ExecuteRunnableAsync(8);
        await model.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        try
        {
            var active = (await factory.Services.GetRequiredService<ITenantRunStore>().FindTenantRunAsync(tenant, submitted.Run))!;
            Assert.NotEmpty(AgentRunToolCallCheckpoint.ReadExecutionState(active.Checkpoint).TenantMemorySources!);
            Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync($"/api/v2/tenants/{tenant}/operation-policies/memory.read", new {
                mode = "always", independentReview = false, expectedRevision = await Revision(owner, tenant), expectedPolicyRevision = 0 })).StatusCode);
            model.Release.TrySetResult(); var terminal = await ProfileTerminal(factory, tenant, submitted.Run); Assert.NotEqual(AgentRunStatus.Completed, terminal.Status);
            var history = await Json(await owner.GetAsync($"/api/v2/tenants/{tenant}/sessions/{submitted.Session}/messages"));
            Assert.DoesNotContain("The current request is complete.", history.GetRawText());
        }
        finally { model.Release.TrySetResult(); }
    }
    [Fact(Timeout = 120000)]
    public async Task Tenant_learned_memory_actual_SQLite_host_restart_retains_owned_values_receipts_and_new_Run_recall()
    {
        var model = new LearnedMemoryModel(); model.Turns.Enqueue([Learn()]); await using var first = new RuntimeStateFactory(true, model: model);
        using var owner = first.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") });
        var (tenant, instance) = await OwnedMemoryAgent(owner); await MemoryExecutor(owner, tenant, instance); var saved = await MemoryRequest(first, owner, tenant, instance);
        await first.DisposeAsync(); var nextModel = new LearnedMemoryModel(); await using var reopened = new RuntimeStateFactory(true, model: nextModel, root: first.Root);
        using var again = reopened.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") }); await Login(again, "owner@example.test", "explicit-synthetic-test-password");
        var retained = await Json(await again.GetAsync($"/api/v2/tenants/{tenant}/sessions/{saved.Session}/messages")); Assert.Contains("stored", retained.GetRawText());
        var old = (await reopened.Services.GetRequiredService<ITenantRunStore>().FindTenantRunAsync(tenant, saved.Run.AgentRunId))!; Assert.Equal(AgentRunStatus.Completed, old.Status);
        await MemoryRequest(reopened, again, tenant, instance); Assert.Contains(Assert.Single(nextModel.Requests).Context!.Sections,
            s => s.Id == ContextSectionId.Memory && s.Message.Text.Contains("PRIVATE_CUSTOMER_EVIDENCE", StringComparison.Ordinal));
        Assert.Single(model.Requests);
    }
    [Theory(Timeout = 120000)] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_learned_memory_Session_delete_clears_local_facts_and_preserves_explicitly_promoted_User_data(bool sqlite)
    {
        var model = new LearnedMemoryModel(); model.Turns.Enqueue([Learn()]); await using var factory = new RuntimeStateFactory(sqlite, model: model);
        using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") });
        var (tenant, instance) = await OwnedMemoryAgent(owner); await MemoryExecutor(owner, tenant, instance); var saved = await MemoryRequest(factory, owner, tenant, instance);
        var route = $"/api/v2/tenants/{tenant}/sessions/{saved.Session}"; var current = await Json(await owner.GetAsync(route));
        var response = await owner.DeleteAsync(route + $"?expectedRevision={await Revision(owner, tenant)}&expectedSessionRevision={current.GetProperty("revision").GetInt64()}");
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        await MemoryRequest(factory, owner, tenant, instance); Assert.Contains(model.Requests.Last().Context!.Sections,
            s => s.Id == ContextSectionId.Memory && s.Message.Text.Contains("PRIVATE_CUSTOMER_EVIDENCE", StringComparison.Ordinal));
        if (sqlite)
        {
            var contexts = factory.Services.GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<AgentCore.Infrastructure.Persistence.AgentCoreDbContext>>();
            await using var db = contexts.CreateDbContext(); var rows = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToArrayAsync(db.Set<AgentCore.Infrastructure.Security.TenantLearnedMemoryRecord>());
            Assert.Equal(2, rows.Count(i => i.Status == 0)); var local = Assert.Single(rows, i => i.Scope == 0); Assert.Equal(2, local.Status); Assert.Empty(local.Content); Assert.Empty(local.Subject);
        }
    }
}
