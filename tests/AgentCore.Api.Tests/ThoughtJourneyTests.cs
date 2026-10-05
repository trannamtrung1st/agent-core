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
using AgentCore.Domain.Work;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed class ThoughtJourneyTests
{
    [Theory]
    [InlineData(14, false)]
    [InlineData(15, true)]
    [InlineData(60, true)]
    [InlineData(3600, true)]
    [InlineData(604800, true)]
    [InlineData(604801, false)]
    public async Task Thought_intervals_enforce_seconds_bounds(int seconds, bool accepted)
    {
        var db = Path.Combine(Path.GetTempPath(), $"thought-interval-{Guid.NewGuid():N}.db");
        await using var host = new ExperienceHost(db);
        var instance = await host.Services.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 9);
        var client = TestOwnerCapability.CreateOwnerClient(host);
        var path = $"/api/v2/admin/agent-instances/{instance.InstanceId}/thoughts";
        var response = await client.PostAsJsonAsync(path, Draft(0, seconds) with { Enabled = false });
        Assert.Equal(accepted ? HttpStatusCode.OK : HttpStatusCode.BadRequest, response.StatusCode);
        if (accepted)
        {
            var stored = (await response.Content.ReadFromJsonAsync<ThoughtRegistrationResponse>())!;
            Assert.Equal(seconds, stored.IntervalSeconds);
            var review = await client.GetFromJsonAsync<JsonElement>(path);
            Assert.Equal(15, review.GetProperty("minIntervalSeconds").GetInt32());
            Assert.Equal(seconds, review.GetProperty("items")[0].GetProperty("intervalSeconds").GetInt32());
        }
    }

    [Fact(Timeout = 90000)]
    public async Task Experience_to_thought_to_approved_skill_survives_restart_then_finishes_quietly_without_more_work()
    {
        var db = Path.Combine(Path.GetTempPath(), $"thought-{Guid.NewGuid():N}.db");
        Guid instanceId, registrationId, workId; int originalVersion;
        await using (var host = new ExperienceHost(db))
        {
            var s = host.Services;
            var instance = await s.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 9);
            instanceId = instance.InstanceId; originalVersion = instance.ActiveVersion;
            var source = await ExperienceJourneyTests.SeedAsync(s, instanceId);
            var service = s.GetRequiredService<ExperienceService>();
            await s.GetRequiredService<IExperienceStore>().ConfigureAsync(instanceId, 0, true);
            await service.RequestSessionAsync(instanceId, source.SessionId);
            var repeated = await ExperienceJourneyTests.SeedAsync(s, instanceId);
            await service.RequestSessionAsync(instanceId, repeated.SessionId);
            Assert.Equal(2, await s.GetRequiredService<DurableReminderExecutor>().ExecuteDueAsync(DateTimeOffset.UtcNow, 100));
            instance = await s.GetRequiredService<HarnessManagementService>().ConfigureAsync(instanceId, instance.Revision,
                new(HarnessManagementMode.Assisted, [HarnessManagementScope.Skills, HarnessManagementScope.ToolSelection], [], [ToolCatalog.WebFetch]));
            var client = TestOwnerCapability.CreateOwnerClient(host);
            var path = $"/api/v2/admin/agent-instances/{instanceId}/thoughts";
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(path, Draft(0, 10))).StatusCode);
            var created = await client.PostAsJsonAsync(path, Draft(0)); created.EnsureSuccessStatusCode();
            var r = (await created.Content.ReadFromJsonAsync<ThoughtRegistrationResponse>())!;
            registrationId = Guid.Parse(r.RegistrationId);
            Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync(path + "/" + registrationId, Draft(0))).StatusCode);
            var run = await client.PostAsJsonAsync(path + "/" + registrationId + "/run", new ContinuityRevisionRequest(r.Revision)); run.EnsureSuccessStatusCode();
            Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(path + "/" + registrationId + "/run", new ContinuityRevisionRequest(r.Revision))).StatusCode);
            await Intake(s);
            var work = s.GetRequiredService<IWorkItemStore>();
            var owner = new WorkOwner(instanceId, AgentCore.Domain.Conversation.LocalUserProfile.Id);
            var item = (await work.ListAsync(owner, 100)).Single(w => w.Provenance.SourceKind == WorkSourceKind.ThoughtActivation);
            workId = item.WorkItemId;
            Assert.Equal("Thought activation", item.OriginLabel);
            Assert.Contains("synthetic-thought-improve", item.Provenance.EvidenceJson);
            Assert.Equal(1, await s.GetRequiredService<DurableReminderExecutor>().ExecuteDueAsync(DateTimeOffset.UtcNow, 100));
            item = (await work.GetAsync(owner, workId))!;
            Assert.Equal(WorkItemStatus.WaitingForApproval, item.Status);
            Assert.Equal("harness.skill.upsert", item.Approval!.ToolName);
            Assert.Equal(originalVersion, (await s.GetRequiredService<IAgentInstanceStore>().FindAsync(instanceId))!.ActiveVersion);
            var edited = await client.PutAsJsonAsync(path + "/" + registrationId, Draft(r.Revision) with { ThinkingPrompt = "synthetic-thought-attention future prompt" }); edited.EnsureSuccessStatusCode();
            var scheduler = await s.GetRequiredService<TriggerScheduler>().RunOnceAsync(DateTimeOffset.UtcNow.AddHours(10));
            Assert.Equal(0, scheduler.Admitted);
            Assert.Single(await work.ListAsync(owner, 100), w => w.Provenance.SourceKind == WorkSourceKind.ThoughtActivation);
            Assert.Contains("synthetic-thought-improve", (await work.GetAsync(owner, workId))!.Provenance.EvidenceJson);
            var toolExecutor = s.GetRequiredService<SessionToolExecutor>();
            var definition = (await s.GetRequiredService<IAgentDefinitionStore>().GetAsync("general-assistant", originalVersion))!;
            var harness = await toolExecutor.HarnessContextAsync(instanceId, default);
            var thought = new ToolExecutionAdmission(true, TriggerKind.ThoughtActivation, AgentInstanceId: instanceId, Harness: harness);
            var user = thought with { Detached = false, TriggerKind = TriggerKind.UserTurn };
            Assert.Equal(ToolPolicyDecision.Deny, toolExecutor.EvaluateExecutionPolicy(definition, "harness.tool.select", admission: thought));
            Assert.Equal(ToolPolicyDecision.RequireApproval, toolExecutor.EvaluateExecutionPolicy(definition, "harness.tool.select", admission: user));
            Assert.Equal(ToolPolicyDecision.Deny, toolExecutor.EvaluateExecutionPolicy(definition, ToolCatalog.TriggerCancel, admission: thought));
            Assert.Equal(ToolPolicyDecision.Deny, toolExecutor.EvaluateExecutionPolicy(definition, ToolCatalog.AppMessageSend, admission: thought));
            Assert.DoesNotContain("harness.tool.select", item.Checkpoint!.PayloadJson);
            Assert.Empty(await Alerts(work, owner));
        }
        await using (var host = new ExperienceHost(db))
        {
            var s = host.Services; var client = TestOwnerCapability.CreateOwnerClient(host);
            var work = s.GetRequiredService<IWorkItemStore>(); var owner = new WorkOwner(instanceId, AgentCore.Domain.Conversation.LocalUserProfile.Id);
            var item = (await work.GetAsync(owner, workId))!;
            Assert.Equal(WorkItemStatus.WaitingForApproval, item.Status);
            var a = item.Approval!;
            var url = $"/api/v2/admin/agent-instances/{instanceId}/work-items/{workId}/approvals/{a.ApprovalId}/approve";
            Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(url, new DecideWorkApprovalRequest(item.Revision, a.Revision, "forged-hash"))).StatusCode);
            (await client.PostAsJsonAsync(url, new DecideWorkApprovalRequest(item.Revision, a.Revision, a.ActionHash))).EnsureSuccessStatusCode();
            Assert.Equal(1, await s.GetRequiredService<DurableReminderExecutor>().ExecuteDueAsync(DateTimeOffset.UtcNow, 100));
            item = (await work.GetAsync(owner, workId))!;
            Assert.Equal(WorkItemStatus.Completed, item.Status);
            Assert.Equal("ActionCompleted", ThoughtCompletion.Outcome(item.Result!.Text));
            Assert.False(item.Result.AttentionRequired);
            var instance = (await s.GetRequiredService<IAgentInstanceStore>().FindAsync(instanceId))!;
            Assert.True(instance.ActiveVersion > originalVersion);
            var future = (await s.GetRequiredService<IAgentDefinitionStore>().GetAsync(instance.DefinitionId, instance.ActiveVersion))!;
            Assert.Single(future.SkillList, skill => skill.Name == "Experience review");
            Assert.Empty(await Alerts(work, owner));
            var r = (await s.GetRequiredService<ITriggerStore>().GetAsync(new(instanceId, owner.ProfileId), registrationId))!;
            r = await s.GetRequiredService<ThoughtRegistrationService>().SaveAsync(instanceId, registrationId, r.Revision, true, 3600, "synthetic-thought-improve", null, null);
            await s.GetRequiredService<ThoughtRegistrationService>().RunNowAsync(instanceId, registrationId, r.Revision);
            await Intake(s);
            await s.GetRequiredService<DurableReminderExecutor>().ExecuteDueAsync(DateTimeOffset.UtcNow, 100);
            var second = (await work.ListAsync(owner, 100)).First(w => w.WorkItemId != workId && w.Provenance.SourceKind == WorkSourceKind.ThoughtActivation);
            Assert.Equal(WorkItemStatus.Completed, second.Status);
            Assert.Equal("NoAction", ThoughtCompletion.Outcome(second.Result!.Text));
            Assert.Empty(await Alerts(work, owner));
            Assert.Single(await s.GetRequiredService<IExperienceStore>().ListAsync(instanceId, 100), e => e.SourceId == workId);
            Assert.DoesNotContain(await s.GetRequiredService<IExperienceStore>().ListAsync(instanceId, 100), e => e.SourceId == second.WorkItemId);
            r = await s.GetRequiredService<ThoughtRegistrationService>().SaveAsync(instanceId, registrationId, r.Revision, true, 3600, "synthetic-thought-attention", null, null);
            await s.GetRequiredService<ThoughtRegistrationService>().RunNowAsync(instanceId, registrationId, r.Revision); await Intake(s);
            await s.GetRequiredService<DurableReminderExecutor>().ExecuteDueAsync(DateTimeOffset.UtcNow, 100);
            Assert.Single(await Alerts(work, owner));
            r = await s.GetRequiredService<ThoughtRegistrationService>().SaveAsync(instanceId, registrationId, r.Revision, false, 3600, "Review", null, null);
            Assert.Equal(TriggerRegistrationStatus.Disabled, r.Status);
            await Assert.ThrowsAsync<AgentCore.Application.Sessions.AgentCoreException>(() => s.GetRequiredService<ThoughtRegistrationService>().RunNowAsync(instanceId, registrationId, r.Revision).AsTask());
            await s.GetRequiredService<ThoughtRegistrationService>().DeleteAsync(instanceId, registrationId, r.Revision);
            Assert.Equal(TriggerRegistrationStatus.Cancelled, (await s.GetRequiredService<ITriggerStore>().GetAsync(new(instanceId, owner.ProfileId), registrationId))!.Status);
        }
    }
    [Fact(Timeout = 60000)]
    public async Task Overdue_periodic_thought_coalesces_once_and_frozen_admission_resumes_after_restart()
    {
        var db = Path.Combine(Path.GetTempPath(), $"thought-periodic-{Guid.NewGuid():N}.db");
        var clock = new ThoughtClock(DateTimeOffset.UtcNow.AddHours(2));
        Guid instanceId, workId;
        await using (var host = new ExperienceHost(db, clock: clock))
        {
            var services = host.Services;
            var instance = await services.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 9);
            instanceId = instance.InstanceId;
            var thoughts = services.GetRequiredService<ThoughtRegistrationService>();
            var registration = await thoughts.SaveAsync(instanceId, null, 0, true, 3600, "Review; do nothing if no useful action is available.", null, null);
            clock.Advance(TimeSpan.FromHours(10));
            var scheduler = services.GetRequiredService<TriggerScheduler>();
            Assert.Equal(1, (await scheduler.RunOnceAsync(clock.GetUtcNow())).Admitted);
            Assert.Equal(0, (await scheduler.RunOnceAsync(clock.GetUtcNow())).Admitted);
            registration = (await services.GetRequiredService<ITriggerStore>().GetAsync(registration.Owner, registration.RegistrationId))!;
            Assert.True(registration.NextOccurrenceAtUtc > clock.GetUtcNow());
            // An edit after atomic admission changes only future activations and their models.
            await thoughts.SaveAsync(instanceId, registration.RegistrationId, registration.Revision, true, 3600,
                "synthetic-thought-attention future activation", "scripted-beta", null);
            await Intake(services);
            var work = Assert.Single(await services.GetRequiredService<IWorkItemStore>().ListAsync(new(instanceId, registration.Owner.ProfileId), 100));
            workId = work.WorkItemId;
            Assert.Equal(WorkSourceKind.ThoughtActivation, work.Provenance.SourceKind);
            Assert.Equal("scripted-alpha", work.Model.CatalogKey);
            Assert.Contains("Review; do nothing", work.Provenance.EvidenceJson);
            Assert.DoesNotContain("synthetic-thought-attention", work.Provenance.EvidenceJson);
            Assert.Null(work.Provenance.SourceSessionId);
            Assert.Equal(0, (await scheduler.RunOnceAsync(clock.GetUtcNow().AddHours(10))).Admitted);
        }
        await using var reopened = new ExperienceHost(db, clock: clock);
        var s = reopened.Services;
        var owner = new WorkOwner(instanceId, AgentCore.Domain.Conversation.LocalUserProfile.Id);
        await s.GetRequiredService<DurableReminderExecutor>().ExecuteDueAsync(clock.GetUtcNow(), 100);
        var recovered = (await s.GetRequiredService<IWorkItemStore>().GetAsync(owner, workId))!;
        Assert.Equal(WorkItemStatus.Completed, recovered.Status);
        Assert.Equal("NoAction", ThoughtCompletion.Outcome(recovered.Result!.Text));
        Assert.False(recovered.Result.AttentionRequired);
        Assert.Single(await s.GetRequiredService<IWorkItemStore>().ListAsync(owner, 100));
        Assert.Empty(await s.GetRequiredService<IExperienceStore>().ListAsync(instanceId, 100));
        Assert.Empty(await Alerts(s.GetRequiredService<IWorkItemStore>(), owner));
    }
    private sealed class ThoughtClock(DateTimeOffset initial) : TimeProvider
    {
        private DateTimeOffset now = initial;
        public override DateTimeOffset GetUtcNow() => now;
        internal void Advance(TimeSpan elapsed) => now += elapsed;
    }
    private static async Task<List<string>> Alerts(IWorkItemStore work, WorkOwner owner)
    {
        var all = new List<string>();
        foreach (var item in await work.ListAsync(owner, 100)) all.AddRange(await work.ListAttentionAlertKeysAsync(item.WorkItemId));
        return all;
    }
    internal static ThoughtRegistrationRequest Draft(long revision, int interval = 3600) => new(revision, true, interval, "synthetic-thought-improve", null, null);
    internal static async Task Intake(IServiceProvider services)
    {
        await services.GetRequiredService<TriggerOccurrenceRouter>().RouteOnceAsync();
        Assert.Equal(1, (await services.GetRequiredService<DurableWorkIntake>().AcceptAwaitingAsync()).Accepted);
    }
}
