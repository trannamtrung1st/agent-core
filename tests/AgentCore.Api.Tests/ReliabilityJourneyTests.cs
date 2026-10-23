using AgentCore.Application.Context;
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
            services.AddSingleton<ITriggerCommandAuthorizer>(new ModelTriggerCommandAuthorizer(new DenyingClassifier()));
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
        var classifier = new AllowingClassifier();
        Guid instanceId, sessionId;
        await using (var host = new ExperienceHost(path, model, clock, configure: services =>
        {
            services.RemoveAll<ITriggerCommandAuthorizer>();
            services.AddSingleton<ITriggerCommandAuthorizer>(new ModelTriggerCommandAuthorizer(classifier));
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
        Assert.Equal(1, classifier.Calls);
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
            services.AddSingleton<ITriggerCommandAuthorizer>(new ModelTriggerCommandAuthorizer(new ClassifierModel(mode)));
        });
        var s = host.Services;
        var instance = await s.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 24);
        var snapshot = await s.GetRequiredService<SessionManager>().CreateForInstanceAsync(instance.InstanceId, SessionMode.Text);
        var output = new AgentCore.Application.Testing.CapturingSessionOutput();
        await using var runtime = s.GetRequiredService<SessionRuntimeFactory>().Create(snapshot, output);
        await runtime.AttachAsync();
        Assert.True(await runtime.SubmitUserTextAsync(request));
        await runtime.WaitUntilIdleAsync().WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Empty(await s.GetRequiredService<ITriggerStore>().ListAsync(new(instance.InstanceId, LocalUserProfile.Id), null));
        Assert.Contains(model.ScheduleResults, text => text.Contains("\"error\":\"" + (mode is "invalid" or "conflicting" or "unavailable" ? "forbidden" : expected) + "\"", StringComparison.Ordinal));
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

    private sealed class AllowingClassifier : ILanguageModel
    {
        public int Calls { get; private set; }
        public ModelCapabilities Capabilities { get; } = new(true, true);
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            Calls++;
            yield return new ModelTextDelta("{\"decision\":\"allow\"}");
            yield return new ModelCompleted(ModelStopReason.Completed);
        }
    }

    private sealed class DenyingClassifier : ILanguageModel
    {
        public ModelCapabilities Capabilities { get; } = new(true, true);
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield return new ModelTextDelta("{\"decision\":\"deny\"}");
            yield return new ModelCompleted(ModelStopReason.Completed);
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
            var user = request.Context?.Text(ContextSectionId.RunTask) is { Length: > 0 } task
                ? task : (request.Interaction ?? request.Messages).LastOrDefault(m => m.Role == ModelRole.User)?.Text ?? "";
            var scheduled = request.Context?.Sections.Any(section => section.Id == ContextSectionId.CoreAutomation) == true;
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
                    // Interpretation belongs to the agent fixture; Core never merges natural-language corrections.
                    yield return Call(ToolCatalog.AutomationCreate, new { executionTarget = "currentSession", instructions = "Wake me up", kind = "fixed_interval", intervalSeconds = 60 });
                yield return new ModelCompleted(ModelStopReason.ToolCalls);
                yield break;
            }
            // Model fixture reproduces historical instruction dominance when Core
            // leaves those turns in native conversation roles. Live acceptance
            // separately verifies actual model behavior after the prompt correction.
            var text = scheduled
                ? request.Messages.Any(m => m.Role == ModelRole.User && m.Text is ConflictingChat or UnrelatedChat)
                    ? "The schedule is still active instead of greeting."
                    : user == "Say welcome" ? "Welcome!" : "Hello!"
                : "Schedule result received.";
            yield return new ModelDisplayDelta(text);
            yield return new ModelSemanticResponseReady(new(text, new(ModelSpeechMode.None, null), []));
            yield return new ModelCompleted(ModelStopReason.Completed);
        }
    }


    private static ModelToolCallEvent Call(string name, object args) => new(new(Guid.NewGuid().ToString("N"), name, JsonSerializer.Serialize(args)));
}
