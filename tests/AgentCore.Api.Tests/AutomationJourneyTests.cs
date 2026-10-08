using AgentCore.Application.Execution;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentCore.Application.Admin;
using AgentCore.Application.Experience;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Application.Triggers;
using AgentCore.Application.Work;
using AgentCore.Contracts.Http;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Triggers;
using AgentCore.Domain.Conversation;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed class AutomationJourneyTests
{
    [Theory]
    [InlineData(14, false)]
    [InlineData(15, false)]
    [InlineData(60, true)]
    [InlineData(3600, true)]
    [InlineData(604800, true)]
    [InlineData(604801, false)]
    public async Task Automation_intervals_enforce_seconds_bounds(int seconds, bool accepted)
    {
        var db = Path.Combine(Path.GetTempPath(), $"automation-interval-{Guid.NewGuid():N}.db");
        await using var host = new ExperienceHost(db);
        var instance = await host.Services.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 17);
        var client = TestOwnerCapability.CreateOwnerClient(host);
        var path = $"/api/v2/admin/agent-instances/{instance.InstanceId}/automations";
        var response = await client.PostAsJsonAsync(path, Draft(0, seconds) with { Enabled = false });
        Assert.Equal(accepted ? HttpStatusCode.OK : HttpStatusCode.BadRequest, response.StatusCode);
        if (accepted)
        {
            var stored = (await response.Content.ReadFromJsonAsync<AutomationResponse>())!;
            Assert.Equal(seconds, stored.Trigger.Schedule!.Interval);
            var review = await client.GetFromJsonAsync<JsonElement>(path);
            Assert.Equal(60, review.GetProperty("policy").GetProperty("minFixedIntervalSeconds").GetInt32());
            Assert.Equal(seconds, review.GetProperty("items")[0].GetProperty("trigger").GetProperty("schedule").GetProperty("interval").GetInt32());
        }
    }

    [Fact(Timeout = 90000)]
    public async Task Experience_to_automation_to_instance_skill_survives_restart_then_finishes_quietly_without_more_work()
    {
        var db = Path.Combine(Path.GetTempPath(), $"automation-{Guid.NewGuid():N}.db");
        Guid instanceId, automationId, workId; int originalVersion;
        await using (var host = new ExperienceHost(db))
        {
            var s = host.Services;
            var instance = await s.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 17);
            instanceId = instance.InstanceId; originalVersion = instance.ActiveVersion;
            var source = await ExperienceJourneyTests.SeedAsync(s, instanceId);
            var service = s.GetRequiredService<ExperienceService>();
            await s.GetRequiredService<IExperienceStore>().ConfigureAsync(instanceId, 0, true);
            await service.RequestSessionAsync(instanceId, source.SessionId);
            var repeated = await ExperienceJourneyTests.SeedAsync(s, instanceId);
            await service.RequestSessionAsync(instanceId, repeated.SessionId);
            Assert.Equal(2, await s.ExecuteRunsAsync(100));
            instance = await s.GetRequiredService<HarnessManagementService>().ConfigureAsync(instanceId, instance.Revision,
                new(HarnessManagementMode.Assisted, [ HarnessManagementScope.ToolSelection], [], [ToolCatalog.WebFetch]));
            var client = TestOwnerCapability.CreateOwnerClient(host);
            var path = $"/api/v2/admin/agent-instances/{instanceId}/automations";
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(path, Draft(0, 10))).StatusCode);
            var created = await client.PostAsJsonAsync(path, Draft(0)); created.EnsureSuccessStatusCode();
            var r = (await created.Content.ReadFromJsonAsync<AutomationResponse>())!;
            automationId = Guid.Parse(r.AutomationId);
            Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync(path + "/" + automationId, Draft(0))).StatusCode);
            var run = await client.PostAsJsonAsync(path + "/" + automationId + "/run", new ContinuityRevisionRequest(r.Revision)); run.EnsureSuccessStatusCode();
            Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(path + "/" + automationId + "/run", new ContinuityRevisionRequest(r.Revision))).StatusCode);
            await Intake(s);
            var work = s.GetRequiredService<IAgentRunStore>();
            var owner = new AgentRunOwner(instanceId, AgentCore.Domain.Conversation.LocalUserProfile.Id);
            var item = Assert.Single(await s.AutomationRunsAsync(owner, automationId));
            workId = item.AgentRunId;
            Assert.Equal(SessionOriginKind.AutomationOccurrence, (await s.SessionAsync(item)).Origin.Kind);
            var detailPath = $"/api/v2/agent-instances/{instanceId}/agent-runs/{workId}";
            var detail = (await client.GetFromJsonAsync<AgentRunResponse>(detailPath))!;
            Assert.Equal(workId.ToString("D"), detail.AgentRunId);
            Assert.Equal(automationId.ToString("D"), detail.AutomationId);
            Assert.Equal("ManualBackground", detail.ActivationKind);
            Assert.Contains("synthetic-automation-improve", Assert.Single((await s.SessionAsync(item)).Entries, e => e.Role == ConversationRole.User).Text);
            var listPath = $"/api/v2/agent-instances/{instanceId}/agent-runs";
            var listed = (await client.GetFromJsonAsync<AgentRunPageResponse>(listPath))!;
            Assert.Equal(detail.ActivationId, Assert.Single(listed.Items, row => row.AgentRunId == detail.AgentRunId).ActivationId);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v2/agent-instances/{instanceId}/agent-runs/{Guid.NewGuid()}")).StatusCode);
            var other = await s.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 17);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v2/agent-instances/{other.InstanceId}/agent-runs/{workId}")).StatusCode);
            using var anonymous = host.CreateClient();
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(detailPath)).StatusCode);

            Assert.Contains("synthetic-automation-improve", (await s.SessionAsync(item)).Entries[0].Text);
            Assert.Equal(1, await s.ExecuteRunsAsync(100));
            item = (await work.GetAsync(owner, workId))!;
            Assert.Equal(AgentRunStatus.Completed, item.Status);
            Assert.Null(item.Approval);
            Assert.Single((await s.GetRequiredService<IAgentInstanceStore>().ReadSkillsAsync(instanceId)).InstanceSkills, skill => skill.Name == "Experience review");
            Assert.Equal(originalVersion, (await s.GetRequiredService<IAgentInstanceStore>().FindAsync(instanceId))!.ActiveVersion);
            var edited = await client.PutAsJsonAsync(path + "/" + automationId, Draft(r.Revision) with { Instructions = "synthetic-automation-attention future prompt" }); edited.EnsureSuccessStatusCode();
            var historical = (await client.GetFromJsonAsync<AgentRunResponse>(detailPath))!;
            Assert.Equal(detail.ActivationId, historical.ActivationId);
            var scheduler = await s.GetRequiredService<TriggerScheduler>().RunOnceAsync(DateTimeOffset.UtcNow);
            Assert.Equal(0, scheduler.Admitted);
            Assert.Single(await s.AutomationRunsAsync(owner, automationId));
            Assert.Contains("synthetic-automation-improve", (await s.SessionAsync((await work.GetAsync(owner, workId))!)).Entries[0].Text);
            var toolExecutor = s.GetRequiredService<SessionToolExecutor>();
            var definition = (await s.GetRequiredService<IAgentDefinitionStore>().GetAsync("general-assistant", originalVersion))!;
            var harness = await toolExecutor.HarnessContextAsync(instanceId, default);
            var automation = new ToolExecutionAdmission(true, TriggerKind.ManualInvocation, AgentInstanceId: instanceId, Harness: harness);
            var user = automation with { Detached = false, TriggerKind = TriggerKind.UserTurn };
            Assert.Equal(ToolPolicyDecision.Deny, toolExecutor.EvaluateExecutionPolicy(definition, "harness.tool.select", admission: automation));
            Assert.Equal(ToolPolicyDecision.RequireApproval, toolExecutor.EvaluateExecutionPolicy(definition, "harness.tool.select", admission: user));
            Assert.Equal(ToolPolicyDecision.Deny, toolExecutor.EvaluateExecutionPolicy(definition, ToolCatalog.AutomationDelete, admission: automation));
            Assert.Equal(ToolPolicyDecision.Deny, toolExecutor.EvaluateExecutionPolicy(definition, ToolCatalog.AppMessageSend, admission: automation));
            Assert.DoesNotContain("harness.tool.select", item.Checkpoint!.PayloadJson);
            Assert.Empty(await Alerts(work, owner));
        }
        await using (var host = new ExperienceHost(db))
        {
            var s = host.Services; var client = TestOwnerCapability.CreateOwnerClient(host);
            var work = s.GetRequiredService<IAgentRunStore>(); var owner = new AgentRunOwner(instanceId, AgentCore.Domain.Conversation.LocalUserProfile.Id);
            var item = (await work.GetAsync(owner, workId))!;
            Assert.Equal(AgentRunStatus.Completed, item.Status);
            Assert.Equal("Response", item.Result!.OutcomeKind.ToString());
            Assert.False(item.Result.AttentionRequired);
            var instance = (await s.GetRequiredService<IAgentInstanceStore>().FindAsync(instanceId))!;
            Assert.Equal(originalVersion, instance.ActiveVersion);
            var future = (await s.GetRequiredService<IAgentDefinitionStore>().GetAsync(instance.DefinitionId, instance.ActiveVersion))!;
            Assert.DoesNotContain(future.SkillList, skill => skill.Name == "Experience review");
            Assert.Single((await s.GetRequiredService<IAgentInstanceStore>().ReadSkillsAsync(instanceId)).InstanceSkills, skill => skill.Name == "Experience review" && skill.CreatedBy == SkillAuthor.Agent);
            Assert.Empty(await Alerts(work, owner));
            var r = (await s.GetRequiredService<ITriggerStore>().GetAsync(new(instanceId, owner.ProfileId), automationId))!;
            r = await s.GetRequiredService<AdminAutomationAuthoringService>().SaveAsync(instanceId, automationId, r.Revision, true, 3600, "synthetic-automation-improve", null, null);
            await s.GetRequiredService<AdminAutomationAuthoringService>().RunNowAsync(instanceId, automationId, r.Revision);
            await Intake(s);
            await s.ExecuteRunsAsync(100);
            var second = (await s.AutomationRunsAsync(owner, automationId)).First(w => w.AgentRunId != workId);
            Assert.Equal(AgentRunStatus.Completed, second.Status);
            Assert.Equal("NoAction", second.Result!.OutcomeKind.ToString());
            Assert.Empty(await Alerts(work, owner));
            Assert.DoesNotContain(await s.GetRequiredService<IExperienceStore>().ListAsync(instanceId, 100), e => e.SourceId == workId);
            Assert.DoesNotContain(await s.GetRequiredService<IExperienceStore>().ListAsync(instanceId, 100), e => e.SourceId == second.AgentRunId);
            r = await s.GetRequiredService<AdminAutomationAuthoringService>().SaveAsync(instanceId, automationId, r.Revision, true, 3600, "synthetic-automation-attention", null, null);
            await s.GetRequiredService<AdminAutomationAuthoringService>().RunNowAsync(instanceId, automationId, r.Revision); await Intake(s);
            await s.ExecuteRunsAsync(100);
            Assert.Single(await Alerts(work, owner));
            r = await s.GetRequiredService<AdminAutomationAuthoringService>().SaveAsync(instanceId, automationId, r.Revision, false, 3600, "Review", null, null);
            Assert.Equal(AutomationStatus.Disabled, r.Status);
            await Assert.ThrowsAsync<AgentCore.Application.Sessions.AgentCoreException>(() => s.GetRequiredService<AdminAutomationAuthoringService>().RunNowAsync(instanceId, automationId, r.Revision).AsTask());
            await s.GetRequiredService<AdminAutomationAuthoringService>().DeleteAsync(instanceId, automationId, r.Revision);
            Assert.Equal(AutomationStatus.Cancelled, (await s.GetRequiredService<ITriggerStore>().GetAsync(new(instanceId, owner.ProfileId), automationId))!.Status);
        }
    }
    [Fact(Timeout = 60000)]
    public async Task Overdue_periodic_automation_coalesces_once_and_frozen_admission_resumes_after_restart()
    {
        var db = Path.Combine(Path.GetTempPath(), $"automation-periodic-{Guid.NewGuid():N}.db");
        var clock = new AutomationClock(DateTimeOffset.UtcNow.AddHours(2));
        Guid instanceId, workId;
        await using (var host = new ExperienceHost(db, clock: clock))
        {
            var services = host.Services;
            var instance = await services.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 17);
            instanceId = instance.InstanceId;
            var automations = services.GetRequiredService<AdminAutomationAuthoringService>();
            var registration = await automations.SaveAsync(instanceId, null, 0, true, 3600, "Review; do nothing if no useful action is available.", null, null);
            clock.Advance(TimeSpan.FromHours(10));
            var scheduler = services.GetRequiredService<TriggerScheduler>();
            Assert.Equal(1, (await scheduler.RunOnceAsync(clock.GetUtcNow())).Admitted);
            Assert.Equal(0, (await scheduler.RunOnceAsync(clock.GetUtcNow())).Admitted);
            registration = (await services.GetRequiredService<ITriggerStore>().GetAsync(registration.Owner, registration.AutomationId))!;
            Assert.True(registration.NextOccurrenceAtUtc > clock.GetUtcNow());
            // An edit after atomic admission changes only future activations and their models.
            await automations.SaveAsync(instanceId, registration.AutomationId, registration.Revision, true, 3600,
                "synthetic-automation-attention future activation", "scripted-beta", null);
            await Intake(services);
            var work = Assert.Single(await services.GetRequiredService<IAgentRunStore>().ListAsync(new(instanceId, registration.Owner.ProfileId), 100));
            workId = work.AgentRunId;
            Assert.Equal(ActivationKind.ScheduledWork, work.Admission.Activation.Kind);
            Assert.Equal("scripted-alpha", work.PinnedModel.CatalogKey);
            Assert.Contains("Review; do nothing", (await services.SessionAsync(work)).Entries[0].Text);
            Assert.DoesNotContain("synthetic-automation-attention", (await services.SessionAsync(work)).Entries[0].Text);
            Assert.Null((await services.SessionAsync(work)).Origin.OriginatingSessionId);
            Assert.Equal(0, (await scheduler.RunOnceAsync(clock.GetUtcNow().AddHours(10))).Admitted);
        }
        await using var reopened = new ExperienceHost(db, clock: clock);
        var s = reopened.Services;
        var owner = new AgentRunOwner(instanceId, AgentCore.Domain.Conversation.LocalUserProfile.Id);
        await s.ExecuteRunsAsync(100);
        var recovered = (await s.GetRequiredService<IAgentRunStore>().GetAsync(owner, workId))!;
        Assert.Equal(AgentRunStatus.Completed, recovered.Status);
        Assert.Equal("NoAction", recovered.Result!.OutcomeKind.ToString());
        Assert.False(recovered.Result.AttentionRequired);
        Assert.Single(await s.GetRequiredService<IAgentRunStore>().ListAsync(owner, 100));
        Assert.Empty(await s.GetRequiredService<IExperienceStore>().ListAsync(instanceId, 100));
        Assert.Empty(await Alerts(s.GetRequiredService<IAgentRunStore>(), owner));
    }
    private sealed class AutomationClock(DateTimeOffset initial) : TimeProvider
    {
        private DateTimeOffset now = initial;
        public override DateTimeOffset GetUtcNow() => now;
        internal void Advance(TimeSpan elapsed) => now += elapsed;
    }
    private static async Task<List<string>> Alerts(IAgentRunStore work, AgentRunOwner owner)
    {
        var all = new List<string>();
        foreach (var item in await work.ListAsync(owner, 100)) if (item.Result?.AttentionRequired == true) all.Add(AgentRunAttentionKey.Format(item.AgentRunId, item.Revision));
        return all;
    }
    internal static IntervalAutomationDraft Draft(long revision, int interval = 3600) => new(revision, true, interval, "synthetic-automation-improve", null, null);
    internal static async Task Intake(IServiceProvider services)
    {
        await services.GetRequiredService<TriggerOccurrenceRouter>().RouteOnceAsync();
        Assert.Equal(1, (await services.GetRequiredService<BackgroundOccurrenceIntake>().AcceptAwaitingAsync()).Accepted);
    }
}
