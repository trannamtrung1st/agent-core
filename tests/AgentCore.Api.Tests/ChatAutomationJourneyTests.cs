using AgentCore.Application.Execution;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AgentCore.Application.Admin;
using AgentCore.Application.Experience;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Tools;
using AgentCore.Application.Triggers;
using AgentCore.Application.Work;
using AgentCore.Contracts.Realtime;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Triggers;
using AgentCore.Infrastructure.Providers.Synthetic;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentCore.Api.Tests;

public sealed class ChatAutomationJourneyTests
{
    private const string UserRequest = "Every hour, review recent experience and consolidate it when useful.";
    private const string Instructions = "Review recent experience and consolidate it when useful.";

    [Theory(Timeout = 60000)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Chat_authored_recurring_action_runs_on_schedule_with_current_consolidation_permission(bool authorizedAtRun)
    {
        var clock = new AutomationClock(DateTimeOffset.UtcNow);
        var model = new RecurringActionModel();
        var db = Path.Combine(Path.GetTempPath(), $"chat-automation-{Guid.NewGuid():N}.db");
        await using var host = new ExperienceHost(db, languageModel: model, clock: clock, configure: services =>
        {
            services.RemoveAll<ILanguageModelResolver>();
            services.AddSingleton<ILanguageModelResolver>(new StaticLanguageModelResolver(model));
        });
        var services = host.Services;
        var instanceId = (await services.GetRequiredService<AdminAgentInstanceService>()
            .CreateManagedAsync("general-assistant", 16)).InstanceId;
        var experience = services.GetRequiredService<IExperienceStore>();
        await experience.ConfigureAsync(instanceId, 0, true);
        await experience.ConfigureMaintenanceAsync(instanceId, 0, true);
        for (var i = 0; i < 3; i++)
        {
            var source = await ExperienceJourneyTests.SeedAsync(services, instanceId);
            await services.GetRequiredService<ExperienceService>().RequestSessionAsync(instanceId, source.SessionId);
        }
        var executor = services.GetRequiredService<AgentRunCoordinator>();
        Assert.Equal(3, await services.ExecuteRunsAsync(100));
        var sources = await experience.ListAsync(instanceId, 100);
        Assert.Equal(3, sources.Count);

        var session = await services.GetRequiredService<SessionManager>().CreateForInstanceAsync(instanceId, SessionMode.Text);
        await using (var hub = new HubConnectionBuilder().WithUrl(new Uri(host.Server.BaseAddress, "/hubs/session"), options =>
        {
            options.HttpMessageHandlerFactory = _ => host.Server.CreateHandler();
            options.Transports = HttpTransportType.LongPolling;
            TestOwnerCapability.Apply(options, services);
        }).AddMessagePackProtocol().Build())
        {
            var ready = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            hub.On<ServerEvent>("SessionEvent", e =>
            {
                if (e.Type == "session.ready") ready.TrySetResult(e.AttachmentId!);
                if (e.Type == "agent.response.completed") completed.TrySetResult();
            });
            ClientCommand<T> Command<T>(string type, long sequence, T payload, string? attachment = null) => new()
            {
                ProtocolVersion = 1, SessionId = session.SessionId.ToString(), EventId = Guid.NewGuid().ToString(),
                Timestamp = clock.GetUtcNow().ToString("o"), Sequence = sequence, Type = type,
                Payload = payload, AttachmentId = attachment
            };
            await hub.StartAsync();
            Assert.True((await hub.InvokeAsync<CommandAck>("Attach", Command("session.attach", 0,
                new AttachPayload { OwnerCapability = TestOwnerCapability.Token(services) }))).Accepted);
            var attachment = await ready.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.True((await hub.InvokeAsync<CommandAck>("SendText", Command("user.text", 1,
                new UserTextPayload { Text = UserRequest }, attachment))).Accepted);
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(20));
        }

        var registration = Assert.Single(await services.GetRequiredService<ITriggerStore>()
            .ListAsync(new(instanceId, LocalUserProfile.Id), null));
        Assert.Equal(Instructions, registration.Instructions);
        Assert.Equal(3600, Assert.IsType<FixedIntervalSchedule>(registration.Schedule).IntervalSeconds);
        Assert.Equal(TriggerAuthorizationOrigin.CurrentUserTurn, registration.Provenance.AuthorizationOrigin);
        Assert.Equal(session.SessionId, registration.Provenance.SourceSessionId);
        Assert.Equal(0, (await services.GetRequiredService<TriggerScheduler>().RunOnceAsync(clock.GetUtcNow())).Admitted);
        if (!authorizedAtRun) await experience.ConfigureMaintenanceAsync(instanceId, 1, false);

        clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(1, (await services.GetRequiredService<TriggerScheduler>().RunOnceAsync(clock.GetUtcNow())).Admitted);
        await AutomationJourneyTests.Intake(services);
        Assert.Equal(1, await services.ExecuteRunsAsync(100));
        var run = Assert.Single(await services.AutomationRunsAsync(new(instanceId, LocalUserProfile.Id), registration.AutomationId));
        Assert.Equal(ActivationKind.ScheduledWork, run.Admission.Activation.Kind);
        Assert.Contains(Instructions, (await services.SessionAsync(run)).Entries[0].Text);
        Assert.Equal(AgentRunStatus.Completed, run.Status);
        Assert.Equal(authorizedAtRun ? "Response" : "NoAction", run.Result!.OutcomeKind.ToString());
        Assert.False(run.Result.AttentionRequired);
        Assert.Contains(ToolCatalog.WorkComplete, model.Calls);
        var retained = await experience.ListAsync(instanceId, 100);
        if (authorizedAtRun)
        {
            Assert.Contains(ToolCatalog.ContinuitySearch, run.Checkpoint!.PayloadJson);
            Assert.Contains(ToolCatalog.ContinuityGet, run.Checkpoint.PayloadJson);
            Assert.Contains(ToolCatalog.ExperienceConsolidate, run.Checkpoint.PayloadJson);
            Assert.Equal(3, retained.Count(e => e.Visibility.ToString() == "Superseded"));
            var consolidated = Assert.Single(retained, e => e.DerivedFromExperienceIds?.Count == 3);
            Assert.Equal(sources.Select(e => e.ExperienceId).Order(), consolidated.DerivedFromExperienceIds!.Order());
        }
        else
        {
            Assert.DoesNotContain(ToolCatalog.ExperienceConsolidate, model.Calls);
            Assert.Equal(JsonSerializer.Serialize(sources.OrderBy(e => e.ExperienceId)),
                JsonSerializer.Serialize(retained.OrderBy(e => e.ExperienceId)));
        }
        Assert.False(run.Result?.AttentionRequired ?? false);
        var chat = Assert.Single(model.ChatRequests);
        Assert.Contains(chat.Messages, m => m.Role == ModelRole.System && m.Text.Contains("action-oriented Instructions"));
        Assert.Contains(chat.Messages, m => m.Role == ModelRole.System && m.Text.Contains("exact-action approvals still apply"));
        Assert.DoesNotContain(chat.Messages, m => m.Text.Contains("not perform the action each time"));
        Assert.Contains(model.RunRequests, r => r.Messages.Any(m => m.Role == ModelRole.User && m.Text.Contains(Instructions))
            && r.Tools!.Any(t => t.Name == ToolCatalog.ExperienceConsolidate) == authorizedAtRun);
    }

