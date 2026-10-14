using System.Runtime.CompilerServices;
using System.Text.Json;
using AgentCore.Api.Realtime;
using AgentCore.Application.Admin;
using AgentCore.Application.Execution;
using AgentCore.Application.Events;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Tools;
using AgentCore.Application.Triggers;
using AgentCore.Contracts.Realtime;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Triggers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentCore.Api.Tests;

public sealed class ReliabilityJourneyTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 0, 0, 0, TimeSpan.Zero);
    private const string ConflictingChat = "Don't greet me every 60 seconds. Explain the schedule instead.";
    private const string UnrelatedChat = "The kettle is blue. For this chat reply say only noted.";

    [Fact(Timeout = 60000)]
    public async Task Automation_occurrences_keep_their_task_across_chat_history_owner_edits_and_disable()
    {
        var path = Path.Combine(Path.GetTempPath(), $"reliability-task-isolation-{Guid.NewGuid():N}.db");
        var clock = new AutomationClock(Now);
        var model = new SchedulingModel();
        Guid instanceId, sessionId;
        await using (var host = new ExperienceHost(path, model, clock))
        {
            var s = host.Services;
            instanceId = (await s.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 24)).InstanceId;
            var snapshot = await s.GetRequiredService<SessionManager>().CreateForInstanceAsync(instanceId, SessionMode.Text);
            sessionId = snapshot.SessionId;
            await using var runtime = s.GetRequiredService<SessionRuntimeFactory>().Create(snapshot, new QuietOutput());
            await runtime.AttachAsync();
            Assert.True(await runtime.SubmitUserTextAsync("greet me every 60 seconds"));
            await runtime.WaitUntilIdleAsync().WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Single(await s.GetRequiredService<ITriggerStore>().ListAsync(new(instanceId, LocalUserProfile.Id), null));
        }
        await using var reopened = new ExperienceHost(path, model, clock, configure: services => ConfigureArtifacts(services, path));
        var services = reopened.Services;
        var owner = new AgentRunOwner(instanceId, LocalUserProfile.Id);
        var triggers = services.GetRequiredService<ITriggerStore>();
        var authoring = services.GetRequiredService<AdminAutomationAuthoringService>();
        var liveHost = services.GetRequiredService<SessionHost>();
        const string connection = "reliability-schedule-owner";
        for (var slot = 0; slot < 3; slot++)
        {
            clock.Advance(TimeSpan.FromSeconds(60));
            for (var pass = 0; pass < 2; pass++)
            {
                await services.GetRequiredService<TriggerScheduler>().RunOnceAsync(clock.GetUtcNow());
                await Intake(services);
                await services.ExecuteRunsAsync();
            }
            if (slot < 2)
            {
                Assert.True((await liveHost.AttachAsync(connection, new ClientCommand<AttachPayload>
                {
                    ProtocolVersion = 1, SessionId = sessionId.ToString(), EventId = Guid.NewGuid().ToString(),
                    Sequence = 0, Timestamp = clock.GetUtcNow().ToString("o"), Type = "session.attach",
                    Payload = new AttachPayload { OwnerCapability = TestOwnerCapability.Token(services) }
                }, CancellationToken.None)).Accepted);
                // Conversation and occurrences must use the same managed Runtime.
                // A standalone factory Runtime races the SessionHost's durable history.
                Assert.True((await liveHost.SendTextAsync(connection, new ClientCommand<UserTextPayload>
                {
                    ProtocolVersion = 1, SessionId = sessionId.ToString(), EventId = Guid.NewGuid().ToString(),
                    AttachmentId = liveHost.LiveAttachmentId(sessionId)!.Value.ToString(), Sequence = slot + 1,
                    Timestamp = clock.GetUtcNow().ToString("o"), Type = "user.text",
                    Payload = new UserTextPayload { Text = slot == 0 ? ConflictingChat : UnrelatedChat }
                }, CancellationToken.None)).Accepted);
                // This host disables the background dispatcher; drive accepted input
                // after detach through the fixture's normal detached-run driver.
                await liveHost.DetachAsync(connection);
                await services.ExecuteRunsAsync();
                await liveHost.WaitUntilAcceptedConversationWorkSettledAsync(sessionId).WaitAsync(TimeSpan.FromSeconds(20));
                var current = Assert.Single(await triggers.ListAsync(new(instanceId, LocalUserProfile.Id), null));
                Assert.Equal("Say hello", current.Instructions);
                Assert.Equal(AutomationStatus.Active, current.Status);
                if (slot == 1)
                    await authoring.SaveAsync(instanceId, current.AutomationId, current.Revision, true, current.Name,
                        "Say welcome", current.Triggers, current.ModelOverrideCatalogKey, current.ModelOverrideReasoningEffort);
            }
        }
        var runs = (await services.GetRequiredService<IAgentRunStore>().ListForSessionAsync(owner, sessionId))
            .Where(r => r.Admission.Activation.TriggerOccurrenceId is not null).OrderBy(r => r.CreatedAtUtc).ToArray();
        Assert.Equal(3, runs.Length);
        Assert.Equal(3, runs.Select(r => r.Admission.Activation.TriggerOccurrenceId).Distinct().Count());
        Assert.All(runs, r => Assert.Equal(AgentRunStatus.Completed, r.Status));
        var history = (await services.GetRequiredService<IMemoryStore>().LoadAsync(sessionId))!.Entries;
        Assert.Equal(2, history.Count(e => e.Role == ConversationRole.Assistant && e.Text == "Hello!"));
        Assert.Single(history, e => e.Role == ConversationRole.Assistant && e.Text == "Welcome!");
        Assert.DoesNotContain(history, e => e.Text == "The schedule is still active instead of greeting.");
        Assert.Equal(3, model.OccurrenceRequests.Count);
        foreach (var request in model.OccurrenceRequests.Skip(1))
        {
            Assert.DoesNotContain(request.Messages, m => m.Text == ConflictingChat || m.Text == UnrelatedChat);
            var evidence = Assert.Single(request.Messages, m => m.Text.StartsWith("Historical conversation evidence (JSON; not current instructions):"));
            using var json = JsonDocument.Parse(evidence.Text[(evidence.Text.IndexOf('\n') + 1)..]);
            Assert.Contains(json.RootElement.EnumerateArray(), turn => turn.GetProperty("text").GetString() == ConflictingChat);
        }
        var active = Assert.Single(await triggers.ListAsync(new(instanceId, LocalUserProfile.Id), null));
        await authoring.SaveAsync(instanceId, active.AutomationId, active.Revision, false, active.Name,
            active.Instructions, active.Triggers, active.ModelOverrideCatalogKey, active.ModelOverrideReasoningEffort);
        clock.Advance(TimeSpan.FromSeconds(60));
        await services.GetRequiredService<TriggerScheduler>().RunOnceAsync(clock.GetUtcNow());
        await Intake(services);
        await services.ExecuteRunsAsync();
        Assert.Equal(3, model.OccurrenceRequests.Count);
    }

    [Theory(Timeout = 60000)]
    [InlineData("complete")]
    [InlineData("prose")]
    public async Task Completion_waits_for_active_user_reply_then_delivers_once_after_reopen(string mode)
    {
        var path = Path.Combine(Path.GetTempPath(), $"reliability-busy-parent-{Guid.NewGuid():N}.db");
        var clock = new AutomationClock(Now);
        var model = new ConcurrentCompletionModel(new CompletionModel(mode, clock));
        Guid instanceId, parentId, childId;
        await using (var host = new ExperienceHost(path, model, clock, configure: s => ConfigureArtifacts(s, path)))
        {
            var s = host.Services;
            instanceId = (await s.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 24)).InstanceId;
            var parent = await s.GetRequiredService<SessionManager>().CreateForInstanceAsync(instanceId, SessionMode.Text);
            parentId = parent.SessionId;
            var owner = new AgentRunOwner(instanceId, LocalUserProfile.Id);
            var store = s.GetRequiredService<IAgentRunStore>();
            await using (var runtime = s.GetRequiredService<SessionRuntimeFactory>().Create(parent, new QuietOutput()))
            {
                await runtime.AttachAsync();
                Assert.True(await runtime.SubmitUserTextAsync("Hold the foreground reply"));
                await model.ParentStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                try
                {
                    var automation = await s.GetRequiredService<AdminAutomationAuthoringService>().SaveAsync(instanceId, null, 0, true,
                        "Fixture", "Save a verified fixture and report its result", new ScheduleTrigger(new FixedIntervalSchedule(60, Now.AddSeconds(60))),
                        null, null, executionTarget: AutomationExecutionTarget.Background, completionDelivery: AutomationCompletionDelivery.ToSession(parentId), requiresTools: true);
                    await s.GetRequiredService<AdminAutomationAuthoringService>().RunNowAsync(instanceId, automation.AutomationId, automation.Revision);
                    await Intake(s);
                    await s.ExecuteRunsAsync();
                    var child = Assert.Single(await store.ListAsync(owner, 100), r => r.Admission.OutputContract == AgentRunOutputContract.BackgroundOutcome);
                    childId = child.AgentRunId;
                    Assert.True(child.IsTerminal);
                    await s.GetRequiredService<BackgroundCompletionReporter>().ReportPendingAsync(100);
                    Assert.Equal("pending", (await store.GetCompletionDeliveryAsync(owner, childId)).Status);
                    Assert.DoesNotContain(await store.ListForSessionAsync(owner, parentId), r => r.Admission.Activation.Kind == ActivationKind.BackgroundCompleted);
                    Assert.Equal(EntryStatus.Streaming, runtime.Snapshot.Entries.Last(e => e.Role == ConversationRole.Assistant).Status);
                }
                finally { model.ReleaseParent.TrySetResult(); }
                await runtime.WaitUntilIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(EntryStatus.Completed, runtime.Snapshot.Entries.Last(e => e.Role == ConversationRole.Assistant).Status);
            }
            await s.GetRequiredService<BackgroundCompletionReporter>().ReportPendingAsync(100);
            await s.ExecuteRunsAsync();
            await s.GetRequiredService<BackgroundCompletionReporter>().ReportPendingAsync(100);
            Assert.Equal("delivered", (await store.GetCompletionDeliveryAsync(owner, childId)).Status);
            Assert.Equal(1, model.ParentRequests);
        }
        await using var reopened = new ExperienceHost(path, model, clock, configure: s => ConfigureArtifacts(s, path));
        await reopened.Services.ExecuteRunsAsync();
        var runStore = reopened.Services.GetRequiredService<IAgentRunStore>();
        var history = (await reopened.Services.GetRequiredService<IMemoryStore>().LoadAsync(parentId))!;
        Assert.Single(history.Entries, e => e.Role == ConversationRole.Assistant && e.Text == "Foreground reply finished.");
        Assert.Single(history.Entries, e => e.Role == ConversationRole.Assistant && e.Text.StartsWith("Background work", StringComparison.Ordinal));
        Assert.Single(await runStore.ListForSessionAsync(new(instanceId, LocalUserProfile.Id), parentId), r => r.Admission.Activation.Kind == ActivationKind.BackgroundCompleted);
        var source = (await runStore.GetAsync(new(instanceId, LocalUserProfile.Id), childId))!;
        Assert.Equal(mode == "complete" ? AgentRunStatus.Completed : AgentRunStatus.Failed, source.Status);
        Assert.Single(await reopened.Services.GetRequiredService<IArtifactStore>().ListAsync(source.SessionId));
        Assert.Equal(1, model.ParentRequests);
    }

    [Theory(Timeout = 60000)]
    [InlineData("don't greet me every 60 seconds", false)]
    [InlineData("can you greet me every 60 seconds?", false)]
    [InlineData("yesterday I asked you to greet me every 60 seconds", false)]
    [InlineData("the kettle is blue", false)]
    [InlineData("don't say hello in 15s", false)]
    [InlineData("do not schedule it for every 60s", true)]
    [InlineData("delete it", true)]
    public async Task Negation_and_unrelated_followups_cannot_authorize_a_schedule(string followup, bool draft)
    {
        var path = Path.Combine(Path.GetTempPath(), $"reliability-denied-{Guid.NewGuid():N}.db");
        await using var host = new ExperienceHost(path, new SchedulingModel(), new AutomationClock(Now), configure: services =>
        {
            services.RemoveAll<ITriggerCommandAuthorizer>();
            services.AddSingleton<ITriggerCommandAuthorizer>(new ModelTriggerCommandAuthorizer(new UnavailableClassifier(), new()));
        });
        var s = host.Services;
        var instance = await s.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 24);
        var snapshot = await s.GetRequiredService<SessionManager>().CreateForInstanceAsync(instance.InstanceId, SessionMode.Text);
        await using var runtime = s.GetRequiredService<SessionRuntimeFactory>().Create(snapshot, new QuietOutput());
        await runtime.AttachAsync();
        foreach (var text in draft ? new[] { "every 30s, wake me up", followup, "60 seconds" } : new[] { followup })
        {
            Assert.True(await runtime.SubmitUserTextAsync(text));
            await runtime.WaitUntilIdleAsync().WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Empty(await s.GetRequiredService<ITriggerStore>().ListAsync(new(instance.InstanceId, LocalUserProfile.Id), null));
        }
    }

    [Theory(Timeout = 60000)]
    [InlineData("greet me every 60 seconds", null, 60)]
    [InlineData("every 60 seconds, greet me", null, 60)]
    [InlineData("please greet me every minute", null, 60)]
    [InlineData("say hello to me every 60s", null, 60)]
    [InlineData("say hello in 15s", null, 15)]
    [InlineData("every 60s, wake me up", null, 60)]
    [InlineData("every 30s, wake me up", "okay so 60s", 60)]
    [InlineData("every 30s, wake me up", "60 seconds", 60)]
    [InlineData("every 30s, wake me up", "1 minute", 60)]
    public async Task Chat_schedule_persists_executes_and_delivers_once_after_reopen(string request, string? correction, int seconds)
    {
        var path = Path.Combine(Path.GetTempPath(), $"reliability-schedule-{Guid.NewGuid():N}.db");
        var clock = new AutomationClock(Now);
        var model = new SchedulingModel();
        var classifier = new ForbiddenClassifier();
        Guid instanceId, sessionId;
        await using (var host = new ExperienceHost(path, model, clock, configure: services =>
        {
            services.RemoveAll<ITriggerCommandAuthorizer>();
            services.AddSingleton<ITriggerCommandAuthorizer>(new ModelTriggerCommandAuthorizer(classifier, new()));
        }))
        {
            var s = host.Services;
            instanceId = (await s.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 24)).InstanceId;
            var snapshot = await s.GetRequiredService<SessionManager>().CreateForInstanceAsync(instanceId, SessionMode.Text);
            sessionId = snapshot.SessionId;
            await using var runtime = s.GetRequiredService<SessionRuntimeFactory>().Create(snapshot, new QuietOutput());
            await runtime.AttachAsync();
            Assert.True(await runtime.SubmitUserTextAsync(request));
            await runtime.WaitUntilIdleAsync().WaitAsync(TimeSpan.FromSeconds(20));
            if (correction is not null)
            {
                Assert.Empty(await s.GetRequiredService<ITriggerStore>().ListAsync(new(instanceId, LocalUserProfile.Id), null));
                Assert.True(await runtime.SubmitUserTextAsync(correction));
                await runtime.WaitUntilIdleAsync().WaitAsync(TimeSpan.FromSeconds(20));
            }
            var automation = Assert.Single(await s.GetRequiredService<ITriggerStore>().ListAsync(new(instanceId, LocalUserProfile.Id), null));
            Assert.Equal(request.Contains("greet") || request.Contains("hello") ? "Say hello" : "Wake me up", automation.Instructions);
            Assert.Equal(AutomationExecutionTarget.Existing(sessionId), automation.ExecutionTarget);
            Assert.Equal(seconds, automation.Schedule is FixedIntervalSchedule recurring ? recurring.IntervalSeconds
                : (int)(Assert.IsType<OneShotSchedule>(automation.Schedule).AtUtc - Now).TotalSeconds);
        }
        clock.Advance(TimeSpan.FromSeconds(seconds));
        await using var reopened = new ExperienceHost(path, model, clock, configure: services => ConfigureArtifacts(services, path));
        var services = reopened.Services;
        await services.GetRequiredService<TriggerScheduler>().RunOnceAsync(clock.GetUtcNow());
        await Intake(services);
        await services.ExecuteRunsAsync();
        await services.GetRequiredService<TriggerScheduler>().RunOnceAsync(clock.GetUtcNow());
        await Intake(services);
        await services.ExecuteRunsAsync();
        var runs = await services.GetRequiredService<IAgentRunStore>().ListForSessionAsync(new(instanceId, LocalUserProfile.Id), sessionId);
        var occurrence = Assert.Single(runs, r => r.Admission.Activation.TriggerOccurrenceId is not null);
        Assert.Equal(AgentRunStatus.Completed, occurrence.Status);
        var history = (await services.GetRequiredService<IMemoryStore>().LoadAsync(sessionId))!.Entries;
        Assert.Single(history, e => e.Role == ConversationRole.Assistant && e.Text == "Hello!");
        Assert.Equal(0, classifier.Calls);
        if (request.Contains("greet") || request.Contains("every 60s") && request.Contains("hello"))
        {
            clock.Advance(TimeSpan.FromSeconds(seconds));
            for (var pass = 0; pass < 2; pass++)
            {
                await services.GetRequiredService<TriggerScheduler>().RunOnceAsync(clock.GetUtcNow());
                await Intake(services);
                await services.ExecuteRunsAsync();
            }
            var scheduledRuns = (await services.GetRequiredService<IAgentRunStore>()
                .ListForSessionAsync(new(instanceId, LocalUserProfile.Id), sessionId))
                .Where(r => r.Admission.Activation.TriggerOccurrenceId is not null).ToArray();
            Assert.Equal(2, scheduledRuns.Length);
            Assert.Equal(2, scheduledRuns.Select(r => r.Admission.Activation.TriggerOccurrenceId).Distinct().Count());
            Assert.All(scheduledRuns, r => Assert.Equal(AgentRunStatus.Completed, r.Status));
            Assert.Equal(2, (await services.GetRequiredService<IMemoryStore>().LoadAsync(sessionId))!.Entries
                .Count(e => e.Role == ConversationRole.Assistant && e.Text == "Hello!"));
        }
    }

    [Theory(Timeout = 60000)]
    [InlineData("maybe greet me every minute", "unavailable", "authorization_classifier_unavailable")]
    [InlineData("maybe greet me every minute", "invalid", "authorization_classifier_invalid_output")]
    [InlineData("maybe greet me every minute", "ambiguous", "authorization_ambiguous")]
    [InlineData("maybe greet me every minute", "deny", "authorization_denied")]
    [InlineData("create an automation to greet me every minute if I return", "ambiguous", "authorization_ambiguous")]
    [InlineData("if I return, create an automation to greet me every minute", "unavailable", "authorization_classifier_unavailable")]
    [InlineData("create an automation if I return", "ambiguous", "authorization_ambiguous")]
    [InlineData("we used to create an automation every minute", "deny", "authorization_denied")]
    [InlineData("we used to create an automation", "deny", "authorization_denied")]
    [InlineData("maybe greet me every minute", "conflicting", "authorization_classifier_invalid_output")]
    public async Task Ambiguous_schedule_diagnostics_are_distinct_and_never_create(string request, string mode, string expected)
    {
        var path = Path.Combine(Path.GetTempPath(), $"reliability-classifier-{Guid.NewGuid():N}.db");
        var model = new SchedulingModel();
        await using var host = new ExperienceHost(path, model, new AutomationClock(Now), configure: services =>
        {
            services.RemoveAll<ITriggerCommandAuthorizer>();
            services.AddSingleton<ITriggerCommandAuthorizer>(new ModelTriggerCommandAuthorizer(new ClassifierModel(mode), new()));
        });
        var s = host.Services;
        var instance = await s.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 24);
        var snapshot = await s.GetRequiredService<SessionManager>().CreateForInstanceAsync(instance.InstanceId, SessionMode.Text);
        await using var runtime = s.GetRequiredService<SessionRuntimeFactory>().Create(snapshot, new QuietOutput());
        await runtime.AttachAsync();
        Assert.True(await runtime.SubmitUserTextAsync(request));
        await runtime.WaitUntilIdleAsync().WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Empty(await s.GetRequiredService<ITriggerStore>().ListAsync(new(instance.InstanceId, LocalUserProfile.Id), null));
        Assert.Contains(model.ScheduleResults, text => text.Contains("\"error\":\"" + expected + "\"", StringComparison.Ordinal));
    }

    private sealed class ClassifierModel(string mode) : ILanguageModel
    {
        public ModelCapabilities Capabilities { get; } = new(true, true);
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            if (mode == "unavailable") yield return new ModelFailed(new(ProviderErrorCode.Unavailable, "Classifier unavailable."));
            else
            {
                yield return new ModelTextDelta(mode switch
                {
                    "invalid" => "{\"decision\":42}",
                    "conflicting" => "{\"decision\":\"deny\",\"decision\":\"allow\"}",
                    _ => "{\"decision\":\"" + mode + "\"}"
                });
                yield return new ModelCompleted(ModelStopReason.Completed);
            }
        }
    }

    [Theory(Timeout = 60000)]
    [InlineData("complete", AgentRunStatus.Completed)]
    [InlineData("prose", AgentRunStatus.Failed)]
    [InlineData("wrong-tool", AgentRunStatus.Failed)]
    [InlineData("transport", AgentRunStatus.Completed)]
    [InlineData("semantic", AgentRunStatus.Failed)]
    [InlineData("incomplete-tool", AgentRunStatus.Failed)]
    [InlineData("authentication", AgentRunStatus.Failed)]
    [InlineData("reserve", AgentRunStatus.Completed)]
    public async Task Background_completion_recovery_keeps_artifact_and_never_replays_work(string mode, AgentRunStatus expected)
    {
        var path = Path.Combine(Path.GetTempPath(), $"reliability-background-{Guid.NewGuid():N}.db");
        var clock = new AutomationClock(Now);
        var model = new CompletionModel(mode, clock);
        Guid instanceId, runId, childSessionId;
        await using (var host = new ExperienceHost(path, model, clock, configure: services => ConfigureArtifacts(services, path)))
        {
            var s = host.Services;
            instanceId = (await s.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 24)).InstanceId;
            var a = await s.GetRequiredService<AdminAutomationAuthoringService>().SaveAsync(instanceId, null, 0, true,
                "Fixture", "Save a verified fixture and report its result", new ScheduleTrigger(new FixedIntervalSchedule(60, Now.AddSeconds(60))),
                null, null, executionTarget: AutomationExecutionTarget.Background, requiresTools: true);
            await s.GetRequiredService<AdminAutomationAuthoringService>().RunNowAsync(instanceId, a.AutomationId, a.Revision);
            await Intake(s);
            await s.ExecuteRunsAsync();
            var run = Assert.Single(await s.GetRequiredService<IAgentRunStore>().ListAsync(new(instanceId, LocalUserProfile.Id), 100));
            runId = run.AgentRunId; childSessionId = run.SessionId;
            Assert.True(run.Status == (mode == "transport" ? AgentRunStatus.WaitingToRetry : expected),
                $"Status={run.Status}; Failure={run.Failure?.Code}; Summary={run.Failure?.Summary}; Budget={run.Admission.ExecutionBudget}; Requests={model.RecoveryRequests.Count}");
            Assert.Contains("Core finalization phase: " + (mode == "reserve" ? "runDeadline" : "completionRequired"), run.Checkpoint!.PayloadJson);
        }
        clock.Advance(TimeSpan.FromSeconds(10));
        await using var reopened = new ExperienceHost(path, model, clock, configure: services => ConfigureArtifacts(services, path));
        await reopened.Services.ExecuteRunsAsync();
        var retained = (await reopened.Services.GetRequiredService<IAgentRunStore>().GetAsync(new(instanceId, LocalUserProfile.Id), runId))!;
        Assert.Equal(expected, retained.Status);
        Assert.Equal(mode == "transport" ? 2 : 1, retained.AttemptCount);
        Assert.Equal(1, model.Writes);
        Assert.Equal(1, model.Artifacts);
        var artifacts = await reopened.Services.GetRequiredService<IArtifactStore>().ListAsync(childSessionId);
        var files = await reopened.Services.GetRequiredService<SessionManager>().ListWorkspaceAsync(childSessionId, "/working");
        Assert.DoesNotContain(files, file => file.LogicalPath.EndsWith("/replay.txt", StringComparison.Ordinal));
        Assert.True(artifacts.Count == 1, string.Join("\n", model.RecoveryRequests.First().Messages.Where(m => m.Role == ModelRole.Tool).Select(m => m.Name + ": " + m.Text)));
        if (mode == "prose") Assert.Equal("completion-required", retained.Failure!.Code);
        if (mode == "semantic") Assert.Equal("semantic-response-invalid", retained.Failure!.Code);
        if (mode == "incomplete-tool") Assert.Equal("tool-call-invalid", retained.Failure!.Code);
        if (mode == "authentication") Assert.Equal("provider-authentication", retained.Failure!.Code);
        var diagnostics = RunDiagnosticProjection.From(retained)!;
        if (mode == "transport") Assert.Equal("Unavailable", Assert.Single(diagnostics.FailedAttempts).Code);
        if (expected == AgentRunStatus.Failed)
        {
            Assert.Single(diagnostics.FailedAttempts);
            var history = (await reopened.Services.GetRequiredService<IMemoryStore>().LoadAsync(childSessionId))!;
            Assert.Equal(retained.Failure!.DiagnosticId, history.Entries.Last(e => e.Role == ConversationRole.Assistant).Failure!.DiagnosticId);
        }
        foreach (var recovery in model.RecoveryRequests)
        {
            Assert.Equal(ToolCatalog.WorkComplete, Assert.Single(recovery.Tools!).Name);
            Assert.Equal(ModelToolChoice.Named, recovery.ToolChoice);
            Assert.Null(recovery.ResponseContract);
        }
        Assert.NotEmpty(model.RecoveryRequests);
    }

    private static void ConfigureArtifacts(IServiceCollection services, string path)
    {
        services.RemoveAll<IArtifactStore>();
        services.AddSingleton<IArtifactStore>(sp => new AgentCore.Infrastructure.Persistence.SqliteArtifactStore(
            sp.GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<AgentCore.Infrastructure.Persistence.AgentCoreDbContext>>(),
            sp.GetRequiredService<TimeProvider>(), path + ".artifacts"));
    }

    private static async Task Intake(IServiceProvider services)
    {
        await services.GetRequiredService<TriggerOccurrenceRouter>().RouteOnceAsync();
        await services.GetRequiredService<BackgroundOccurrenceIntake>().AcceptAwaitingAsync();
    }

    private sealed class AutomationClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset current = now;
        public override DateTimeOffset GetUtcNow() => current;
        public void Advance(TimeSpan elapsed) => current += elapsed;
    }

    private sealed class QuietOutput : ISessionOutput
    {
        public ValueTask PublishAsync(SessionOutput item, CancellationToken ct = default) => ValueTask.CompletedTask;
    }

    private sealed class ConcurrentCompletionModel(ILanguageModel background) : ILanguageModel
    {
        public ModelCapabilities Capabilities => background.Capabilities;
        public TaskCompletionSource ParentStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseParent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ParentRequests { get; private set; }
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            if (request.Messages.LastOrDefault(m => m.Role == ModelRole.User)?.Text == "Hold the foreground reply")
            {
                ParentRequests++;
                ParentStarted.TrySetResult();
                await ReleaseParent.Task.WaitAsync(ct);
                yield return new ModelDisplayDelta("Foreground reply finished.");
                yield return new ModelSemanticResponseReady(new("Foreground reply finished.", new(ModelSpeechMode.None, null), []));
                yield return new ModelCompleted(ModelStopReason.Completed);
            }
            else await foreach (var item in background.GenerateAsync(request, ct)) yield return item;
        }
    }

    private sealed class ForbiddenClassifier : ILanguageModel
    {
        public int Calls { get; private set; }
        public ModelCapabilities Capabilities { get; } = new(true, true);
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            Calls++;
            throw new InvalidOperationException("Clear scheduling must not call the classifier.");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }
    }

    private sealed class UnavailableClassifier : ILanguageModel
    {
        public ModelCapabilities Capabilities { get; } = new(true, true);
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield return new ModelFailed(new(ProviderErrorCode.Unavailable, "Classifier unavailable."));
        }
    }

    private sealed class SchedulingModel : ILanguageModel
    {
        public List<string> ScheduleResults { get; } = [];
        public List<ModelRequest> OccurrenceRequests { get; } = [];
        public ModelCapabilities Capabilities { get; } = new(true, true, Tools: true, StructuredOutput: true);
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            var user = request.Messages.LastOrDefault(m => m.Role == ModelRole.User)?.Text ?? "";
            var scheduled = request.Messages.Any(m => m.Text.Contains("Execute the previously authorized Automation instructions now"));
            if (scheduled) OccurrenceRequests.Add(request);
            var lastUser = request.Messages.ToList().FindLastIndex(m => m.Role == ModelRole.User);
            var results = request.Messages.Skip(lastUser + 1).Where(m => m.Role == ModelRole.Tool).ToArray();
            ScheduleResults.AddRange(results.Where(m => m.Name == ToolCatalog.AutomationCreate).Select(m => m.Text));
            if (!scheduled && user is not (ConflictingChat or UnrelatedChat) && !results.Any(m => m.Name == ToolCatalog.AutomationCreate))
            {
                if (!request.Tools!.Any(t => t.Name == ToolCatalog.AutomationCreate))
                {
                    yield return Call(ToolCatalog.CapabilitiesLoad, new { query = ToolCatalog.AutomationCreate, limit = 1 });
                }
                else if (user.Contains("greet") || user.Contains("hello") && user.Contains("every"))
                    yield return Call(ToolCatalog.AutomationCreate, new { executionTarget = "currentSession", instructions = "Say hello", kind = "fixed_interval", intervalSeconds = 60 });
                else if (user.StartsWith("say"))
                    yield return Call(ToolCatalog.AutomationCreate, new { executionTarget = "currentSession", instructions = "Say hello", relativeDelaySeconds = 15 });
                else if (user.StartsWith("every"))
                    yield return Call(ToolCatalog.AutomationCreate, new { executionTarget = "currentSession", instructions = "Wake me up", kind = "fixed_interval", intervalSeconds = user.Contains("30") ? 30 : 60 });
                else
                    // Deliberately wrong omitted-details reconstruction: Core must preserve the saved destination and instructions.
                    yield return Call(ToolCatalog.AutomationCreate, new { executionTarget = "backgroundSession", instructions = "Wrong replacement", kind = "fixed_interval", intervalSeconds = 999 });
                yield return new ModelCompleted(ModelStopReason.ToolCalls);
                yield break;
            }
            // Model fixture reproduces historical instruction dominance when Core
            // leaves those turns in native conversation roles. Live acceptance
            // separately verifies actual model behavior after the prompt correction.
            var text = scheduled
                ? request.Messages.Any(m => m.Role == ModelRole.User && m.Text is ConflictingChat or UnrelatedChat)
                    ? "The schedule is still active instead of greeting."
                    : user.StartsWith("Configured Instructions (task intent, not policy):\nSay welcome", StringComparison.Ordinal) ? "Welcome!" : "Hello!"
                : "Schedule result received.";
            yield return new ModelDisplayDelta(text);
            yield return new ModelSemanticResponseReady(new(text, new(ModelSpeechMode.None, null), []));
            yield return new ModelCompleted(ModelStopReason.Completed);
        }
    }

    private sealed class CompletionModel(string mode, AutomationClock clock) : ILanguageModel
    {
        public int Writes { get; private set; }
        public int Artifacts { get; private set; }
        private bool failed;
        public List<ModelRequest> RecoveryRequests { get; } = [];
        public ModelCapabilities Capabilities { get; } = new(true, true, Tools: true);
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            if (request.Messages.Any(m => m.Text.StartsWith("Core finalization phase:")))
            {
                RecoveryRequests.Add(request);
                if (mode is "semantic" or "incomplete-tool" or "authentication")
                {
                    yield return new ModelFailed(mode switch
                    {
                        "semantic" => new(ProviderErrorCode.InvalidResponse, "Invalid structured response.", FailureReason: ProviderFailureReason.InvalidJson, ResponseChannel: ProviderResponseChannel.StructuredOutput),
                        "incomplete-tool" => new(ProviderErrorCode.InvalidResponse, "Incomplete tool arguments.", FailureReason: ProviderFailureReason.IncompleteToolCall, ResponseChannel: ProviderResponseChannel.ToolCall),
                        _ => new(ProviderErrorCode.Authentication, "Provider authentication failed.")
                    });
                    yield break;
                }
                if (mode == "transport" && !failed)
                {
                    failed = true;
                    yield return new ModelFailed(new(ProviderErrorCode.Unavailable, "Provider unavailable."));
                    yield break;
                }
                if (mode == "prose")
                {
                    yield return new ModelTextDelta("This prose must never become a successful outcome.");
                    yield return new ModelCompleted(ModelStopReason.Completed);
                    yield break;
                }
                yield return mode == "wrong-tool" ? Call(ToolCatalog.WorkspaceWrite, new { path = "/working/replay.txt", content = "must not execute" })
                    : Call(ToolCatalog.WorkComplete, new { summary = "Verified fixture saved.", attentionRequired = false, outcome = "Response" });
                yield return new ModelCompleted(ModelStopReason.ToolCalls);
                yield break;
            }
            var tool = !request.Messages.Any(m => m.Role == ModelRole.Tool && m.Name == ToolCatalog.WorkspaceWrite) ? ToolCatalog.WorkspaceWrite
                : !request.Messages.Any(m => m.Role == ModelRole.Tool && m.Name == ToolCatalog.ArtifactsCreateFromWorkspace) ? ToolCatalog.ArtifactsCreateFromWorkspace : null;
            if (tool is null)
            {
                if (mode == "reserve")
                {
                    // Enter the pinned completion reserve with an unexecuted substantive call.
                    clock.Advance(TimeSpan.FromSeconds(190));
                    yield return Call(ToolCatalog.WorkspaceWrite, new { path = "/working/replay.txt", content = "must not execute" });
                    yield return new ModelCompleted(ModelStopReason.ToolCalls);
                    yield break;
                }
                yield return new ModelTextDelta("Verified fixture saved.");
                yield return new ModelCompleted(ModelStopReason.Completed);
                yield break;
            }
            if (!request.Tools!.Any(t => t.Name == tool)) yield return Call(ToolCatalog.CapabilitiesLoad, new { query = tool, limit = 1 });
            else if (tool == ToolCatalog.WorkspaceWrite) { Writes++; yield return Call(tool, new { path = "/working/verified.txt", content = "verified fixture" }); }
            else { Artifacts++; yield return Call(tool, new { path = "/working/verified.txt", displayName = "verified.txt" }); }
            yield return new ModelCompleted(ModelStopReason.ToolCalls);
        }
    }

    private static ModelToolCallEvent Call(string name, object args) => new(new(Guid.NewGuid().ToString("N"), name, JsonSerializer.Serialize(args)));
}
