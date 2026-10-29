using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Application.Triggers;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Domain.Triggers;
using AgentCore.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;
public sealed partial class TenantAgentAccessTests
{
    private sealed class AutomationInvocationModel : ILanguageModel
    {
        public ModelCapabilities Capabilities => new(true, true, true);
        public ConcurrentQueue<ModelRequest> Requests { get; } = new();
        public Guid Target; public bool Hold; public bool Admitted; public Guid? Child;
        public TaskCompletionSource Observed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Arguments => JsonSerializer.Serialize(new { automationId = Target, expectedRevision = 1 });
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask; ct.ThrowIfCancellationRequested(); Requests.Enqueue(request);
            if (request.Tools?.Any(t => t.Name == ToolCatalog.WorkComplete) == true)
            {
                yield return new ModelToolCallEvent(new("manual-task-completion", ToolCatalog.WorkComplete,
                    JsonSerializer.Serialize(new { outcome = "Response", summary = "The accepted manual task completed.", attentionRequired = false })));
                yield return new ModelCompleted(ModelStopReason.ToolCalls); yield break;
            }
            if (request.Context?.Sections.Any(s => s.Id == AgentCore.Application.Context.ContextSectionId.RunTask) == true)
            {
                yield return new ModelSemanticResponseReady(new("The accepted manual conversation task completed.", new(ModelSpeechMode.None, null), []));
                yield return new ModelCompleted(ModelStopReason.Completed); yield break;
            }
            Assert.Contains(request.Tools!, t => t.Name == ToolCatalog.AutomationRun && t.Description.Contains("actual current human request", StringComparison.Ordinal));
            var inspection = request.Messages.LastOrDefault(m => m.Role == ModelRole.Tool && m.Name == ToolCatalog.AutomationInspect);
            var invoked = request.Messages.LastOrDefault(m => m.Role == ModelRole.Tool && m.Name == ToolCatalog.AutomationRun);
            if (inspection is null)
            {
                yield return new ModelToolCallEvent(new("inspect-before-manual", ToolCatalog.AutomationInspect, JsonSerializer.Serialize(new { automationId = Target })));
                yield return new ModelCompleted(ModelStopReason.ToolCalls); yield break;
            }
            if (invoked is null)
            {
                yield return new ModelToolCallEvent(new("manual-invoke", ToolCatalog.AutomationRun, Arguments));
                yield return new ModelCompleted(ModelStopReason.ToolCalls); yield break;
            }
            using var first = JsonDocument.Parse(invoked.Text); var result = first.RootElement;
            if (result.TryGetProperty("decision", out var gate) && gate.GetString() == "requiresApproval")
            {
                var consent = request.Messages.LastOrDefault(m => m.Role == ModelRole.Tool && m.Name == ToolCatalog.RequestAuthorization);
                if (consent is null)
                {
                    using var arguments = JsonDocument.Parse(Arguments);
                    yield return new ModelToolCallEvent(new("manual-invoke-consent", ToolCatalog.RequestAuthorization,
                        JsonSerializer.Serialize(new { operation = "mutate_automation", automation = new { tool = ToolCatalog.AutomationRun, arguments = arguments.RootElement } })));
                    yield return new ModelCompleted(ModelStopReason.ToolCalls); yield break;
                }
                using var applied = JsonDocument.Parse(consent.Text); result = applied.RootElement.Clone();
            }
            Admitted = result.TryGetProperty("applied", out var saved) && saved.ValueKind == JsonValueKind.True;
            if (Admitted) { Assert.True(result.GetProperty("occurrenceAdmitted").GetBoolean()); Assert.False(result.GetProperty("executionCompleted").GetBoolean()); Child = result.GetProperty("admittedRunId").GetGuid(); }
            Observed.TrySetResult(); if (Hold) await Release.Task.WaitAsync(ct);
            yield return new ModelSemanticResponseReady(new(Admitted ? "One manual occurrence was admitted; its execution result is separate." : "The manual occurrence was withheld.", new(ModelSpeechMode.None, null), []));
            yield return new ModelCompleted(ModelStopReason.Completed);
        }
    }
    private static async Task<(Guid Tenant, Guid Instance, Guid Assignment, Automation Original)> InvocationAgent(RuntimeStateFactory factory, HttpClient owner, AutomationInvocationModel model, bool run = true, bool creation = true, bool existingConversation = false)
    {
        var (tenant, instance, _) = await MutationAgent(factory, owner); var api = $"/api/v2/tenants/{tenant}";
        var principal = (await Json(await owner.GetAsync(api + $"/agents/{instance}"))).GetProperty("principalId").GetGuid();
        var permissions = (run ? new[] { "automations.run" } : Array.Empty<string>()).Concat(creation ? existingConversation ? new[] { "agent-runs.create" } : new[] { "sessions.create", "agent-runs.create" } : []).ToArray();
        var role = await Json(await owner.PostAsJsonAsync(api + "/roles", new { name = "Manual occurrence admission", permissions, expectedRevision = await Revision(owner, tenant) }));
        var assignment = await Json(await owner.PostAsJsonAsync(api + "/role-assignments", new { principalId = principal, roleId = role.GetProperty("roleId").GetGuid(), expectedRevision = await Revision(owner, tenant) }));
        Guid? destination = null;
        if (existingConversation)
        {
            using var created = await owner.PostAsJsonAsync(api + "/sessions", new { retentionKind = "saved", agentInstanceId = instance, mode = "text", expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1 });
            Assert.Equal(System.Net.HttpStatusCode.Created, created.StatusCode); destination = (await Json(created)).GetProperty("sessionId").GetGuid();
        }
        var saved = await Json(await owner.PostAsJsonAsync(api + $"/agents/{instance}/automations", new { enabled = true, name = "Exact manual target", instructions = "Execute only the accepted manual task.", requiresTools = false, targetSessionId = destination,
            schedule = new { kind = "oneShot", timeZone = "UTC", atUtc = DateTimeOffset.UtcNow.AddHours(2).ToString("O") }, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1 }));
        model.Target = saved.GetProperty("automationId").GetGuid();
        return (tenant, instance, assignment.GetProperty("assignmentId").GetGuid(), (await factory.Services.GetRequiredService<ITenantAutomationStore>().FindTenantAutomationAsync(tenant, model.Target))!);
    }

    [Theory(Timeout = 120000)] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_model_manual_Automation_invocation_commits_one_occurrence_and_factual_receipt_then_executes_with_its_actual_human(bool sqlite)
    {
        var model = new AutomationInvocationModel { Hold = true }; var verifier = new MutationVerifier();
        await using var factory = new RuntimeStateFactory(sqlite, model: model, semantic: verifier, manualDispatch: true); using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") });
        var (tenant, _, _, original) = await InvocationAgent(factory, owner, model); var submitted = await SubmitProfileRequest(owner, tenant, original.Owner.AgentInstanceId, message: "Inspect this Automation and run it exactly once now.");
        await factory.Services.GetRequiredService<ITenantRunExecution>().ExecuteRunnableAsync(8); await model.Observed.Task.WaitAsync(TimeSpan.FromSeconds(25));
        try
        {
            Assert.True(model.Admitted, string.Join(";", model.Requests.SelectMany(r => r.Messages).Where(m => m.Role == ModelRole.Tool).Select(m => m.Name + ":" + m.Text)));
            var run = (await factory.Services.GetRequiredService<ITenantRunStore>().FindTenantRunAsync(tenant, submitted.Run))!;
            var service = factory.Services.GetRequiredService<TenantAutomationMutationService>(); var frame = new TenantRuntimeFrame(run.Owner, run.SessionId, run.AgentRunId, run.Claim!.Generation);
            using var replay = JsonDocument.Parse(await service.ExecuteAsync(frame, new("manual-invoke", ToolCatalog.AutomationRun, model.Arguments), CancellationToken.None));
            Assert.True(replay.RootElement.GetProperty("alreadyCompleted").GetBoolean()); Assert.False(replay.RootElement.GetProperty("applied").GetBoolean()); Assert.Equal(model.Child, replay.RootElement.GetProperty("admittedRunId").GetGuid());
            var receipt = await factory.Services.GetRequiredService<ITenantDirectOperationStore>().FindDirectOperationReceiptAsync(tenant, run.AgentRunId, "manual-invoke");
            Assert.True(TenantAutomationMutationEvidence.Valid(receipt!.AutomationMutation!)); Assert.Equal(model.Child, receipt.AutomationMutation!.InvocationRunId);
            var parent = (await factory.Services.GetRequiredService<ITenantAutomationStore>().FindTenantAutomationAsync(tenant, model.Target))!;
            Assert.Equal(2, parent.Revision); Assert.Equal(original.NextOccurrenceAtUtc, parent.NextOccurrenceAtUtc); Assert.Equal(original.OccurrenceCount, parent.OccurrenceCount); Assert.Equal(original.Instructions, parent.Instructions);
            var occurrence = (await factory.Services.GetRequiredService<ITenantAutomationStore>().FindTenantOccurrenceAsync(tenant, receipt.AutomationMutation.InvocationOccurrenceId!.Value))!;
            Assert.Equal(TriggerSourceKind.ManualInvocation, occurrence.SourceKind); Assert.Equal(model.Child, occurrence.AcceptedAgentRunId);
            if (sqlite)
            {
                await using var db = await factory.Services.GetRequiredService<IDbContextFactory<AgentCoreDbContext>>().CreateDbContextAsync();
                await Assert.ThrowsAsync<SqliteException>(() => db.GetService<IMigrator>().MigrateAsync("20261022041619_TenantAutomationMutations"));
                Assert.Contains("20261022060000_TenantAutomationInvocations", await db.Database.GetAppliedMigrationsAsync());
                await db.GetService<IMigrator>().MigrateAsync();
                Assert.False(db.Database.HasPendingModelChanges());
            }
        }
        finally { model.Release.TrySetResult(); }
        var completed = await ProfileTerminal(factory, tenant, submitted.Run); Assert.Equal(AgentRunStatus.Completed, completed.Status);
        await factory.Services.GetRequiredService<ITenantRunExecution>().ExecuteRunnableAsync(8); var child = await ProfileTerminal(factory, tenant, model.Child!.Value);
        Assert.Equal(AgentRunStatus.Completed, child.Status); Assert.Equal(ExecutionOrigin.ManualInvocation, child.Admission.ExecutionContext!.Origin);
        Assert.Equal(completed.Admission.ExecutionContext!.OriginalInitiatorPrincipalId, child.Admission.ExecutionContext.OriginalInitiatorPrincipalId);
        Assert.Null(child.Admission.ExecutionContext.ParentRunId); Assert.Equal(child.ActivationId, child.Admission.ExecutionContext.RootActivationId);
        Assert.Equal(AgentRunTaskSource.Automation, child.Admission.TaskIntent!.Source); Assert.Single(verifier.Requests); Assert.False(verifier.Requests.Single().AdmittedTask);
    }

    [Theory(Timeout = 120000)]
    [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task Tenant_model_manual_Automation_invocation_uses_the_saved_conversation_without_Session_creation_authority(bool sqlite, bool review)
    {
        var model = new AutomationInvocationModel(); await using var factory = new RuntimeStateFactory(sqlite, model: model, semantic: new MutationVerifier(), manualDispatch: true);
        using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") });
        var (tenant, instance, _, original) = await InvocationAgent(factory, owner, model, existingConversation: true); var api = $"/api/v2/tenants/{tenant}";
        if (review) await Json(await owner.PutAsJsonAsync(api + "/operation-policies/automations.run", new { mode = "always", independentReview = false, expectedRevision = await Revision(owner, tenant), expectedPolicyRevision = 0 }));
        var submitted = await SubmitProfileRequest(owner, tenant, instance, message: "Inspect the Automation and run its exact task once in its saved conversation.");
        await factory.Services.GetRequiredService<ITenantRunExecution>().ExecuteRunnableAsync(8);
        if (review)
        {
            var runs = factory.Services.GetRequiredService<ITenantRunStore>(); AgentRun? pending = null;
            for (var pass = 0; pass < 500; pass++) { pending = await runs.FindTenantRunAsync(tenant, submitted.Run); if (pending!.IsTerminal || pending.Status == AgentRunStatus.WaitingForApproval) break; await Task.Delay(25); }
            Assert.Equal(AgentRunStatus.WaitingForApproval, pending!.Status);
            var request = (await factory.Services.GetRequiredService<ITenantApprovalStore>().FindApprovalAsync(tenant, pending.ApprovalRequestId!.Value))!;
            var detail = await Json(await owner.GetAsync(api + $"/approvals/{request.RequestId}")); Assert.Equal("invoke_automation", detail.GetProperty("preparedAction").GetProperty("operation").GetString());
            await Json(await owner.PostAsJsonAsync(api + $"/approvals/{request.RequestId}/decision", new { decision = "approve", actionHash = request.Operation.ActionHash, expectedRevision = await Revision(owner, tenant), expectedRequestRevision = request.Revision }));
        }
        var parent = review ? await ExperienceMaintenanceFinal(factory, tenant, submitted.Run) : await ProfileTerminal(factory, tenant, submitted.Run);
        Assert.True(parent.Status == AgentRunStatus.Completed && model.Admitted, $"{parent.Status}: {parent.Failure}; " + string.Join(";", model.Requests.SelectMany(r => r.Messages).Where(m => m.Role == ModelRole.Tool).Select(m => m.Name + ":" + m.Text)));
        await factory.Services.GetRequiredService<ITenantRunExecution>().ExecuteRunnableAsync(8); var child = await ProfileTerminal(factory, tenant, model.Child!.Value);
        Assert.Equal(AgentRunStatus.Completed, child.Status); Assert.Equal(original.ExecutionTarget.SessionId, child.SessionId); Assert.NotEqual(submitted.Session, child.SessionId);
        Assert.Equal(AgentRunOutputContract.ConversationResponse, child.Admission.OutputContract); Assert.Equal(ExecutionOrigin.ManualInvocation, child.Admission.ExecutionContext!.Origin);
        var target = (await factory.Services.GetRequiredService<ITenantSessionStore>().FindTenantSessionAsync(tenant, child.SessionId))!;
        Assert.Equal(SessionOriginKind.UserChat, target.Snapshot.Origin.Kind); Assert.Equal(parent.Admission.ExecutionContext!.OriginalInitiatorPrincipalId, target.Owner.CreatedByPrincipalId);
        var history = await Json(await owner.GetAsync(api + $"/sessions/{child.SessionId}/messages"));
        Assert.Contains(history.GetProperty("items").EnumerateArray(), e => e.GetProperty("role").GetString() == "assistant" && e.GetProperty("text").GetString()!.Contains("manual conversation task completed", StringComparison.Ordinal));
    }

    [Fact(Timeout = 120000)]
    public async Task Tenant_model_manual_Automation_pending_consent_survives_actual_SQLite_host_recreation_without_early_or_duplicate_admission()
    {
        var model = new AutomationInvocationModel(); await using var first = new RuntimeStateFactory(true, model: model, semantic: new MutationVerifier(), manualDispatch: true);
        using var owner = first.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") });
        var (tenant, instance, _, _) = await InvocationAgent(first, owner, model); var api = $"/api/v2/tenants/{tenant}";
        await Json(await owner.PutAsJsonAsync(api + "/operation-policies/automations.run", new { mode = "always", independentReview = false, expectedRevision = await Revision(owner, tenant), expectedPolicyRevision = 0 }));
        var submitted = await SubmitProfileRequest(owner, tenant, instance, message: "Inspect the Automation and admit its task exactly once now."); await first.Services.GetRequiredService<ITenantRunExecution>().ExecuteRunnableAsync(8);
        AgentRun? pending = null;
        for (var pass = 0; pass < 500; pass++) { pending = await first.Services.GetRequiredService<ITenantRunStore>().FindTenantRunAsync(tenant, submitted.Run); if (pending!.IsTerminal || pending.Status == AgentRunStatus.WaitingForApproval) break; await Task.Delay(25); }
        Assert.Equal(AgentRunStatus.WaitingForApproval, pending!.Status);
        var original = (await first.Services.GetRequiredService<ITenantApprovalStore>().FindApprovalAsync(tenant, pending.ApprovalRequestId!.Value))!;
        await first.DisposeAsync(); await using var reopened = new RuntimeStateFactory(true, model: model, root: first.Root, semantic: new MutationVerifier(), manualDispatch: true);
        using var again = reopened.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") }); await Login(again, "owner@example.test", "explicit-synthetic-test-password");
        Assert.DoesNotContain(reopened.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>(), service => service is AgentCore.Api.AgentRunHostedService or AgentCore.Api.TriggerSchedulerHostedService);
        var saved = (await reopened.Services.GetRequiredService<ITenantApprovalStore>().FindApprovalAsync(tenant, original.RequestId))!;
        Assert.Equal(original.Operation, saved.Operation); Assert.Null(await reopened.Services.GetRequiredService<ITenantRunStore>().FindTenantRunAsync(tenant, saved.Operation.AutomationMutation!.Invocation!.RunId));
        await using (var db = await reopened.Services.GetRequiredService<IDbContextFactory<AgentCoreDbContext>>().CreateDbContextAsync())
        {
            await Assert.ThrowsAsync<SqliteException>(() => db.GetService<IMigrator>().MigrateAsync("20261022041619_TenantAutomationMutations"));
            await db.GetService<IMigrator>().MigrateAsync();
            Assert.False(db.Database.HasPendingModelChanges());
        }
        await Json(await again.PostAsJsonAsync(api + $"/approvals/{saved.RequestId}/decision", new { decision = "approve", actionHash = saved.Operation.ActionHash, expectedRevision = await Revision(again, tenant), expectedRequestRevision = saved.Revision }));
        Assert.Equal(AgentRunStatus.Completed, (await ExperienceMaintenanceFinal(reopened, tenant, submitted.Run)).Status);
        Assert.True(model.Admitted); Assert.Equal(saved.Operation.AutomationMutation.Invocation.RunId, model.Child);
        await reopened.Services.GetRequiredService<ITenantRunExecution>().ExecuteRunnableAsync(8); Assert.Equal(AgentRunStatus.Completed, (await ProfileTerminal(reopened, tenant, model.Child!.Value)).Status);
        var receipt = await reopened.Services.GetRequiredService<ITenantApprovedOperationStore>().FindOperationReceiptAsync(tenant, saved.Operation.OperationId);
        Assert.Equal(TenantOperationOutcome.Completed, receipt!.Outcome); Assert.Equal(model.Child, receipt.AutomationMutation!.InvocationRunId);
        Assert.Equal(2, (await reopened.Services.GetRequiredService<ITenantAutomationStore>().FindTenantAutomationAsync(tenant, model.Target))!.Revision);
    }

    [Theory(Timeout = 120000)]
    [InlineData(false, "creation")] [InlineData(true, "creation")] [InlineData(false, "intent")] [InlineData(true, "intent")]
    public async Task Tenant_model_manual_Automation_invocation_does_not_admit_work_without_secondary_creation_authority_or_actual_intent(bool sqlite, string mode)
    {
        var model = new AutomationInvocationModel(); var verifier = new MutationVerifier(mode == "intent" ? TriggerCommandAuthorizationDecision.Deny : TriggerCommandAuthorizationDecision.Allow);
        await using var factory = new RuntimeStateFactory(sqlite, model: model, semantic: verifier, manualDispatch: true); using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") });
        var (tenant, instance, _, original) = await InvocationAgent(factory, owner, model, creation: mode != "creation");
        var submitted = await SubmitProfileRequest(owner, tenant, instance, message: mode == "intent" ? "Can this Agent run Automations?" : "Inspect the saved Automation and run it once now.");
        await factory.Services.GetRequiredService<ITenantRunExecution>().ExecuteRunnableAsync(8); var parent = await ProfileTerminal(factory, tenant, submitted.Run);
        Assert.Equal(AgentRunStatus.Completed, parent.Status); Assert.False(model.Admitted); Assert.Null(model.Child);
        Assert.True(TenantAutomationRules.Same(original, (await factory.Services.GetRequiredService<ITenantAutomationStore>().FindTenantAutomationAsync(tenant, original.AutomationId))!));
        Assert.Null(await factory.Services.GetRequiredService<ITenantDirectOperationStore>().FindDirectOperationReceiptAsync(tenant, parent.AgentRunId, "manual-invoke"));
        Assert.Empty((await factory.Services.GetRequiredService<ITenantApprovalStore>().ListApprovalsAsync(tenant, null, null, new())).Items);
        Assert.Equal(mode == "intent" ? 1 : 0, verifier.Requests.Count);
    }

    [Theory(Timeout = 120000)]
    [InlineData(false, "review")] [InlineData(true, "review")] [InlineData(false, "exception")] [InlineData(true, "exception")]
    [InlineData(false, "semantic")] [InlineData(true, "semantic")] [InlineData(false, "reject")] [InlineData(true, "reject")]
    [InlineData(false, "changed")] [InlineData(true, "changed")] [InlineData(false, "revoked")] [InlineData(true, "revoked")]
    public async Task Tenant_model_manual_Automation_consent_admits_only_the_exact_current_occurrence(bool sqlite, string mode)
    {
        var model = new AutomationInvocationModel(); var verifier = new MutationVerifier(mode == "semantic" ? TriggerCommandAuthorizationDecision.ClassifierUnavailable : TriggerCommandAuthorizationDecision.Allow);
        await using var factory = new RuntimeStateFactory(sqlite, model: model, semantic: verifier, manualDispatch: true); using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") });
        var (tenant, instance, assignment, original) = await InvocationAgent(factory, owner, model, run: mode != "exception"); var api = $"/api/v2/tenants/{tenant}";
        if (mode is "review" or "reject" or "changed" or "revoked") await Json(await owner.PutAsJsonAsync(api + "/operation-policies/automations.run", new { mode = "always", independentReview = false, expectedRevision = await Revision(owner, tenant), expectedPolicyRevision = 0 }));
        var submitted = await SubmitProfileRequest(owner, tenant, instance, message: "Inspect the saved Automation and invoke its task exactly once now.");
        await factory.Services.GetRequiredService<ITenantRunExecution>().ExecuteRunnableAsync(8); var runs = factory.Services.GetRequiredService<ITenantRunStore>(); AgentRun? pending = null;
        for (var pass = 0; pass < 500; pass++) { pending = await runs.FindTenantRunAsync(tenant, submitted.Run); if (pending!.IsTerminal || pending.Status == AgentRunStatus.WaitingForApproval) break; await Task.Delay(25); }
        Assert.True(pending!.Status == AgentRunStatus.WaitingForApproval, $"{pending.Status}: {pending.Failure}; " + string.Join(";", model.Requests.SelectMany(r => r.Messages).Where(m => m.Role == ModelRole.Tool).Select(m => m.Name + ":" + m.Text)));
        var approvals = factory.Services.GetRequiredService<ITenantApprovalStore>(); var request = (await approvals.FindApprovalAsync(tenant, pending.ApprovalRequestId!.Value))!;
        Assert.NotNull(request.Operation.AutomationMutation!.Invocation); Assert.Equal("automations.run", request.Permission);
        var chat = await Json(await owner.GetAsync(api + $"/sessions/{submitted.Session}/approvals"));
        Assert.Equal(1, chat.GetProperty("total").GetInt32()); Assert.Equal("invoke_automation", Assert.Single(chat.GetProperty("items").EnumerateArray()).GetProperty("operation").GetString());
        Assert.Equal(mode == "exception" ? ApprovalKind.ExceptionalGrant : ApprovalKind.Review, request.Kind);
        var detail = await Json(await owner.GetAsync(api + $"/approvals/{request.RequestId}")); Assert.Equal("invoke_automation", detail.GetProperty("preparedAction").GetProperty("operation").GetString());
        Assert.Contains(detail.GetProperty("review").GetProperty("changes").EnumerateArray(), c => c.GetProperty("field").GetString() == "Manual occurrence");
        Assert.Null(await runs.FindTenantRunAsync(tenant, request.Operation.AutomationMutation.Invocation!.RunId));
        Assert.Equal(1, (await factory.Services.GetRequiredService<ITenantAutomationStore>().FindTenantAutomationAsync(tenant, model.Target))!.Revision);
        if (mode == "changed") await Json(await owner.PutAsJsonAsync(api + $"/agents/{instance}/automations/{model.Target}", new { enabled = true, name = "Changed pending target", original.Instructions, requiresTools = false,
            schedule = new { kind = "oneShot", timeZone = "UTC", atUtc = DateTimeOffset.UtcNow.AddHours(3).ToString("O") }, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1, expectedAutomationRevision = 1 }));
        if (mode == "revoked")
        {
            var actor = (await Json(await owner.GetAsync(api + "/access/me"))).GetProperty("principalId").GetGuid(); await factory.Services.GetRequiredService<TenantAccessService>().RevokeAssignmentAsync(actor, tenant, assignment, await Revision(owner, tenant));
        }
        using var decision = await owner.PostAsJsonAsync(api + $"/approvals/{request.RequestId}/decision", new { decision = mode == "reject" ? "reject" : "approve", actionHash = request.Operation.ActionHash, expectedRevision = await Revision(owner, tenant), expectedRequestRevision = request.Revision });
        if (mode is "changed" or "revoked")
        { Assert.False(decision.IsSuccessStatusCode); Assert.Null(await runs.FindTenantRunAsync(tenant, request.Operation.AutomationMutation.Invocation.RunId)); return; }
        Assert.True(decision.IsSuccessStatusCode, await decision.Content.ReadAsStringAsync()); var parent = await ExperienceMaintenanceFinal(factory, tenant, submitted.Run);
        Assert.Equal(AgentRunStatus.Completed, parent.Status); Assert.Equal(mode != "reject", model.Admitted);
        var after = await Json(await owner.GetAsync(api + $"/sessions/{submitted.Session}/approvals"));
        Assert.Equal(1, after.GetProperty("total").GetInt32()); var recorded = Assert.Single(after.GetProperty("items").EnumerateArray());
        if (mode == "reject") { Assert.Equal("rejected", recorded.GetProperty("status").GetString()); Assert.Equal(JsonValueKind.Null, recorded.GetProperty("outcome").ValueKind); }
        else Assert.Equal("completed", recorded.GetProperty("outcome").GetString());
        if (mode == "reject") Assert.Null(await runs.FindTenantRunAsync(tenant, request.Operation.AutomationMutation.Invocation.RunId));
        else
        {
            await factory.Services.GetRequiredService<ITenantRunExecution>().ExecuteRunnableAsync(8); var child = await ProfileTerminal(factory, tenant, model.Child!.Value); Assert.Equal(AgentRunStatus.Completed, child.Status);
            var receipt = await factory.Services.GetRequiredService<ITenantApprovedOperationStore>().FindOperationReceiptAsync(tenant, request.Operation.OperationId);
            Assert.Equal(model.Child, receipt!.AutomationMutation!.InvocationRunId);
            if (mode == "exception") Assert.Equal(OperationGrantStatus.Consumed, (await approvals.FindOperationGrantAsync(tenant, request.RequestId))!.Status);
        }
    }
}
