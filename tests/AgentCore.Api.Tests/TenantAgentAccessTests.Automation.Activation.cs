using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Events;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Domain.Triggers;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Api.Tests;

public sealed partial class TenantAgentAccessTests
{
    public static IEnumerable<object[]> EventActivationCases => new[] { false, true }.SelectMany(sqlite => new[] {
        "human-omitted", "human-enabled", "human-disabled", "task-omitted", "task-enabled",
        "human-omitted-always", "task-omitted-always", "task-enabled-always-reject", "task-omitted-missing-source"
    }.Select(mode => new object[] { sqlite, mode }));

    [Theory(Timeout = 120000)] [MemberData(nameof(EventActivationCases))]
    public async Task Tenant_model_Event_creation_defaults_disabled_and_actual_tasks_keep_current_consent_policy(bool sqlite, string mode)
    {
        var independent = mode.StartsWith("task-", StringComparison.Ordinal); var review = mode.Contains("always", StringComparison.Ordinal);
        var reject = mode.EndsWith("reject", StringComparison.Ordinal); var missingSource = mode.EndsWith("missing-source", StringComparison.Ordinal); var enabled = mode.Contains("-enabled", StringComparison.Ordinal);
        var json = new JsonObject { ["name"] = "Activation default watch", ["instructions"] = "Keep the exact accepted event task.",
            ["executionTarget"] = "backgroundSession", ["requiresTools"] = false, ["coreEventKey"] = "run.completed" };
        if (enabled || mode.Contains("-disabled", StringComparison.Ordinal)) json["enabled"] = enabled;
        var model = new AutomationMutationModel { CreateOnly = true, Background = independent, CreateArguments = json.ToJsonString() };
        var verifier = new MutationVerifier(); var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var factory = new RuntimeStateFactory(sqlite, clock, model, semantic: verifier);
        using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") });
        var (tenant, instance, _) = await MutationAgent(factory, owner, events: true, eventSourceRead: !missingSource); var api = $"/api/v2/tenants/{tenant}";
        if (independent) await ExperienceAutomationRights(owner, tenant, instance);
        if (review) await Json(await owner.PutAsJsonAsync(api + "/operation-policies/automations.create", new { mode = "always", independentReview = false,
            expectedRevision = await Revision(owner, tenant), expectedPolicyRevision = 0 }));
        var intent = enabled ? "Create and enable a background Event watch for completed Runs." : "Create a disabled background Event watch for completed Runs.";
        AgentRun run;
        if (independent) run = await ExperienceAutomation(factory, owner, clock, tenant, instance, 1, scheduled: true, instructions: intent, waitForApproval: review);
        else
        {
            var submitted = await SubmitProfileRequest(owner, tenant, instance, message: intent);
            await factory.Services.GetRequiredService<ITenantRunExecution>().ExecuteRunnableAsync(8);
            if (!review) run = await ProfileTerminal(factory, tenant, submitted.Run);
            else
            {
                AgentRun? observed = null;
                for (var pass = 0; pass < 500; pass++)
                { observed = await factory.Services.GetRequiredService<ITenantRunStore>().FindTenantRunAsync(tenant, submitted.Run); if (observed!.IsTerminal || observed.Status == AgentRunStatus.WaitingForApproval) break; await Task.Delay(25); }
                run = observed!;
            }
        }
        Assert.Equal(independent, run.Admission.ExecutionContext!.OriginalInitiatorPrincipalId is null);
        string Trace() => string.Join(";", model.Requests.SelectMany(request => request.Messages).Where(message => message.Role == ModelRole.Tool).TakeLast(8).Select(message => message.Name + ":" + message.Text[..Math.Min(600, message.Text.Length)]));
        var store = factory.Services.GetRequiredService<ITenantAutomationStore>(); var approvals = factory.Services.GetRequiredService<ITenantApprovalStore>();
        if (review)
        {
            Assert.True(run.Status == AgentRunStatus.WaitingForApproval, $"{run.Status}: {run.Failure?.Code} {run.Failure?.Summary}; {Trace()}");
            var request = (await approvals.FindApprovalAsync(tenant, run.ApprovalRequestId!.Value))!; Assert.Equal(ApprovalKind.Review, request.Kind);
            Assert.Equal(run.Owner.ExecutorPrincipalId, request.ExecutorPrincipalId); Assert.NotNull(request.Operation.AutomationMutation);
            Assert.DoesNotContain((await store.ListTenantAutomationsAsync(tenant, instance, null, 0, 100)).Items, item => item.Name == "Activation default watch");
            var detail = await Json(await owner.GetAsync(api + $"/approvals/{request.RequestId}"));
            Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync(api + $"/approvals/{request.RequestId}/decision", new { decision = reject ? "reject" : "approve",
                actionHash = request.Operation.ActionHash, expectedRevision = detail.GetProperty("securityRevision").GetInt64(), expectedRequestRevision = request.Revision })).StatusCode);
            run = await ExperienceMaintenanceFinal(factory, tenant, run.AgentRunId);
        }
        else Assert.Empty((await approvals.ListApprovalsAsync(tenant, null, null, new())).Items);
        Assert.True(run.Status == AgentRunStatus.Completed, $"{run.Status}: {run.Failure?.Code} {run.Failure?.Summary}; {Trace()}");
        if (missingSource)
        {
            Assert.True(model.Blocked); Assert.Empty(verifier.Requests); Assert.Equal(Guid.Empty, model.CreatedId);
            Assert.DoesNotContain((await store.ListTenantAutomationsAsync(tenant, instance, null, 0, 100)).Items, item => item.Name == "Activation default watch");
            Assert.Equal(AgentRunOutcomeKind.NoAction, run.Result!.OutcomeKind); return;
        }
        if (!reject) Assert.False(model.Blocked, Trace());
        Assert.NotEmpty(verifier.Requests); Assert.All(verifier.Requests, request => { Assert.Equal(independent, request.AdmittedTask); Assert.Equal(intent, request.CurrentUserText); });
        if (reject)
        { Assert.True(model.Blocked); Assert.Equal(AgentRunOutcomeKind.NoAction, run.Result!.OutcomeKind); Assert.Equal(Guid.Empty, model.CreatedId); return; }
        var saved = (await store.FindTenantAutomationAsync(tenant, model.CreatedId))!;
        Assert.Equal(enabled ? AutomationStatus.Active : AutomationStatus.Disabled, saved.Status); Assert.Null(saved.Owner.ProfileIdOrNull);
        Assert.True(Assert.Single(saved.Triggers).Enabled); Assert.IsType<CoreEventTrigger>(saved.Triggers[0].Configuration); Assert.Null(saved.NextOccurrenceAtUtc);
        if (!enabled)
        {
            Assert.True((await factory.Services.GetRequiredService<ITenantEventStore>().ListTenantCoreEventsAsync(tenant, instance, "run.completed", 0, 100)).Total > 0);
            await factory.Services.GetRequiredService<TenantEventProcessor>().RunOnceAsync();
            Assert.DoesNotContain((await factory.Services.GetRequiredService<ITenantRunStore>().ListTenantSessionRunsAsync(tenant, null, null, 0, 100, instanceId: instance)).Items,
                candidate => candidate.Admission.TaskIntent?.Source == AgentRunTaskSource.Automation &&
                    TenantAutomationRules.SourceAutomation(candidate) == saved.AutomationId);
        }
    }
}
