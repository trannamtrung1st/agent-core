using System.Net;
using System.Net.Http.Json;
using AgentCore.Application.Admin;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Triggers;
using AgentCore.Contracts.Http;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Triggers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using AgentCore.Infrastructure.Providers;


namespace AgentCore.Api.Tests;

public sealed class AutomationDestinationJourneyTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 3, 0, 0, TimeSpan.Zero);

    [Theory(Timeout = 60000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Greeting_due_in_exact_conversation_survives_headless_dispatch_and_restart(bool detached)
    {
        var db = Path.Combine(Path.GetTempPath(), $"automation-target-{Guid.NewGuid():N}.db");
        var clock = new AutomationClock(Now);
        Guid sessionId, instanceId, runId;
        await using (var host = new ExperienceHost(db, clock: clock))
        {
            var s = host.Services;
            instanceId = (await s.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 17)).InstanceId;
            var session = await s.GetRequiredService<SessionManager>().CreateForInstanceAsync(instanceId, SessionMode.Text);
            sessionId = session.SessionId;
            var unrelated = await s.GetRequiredService<SessionManager>().CreateForInstanceAsync(instanceId, SessionMode.Text);
            if (detached) await s.GetRequiredService<IMemoryStore>().SaveAsync(session with { Revision = session.Revision + 1,
                Status = SessionStatus.Paused, PauseReason = "disconnected" }, session.Revision);
            using var client = TestOwnerCapability.CreateOwnerClient(host);
            var saved = await client.PostAsJsonAsync($"/api/v2/admin/agent-instances/{instanceId}/automations", new AutomationRequest(
                0, true, "Greeting", "Say hello once in this conversation", new("schedule", new("oneShot", AtUtc: Now.AddSeconds(30).ToString("O"))),
                ExecutionTarget: new("existingSession", sessionId.ToString("D")), CompletionDelivery: new("none")));
            saved.EnsureSuccessStatusCode();
            var automation = (await saved.Content.ReadFromJsonAsync<AutomationResponse>())!;
            Assert.Equal(sessionId.ToString("D"), automation.ExecutionTarget.SessionId);
            clock.Advance(TimeSpan.FromSeconds(30));
            await s.GetRequiredService<TriggerScheduler>().RunOnceAsync(clock.GetUtcNow());
            await AdmitAndExecute(s);
            var owner = new AgentRunOwner(instanceId, LocalUserProfile.Id);
            var run = Assert.Single(await s.GetRequiredService<IAgentRunStore>().ListForSessionAsync(owner, sessionId));
            runId = run.AgentRunId;
            Assert.Equal(AgentRunStatus.Completed, run.Status);
            Assert.Equal(AgentRunOutputContract.ConversationResponse, run.Admission.OutputContract);
            Assert.Empty(run.Admission.Activation.SourceEntryIds);
            var history = await s.GetRequiredService<IMemoryStore>().LoadAsync(sessionId);
            Assert.Equal("Hello!", Assert.Single(history!.Entries).Text);
            Assert.Empty((await s.GetRequiredService<IMemoryStore>().LoadAsync(unrelated.SessionId))!.Entries);
            Assert.Empty((await s.GetRequiredService<IMemoryStore>().ListBackgroundSessionsAsync(owner, null, 50, false)).Items);
            await s.GetRequiredService<TriggerOccurrenceRouter>().RouteOnceAsync();
            await s.GetRequiredService<BackgroundOccurrenceIntake>().AcceptAwaitingAsync();
            Assert.Single(await s.GetRequiredService<IAgentRunStore>().ListForSessionAsync(owner, sessionId));
        }
        await using var restarted = new ExperienceHost(db, clock: clock);
        var restored = await restarted.Services.GetRequiredService<IMemoryStore>().LoadAsync(sessionId);
        Assert.Equal("Hello!", Assert.Single(restored!.Entries).Text);
        Assert.Equal(runId, Assert.Single(await restarted.Services.GetRequiredService<IAgentRunStore>()
            .ListForSessionAsync(new(instanceId, LocalUserProfile.Id), sessionId)).AgentRunId);
    }

    [Theory(Timeout = 60000)]
    [InlineData(false, "user")]
    [InlineData(true, "user")]
    [InlineData(false, "ended")]
    [InlineData(true, "ended")]
    [InlineData(false, "archived")]
    [InlineData(true, "archived")]
    [InlineData(false, "deleted")]
    [InlineData(true, "deleted")]
    public async Task Unavailable_target_has_visible_rejection_and_recurring_suspension_without_fallback(bool recurring, string lifecycle)
    {
        var clock = new AutomationClock(Now);
        await using var host = new ExperienceHost(Path.Combine(Path.GetTempPath(), $"target-unavailable-{Guid.NewGuid():N}.db"), clock: clock);
        var s = host.Services;
        var instance = await s.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 17);
        var session = await s.GetRequiredService<SessionManager>().CreateForInstanceAsync(instance.InstanceId, SessionMode.Text);
        var authoring = s.GetRequiredService<AdminAutomationAuthoringService>();
        var automation = await authoring.SaveAsync(instance.InstanceId, null, 0, true, "Greeting", "Say hello",
            new ScheduleTrigger(recurring ? new FixedIntervalSchedule(60, Now.AddSeconds(60)) : new OneShotSchedule(Now.AddSeconds(60), "UTC")),
            null, null, executionTarget: AutomationExecutionTarget.Existing(session.SessionId), completionDelivery: AutomationCompletionDelivery.None);
        if (lifecycle == "archived") await s.GetRequiredService<SessionManager>().ArchiveAsync(session.SessionId);
        else if (lifecycle == "deleted") await s.GetRequiredService<SessionManager>().DurablyDeleteAsync(session.SessionId);
        else await s.GetRequiredService<IMemoryStore>().SaveAsync(session with { Revision = session.Revision + 1,
            Status = lifecycle == "ended" ? SessionStatus.Ended : SessionStatus.Paused, PauseReason = lifecycle == "user" ? "user" : null }, session.Revision);
        clock.Advance(TimeSpan.FromSeconds(60));
        await s.GetRequiredService<TriggerScheduler>().RunOnceAsync(clock.GetUtcNow());
        await AdmitAndExecute(s);
        var occurrence = Assert.Single(await s.GetRequiredService<ITriggerStore>().ListByDispositionAsync(OccurrenceRoutingDisposition.Rejected, 10));
        Assert.Equal("target-unavailable", occurrence.DispositionReason);
        Assert.Null(occurrence.ExecutionSessionId);
        if (recurring) Assert.Equal(AutomationStatus.SuspendedPolicy, (await s.GetRequiredService<ITriggerStore>().GetAsync(automation.Owner, automation.AutomationId))!.Status);
        Assert.Empty((await s.GetRequiredService<IMemoryStore>().ListBackgroundSessionsAsync(new(instance.InstanceId, LocalUserProfile.Id), null, 50, false)).Items);
        var current = (await s.GetRequiredService<ITriggerStore>().GetAsync(automation.Owner, automation.AutomationId))!;
        var disabled = await authoring.SaveAsync(instance.InstanceId, current.AutomationId, current.Revision, false,
            current.Name, current.Instructions, current.Trigger, current.ModelOverrideCatalogKey, current.ModelOverrideReasoningEffort);
        Assert.Equal(AutomationStatus.Disabled, disabled.Status);
        Assert.Equal(current.ExecutionTarget, disabled.ExecutionTarget);
    }

    [Theory(Timeout = 60000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Scheduled_background_report_is_explicit_once_per_occurrence_and_works_with_Initiative_off(bool reportBack)
    {
        var clock = new AutomationClock(Now);
        await using var host = new ExperienceHost(Path.Combine(Path.GetTempPath(), $"report-target-{Guid.NewGuid():N}.db"), clock: clock);
        var s = host.Services;
        var instance = await s.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 17);
        var parent = await s.GetRequiredService<SessionManager>().CreateForInstanceAsync(instance.InstanceId, SessionMode.Text);
        Assert.False(parent.Definition.InitiativePolicy.Enabled);
        var a = await s.GetRequiredService<AdminAutomationAuthoringService>().SaveAsync(instance.InstanceId, null, 0, true,
            "Check progress", "synthetic-automation-attention: report the actual progress", new ScheduleTrigger(new FixedIntervalSchedule(60, Now.AddSeconds(60))), null, null,
            executionTarget: AutomationExecutionTarget.Background, completionDelivery: reportBack ? AutomationCompletionDelivery.ToSession(parent.SessionId) : AutomationCompletionDelivery.None);
        for (var count = 1; count <= 2; count++)
        {
            clock.Advance(TimeSpan.FromSeconds(60));
            await s.GetRequiredService<TriggerScheduler>().RunOnceAsync(clock.GetUtcNow());
            await AdmitAndExecute(s);
            await s.GetRequiredService<BackgroundCompletionReporter>().ReportPendingAsync(100);
            await s.ExecuteRunsAsync(); // now wait for the admitted parent response
            var owner = new AgentRunOwner(instance.InstanceId, LocalUserProfile.Id);
            var children = (await s.GetRequiredService<IMemoryStore>().ListBackgroundSessionsAsync(owner, null, 50, false)).Items;
            Assert.Equal(count, children.Count);
            foreach (var child in children)
            {
                var delivery = await s.GetRequiredService<IAgentRunStore>().GetCompletionDeliveryAsync(owner, child.Origin.InitialBackgroundAgentRunId!.Value);
                Assert.Equal(reportBack ? "delivered" : "notRequested", delivery.Status);
            }
            var history = await s.GetRequiredService<IMemoryStore>().LoadAsync(parent.SessionId);
            Assert.Equal(reportBack ? count : 0, history!.Entries.Count(e => e.Role == ConversationRole.Assistant));
            Assert.All(history.Entries, e => Assert.Contains("An unresolved checkpoint", e.Text));
            await s.GetRequiredService<BackgroundCompletionReporter>().ReportPendingAsync(100);
            Assert.Equal(reportBack ? count : 0, (await s.GetRequiredService<IAgentRunStore>().ListForSessionAsync(owner, parent.SessionId)).Count);
        }
    }

    [Fact(Timeout = 60000)]
    public async Task Recurring_conversation_occurrences_keep_one_response_each_in_the_exact_session()
    {
        var clock = new AutomationClock(Now);
        await using var host = new ExperienceHost(Path.Combine(Path.GetTempPath(), $"recurring-target-{Guid.NewGuid():N}.db"), clock: clock);
        var s = host.Services;
        var instance = await s.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 17);
        var target = await s.GetRequiredService<SessionManager>().CreateForInstanceAsync(instance.InstanceId, SessionMode.Text);
        await s.GetRequiredService<AdminAutomationAuthoringService>().SaveAsync(instance.InstanceId, null, 0, true, "Greeting", "Say hello",
            new ScheduleTrigger(new FixedIntervalSchedule(60, Now.AddSeconds(60))), null, null,
            executionTarget: AutomationExecutionTarget.Existing(target.SessionId), completionDelivery: AutomationCompletionDelivery.None);
        for (var count = 1; count <= 2; count++)
        {
            clock.Advance(TimeSpan.FromSeconds(60));
            await s.GetRequiredService<TriggerScheduler>().RunOnceAsync(clock.GetUtcNow());
            await AdmitAndExecute(s);
            await AdmitAndExecute(s);
            Assert.Equal(count, (await s.GetRequiredService<IAgentRunStore>().ListForSessionAsync(new(instance.InstanceId, LocalUserProfile.Id), target.SessionId)).Count);
            var history = (await s.GetRequiredService<IMemoryStore>().LoadAsync(target.SessionId))!;
            Assert.Equal(count, history.Entries.Count);
            Assert.All(history.Entries, entry => { Assert.Equal(ConversationRole.Assistant, entry.Role); Assert.Equal("Hello!", entry.Text); });
        }
    }

    [Theory(Timeout = 60000)]
    [InlineData("failed")]
    [InlineData("cancelled")]
    [InlineData("approval")]
    [InlineData("quiet")]
    [InlineData("parent-unavailable")]
    [InlineData("policy-revoked")]
    [InlineData("model-unavailable")]
    public async Task Initial_child_disposition_and_current_authority_control_truthful_delivery(string disposition)
    {
        var clock = new AutomationClock(Now);
        var catalog = new ReportModelCatalog();
        Action<IServiceCollection>? configure = disposition == "model-unavailable" ? services =>
        { services.RemoveAll<IModelCatalog>(); services.AddSingleton<IModelCatalog>(catalog); } : null;
        await using var host = new ExperienceHost(Path.Combine(Path.GetTempPath(), $"report-disposition-{Guid.NewGuid():N}.db"), clock: clock, configure: configure);
        var s = host.Services;
        var instance = await s.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 17);
        var parent = await s.GetRequiredService<SessionManager>().CreateForInstanceAsync(instance.InstanceId, SessionMode.Text);
        if (disposition == "model-unavailable")
        {
            var selected = parent with { Revision = parent.Revision + 1,
                ModelSelection = new("report-model", "primary-llm", "scripted-beta", ModelSelectionSource.Host, null) };
            await s.GetRequiredService<IMemoryStore>().SaveAsync(selected, parent.Revision);
            parent = selected;
        }
        var authoring = s.GetRequiredService<AdminAutomationAuthoringService>();
        var automation = await authoring.SaveAsync(instance.InstanceId, null, 0, true, "Check", "Inspect the authorized task",
            new ScheduleTrigger(new FixedIntervalSchedule(60, Now.AddSeconds(60))), null, null,
            executionTarget: AutomationExecutionTarget.Background, completionDelivery: AutomationCompletionDelivery.ToSession(parent.SessionId));
        await authoring.RunNowAsync(instance.InstanceId, automation.AutomationId, automation.Revision);
        await s.GetRequiredService<TriggerOccurrenceRouter>().RouteOnceAsync();
        await s.GetRequiredService<BackgroundOccurrenceIntake>().AcceptAwaitingAsync();
        var runs = s.GetRequiredService<IAgentRunStore>();
        var owner = new AgentRunOwner(instance.InstanceId, LocalUserProfile.Id);
        var child = Assert.Single(await runs.ListRunnableAsync(Now, 100));
        child = await runs.ApplyAsync(owner, child.AgentRunId, new AgentRunCommand.Claim(child.Revision, Now, Guid.NewGuid(), Now.AddMinutes(5)));
        if (disposition == "approval")
            await runs.ApplyAsync(owner, child.AgentRunId, new AgentRunCommand.BeginApproval(child.Revision, Now, child.Claim!.Generation,
                Guid.NewGuid(), "http.request", "{}", new string('a', 64), "Approve the action", Now.AddMinutes(2)));
        else if (disposition == "cancelled")
        {
            child = await runs.ApplyAsync(owner, child.AgentRunId, new AgentRunCommand.RequestCancellation(child.Revision, Now, null));
            await runs.ApplyAsync(owner, child.AgentRunId, new AgentRunCommand.CommitCancellation(child.Revision, Now, child.Claim!.Generation, "The task was cancelled."));
        }
        else if (disposition == "quiet")
            await runs.ApplyAsync(owner, child.AgentRunId, new AgentRunCommand.Complete(child.Revision, Now, child.Claim!.Generation, "", AgentRunOutcomeKind.NoAction, null));
        else
            await runs.ApplyAsync(owner, child.AgentRunId, new AgentRunCommand.Fail(child.Revision, Now, child.Claim!.Generation,
                "fixture-failure", "The task failed before completion.", false, null));
        if (disposition == "parent-unavailable") await s.GetRequiredService<SessionManager>().ArchiveAsync(parent.SessionId);
        if (disposition == "model-unavailable") catalog.IncludeReportModel = false;
        if (disposition == "policy-revoked")
        {
            var current = (await s.GetRequiredService<ITriggerStore>().GetAsync(automation.Owner, automation.AutomationId))!;
            await authoring.SaveAsync(instance.InstanceId, current.AutomationId, current.Revision, false, current.Name, current.Instructions, current.Trigger, null, null);
        }
        await s.GetRequiredService<BackgroundCompletionReporter>().ReportPendingAsync(100);
        await s.ExecuteRunsAsync();
        var delivery = await runs.GetCompletionDeliveryAsync(owner, child.AgentRunId);
        Assert.Equal(disposition switch { "failed" or "cancelled" => "delivered", "approval" => "pending", _ => "skipped" }, delivery.Status);
        var history = (await s.GetRequiredService<IMemoryStore>().LoadAsync(parent.SessionId))!;
        Assert.Equal(disposition is "failed" or "cancelled" ? 1 : 0, history.Entries.Count);
        if (disposition == "failed") Assert.Contains("task failed", history.Entries[0].Text);
        if (disposition == "cancelled") Assert.Contains("cancelled", history.Entries[0].Text);
        await s.GetRequiredService<BackgroundCompletionReporter>().ReportPendingAsync(100);
        Assert.Equal(history.Entries.Count, (await s.GetRequiredService<IMemoryStore>().LoadAsync(parent.SessionId))!.Entries.Count);
    }

    [Fact(Timeout = 60000)]
    public async Task Admin_rejects_foreign_target_conflicting_model_and_duplicate_delivery()
    {
        await using var host = new ExperienceHost(Path.Combine(Path.GetTempPath(), $"target-authority-{Guid.NewGuid():N}.db"));
        var s = host.Services;
        var admin = s.GetRequiredService<AdminAgentInstanceService>();
        var a = await admin.CreateManagedAsync("general-assistant", 17);
        var b = await admin.CreateManagedAsync("general-assistant", 17);
        var session = await s.GetRequiredService<SessionManager>().CreateForInstanceAsync(b.InstanceId, SessionMode.Text);
        using var client = TestOwnerCapability.CreateOwnerClient(host);
        var draft = new AutomationRequest(0, true, "Greeting", "Say hello", new("schedule", new("oneShot", AtUtc: DateTimeOffset.UtcNow.AddHours(1).ToString("O"))),
            ExecutionTarget: new("existingSession", session.SessionId.ToString("D")), CompletionDelivery: new("none"));
        var path = $"/api/v2/admin/agent-instances/{a.InstanceId}/automations";
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(path, draft)).StatusCode);
        var own = await s.GetRequiredService<SessionManager>().CreateForInstanceAsync(a.InstanceId, SessionMode.Text);
        draft = draft with { ExecutionTarget = new("existingSession", own.SessionId.ToString("D")) };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(path, draft with { CompletionDelivery = new("toSession", own.SessionId.ToString("D")) })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(path, draft with { ModelKey = "missing-model" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(path, draft with { ExecutionTarget = null })).StatusCode);
        (await client.PostAsJsonAsync(path, draft)).EnsureSuccessStatusCode();
    }

    [Fact(Timeout = 60000)]
    public async Task Text_only_target_supports_conversation_but_rejects_explicit_tool_and_background_requirements()
    {
        var clock = new AutomationClock(Now);
        await using var host = new ExperienceHost(Path.Combine(Path.GetTempPath(), $"target-text-model-{Guid.NewGuid():N}.db"), clock: clock,
            configure: services => {
                services.RemoveAll<IModelCatalog>();
                services.AddSingleton<IModelCatalog>(new ConfigurationModelCatalog("text-only", [
                    new("text-only", "Text only", "primary-llm", "scripted-alpha", false, false, true, false, [], null)]));
            });
        var s = host.Services;
        var instance = await s.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 17);
        var parent = await s.GetRequiredService<SessionManager>().CreateForInstanceAsync(instance.InstanceId, SessionMode.Text);
        var authoring = s.GetRequiredService<AdminAutomationAuthoringService>();
        await Assert.ThrowsAsync<AgentCoreException>(() => authoring.SaveAsync(instance.InstanceId, null, 0, true,
            "Tools", "Inspect current state with tools", new ScheduleTrigger(new OneShotSchedule(Now.AddSeconds(30), "UTC")), null, null,
            executionTarget: AutomationExecutionTarget.Existing(parent.SessionId), completionDelivery: AutomationCompletionDelivery.None, requiresTools: true).AsTask());
        await Assert.ThrowsAsync<AgentCoreException>(() => authoring.SaveAsync(instance.InstanceId, null, 0, true,
            "Background", "Inspect current state", new ScheduleTrigger(new OneShotSchedule(Now.AddSeconds(30), "UTC")), null, null,
            executionTarget: AutomationExecutionTarget.Background, completionDelivery: AutomationCompletionDelivery.None).AsTask());
        await authoring.SaveAsync(instance.InstanceId, null, 0, true, "Greeting", "Say hello",
            new ScheduleTrigger(new OneShotSchedule(Now.AddSeconds(30), "UTC")), null, null,
            executionTarget: AutomationExecutionTarget.Existing(parent.SessionId), completionDelivery: AutomationCompletionDelivery.None);
        clock.Advance(TimeSpan.FromSeconds(30));
        await s.GetRequiredService<TriggerScheduler>().RunOnceAsync(clock.GetUtcNow());
        await AdmitAndExecute(s);
        Assert.Equal("Hello!", Assert.Single((await s.GetRequiredService<IMemoryStore>().LoadAsync(parent.SessionId))!.Entries).Text);
    }

    private static async Task AdmitAndExecute(IServiceProvider services)
    {
        await services.GetRequiredService<TriggerOccurrenceRouter>().RouteOnceAsync();
        await services.GetRequiredService<BackgroundOccurrenceIntake>().AcceptAwaitingAsync();
        await services.ExecuteRunsAsync();
    }
    private sealed class AutomationClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset current = now;
        public override DateTimeOffset GetUtcNow() => current;
        public void Advance(TimeSpan elapsed) => current += elapsed;
    }
    private sealed class ReportModelCatalog : IModelCatalog
    {
        private static readonly ModelDescriptor Execution = new("scripted-alpha", "Execution", "primary-llm", "scripted-alpha", true, true, true, false, [], null);
        private static readonly ModelDescriptor Report = new("report-model", "Report", "primary-llm", "scripted-beta", true, true, true, false, [], null);
        public bool IncludeReportModel { get; set; } = true;
        public string DefaultKey => Execution.Key;
        public ModelDescriptor Default => Execution;
        public IReadOnlyList<ModelDescriptor> Models => IncludeReportModel ? [Execution, Report] : [Execution];
        public ModelDescriptor? Get(string key) => Models.FirstOrDefault(model => model.Key == key);
    }
}
