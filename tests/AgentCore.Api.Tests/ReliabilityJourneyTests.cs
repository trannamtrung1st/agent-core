using System.Runtime.CompilerServices;
using System.Text.Json;
using AgentCore.Application.Admin;
using AgentCore.Application.Execution;
using AgentCore.Application.Events;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Tools;
using AgentCore.Application.Triggers;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Triggers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentCore.Api.Tests;

public sealed class ReliabilityJourneyTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 0, 0, 0, TimeSpan.Zero);

    [Theory(Timeout = 60000)]
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
        Guid instanceId, sessionId;
        await using (var host = new ExperienceHost(path, model, clock, configure: services =>
        {
            services.RemoveAll<ITriggerCommandAuthorizer>();
            services.AddSingleton<ITriggerCommandAuthorizer>(new ModelTriggerCommandAuthorizer(new UnavailableClassifier(), new()));
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
            Assert.Equal(request.StartsWith("say") ? "Say hello" : "Wake me up", automation.Instructions);
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
        await Intake(services);
        await services.ExecuteRunsAsync();
        var runs = await services.GetRequiredService<IAgentRunStore>().ListForSessionAsync(new(instanceId, LocalUserProfile.Id), sessionId);
        var occurrence = Assert.Single(runs, r => r.Admission.Activation.TriggerOccurrenceId is not null);
        Assert.Equal(AgentRunStatus.Completed, occurrence.Status);
        var history = (await services.GetRequiredService<IMemoryStore>().LoadAsync(sessionId))!.Entries;
        Assert.Single(history, e => e.Role == ConversationRole.Assistant && e.Text == "Hello!");
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
        public ModelCapabilities Capabilities { get; } = new(true, true, Tools: true, StructuredOutput: true);
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            var user = request.Messages.LastOrDefault(m => m.Role == ModelRole.User)?.Text ?? "";
            var scheduled = request.Messages.Any(m => m.Text.Contains("Execute the previously authorized Automation instructions now"));
            var lastUser = request.Messages.ToList().FindLastIndex(m => m.Role == ModelRole.User);
            var results = request.Messages.Skip(lastUser + 1).Where(m => m.Role == ModelRole.Tool).ToArray();
            if (!scheduled && !results.Any(m => m.Name == ToolCatalog.AutomationCreate))
            {
                if (!request.Tools!.Any(t => t.Name == ToolCatalog.AutomationCreate))
                {
                    yield return Call(ToolCatalog.CapabilitiesLoad, new { query = ToolCatalog.AutomationCreate, limit = 1 });
                }
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
            var text = scheduled ? "Hello!" : "Schedule result received.";
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
