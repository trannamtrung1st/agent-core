using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using Microsoft.Extensions.Time.Testing;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
namespace AgentCore.Api.Tests;
public sealed partial class TenantAgentAccessTests
{
    private sealed class AutomationReadModel : ILanguageModel
    {
        public ModelCapabilities Capabilities => new(true, true, true);
        public Guid Target; public bool List; public bool Hold; public bool Background; public bool Quiet;
        public TaskCompletionSource Observed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously); public readonly System.Collections.Concurrent.ConcurrentQueue<ModelRequest> Requests = new();
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask; Requests.Enqueue(request); ct.ThrowIfCancellationRequested(); var tool = List ? ToolCatalog.AutomationList : ToolCatalog.AutomationInspect;
            var result = request.Messages.LastOrDefault(m => m.Role == ModelRole.Tool && m.Name == tool);
            if (result is null)
            {
                Assert.Contains(request.Tools!, t => t.Name == tool && t.ParametersJson.Contains("additionalProperties", StringComparison.Ordinal));
                yield return new ModelToolCallEvent(new("automation-read", tool, List ? "{}" : JsonSerializer.Serialize(new { automationId = Target })));
                yield return new ModelCompleted(ModelStopReason.ToolCalls); yield break;
            }
            using var direct = JsonDocument.Parse(result.Text);
            if (direct.RootElement.TryGetProperty("decision", out var decision) && decision.GetString() == "requiresApproval"
                && !request.Messages.Any(m => m.Role == ModelRole.Tool && m.Name == ToolCatalog.RequestAuthorization))
            {
                yield return new ModelToolCallEvent(new("automation-read-consent", ToolCatalog.RequestAuthorization,
                    JsonSerializer.Serialize(new { operation = "inspect_automations", read = new { tool, arguments = List ? JsonSerializer.SerializeToElement(new { }) : JsonSerializer.SerializeToElement(new { automationId = Target }) } })));
                yield return new ModelCompleted(ModelStopReason.ToolCalls); yield break;
            }
            Observed.TrySetResult(); if (Hold) await Release.Task.WaitAsync(ct);
            if (Background)
            {
                yield return new ModelToolCallEvent(new("finish-automation-inspection", ToolCatalog.WorkComplete,
                    JsonSerializer.Serialize(new { outcome = Quiet ? "NoAction" : "Response", summary = "Inspected currently readable Automation metadata.", attentionRequired = false })));
                yield return new ModelCompleted(ModelStopReason.ToolCalls); yield break;
            }
            yield return new ModelSemanticResponseReady(new("Observed only currently readable Automation metadata.", new(ModelSpeechMode.None, null), []));
            yield return new ModelCompleted(ModelStopReason.Completed);
        }
    }
    [Theory(Timeout = 120000)] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task Tenant_model_Automation_reads_use_owned_configuration_current_executor_reads_and_durable_sources(bool sqlite, bool list)
    {
        var model = new AutomationReadModel { List = list }; await using var factory = new RuntimeStateFactory(sqlite, model: model);
        using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") });
        foreach (var host in factory.Services.GetServices<IHostedService>().Where(s => s is AgentCore.Api.AgentRunHostedService or AgentCore.Api.TriggerSchedulerHostedService)) await host.StopAsync(CancellationToken.None);
        var (tenant, instance) = await OwnedMemoryAgent(owner, enabled: false, automation: true, automationReads: true); await MemoryExecutor(owner, tenant, instance, operations: true);
        var api = $"/api/v2/tenants/{tenant}"; var principal = (await Json(await owner.GetAsync(api + $"/agents/{instance}"))).GetProperty("principalId").GetGuid();
        var role = await Json(await owner.PostAsJsonAsync(api + "/roles", new { name = "Exact Automation reader", permissions = new[] { "automations.read" }, expectedRevision = await Revision(owner, tenant) }));
        var row = await Json(await owner.PostAsJsonAsync(api + $"/agents/{instance}/automations", new { enabled = false, name = "Readable owned task", instructions = "SAVED_AUTOMATION_METADATA",
            requiresTools = false, schedule = new { kind = "oneShot", timeZone = "UTC", atUtc = DateTimeOffset.UtcNow.AddHours(1).ToString("O") }, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1 }));
        model.Target = row.GetProperty("automationId").GetGuid();
        await Json(await owner.PostAsJsonAsync(api + "/role-assignments", new { principalId = principal, roleId = role.GetProperty("roleId").GetGuid(), resourceType = "automation", resourceId = model.Target, expectedRevision = await Revision(owner, tenant) }));
        var run = await MemoryRequest(factory, owner, tenant, instance); Assert.Equal(AgentRunStatus.Completed, run.Run.Status);
        Assert.Single(TenantAutomationReadReferences.Read(run.Run.Checkpoint));
        var read = model.Requests.SelectMany(r => r.Messages).Last(m => m.Role == ModelRole.Tool && m.Name == (list ? ToolCatalog.AutomationList : ToolCatalog.AutomationInspect));
        using var response = JsonDocument.Parse(read.Text); var item = Assert.Single(response.RootElement.GetProperty("observation").GetProperty("items").EnumerateArray());
        Assert.Equal(model.Target, item.GetProperty("automationId").GetGuid()); Assert.Contains("SAVED_AUTOMATION_METADATA", item.GetProperty("instructions").GetString());
        Assert.Null(run.Run.Owner.ProfileIdOrNull);
    }

    private static async Task<(Guid Tenant, Guid Instance, Guid Role, Guid Target)> AutomationReadSetup(RuntimeStateFactory factory, HttpClient owner, AutomationReadModel model, bool chat = true)
    {
        foreach (var host in factory.Services.GetServices<IHostedService>().Where(s => s is AgentCore.Api.AgentRunHostedService or AgentCore.Api.TriggerSchedulerHostedService)) await host.StopAsync(CancellationToken.None);
        var (tenant, instance) = await OwnedMemoryAgent(owner, enabled: false, automation: true, automationReads: true);
        if (chat) await MemoryExecutor(owner, tenant, instance, read: false, manage: false, runRead: false);
        var api = $"/api/v2/tenants/{tenant}";
        var actor = (await Json(await owner.GetAsync(api + $"/agents/{instance}"))).GetProperty("principalId").GetGuid();
        var role = await Json(await owner.PostAsJsonAsync(api + "/roles", new { name = "Scoped Automation reader", permissions = new[] { "automations.read" }, expectedRevision = await Revision(owner, tenant) }));
        var row = await NewReadAutomation(owner, tenant, instance, "SAVED_AUTOMATION_METADATA"); model.Target = row.GetProperty("automationId").GetGuid();
        await Json(await owner.PostAsJsonAsync(api + "/role-assignments", new { principalId = actor, roleId = role.GetProperty("roleId").GetGuid(), resourceType = "automation", resourceId = model.Target, expectedRevision = await Revision(owner, tenant) }));
        return (tenant, instance, role.GetProperty("roleId").GetGuid(), model.Target);
    }
    private static Task<JsonElement> NewReadAutomation(HttpClient owner, Guid tenant, Guid instance, string instructions) => CreateReadAutomation(owner, tenant, instance, instructions);
    private static async Task<JsonElement> CreateReadAutomation(HttpClient owner, Guid tenant, Guid instance, string instructions) => await Json(await owner.PostAsJsonAsync($"/api/v2/tenants/{tenant}/agents/{instance}/automations",
        new { enabled = false, name = "Owned read fixture", instructions, requiresTools = false,
            schedule = new { kind = "oneShot", timeZone = "UTC", atUtc = DateTimeOffset.UtcNow.AddHours(1).ToString("O") }, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1 }));

    public static TheoryData<bool, string> AutomationReadReviewCases => new() { { false, "approve" }, { true, "approve" }, { false, "reject" }, { true, "reject" }, { false, "changed" }, { true, "changed" }, { false, "revoked" }, { true, "revoked" }, { false, "list-approve" }, { true, "list-approve" }, { false, "list-reject" }, { true, "list-reject" } };
    [Theory(Timeout = 120000)] [MemberData(nameof(AutomationReadReviewCases))]
    public async Task Tenant_model_Automation_read_Review_binds_exact_configuration_and_current_authority(bool sqlite, string mode)
    {
        var list = mode.StartsWith("list-", StringComparison.Ordinal); if (list) mode = mode[5..];
        var model = new AutomationReadModel { List = list }; await using var factory = new RuntimeStateFactory(sqlite, model: model);
        using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") });
        var setup = await AutomationReadSetup(factory, owner, model); var (tenant, instance, role, target) = setup; var api = $"/api/v2/tenants/{tenant}";
        await Json(await owner.PutAsJsonAsync(api + "/operation-policies/automations.read", new { mode = "always", independentReview = false, expectedRevision = await Revision(owner, tenant), expectedPolicyRevision = 0 }));
        var submitted = await SubmitProfileRequest(owner, tenant, instance); var runs = factory.Services.GetRequiredService<ITenantRunStore>();
        await factory.Services.GetRequiredService<ITenantRunExecution>().ExecuteRunnableAsync(8);
        AgentRun? pending = null;
        for (var pass = 0; pass < 500; pass++) { pending = await runs.FindTenantRunAsync(tenant, submitted.Run); if (pending!.IsTerminal || pending.Status == AgentRunStatus.WaitingForApproval) break; await Task.Delay(25); }
        Assert.True(pending!.Status == AgentRunStatus.WaitingForApproval, $"{pending.Status}: {pending.Failure}; tools=" + string.Join(";", model.Requests.SelectMany(r => r.Messages).Where(m => m.Role == ModelRole.Tool).Select(m => m.Name + ":" + m.Text[..Math.Min(500, m.Text.Length)])));
        var approvals = factory.Services.GetRequiredService<ITenantApprovalStore>(); var request = (await approvals.FindApprovalAsync(tenant, pending.ApprovalRequestId!.Value))!;
        var conversation = await Json(await owner.GetAsync(api + $"/sessions/{submitted.Session}/approvals"));
        Assert.Equal("inspect_automations", Assert.Single(conversation.GetProperty("items").EnumerateArray()).GetProperty("operation").GetString());
        Assert.Equal(ApprovalKind.Review, request.Kind); Assert.NotNull(request.Operation.AutomationRead); Assert.Null(await approvals.FindOperationGrantAsync(tenant, request.RequestId));
        Assert.DoesNotContain(model.Requests.SelectMany(r => r.Messages), m => m.Role == ModelRole.Tool && m.Text.Contains("SAVED_AUTOMATION_METADATA", StringComparison.Ordinal));
        var detail = await Json(await owner.GetAsync(api + $"/approvals/{request.RequestId}")); Assert.Contains("SAVED_AUTOMATION_METADATA", detail.GetRawText());
        Assert.Contains(detail.GetProperty("review").GetProperty("changes").EnumerateArray(), change => change.GetProperty("field").GetString() == "Instructions" && change.GetProperty("after").GetString() == "SAVED_AUTOMATION_METADATA");
        Assert.Contains((await Json(await owner.GetAsync(api + "/approvals"))).GetProperty("items").EnumerateArray(), r => r.GetProperty("requestId").GetGuid() == request.RequestId);
        if (mode == "revoked")
        {
            var actor = (await Json(await owner.GetAsync(api + "/access/me"))).GetProperty("principalId").GetGuid();
            await factory.Services.GetRequiredService<TenantAccessService>().SaveRoleAsync(actor, tenant, role, "Scoped Automation reader", ["automations.run"], await Revision(owner, tenant));
        }
        if (mode == "changed")
        {
            var route = api + $"/agents/{instance}/automations/{target}"; var row = await Json(await owner.GetAsync(route));
            await Json(await owner.PutAsJsonAsync(route, new { name = "Changed after Review", instructions = "SUBSTITUTED_CONFIGURATION", enabled = false, requiresTools = false,
                schedule = new { kind = "oneShot", timeZone = "UTC", atUtc = DateTimeOffset.UtcNow.AddHours(2).ToString("O") }, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1, expectedAutomationRevision = row.GetProperty("revision").GetInt64() }));
        }
        var decided = await owner.PostAsJsonAsync(api + $"/approvals/{request.RequestId}/decision", new { decision = mode == "reject" ? "reject" : "approve", actionHash = request.Operation.ActionHash,
            expectedRevision = await Revision(owner, tenant), expectedRequestRevision = request.Revision });
        if (mode is "changed" or "revoked") { Assert.False(decided.IsSuccessStatusCode); Assert.Null(await factory.Services.GetRequiredService<ITenantApprovedOperationStore>().FindOperationReceiptAsync(tenant, request.Operation.OperationId)); return; }
        Assert.True(decided.StatusCode == HttpStatusCode.OK, await decided.Content.ReadAsStringAsync());
        var completed = await ExperienceMaintenanceFinal(factory, tenant, submitted.Run);
        Assert.True(completed.Status == AgentRunStatus.Completed, $"{completed.Status}: {completed.Failure}");
        var sources = TenantAutomationReadReferences.Read(completed.Checkpoint);
        if (mode == "approve")
        {
            Assert.Single(sources);
            if (sqlite)
            {
                var reopened = new AgentCore.Infrastructure.Security.SqliteIamStore(factory.Services.GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<AgentCore.Infrastructure.Persistence.AgentCoreDbContext>>());
                await reopened.RequireTenantAutomationReadSourcesAsync(new(tenant, completed.AgentRunId), sources, null);
            }
            var receipt = await factory.Services.GetRequiredService<ITenantApprovedOperationStore>().FindOperationReceiptAsync(tenant, request.Operation.OperationId);
            Assert.Equal(TenantAutomationReadRules.Completed, receipt!.ReasonCode);
            Assert.Contains(model.Requests.SelectMany(r => r.Messages), m => m.Role == ModelRole.Tool && m.Text.Contains("SAVED_AUTOMATION_METADATA", StringComparison.Ordinal));
        }
        else { Assert.Empty(sources); Assert.DoesNotContain(model.Requests.SelectMany(r => r.Messages), m => m.Role == ModelRole.Tool && m.Text.Contains("SAVED_AUTOMATION_METADATA", StringComparison.Ordinal)); }
        Assert.Equal(1, (await factory.Services.GetRequiredService<ITenantAutomationStore>().FindTenantAutomationAsync(tenant, target))!.Revision);
    }
    [Theory(Timeout = 120000)] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_model_Automation_list_filters_before_count_and_current_source_loss_blocks_later_output(bool sqlite)
    {
        var model = new AutomationReadModel { List = true, Hold = true }; await using var factory = new RuntimeStateFactory(sqlite, model: model);
        using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") });
        var (tenant, instance, role, target) = await AutomationReadSetup(factory, owner, model); await CreateReadAutomation(owner, tenant, instance, "UNREADABLE_SIBLING");
        var submitted = await SubmitProfileRequest(owner, tenant, instance); await factory.Services.GetRequiredService<ITenantRunExecution>().ExecuteRunnableAsync(8);
        await model.Observed.Task.WaitAsync(TimeSpan.FromSeconds(20));
        try
        {
            var run = (await factory.Services.GetRequiredService<ITenantRunStore>().FindTenantRunAsync(tenant, submitted.Run))!;
            Assert.Single(TenantAutomationReadReferences.Read(run.Checkpoint));
            var observed = await factory.Services.GetRequiredService<TenantAutomationReadService>().PrepareAsync(new(tenant, run.AgentRunId), run.Claim!.Generation, ToolCatalog.AutomationList, "{}", CancellationToken.None);
            using var body = JsonDocument.Parse(observed.Value.ObservationJson); Assert.Equal(1, body.RootElement.GetProperty("total").GetInt32()); Assert.DoesNotContain("UNREADABLE_SIBLING", observed.Value.ObservationJson);
            var limited = TenantAutomationReadService.Serialize(observed.Value, tenant, 512); Assert.DoesNotContain("automationReadSource", limited); Assert.DoesNotContain("SAVED_AUTOMATION_METADATA", limited);
            await Assert.ThrowsAsync<AccessDeniedException>(() => factory.Services.GetRequiredService<TenantAutomationReadService>().PrepareAsync(new(tenant, run.AgentRunId), Guid.NewGuid(), ToolCatalog.AutomationList, "{}", CancellationToken.None).AsTask());
            var actor = (await Json(await owner.GetAsync($"/api/v2/tenants/{tenant}/access/me"))).GetProperty("principalId").GetGuid();
            await factory.Services.GetRequiredService<TenantAccessService>().SaveRoleAsync(actor, tenant, role, "Scoped Automation reader", ["automations.run"], await Revision(owner, tenant));
        }
        finally { model.Release.TrySetResult(); }
        AgentRun? terminal = null;
        for (var pass = 0; pass < 800; pass++)
        {
            await factory.Services.GetRequiredService<ITenantRunExecution>().ExecuteRunnableAsync(8);
            terminal = (await factory.Services.GetRequiredService<ITenantRunStore>().FindTenantRunAsync(tenant, submitted.Run))!;
            if (terminal.IsTerminal) break;
            await Task.Delay(25);
        }
        Assert.True(terminal!.IsTerminal, $"{terminal.Status}: {terminal.Failure}; next={terminal.NextRetryAtUtc:O}; progress={terminal.Progress?.Summary}");
        Assert.NotEqual(AgentRunStatus.Completed, terminal.Status);
        Assert.DoesNotContain("Observed only", terminal.Result?.Text ?? "");
    }
    [Theory(Timeout = 120000)] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task Tenant_independent_Automation_metadata_read_needs_no_conversation_Agent_or_Memory_read_grant(bool sqlite, bool review)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow); var model = new AutomationReadModel { Background = true, Quiet = review };
        await using var factory = new RuntimeStateFactory(sqlite, clock, model); using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") });
        var (tenant, instance, _, _) = await AutomationReadSetup(factory, owner, model, chat: false); await ExperienceAutomationRights(owner, tenant, instance);
        var api = $"/api/v2/tenants/{tenant}";
        if (review) await Json(await owner.PutAsJsonAsync(api + "/operation-policies/automations.read", new { mode = "always", independentReview = false, expectedRevision = await Revision(owner, tenant), expectedPolicyRevision = 0 }));
        var completed = await ExperienceAutomation(factory, owner, clock, tenant, instance, 1, scheduled: true, waitForApproval: review);
        if (review)
        {
            Assert.Equal(AgentRunStatus.WaitingForApproval, completed.Status);
            var request = (await factory.Services.GetRequiredService<ITenantApprovalStore>().FindApprovalAsync(tenant, completed.ApprovalRequestId!.Value))!;
            var detail = await Json(await owner.GetAsync(api + $"/approvals/{request.RequestId}"));
            await Json(await owner.PostAsJsonAsync(api + $"/approvals/{request.RequestId}/decision", new { decision = "approve", actionHash = request.Operation.ActionHash,
                expectedRevision = detail.GetProperty("securityRevision").GetInt64(), expectedRequestRevision = request.Revision }));
            completed = await ExperienceMaintenanceFinal(factory, tenant, completed.AgentRunId);
            Assert.Equal(AgentRunOutcomeKind.NoAction, completed.Result!.OutcomeKind);
        }
        Assert.True(completed.Status == AgentRunStatus.Completed, $"{completed.Status}: {completed.Failure}"); Assert.Null(completed.Admission.ExecutionContext!.OriginalInitiatorPrincipalId);
        Assert.Single(TenantAutomationReadReferences.Read(completed.Checkpoint)); Assert.Null(completed.Owner.ProfileIdOrNull);
        Assert.Contains(model.Requests.SelectMany(r => r.Messages), m => m.Role == ModelRole.Tool && m.Text.Contains("SAVED_AUTOMATION_METADATA", StringComparison.Ordinal));
    }
}
