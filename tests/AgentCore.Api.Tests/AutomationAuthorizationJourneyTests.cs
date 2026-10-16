using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AgentCore.Application.Admin;
using AgentCore.Application.Context;
using AgentCore.Application.Events;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Tools;
using AgentCore.Application.Triggers;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Triggers;
using AgentCore.Infrastructure;
using AgentCore.Infrastructure.Providers.OpenAICompatible;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentCore.Api.Tests;

public sealed class AutomationAuthorizationJourneyTests
{
    private const string Request = "everyday at 9am, think about how you can consolidate your memory, experience";
    private const string TimezoneReply = "use vietname timezone";
    private const string Instructions = "Reflect on learned Memory and recorded Experience; consolidate at most one clearly safe change. No forgetting or invented resolutions.";
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 0, 0, 0, TimeSpan.Zero);

    [Theory(Timeout = 60000)]
    [InlineData("allow", null)]
    [InlineData("deny", "authorization_denied")]
    [InlineData("invalid", "authorization_classifier_invalid_output")]
    [InlineData("unavailable", "authorization_classifier_unavailable")]
    public async Task Calendar_authorization_preserves_timezone_destination_and_fail_closed_outcomes(string outcome, string? error)
    {
        var db = Path.Combine(Path.GetTempPath(), $"automation-authorization-{Guid.NewGuid():N}.db");
        var clock = new Clock(Now);
        var model = new CalendarModel();
        using var handler = new ClassifierHandler(outcome);
        using var http = new HttpClient(handler);
        Guid instanceId, sessionId;
        IModelCatalog catalog;
        await using (var host = new ExperienceHost(db, model, clock, configure: services =>
        {
            services.RemoveAll<IHttpClientFactory>();
            services.AddSingleton<IHttpClientFactory>(new HttpFactory(http));
            // Install the production Real model/authorization registrations in this
            // disposable Synthetic host. Other provider families remain offline.
            var real = new ServiceCollection();
            real.AddAgentCoreInfrastructure(AppContext.BaseDirectory, "Real", new()
            {
                Adapter = "OpenAICompatible", BaseUrl = "https://provider.example/v1/", ApiKey = "offline-fixture",
                DefaultModel = "deepseek/deepseek-v4.1-flash"
            });
            foreach (var type in new[] { typeof(IModelCatalog), typeof(ModelTriggerCommandAuthorizer), typeof(ITriggerCommandAuthorizer) })
            {
                services.RemoveAll(type);
                services.Add(real.Single(registration => registration.ServiceType == type));
            }
            var resolver = real.Single(s => s.ServiceType == typeof(ILanguageModelResolver));
            services.RemoveAll<ILanguageModelResolver>();
            services.AddSingleton<ILanguageModelResolver>(provider => new PurposeResolver(
                (ILanguageModelResolver)resolver.ImplementationFactory!(provider), model));
        }))
        {
            var services = host.Services;
            catalog = services.GetRequiredService<IModelCatalog>();
            instanceId = (await services.GetRequiredService<AdminAgentInstanceService>()
                .CreateManagedAsync("general-assistant", 24)).InstanceId;
            var session = await services.GetRequiredService<SessionManager>().CreateForInstanceAsync(instanceId, SessionMode.Text);
            sessionId = session.SessionId;
            await using var runtime = services.GetRequiredService<SessionRuntimeFactory>().Create(session, new QuietOutput());
            await runtime.AttachAsync();
            Assert.True(await runtime.SubmitUserTextAsync(Request));
            await runtime.WaitUntilIdleAsync().WaitAsync(TimeSpan.FromSeconds(20));
            var triggers = services.GetRequiredService<ITriggerStore>();
            if (error is not null)
            {
                Assert.Empty(await triggers.ListAsync(new(instanceId, LocalUserProfile.Id), null));
                Assert.Contains(model.Results, result => result.GetProperty("error").GetString() == error);
                Assert.Single(handler.Payloads);
                return;
            }
            var created = Assert.Single(await triggers.ListAsync(new(instanceId, LocalUserProfile.Id), null));
            Assert.Equal("UTC", Assert.IsType<DailySchedule>(created.Schedule).TimeZoneId);
            Assert.True(await runtime.SubmitUserTextAsync(TimezoneReply));
            await runtime.WaitUntilIdleAsync().WaitAsync(TimeSpan.FromSeconds(20));
            var updated = Assert.Single(await triggers.ListAsync(new(instanceId, LocalUserProfile.Id), null));
            Assert.Equal(created.AutomationId, updated.AutomationId);
            Assert.Equal(Instructions, updated.Instructions);
            Assert.Equal(AutomationExecutionTarget.Background, updated.ExecutionTarget);
            Assert.Equal(AutomationCompletionDelivery.None, updated.CompletionDelivery);
            Assert.Equal(TriggerAuthorizationOrigin.CurrentUserTurn, updated.Provenance.AuthorizationOrigin);
            var schedule = Assert.IsType<DailySchedule>(updated.Schedule);
            Assert.Equal("Asia/Ho_Chi_Minh", schedule.TimeZoneId);
            Assert.Equal(new TimeOnly(9, 0), schedule.LocalTime);
            Assert.Equal(Now.AddHours(2), updated.NextOccurrenceAtUtc);
            Assert.Equal(2, handler.Payloads.Count);
            using var followup = JsonDocument.Parse(handler.Payloads[1]);
            Assert.Contains("Reflect on learned Memory", followup.RootElement.GetRawText());
            Assert.Contains(TimezoneReply, followup.RootElement.GetRawText());
        }

        await using var reopened = new ExperienceHost(db, model, clock, configure: services =>
        {
            services.RemoveAll<IModelCatalog>();
            services.AddSingleton(catalog);
        });
        var reopenedServices = reopened.Services;
        for (var day = 0; day < 2; day++)
        {
            clock.Current = Now.AddDays(day).AddHours(2).AddSeconds(-1);
            Assert.Equal(0, (await reopenedServices.GetRequiredService<TriggerScheduler>().RunOnceAsync(clock.Current)).Admitted);
            clock.Current = clock.Current.AddSeconds(1);
            for (var scan = 0; scan < 2; scan++)
            {
                Assert.Equal(scan == 0 ? 1 : 0,
                    (await reopenedServices.GetRequiredService<TriggerScheduler>().RunOnceAsync(clock.Current)).Admitted);
                await reopenedServices.GetRequiredService<TriggerOccurrenceRouter>().RouteOnceAsync();
                Assert.Equal(scan == 0 ? 1 : 0,
                    (await reopenedServices.GetRequiredService<BackgroundOccurrenceIntake>().AcceptAwaitingAsync()).Accepted);
                await reopenedServices.ExecuteRunsAsync();
            }
        }
        var runs = await reopenedServices.AutomationRunsAsync(new(instanceId, LocalUserProfile.Id), model.AutomationId);
        Assert.Equal(2, runs.Count);
        Assert.Equal(2, runs.Select(run => run.Admission.Activation.TriggerOccurrenceId).Distinct().Count());
        Assert.All(runs, run =>
        {
            Assert.Equal(AgentRunStatus.Completed, run.Status);
            Assert.Equal("NoAction", run.Result!.OutcomeKind.ToString());
            Assert.False(run.Result.AttentionRequired);
        });
        Assert.Equal(2, model.WorkRuns);
        var history = await reopenedServices.GetRequiredService<IMemoryStore>().LoadAsync(sessionId);
        Assert.DoesNotContain(history!.Entries, entry => entry.Text == "No safe consolidation found.");
    }

    private sealed class PurposeResolver(ILanguageModelResolver real, ILanguageModel scripted) : ILanguageModelResolver
    {
        public ILanguageModel Resolve(SessionModelSelection selection, ModelPurpose purpose) =>
            purpose == ModelPurpose.TriggerAuthorization ? real.Resolve(selection, purpose) : scripted;
    }

    private sealed class CalendarModel : ILanguageModel
    {
        public ModelCapabilities Capabilities { get; } = new(true, true, Tools: true, StructuredOutput: true);
        public Guid AutomationId { get; private set; }
        private long revision;
        public List<JsonElement> Results { get; } = [];
        public int WorkRuns { get; private set; }
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            if (request.Context?.Sections.Any(section => section.Id == ContextSectionId.CoreBackground) == true)
            {
                WorkRuns++;
                yield return Call(ToolCatalog.WorkComplete, new { outcome = "NoAction", summary = "No safe consolidation found.", attentionRequired = false });
                yield return new ModelCompleted(ModelStopReason.ToolCalls);
                yield break;
            }
            var transcript = request.Interaction ?? request.Messages;
            var lastUser = transcript.ToList().FindLastIndex(message => message.Role == ModelRole.User);
            var user = transcript[lastUser].Text;
            var tool = user == TimezoneReply ? ToolCatalog.AutomationUpdate : ToolCatalog.AutomationCreate;
            var result = transcript.Skip(lastUser + 1).LastOrDefault(message => message.Role == ModelRole.Tool && message.Name == tool);
            if (result is not null)
            {
                using var json = JsonDocument.Parse(result.Text);
                Results.Add(json.RootElement.Clone());
                if (json.RootElement.TryGetProperty("automationId", out var id))
                { AutomationId = Guid.Parse(id.GetString()!); revision = json.RootElement.GetProperty("revision").GetInt64(); }
                yield return new ModelSemanticResponseReady(new("Schedule result received.", new(ModelSpeechMode.None, null), []));
                yield return new ModelCompleted(ModelStopReason.Completed);
            }
            else
            {
                if (request.Tools?.Any(descriptor => descriptor.Name == tool) != true)
                    yield return Call(ToolCatalog.CapabilitiesLoad, new { query = tool, limit = 1 });
                else if (tool == ToolCatalog.AutomationCreate)
                    yield return Call(tool, new { instructions = Instructions, kind = "daily", localTime = "09:00", timeZone = "UTC", executionTarget = "backgroundSession", reportBack = false });
                else yield return Call(tool, new { automationId = AutomationId, expectedRevision = revision, kind = "daily", localTime = "09:00", timeZone = "Vietnam time" });
                yield return new ModelCompleted(ModelStopReason.ToolCalls);
            }
        }
        private static ModelToolCallEvent Call(string name, object arguments) => new(new(Guid.NewGuid().ToString("N"), name, JsonSerializer.Serialize(arguments)));
    }

    private sealed class HttpFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        { Assert.Equal(OpenAICompatibleLanguageModel.HttpClientName, name); return client; }
    }
    private sealed class ClassifierHandler(string outcome) : HttpMessageHandler
    {
        public List<string> Payloads { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Payloads.Add(await request.Content!.ReadAsStringAsync(ct));
            if (outcome == "unavailable") return new(HttpStatusCode.ServiceUnavailable);
            var text = outcome == "invalid" ? "invalid" : JsonSerializer.Serialize(new { decision = outcome });
            var delta = JsonSerializer.Serialize(new { choices = new[] { new { delta = new { content = text } } } });
            return new(HttpStatusCode.OK) { Content = new StringContent(
                $"data: {delta}\n\ndata: {{\"choices\":[{{\"delta\":{{}},\"finish_reason\":\"stop\"}}]}}\n\ndata: [DONE]\n\n", Encoding.UTF8, "text/event-stream") };
        }
    }
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    { public DateTimeOffset Current { get; set; } = now; public override DateTimeOffset GetUtcNow() => Current; }
    private sealed class QuietOutput : ISessionOutput
    { public ValueTask PublishAsync(SessionOutput item, CancellationToken ct = default) => ValueTask.CompletedTask; }
}
