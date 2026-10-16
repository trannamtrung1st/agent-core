using System.Runtime.CompilerServices;
using System.Text.Json;
using AgentCore.Application.Admin;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Tools;
using AgentCore.Application.Triggers;
using AgentCore.Contracts.Realtime;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Triggers;
using AgentCore.Infrastructure.Providers.Synthetic;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentCore.Api.Tests;

public sealed class AutomationActivationJourneyTests
{
    [Theory(Timeout = 60000)]
    [InlineData(false, "enable the 60s hello schedule")]
    [InlineData(true, "enable the 60s hello schedule")]
    [InlineData(false, "enable the say hello to me 60s automation job again")]
    [InlineData(true, "enable the say hello to me 60s automation job again")]
    [InlineData(false, "enable my hello say 60s automation job again")]
    [InlineData(false, "please enable the automation job to say hello to me every 60 seconds again, please")]
    public async Task Exact_enable_request_lists_inspects_and_enables_with_exact_semantic_verification_without_duplicate(bool duplicate, string requestText)
    {
        var clock = new Clock(new DateTimeOffset(2026, 10, 12, 8, 0, 0, TimeSpan.Zero));
        var model = new WorkflowModel(requestText);
        var classifier = new AlignmentClassifier(duplicate);
        await using var host = new ExperienceHost(Path.Combine(Path.GetTempPath(), $"enable-chat-{Guid.NewGuid():N}.db"), model, clock,
            configure: services =>
            {
                services.RemoveAll<ILanguageModelResolver>();
                services.AddSingleton<ILanguageModelResolver>(new StaticLanguageModelResolver(model));
                services.RemoveAll<ITriggerCommandAuthorizer>();
                services.AddSingleton<ITriggerCommandAuthorizer>(new ModelTriggerCommandAuthorizer(classifier));
            });
        var services = host.Services;
        var instance = await services.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 25);
        var authoring = services.GetRequiredService<AdminAutomationAuthoringService>();
        var saved = await authoring.SaveAsync(instance.InstanceId, null, 0, false, "Say hello every 60s", "Say hello to me.",
            new ScheduleTrigger(new FixedIntervalSchedule(60, clock.GetUtcNow(), maxOccurrences: 3)), "scripted-alpha", null);
        if (duplicate)
            await authoring.SaveAsync(instance.InstanceId, null, 0, false, "Another hello", "Say hello to me.",
                new ScheduleTrigger(new FixedIntervalSchedule(60, clock.GetUtcNow(), maxOccurrences: 3)), "scripted-alpha", null);
        var session = await services.GetRequiredService<SessionManager>().CreateForInstanceAsync(instance.InstanceId, SessionMode.Text);
        await using var hub = new HubConnectionBuilder().WithUrl(new Uri(host.Server.BaseAddress, "/hubs/session"), options =>
        {
            options.HttpMessageHandlerFactory = _ => host.Server.CreateHandler();
            options.Transports = HttpTransportType.LongPolling;
            TestOwnerCapability.Apply(options, services);
        }).AddMessagePackProtocol().Build();
        var ready = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        hub.On<ServerEvent>("SessionEvent", frame =>
        {
            if (frame.Type == "session.ready") ready.TrySetResult(frame.AttachmentId!);
            if (frame.Type == "agent.response.completed") completed.TrySetResult();
        });
        ClientCommand<T> Command<T>(string type, long sequence, T payload, string? attachment = null) => new()
        {
            ProtocolVersion = 1, SessionId = session.SessionId.ToString(), EventId = Guid.NewGuid().ToString(),
            Timestamp = clock.GetUtcNow().ToString("o"), Sequence = sequence, Type = type, Payload = payload, AttachmentId = attachment
        };
        await hub.StartAsync();
        Assert.True((await hub.InvokeAsync<CommandAck>("Attach", Command("session.attach", 0,
            new AttachPayload { OwnerCapability = TestOwnerCapability.Token(services) }))).Accepted);
        Assert.True((await hub.InvokeAsync<CommandAck>("SendText", Command("user.text", 1,
            new UserTextPayload { Text = requestText }, await ready.Task.WaitAsync(TimeSpan.FromSeconds(20))))).Accepted);
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(1, classifier.Calls);
        Assert.Equal(new[] { ToolCatalog.AutomationList, ToolCatalog.AutomationInspect, ToolCatalog.AutomationUpdate }, model.Calls);
        var rows = await services.GetRequiredService<ITriggerStore>().ListAsync(new(instance.InstanceId, LocalUserProfile.Id), null);
        Assert.Equal(duplicate ? 2 : 1, rows.Count);
        var current = rows.Single(a => a.AutomationId == saved.AutomationId);
        Assert.Equal(saved.Instructions, current.Instructions);
        Assert.Equal(saved.Name, current.Name);
        Assert.Equal(saved.ExecutionTarget, current.ExecutionTarget);
        Assert.Equal(saved.CompletionDelivery, current.CompletionDelivery);
        Assert.Equal(saved.ModelOverrideCatalogKey, current.ModelOverrideCatalogKey);
        Assert.Equal(saved.Triggers.Single().TriggerId, current.Triggers.Single().TriggerId);
        Assert.Equal(saved.Triggers.Single().Revision, current.Triggers.Single().Revision);
        Assert.True(saved.Schedule!.SemanticEquals(current.Schedule!));
        if (duplicate)
        {
            Assert.All(rows, a => Assert.Equal(AutomationStatus.Disabled, a.Status));
            Assert.Contains("authorization_ambiguous", model.LastResult);
        }
        else
        {
            Assert.Equal(AutomationStatus.Active, current.Status);
            Assert.Equal(saved.Revision + 1, current.Revision);
            Assert.NotNull(current.NextOccurrenceAtUtc);
            Assert.Equal(0, (await services.GetRequiredService<TriggerScheduler>().RunOnceAsync(clock.GetUtcNow())).Admitted);
            clock.Advance(TimeSpan.FromSeconds(60));
            Assert.Equal(1, (await services.GetRequiredService<TriggerScheduler>().RunOnceAsync(clock.GetUtcNow())).Admitted);
            Assert.Equal(0, (await services.GetRequiredService<TriggerScheduler>().RunOnceAsync(clock.GetUtcNow())).Admitted);
            await AutomationJourneyTests.Intake(services);
            Assert.Equal(1, await services.ExecuteRunsAsync(100));
            var run = Assert.Single(await services.AutomationRunsAsync(new(instance.InstanceId, LocalUserProfile.Id), current.AutomationId));
            Assert.Equal(AgentRunStatus.Completed, run.Status);
            Assert.Equal(1, classifier.Calls);
            Assert.Single(await services.GetRequiredService<ITriggerStore>().ListAsync(current.Owner, null));
        }
    }

    private sealed class Clock(DateTimeOffset initial) : TimeProvider
    {
        private DateTimeOffset now = initial;
        public override DateTimeOffset GetUtcNow() => now;
        internal void Advance(TimeSpan elapsed) => now += elapsed;
    }

    private sealed class AlignmentClassifier(bool ambiguous) : ILanguageModel
    {
        internal int Calls;
        public ModelCapabilities Capabilities => new(true, true);
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            Calls++;
            await Task.CompletedTask;
            yield return new ModelTextDelta(ambiguous ? "{\"decision\":\"ambiguous\"}" : "{\"decision\":\"allow\"}");
            yield return new ModelCompleted(ModelStopReason.Completed);
        }
    }

    private sealed class WorkflowModel(string requestText) : ILanguageModel
    {
        private readonly ScriptedLanguageModel inner = new();
        public ModelCapabilities Capabilities => inner.Capabilities;
        internal List<string> Calls { get; } = [];
        internal string LastResult = "";
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            if (request.Messages.LastOrDefault(m => m.Role == ModelRole.User)?.Text != requestText)
            {
                await foreach (var item in inner.GenerateAsync(request, ct)) yield return item;
                yield break;
            }
            await Task.CompletedTask;
            var results = request.Messages.Where(m => m.Role == ModelRole.Tool).ToArray();
            string tool; string args;
            if (results.Length == 0) { tool = ToolCatalog.AutomationList; args = "{}"; }
            else if (results.Length == 1)
            {
                using var json = JsonDocument.Parse(results[0].Text);
                var row = json.RootElement.GetProperty("automations")[0];
                tool = ToolCatalog.AutomationInspect;
                args = JsonSerializer.Serialize(new { automationId = row.GetProperty("automationId").GetGuid() });
            }
            else if (results.Length == 2)
            {
                using var json = JsonDocument.Parse(results[1].Text);
                var row = json.RootElement;
                tool = ToolCatalog.AutomationUpdate;
                args = JsonSerializer.Serialize(new { automationId = row.GetProperty("automationId").GetGuid(), expectedRevision = row.GetProperty("revision").GetInt64(), enabled = true });
            }
            else
            {
                LastResult = results.Last().Text;
                yield return new ModelTextDelta(LastResult.Contains("ambiguous_target") ? "Which hello schedule do you mean?" : "Enabled the existing hello schedule.");
                yield return new ModelCompleted(ModelStopReason.Completed);
                yield break;
            }
            Calls.Add(tool);
            yield return new ModelToolCallEvent(new(Guid.NewGuid().ToString(), tool, args));
            yield return new ModelCompleted(ModelStopReason.ToolCalls);
        }
    }
}
