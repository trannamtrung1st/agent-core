using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Net.Http.Json;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Application.Triggers;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Domain.Triggers;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AgentCore.Api.Tests;
public sealed partial class TenantAgentAccessTests
{
    private sealed class MutationVerifier(TriggerCommandAuthorizationDecision decision = TriggerCommandAuthorizationDecision.Allow) : ITriggerCommandAuthorizer
    {
        public ConcurrentQueue<AutomationAuthorizationRequest> Requests { get; } = new();
        public ValueTask<TriggerCommandAuthorizationVerdict> AuthorizeAsync(AutomationAuthorizationRequest proposed, CancellationToken ct = default)
        { ct.ThrowIfCancellationRequested(); Requests.Enqueue(proposed); return ValueTask.FromResult(new TriggerCommandAuthorizationVerdict(decision)); }
    }
    private sealed class AutomationMutationModel : ILanguageModel
    {
        public ModelCapabilities Capabilities => new(true, true, true);
        public ConcurrentQueue<ModelRequest> Requests { get; } = new(); public bool Hold = false; public bool Blocked; public bool CreateOnly; public bool Background = false;
        public TaskCompletionSource Observed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private const string DefaultCreateArguments = """{"enabled":false,"name":"Model-authored owned task","instructions":"Retain the canonical exact task.","relativeDelaySeconds":3600,"executionTarget":"backgroundSession","requiresTools":false}""";
        public string CreateArguments { get; init; } = DefaultCreateArguments;
        public Guid CreatedId; public long SavedRevision;
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask; ct.ThrowIfCancellationRequested(); Requests.Enqueue(request);
            var created = request.Messages.LastOrDefault(m => m.Role == ModelRole.Tool && m.Name == ToolCatalog.AutomationCreate);
            string? tool = null; string arguments = "{}";
            if (created is null)
            {
                Assert.Contains(request.Tools!, t => t.Name == ToolCatalog.AutomationCreate && t.Description.Contains("admitted task", StringComparison.Ordinal));
                tool = ToolCatalog.AutomationCreate; arguments = CreateArguments;
            }
            else
            {
                using var result = JsonDocument.Parse(created.Text);
                var value = result.RootElement;
                if (value.TryGetProperty("decision", out var gate) && gate.GetString() == "requiresApproval")
                {
                    var consent = request.Messages.LastOrDefault(m => m.Role == ModelRole.Tool && m.Name == ToolCatalog.RequestAuthorization);
                    if (consent is null)
                    {
                        using var exact = JsonDocument.Parse(CreateArguments);
                        yield return new ModelToolCallEvent(new("mutation-consent", ToolCatalog.RequestAuthorization, JsonSerializer.Serialize(new { operation = "mutate_automation", automation = new { tool = ToolCatalog.AutomationCreate, arguments = exact.RootElement } })));
                        yield return new ModelCompleted(ModelStopReason.ToolCalls); yield break;
                    }
                    using var receipt = JsonDocument.Parse(consent.Text); value = receipt.RootElement.Clone();
                }
                Blocked = !value.TryGetProperty("applied", out var applied) || !applied.GetBoolean();
                if (!Blocked)
                {
                    CreatedId = value.GetProperty("automationId").GetGuid(); SavedRevision = value.GetProperty("revision").GetInt64();
                    if (!CreateOnly)
                    {
                        if (!request.Messages.Any(m => m.Role == ModelRole.Tool && m.Name == ToolCatalog.AutomationInspect)) { tool = ToolCatalog.AutomationInspect; arguments = JsonSerializer.Serialize(new { automationId = CreatedId }); }
                        else foreach (var step in new[] { ToolCatalog.AutomationUpdate, ToolCatalog.AutomationEnable, ToolCatalog.AutomationDisable })
                        {
                            var previous = request.Messages.LastOrDefault(m => m.Role == ModelRole.Tool && m.Name == step);
                            if (previous is null) { tool = step; arguments = step == ToolCatalog.AutomationUpdate ? JsonSerializer.Serialize(new { automationId = CreatedId, expectedRevision = SavedRevision, name = "Renamed by the current task" }) : JsonSerializer.Serialize(new { automationId = CreatedId, expectedRevision = SavedRevision }); break; }
                            using var changed = JsonDocument.Parse(previous.Text); Assert.True(changed.RootElement.GetProperty("applied").GetBoolean(), previous.Text); SavedRevision = changed.RootElement.GetProperty("revision").GetInt64();
                        }
                    }
                }
            }
            if (tool is not null) { yield return new ModelToolCallEvent(new("tenant-" + tool, tool, arguments)); yield return new ModelCompleted(ModelStopReason.ToolCalls); yield break; }
            Observed.TrySetResult(); if (Hold) await Release.Task.WaitAsync(ct);
            if (Background)
            {
                yield return new ModelToolCallEvent(new("complete-automation-authoring", ToolCatalog.WorkComplete, JsonSerializer.Serialize(new { outcome = Blocked ? "NoAction" : "Response", summary = Blocked ? "The Automation change was withheld." : "Saved and updated the exact owned Automation.", attentionRequired = false })));
                yield return new ModelCompleted(ModelStopReason.ToolCalls); yield break;
            }
            yield return new ModelSemanticResponseReady(new(Blocked ? "The Automation change was withheld." : "Saved and updated the exact owned Automation.", new(ModelSpeechMode.None, null), []));
            yield return new ModelCompleted(ModelStopReason.Completed);
        }
    }
    private static async Task<(Guid Tenant, Guid Instance, Guid Grant)> MutationAgent(RuntimeStateFactory factory, HttpClient owner, bool write = true, bool events = false, bool eventSourceRead = true)
    {
        foreach (var host in factory.Services.GetServices<IHostedService>().Where(s => s is AgentCore.Api.AgentRunHostedService or AgentCore.Api.TriggerSchedulerHostedService)) await host.StopAsync(CancellationToken.None);
        (Guid tenant, Guid instance) owned;
        try { owned = await OwnedMemoryAgent(owner, enabled: false, automation: true, events: events, automationMutations: true); }
        catch (Xunit.Sdk.XunitException error) when (error.Message.StartsWith("CSRF ", StringComparison.Ordinal))
        {
            Assert.Fail(error.Message + "; HTTP diagnostics=" + factory.HttpFailures.Describe());
            throw;
        }
        var (tenant, instance) = owned;
        await MemoryExecutor(owner, tenant, instance, read: false, manage: false, operations: true);
        var api = $"/api/v2/tenants/{tenant}"; var principal = (await Json(await owner.GetAsync(api + $"/agents/{instance}"))).GetProperty("principalId").GetGuid();
        var permissions = write ? new[] { "automations.read", "automations.create", "automations.update", "automations.delete" } : new[] { "automations.read" };
        if (events && eventSourceRead) permissions = [.. permissions, "core-events.read"];
        var role = await Json(await owner.PostAsJsonAsync(api + "/roles", new { name = "Owned Automation author", permissions, expectedRevision = await Revision(owner, tenant) }));
        var grant = await Json(await owner.PostAsJsonAsync(api + "/role-assignments", new { principalId = principal, roleId = role.GetProperty("roleId").GetGuid(), expectedRevision = await Revision(owner, tenant) }));
        return (tenant, instance, grant.GetProperty("assignmentId").GetGuid());
    }
    [Theory(Timeout = 120000)] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_model_Automation_mutations_create_inspect_edit_enable_disable_and_finish_through_the_real_Runtime_without_legacy_authority(bool sqlite)
    {
        var model = new AutomationMutationModel(); var verifier = new MutationVerifier();
        await using var factory = new RuntimeStateFactory(sqlite, model: model, semantic: verifier); using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") });
        var (tenant, instance, _) = await MutationAgent(factory, owner);
        const string task = "Create a disabled background task in one hour, inspect it, rename it, enable it and disable it again.";
        var submitted = await SubmitProfileRequest(owner, tenant, instance, message: task); await factory.Services.GetRequiredService<ITenantRunExecution>().ExecuteRunnableAsync(8);
        var run = await ProfileTerminal(factory, tenant, submitted.Run); Assert.True(run.Status == AgentRunStatus.Completed, $"{run.Status}: {run.Failure?.Code} {run.Failure?.Summary}");
        Assert.True(!model.Blocked, string.Join(";", model.Requests.SelectMany(r => r.Messages).Where(m => m.Role == ModelRole.Tool).Select(m => m.Name + ":" + m.Text[..Math.Min(1200, m.Text.Length)]))); Assert.Equal(4, verifier.Requests.Count); Assert.All(verifier.Requests, r => { Assert.Equal(task, r.CurrentUserText); Assert.False(r.AdmittedTask); });
        var store = factory.Services.GetRequiredService<ITenantAutomationStore>(); var saved = (await store.FindTenantAutomationAsync(tenant, model.CreatedId))!;
        Assert.Equal(AutomationStatus.Disabled, saved.Status); Assert.Equal(4, saved.Revision); Assert.Equal("Renamed by the current task", saved.Name);
        Assert.Equal(TriggerAuthorizationOrigin.AgentTask, saved.Provenance.AuthorizationOrigin); Assert.Null(saved.Owner.ProfileIdOrNull);
        Assert.Equal(submitted.Session, run.SessionId); Assert.Single(TenantAutomationReadReferences.Read(run.Checkpoint));
        using var lastEffect = JsonDocument.Parse(model.Requests.SelectMany(r => r.Messages).Last(m => m.Role == ModelRole.Tool && m.Name == ToolCatalog.AutomationDisable).Text);
        var receipts = await factory.Services.GetRequiredService<ITenantApprovedOperationStore>().FindOperationReceiptAsync(tenant, lastEffect.RootElement.GetProperty("operationId").GetGuid());
        Assert.NotNull(receipts!.AutomationMutation); Assert.Equal(4, receipts.AutomationMutation!.Revision);
        Assert.Empty((await factory.Services.GetRequiredService<ITenantApprovalStore>().ListApprovalsAsync(tenant, null, null, new())).Items);
        if (sqlite)
        {
            var reopened = new SqliteIamStore(factory.Services.GetRequiredService<IDbContextFactory<AgentCoreDbContext>>());
            Assert.True(TenantAutomationRules.Same(saved, (await reopened.FindTenantAutomationAsync(tenant, saved.AutomationId))!));
            await using var db = await factory.Services.GetRequiredService<IDbContextFactory<AgentCoreDbContext>>().CreateDbContextAsync();
            await Assert.ThrowsAsync<SqliteException>(() => db.GetService<IMigrator>().MigrateAsync("20261022020346_TenantAutomationReadConsent"));
            Assert.Contains("20261022041619_TenantAutomationMutations", await db.Database.GetAppliedMigrationsAsync());
            // A refused multi-step Down can have committed safe successor removals.
            // Restore the current schema before using its EF receipt mapping again.
            await db.GetService<IMigrator>().MigrateAsync(); Assert.False(db.Database.HasPendingModelChanges());
            Assert.True(TenantAutomationRules.Same(saved, (await reopened.FindTenantAutomationAsync(tenant, saved.AutomationId))!));
            Assert.NotNull((await reopened.FindOperationReceiptAsync(tenant, receipts.OperationId))!.AutomationMutation);
        }
    }
    [Theory(Timeout = 120000)] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_model_Automation_semantic_denial_keeps_owned_state_unchanged_and_creates_no_effect(bool sqlite)
    {
        var model = new AutomationMutationModel { CreateOnly = true }; var verifier = new MutationVerifier(TriggerCommandAuthorizationDecision.Deny);
        await using var factory = new RuntimeStateFactory(sqlite, model: model, semantic: verifier); using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") });
        var (tenant, instance, _) = await MutationAgent(factory, owner); var submitted = await SubmitProfileRequest(owner, tenant, instance, message: "Does this agent support Automation?");
        await factory.Services.GetRequiredService<ITenantRunExecution>().ExecuteRunnableAsync(8); var run = await ProfileTerminal(factory, tenant, submitted.Run);
        Assert.Equal(AgentRunStatus.Completed, run.Status); Assert.True(model.Blocked); Assert.Single(verifier.Requests);
        Assert.Empty((await factory.Services.GetRequiredService<ITenantAutomationStore>().ListTenantAutomationsAsync(tenant, instance, null, 0, 8)).Items);
        Assert.Null(await factory.Services.GetRequiredService<ITenantDirectOperationStore>().FindDirectOperationReceiptAsync(tenant, run.AgentRunId, "tenant-" + ToolCatalog.AutomationCreate));
    }
    [Theory(Timeout = 120000)]
    [InlineData(false, "review")] [InlineData(true, "review")] [InlineData(false, "exception")] [InlineData(true, "exception")]
    [InlineData(false, "semantic")] [InlineData(true, "semantic")] [InlineData(false, "reject")] [InlineData(true, "reject")]
    public async Task Tenant_model_Automation_exact_consent_resumes_once_or_truthfully_refuses_without_creating_work(bool sqlite, string mode)
    {
        var model = new AutomationMutationModel { CreateOnly = true }; var verifier = new MutationVerifier(mode == "semantic" ? TriggerCommandAuthorizationDecision.ClassifierUnavailable : TriggerCommandAuthorizationDecision.Allow);
        await using var factory = new RuntimeStateFactory(sqlite, model: model, semantic: verifier); using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") });
        var (tenant, instance, _) = await MutationAgent(factory, owner, mode != "exception"); var api = $"/api/v2/tenants/{tenant}";
        if (mode is "review" or "reject") await Json(await owner.PutAsJsonAsync(api + "/operation-policies/automations.create", new { mode = "always", independentReview = false, expectedRevision = await Revision(owner, tenant), expectedPolicyRevision = 0 }));
        var submitted = await SubmitProfileRequest(owner, tenant, instance, message: "Create a disabled background task in one hour.");
        await factory.Services.GetRequiredService<ITenantRunExecution>().ExecuteRunnableAsync(8); var runs = factory.Services.GetRequiredService<ITenantRunStore>(); AgentRun? pending = null;
        for (var pass = 0; pass < 500; pass++) { pending = await runs.FindTenantRunAsync(tenant, submitted.Run); if (pending!.IsTerminal || pending.Status == AgentRunStatus.WaitingForApproval) break; await Task.Delay(25); }
        Assert.True(pending!.Status == AgentRunStatus.WaitingForApproval, $"{pending.Status}: {pending.Failure}; tools=" + string.Join(";", model.Requests.SelectMany(r => r.Messages).Where(m => m.Role == ModelRole.Tool).Select(m => m.Name + ":" + m.Text[..Math.Min(500, m.Text.Length)])));
        var approvals = factory.Services.GetRequiredService<ITenantApprovalStore>(); var request = (await approvals.FindApprovalAsync(tenant, pending.ApprovalRequestId!.Value))!;
        Assert.NotNull(request.Operation.AutomationMutation); Assert.Equal(mode == "exception" ? ApprovalKind.ExceptionalGrant : ApprovalKind.Review, request.Kind);
        Assert.Empty((await factory.Services.GetRequiredService<ITenantAutomationStore>().ListTenantAutomationsAsync(tenant, instance, null, 0, 8)).Items);
        var detail = await Json(await owner.GetAsync(api + $"/approvals/{request.RequestId}"));
        Assert.Contains(detail.GetProperty("review").GetProperty("changes").EnumerateArray(), c => c.GetProperty("field").GetString() == "Instructions" && c.GetProperty("after").GetString() == "Retain the canonical exact task.");
        Assert.Equal("mutate_automation", Assert.Single((await Json(await owner.GetAsync(api + $"/sessions/{submitted.Session}/approvals"))).GetProperty("items").EnumerateArray()).GetProperty("operation").GetString());
        using var decision = await owner.PostAsJsonAsync(api + $"/approvals/{request.RequestId}/decision", new { decision = mode == "reject" ? "reject" : "approve", actionHash = request.Operation.ActionHash, expectedRevision = await Revision(owner, tenant), expectedRequestRevision = request.Revision });
        Assert.True(decision.IsSuccessStatusCode, await decision.Content.ReadAsStringAsync());
        var completed = await ExperienceMaintenanceFinal(factory, tenant, submitted.Run); Assert.True(completed.Status == AgentRunStatus.Completed, $"{completed.Status}: {completed.Failure}");
        var retained = await factory.Services.GetRequiredService<ITenantAutomationStore>().ListTenantAutomationsAsync(tenant, instance, null, 0, 8);
        if (mode == "reject") { Assert.True(model.Blocked); Assert.Empty(retained.Items); }
        else
        {
            Assert.False(model.Blocked); var saved = Assert.Single(retained.Items); Assert.Equal(1, saved.Revision); Assert.Equal(AutomationStatus.Disabled, saved.Status);
            var receipt = await factory.Services.GetRequiredService<ITenantApprovedOperationStore>().FindOperationReceiptAsync(tenant, request.Operation.OperationId);
            Assert.Equal(TenantOperationOutcome.Completed, receipt!.Outcome); Assert.Equal(saved.AutomationId, receipt.AutomationMutation!.AutomationId);
            if (mode == "exception") Assert.Equal(OperationGrantStatus.Consumed, (await approvals.FindOperationGrantAsync(tenant, request.RequestId))!.Status);
        }
    }
    [Theory(Timeout = 120000)] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_model_Automation_own_change_receipts_do_not_waive_revoked_read_authority_before_output(bool sqlite)
    {
        var model = new AutomationMutationModel { Hold = true }; await using var factory = new RuntimeStateFactory(sqlite, model: model, semantic: new MutationVerifier());
        using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") }); var (tenant, instance, assignment) = await MutationAgent(factory, owner);
        var submitted = await SubmitProfileRequest(owner, tenant, instance, message: "Create, inspect, rename, enable and disable a background task in one hour.");
        await factory.Services.GetRequiredService<ITenantRunExecution>().ExecuteRunnableAsync(8); await model.Observed.Task.WaitAsync(TimeSpan.FromSeconds(25));
        try
        {
            var actor = (await Json(await owner.GetAsync($"/api/v2/tenants/{tenant}/access/me"))).GetProperty("principalId").GetGuid();
            await factory.Services.GetRequiredService<TenantAccessService>().RevokeAssignmentAsync(actor, tenant, assignment, await Revision(owner, tenant));
        }
        finally { model.Release.TrySetResult(); }
        var run = await ProfileTerminal(factory, tenant, submitted.Run); Assert.NotEqual(AgentRunStatus.Completed, run.Status);
        Assert.DoesNotContain("Saved and updated", run.Result?.Text ?? "");
        var saved = (await factory.Services.GetRequiredService<ITenantAutomationStore>().FindTenantAutomationAsync(tenant, model.CreatedId))!; Assert.Equal(4, saved.Revision);
    }

    [Theory(Timeout = 120000)] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_independent_Automation_authors_a_new_disabled_task_using_its_actual_admitted_intent_and_own_IAM(bool sqlite)
    {
        var model = new AutomationMutationModel { Background = true }; var verifier = new MutationVerifier(); var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var factory = new RuntimeStateFactory(sqlite, clock, model, semantic: verifier); using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") });
        var (tenant, instance, _) = await MutationAgent(factory, owner); await ExperienceAutomationRights(owner, tenant, instance);
        const string intent = "Create a disabled background task in one hour, inspect, rename, enable and disable it using this admitted task.";
        var run = await ExperienceAutomation(factory, owner, clock, tenant, instance, 1, scheduled: true, instructions: intent);
        Assert.True(run.Status == AgentRunStatus.Completed, $"{run.Status}: {run.Failure}; tools=" + string.Join(";", model.Requests.SelectMany(r => r.Messages).Where(m => m.Role == ModelRole.Tool).Select(m => m.Name + ":" + m.Text[..Math.Min(600, m.Text.Length)]))); Assert.Null(run.Admission.ExecutionContext!.OriginalInitiatorPrincipalId);
        Assert.All(verifier.Requests, r => { Assert.True(r.AdmittedTask); Assert.Equal(intent, r.CurrentUserText); }); Assert.Equal(4, verifier.Requests.Count);
        Assert.All(model.Requests, request => Assert.DoesNotContain(request.Tools ?? [], tool => tool.Name == ToolCatalog.AutomationRun));
        var saved = (await factory.Services.GetRequiredService<ITenantAutomationStore>().FindTenantAutomationAsync(tenant, model.CreatedId))!;
        Assert.Equal(4, saved.Revision); Assert.Equal(AutomationStatus.Disabled, saved.Status); Assert.Null(saved.Owner.ProfileIdOrNull);
    }
    [Theory(Timeout = 120000)] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_Automation_direct_replay_is_factual_and_same_arguments_cannot_substitute_a_different_mutation_tool(bool sqlite)
    {
        var model = new AutomationMutationModel { Hold = true }; await using var factory = new RuntimeStateFactory(sqlite, model: model, semantic: new MutationVerifier());
        using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") }); var (tenant, instance, _) = await MutationAgent(factory, owner);
        var submitted = await SubmitProfileRequest(owner, tenant, instance, message: "Create, inspect, rename, enable and disable a background task in one hour.");
        await factory.Services.GetRequiredService<ITenantRunExecution>().ExecuteRunnableAsync(8); await model.Observed.Task.WaitAsync(TimeSpan.FromSeconds(25));
        try
        {
            var run = (await factory.Services.GetRequiredService<ITenantRunStore>().FindTenantRunAsync(tenant, submitted.Run))!;
            var frame = new TenantRuntimeFrame(run.Owner, run.SessionId, run.AgentRunId, run.Claim!.Generation); var service = factory.Services.GetRequiredService<TenantAutomationMutationService>();
            var args = JsonSerializer.Serialize(new { automationId = model.CreatedId, expectedRevision = 3 }); var call = new ModelToolCall("tenant-" + ToolCatalog.AutomationDisable, ToolCatalog.AutomationDisable, args);
            using var replay = JsonDocument.Parse(await service.ExecuteAsync(frame, call, CancellationToken.None)); Assert.True(replay.RootElement.GetProperty("alreadyCompleted").GetBoolean()); Assert.False(replay.RootElement.GetProperty("applied").GetBoolean());
            var error = await Assert.ThrowsAsync<SecurityConflictException>(() => service.ExecuteAsync(frame, call with { Name = ToolCatalog.AutomationEnable }, CancellationToken.None).AsTask()); Assert.Equal("operation_mismatch", error.ReasonCode);
            Assert.Equal(4, (await factory.Services.GetRequiredService<ITenantAutomationStore>().FindTenantAutomationAsync(tenant, model.CreatedId))!.Revision);
        }
        finally { model.Release.TrySetResult(); }
        Assert.Equal(AgentRunStatus.Completed, (await ProfileTerminal(factory, tenant, submitted.Run)).Status);
    }
    [Theory(Timeout = 120000)]
    [InlineData(false, "approve")] [InlineData(true, "approve")] [InlineData(false, "reject")] [InlineData(true, "reject")]
    [InlineData(false, "changed")] [InlineData(true, "changed")] [InlineData(false, "revoked")] [InlineData(true, "revoked")]
    [InlineData(false, "source-review")] [InlineData(true, "source-review")]
    public async Task Tenant_model_existing_Automation_deletion_requires_exact_current_consent_and_preserves_history(bool sqlite, string mode)
    {
        var verifier = new MutationVerifier(); await using var factory = new RuntimeStateFactory(sqlite, model: new AgentCore.Infrastructure.Providers.Synthetic.ScriptedLanguageModel(), semantic: verifier);
        using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") }); var (tenant, instance, assignment) = await MutationAgent(factory, owner);
        var api = $"/api/v2/tenants/{tenant}"; var route = api + $"/agents/{instance}/automations";
        var body = new { enabled = false, name = "Retained deletion target", instructions = "Keep <script>literal</script> instructions and history.", requiresTools = false,
            schedule = new { kind = "oneShot", timeZone = "UTC", atUtc = DateTimeOffset.UtcNow.AddHours(2).ToString("O") }, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1 };
        var target = await Json(await owner.PostAsJsonAsync(route, body)); var id = target.GetProperty("automationId").GetGuid();
        if (mode == "source-review") await Json(await owner.PutAsJsonAsync(api + "/operation-policies/automations.read", new { mode = "always", independentReview = false, expectedRevision = await Revision(owner, tenant), expectedPolicyRevision = 0 }));
        var prompt = "synthetic-tenant-automation-mutation: " + JsonSerializer.Serialize(new { tool = ToolCatalog.AutomationDelete, arguments = new { automationId = id, expectedRevision = 1 } });
        var submitted = await SubmitProfileRequest(owner, tenant, instance, message: prompt); await factory.Services.GetRequiredService<ITenantRunExecution>().ExecuteRunnableAsync(8);
        var runs = factory.Services.GetRequiredService<ITenantRunStore>(); AgentRun? pending = null;
        for (var pass = 0; pass < 500; pass++) { pending = await runs.FindTenantRunAsync(tenant, submitted.Run); if (pending!.IsTerminal || pending.Status == AgentRunStatus.WaitingForApproval) break; await Task.Delay(25); }
        Assert.True(pending!.Status == AgentRunStatus.WaitingForApproval, $"{pending.Status}: {pending.Failure}; {pending.Result?.Text}");
        var approvals = factory.Services.GetRequiredService<ITenantApprovalStore>(); var request = (await approvals.FindApprovalAsync(tenant, pending.ApprovalRequestId!.Value))!;
        var detail = await Json(await owner.GetAsync(api + $"/approvals/{request.RequestId}")); Assert.Contains("literal", detail.GetRawText());
        var instructionChange = Assert.Single(detail.GetProperty("review").GetProperty("changes").EnumerateArray(), c => c.GetProperty("field").GetString() == "Instructions");
        Assert.True(instructionChange.GetProperty("beforeAvailable").GetBoolean()); Assert.Equal(body.instructions, instructionChange.GetProperty("before").GetString());
        if (mode == "source-review") { Assert.Empty(verifier.Requests); Assert.True(request.Operation.AutomationMutation!.SemanticReviewRequired); }
        if (mode == "changed") await Json(await owner.PutAsJsonAsync(route + $"/{id}", new { body.enabled, name = "Changed while pending", body.instructions, body.requiresTools, body.schedule, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1, expectedAutomationRevision = 1 }));
        if (mode == "revoked")
        {
            var actor = (await Json(await owner.GetAsync(api + "/access/me"))).GetProperty("principalId").GetGuid(); await factory.Services.GetRequiredService<TenantAccessService>().RevokeAssignmentAsync(actor, tenant, assignment, await Revision(owner, tenant));
        }
        using var response = await owner.PostAsJsonAsync(api + $"/approvals/{request.RequestId}/decision", new { decision = mode == "reject" ? "reject" : "approve", actionHash = request.Operation.ActionHash, expectedRevision = await Revision(owner, tenant), expectedRequestRevision = request.Revision });
        if (mode is "changed" or "revoked") Assert.False(response.IsSuccessStatusCode);
        else
        {
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync()); var final = await ExperienceMaintenanceFinal(factory, tenant, submitted.Run); Assert.Equal(AgentRunStatus.Completed, final.Status);
        }
        var saved = (await factory.Services.GetRequiredService<ITenantAutomationStore>().FindTenantAutomationAsync(tenant, id))!;
        Assert.Equal(mode is "approve" or "source-review" ? AutomationStatus.Cancelled : AutomationStatus.Disabled, saved.Status); Assert.Equal(body.instructions, saved.Instructions);
        Assert.Single(saved.Triggers); Assert.Equal(mode is "approve" or "source-review" or "changed" ? 2 : 1, saved.Revision);
    }

}
