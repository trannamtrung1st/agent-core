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
    [Fact(Timeout = 60000)]
    public async Task Overdue_periodic_automation_coalesces_once_and_frozen_admission_resumes_after_restart()
    {
        var db = Path.Combine(Path.GetTempPath(), $"automation-periodic-{Guid.NewGuid():N}.db");
        var clock = new AutomationClock(DateTimeOffset.UtcNow.AddHours(2));
        Guid instanceId, workId;
        await using (var host = new ExperienceHost(db, clock: clock))
        {
            var services = host.Services;
            var instance = await services.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 21);
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