    private sealed class AutomationClock(DateTimeOffset initial) : TimeProvider
    {
        private DateTimeOffset now = initial;
        public override DateTimeOffset GetUtcNow() => now;
        internal void Advance(TimeSpan elapsed) => now += elapsed;
    }

    // Script only the model decisions. Chat authorization, scheduling, intake, tool policy,
    // mutations and completion use the real product services and temporary SQLite.
    private sealed class RecurringActionModel : ILanguageModel
    {
        private readonly ScriptedLanguageModel inner = new();
        internal ConcurrentQueue<ModelRequest> ChatRequests { get; } = new();
        internal ConcurrentQueue<ModelRequest> RunRequests { get; } = new();
        internal ConcurrentQueue<string> Calls { get; } = new();
        public ModelCapabilities Capabilities => inner.Capabilities;

        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var user = request.Messages.LastOrDefault(m => m.Role == ModelRole.User);
            if (user?.Text == UserRequest)
            {
                if (!request.Messages.Any(m => m.Role == ModelRole.Tool && m.Name == ToolCatalog.AutomationCreate))
                {
                    ChatRequests.Enqueue(request);
                    yield return new ModelToolCallEvent(new("create-recurring-action", ToolCatalog.AutomationCreate,
                        """{"kind":"fixed_interval","intervalSeconds":3600,"instructions":"Review recent experience and consolidate it when useful."}"""));
                    yield return new ModelCompleted(ModelStopReason.ToolCalls);
                }
                else
                {
                    yield return new ModelTextDelta("Scheduled the hourly review. Each run uses its current permissions.");
                    yield return new ModelCompleted(ModelStopReason.Completed);
                }
                yield break;
            }
            if (user is not null && user.Text.Contains(Instructions)
                && request.Messages.Any(m => m.Role == ModelRole.System && m.Text.StartsWith("Bounded background Session task.")))
            {
                RunRequests.Enqueue(request);
                // Reuse the deterministic semantic consolidation fixture without storing a test marker in Instructions.
                request = request with { Messages = request.Messages.Select(m => ReferenceEquals(m, user)
                    ? m with { Text = m.Text + " synthetic-maintain-experience" } : m).ToArray() };
            }
            await foreach (var e in inner.GenerateAsync(request, cancellationToken))
            {
                if (e is ModelToolCallEvent call) Calls.Enqueue(call.Call.Name);
                yield return e;
            }
        }
    }
}
