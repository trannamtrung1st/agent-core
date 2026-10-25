using System.Net;
using System.Text.Json;
using AgentCore.Application.Execution;
using AgentCore.Application.Events;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Sessions;
using AgentCore.Application.Testing;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Models;
using AgentCore.Infrastructure.Browser;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Providers;
using AgentCore.Infrastructure.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantCredentialIntegrationTests
{
    [Theory]
    [InlineData(ModelProviderKind.OpenAI, false)] [InlineData(ModelProviderKind.Anthropic, false)] [InlineData(ModelProviderKind.Google, false)]
    [InlineData(ModelProviderKind.OpenRouter, false)] [InlineData(ModelProviderKind.OpenAICompatible, false)] [InlineData(ModelProviderKind.Anthropic, true)]
    public async Task DynamicModels_actual_Runtime_executes_native_tool_checkpoint_and_reply_without_resolving_a_global_model(ModelProviderKind kind, bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var setup = await ProviderSetup(f, kind);
        var tenant = setup.Route.TenantId; var human = new TenantSessionInitiator(setup.Human.AccountId, setup.Human.AuthenticationSessionId, setup.Human.PrincipalId);
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision;
        var agent = await f.Agents.CreateAsync(human.PrincipalId, tenant, "examiner", 1, await Revision());
        var arguments = JsonSerializer.Serialize(new { permission = "agents.read", resourceType = "agent", resourceId = agent.Instance.InstanceId.ToString("D") });
        using var http = new ProviderHttpFixture(RuntimeToolWire(setup.Route, arguments), FinalWire(setup.Route));
        var global = new GlobalModelProbe(); await using var services = RuntimeServices(f, http, global);
        var runtimeFactory = services.GetRequiredService<SessionRuntimeFactory>();
        Assert.Equal(0, global.Resolutions);
        await Models(f).SaveDefaultAsync(setup.Human, tenant, setup.Offering.OfferingId, null, await Revision(), 0);
        var session = await services.GetRequiredService<TenantSessionService>().CreateAsync(tenant, agent.Instance.InstanceId, human, SessionMode.Text, await Revision(), agent.Instance.Revision);
        var receipt = await services.GetRequiredService<TenantRunService>().SubmitTextAsync(tenant, session.Owner.SessionId, human,
            f.Ids.NewId(), "Inspect your current access and report the result.", await Revision(), 1);
        var runs = (ITenantRunStore)f.Iam; var storage = (ITenantRuntimeStore)f.Iam;
        var run = (await runs.FindTenantRunAsync(tenant, receipt.RunId))!;
        run = await storage.ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId,
            new AgentRunCommand.Claim(run.Revision, f.Clock.GetUtcNow(), f.Ids.NewId(), f.Clock.GetUtcNow().AddMinutes(5)), f.Ids.NewId());
        var frame = new TenantRuntimeFrame(run.Owner, run.SessionId, run.AgentRunId, run.Claim!.Generation);
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        http.BeforeResponse = async (count, ct) => { if (count == 2) { waiting.TrySetResult(); await release.Task.WaitAsync(ct); } };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var output = new CapturingSessionOutput();
        await using var runtime = runtimeFactory.CreateTenant((await storage.LoadTenantRuntimeAsync(frame))!, output, frame,
            storage, runs, services.GetRequiredService<TenantRunExecutionAuthority>(), services.GetRequiredService<TenantToolAuthorization>());
        Assert.True(await runtime.DispatchAgentRunAsync(run.AgentRunId, true, deadline.Token));
        try
        {
            while (!waiting.Task.IsCompleted)
            {
                var observed = (await runs.FindTenantRunAsync(tenant, run.AgentRunId))!;
                if (observed.IsTerminal || deadline.IsCancellationRequested)
                    throw new Xunit.Sdk.XunitException($"Native Runtime did not reach its next generation: status={observed.Status}, failure={observed.Failure?.Code}, HTTP={http.Requests.Count}, output={string.Join(',', output.Items.Select(o => o.Payload).OfType<ErrorOutput>().Select(e => e.Code + ':' + e.FailureReason))}; diagnostics={string.Join(';', global.Diagnostics)}");
                await Task.Delay(25);
            }
            var checkpoint = (await runs.FindTenantRunAsync(tenant, run.AgentRunId))!.Checkpoint;
            Assert.True(AgentRunToolCallCheckpoint.TryRead(checkpoint, out var messages));
            Assert.Contains(messages!, m => m.Role == ModelRole.Tool && m.Name == ToolCatalog.IamCheckAccess && m.Text.Contains("permission", StringComparison.Ordinal));
            Assert.DoesNotContain("factory-private-key", JsonSerializer.Serialize(checkpoint));
            Assert.DoesNotContain("fixture-private-signature", JsonSerializer.Serialize(checkpoint));
        }
        finally { release.TrySetResult(); }
        await runtime.WaitUntilIdleAsync(deadline.Token);
        var completed = (await runs.FindTenantRunAsync(tenant, run.AgentRunId))!;
        Assert.Equal(AgentRunStatus.Completed, completed.Status); Assert.Equal("Done ☃", completed.Result!.Text);
        Assert.Equal(setup.Route, completed.PinnedModel.Binding!.Route); Assert.Equal(2, http.Requests.Count);
        Assert.Equal(0, global.Resolutions);
        if (sqlite)
        {
            var reopened = new SqliteIamStore(f.Contexts!, clock: f.Clock);
            var retained = (await reopened.FindTenantRunAsync(tenant, run.AgentRunId))!;
            Assert.Equal(AgentRunStatus.Completed, retained.Status); Assert.Equal(completed.PinnedModel.Binding, retained.PinnedModel.Binding);
        }
    }

    private sealed class GlobalModelProbe : ILoggerProvider
    {
        public int Resolutions;
        public ConcurrentQueue<string> Diagnostics { get; } = new();
        public ILogger CreateLogger(string categoryName) => new ProbeLog(categoryName, Diagnostics);
        public void Dispose() { }
        private sealed class ProbeLog(string category, ConcurrentQueue<string> records) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel level) => level >= LogLevel.Warning;
            public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter)
            { if (IsEnabled(level)) records.Enqueue(category + ":" + formatter(state, error) + ":" + (error?.GetType().Name == "SafeException" ? error.Message + ":" + error.StackTrace : error?.GetType().Name)); }
        }
    }
    private static ServiceProvider RuntimeServices(Fixture f, IHttpClientFactory http, GlobalModelProbe probe, string profile = "Real", AgentCore.Application.Models.ModelEndpointPolicy? modelEndpoints = null)
    {
        var services = new ServiceCollection(); services.AddLogging(b => b.AddProvider(probe)); services.AddSingleton<IIamStore>(f.Iam);
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSingleton<ICredentialProtector>(f.Protector); services.AddSingleton(f.Clock); services.AddSingleton(f.Ids);
        services.AddAgentCoreIdentity();
        services.AddAgentCoreInfrastructure(Path.Combine(FindModelRepo(), "agents"), profile, persistence: new PersistenceOptions
        {
            Provider = "InMemory", WorkspaceRoot = Path.Combine(f.Root, "runtime-workspaces"), AttachmentRoot = Path.Combine(f.Root, "runtime-attachments"),
            ArtifactRoot = Path.Combine(f.Root, "runtime-artifacts"), CredentialProtectionKeyRoot = Path.Combine(f.Root, "runtime-keys"),
            DefinitionResourceRoot = Path.Combine(f.Root, "runtime-resources"), TemplateRoot = Path.Combine(FindModelRepo(), "agents", "templates")
        }, speech: new() { Recognition = new() { Adapter = "Browser" }, Synthesis = new() { Adapter = "Browser" } }, browser: new BrowserOptions { Enabled = false });
        if (profile == "Real")
        {
            Assert.DoesNotContain(services, d => d.ServiceType == typeof(ILanguageModel));
            services.AddSingleton<ILanguageModel>(_ => { probe.Resolutions++; throw new InvalidOperationException("A global model is never a tenant inference source."); });
        }
        if (modelEndpoints is not null) services.AddSingleton(modelEndpoints);
        services.AddSingleton<IHttpClientFactory>(new RuntimeHttpClients(http));
        return services.BuildServiceProvider();
    }
    private sealed class RuntimeHttpClients(IHttpClientFactory model) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => name == ModelProviderHttp.ClientName ? model.CreateClient(name) : new(new RejectNetwork());
        private sealed class RejectNetwork : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => throw new InvalidOperationException("Unexpected non-model network request in offline Runtime fixture.");
        }
    }
    private static string FindModelRepo()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !Directory.Exists(Path.Combine(current.FullName, "agents"))) current = current.Parent;
        return current?.FullName ?? throw new DirectoryNotFoundException();
    }
    private static string RuntimeToolWire(ModelRoutePin route, string args)
    {
        using var document = JsonDocument.Parse(args); var arguments = document.RootElement.Clone(); const string id = "native-iam-call"; const string name = "iam_check_access";
        if (route.Kind == ModelProviderKind.Anthropic)
            return NativeLanguageModelTests.Data(new { type = "message_start", message = new { role = "assistant" } })
                + NativeLanguageModelTests.Data(new { type = "content_block_start", index = 0, content_block = new { type = "tool_use", id, name, input = new { } } })
                + NativeLanguageModelTests.Data(new { type = "content_block_delta", index = 0, delta = new { type = "input_json_delta", partial_json = args } })
                + NativeLanguageModelTests.Data(new { type = "content_block_stop", index = 0 })
                + NativeLanguageModelTests.Data(new { type = "message_delta", delta = new { stop_reason = "tool_use" } }) + NativeLanguageModelTests.Data(new { type = "message_stop" });
        if (route.Kind == ModelProviderKind.Google)
            return NativeLanguageModelTests.Data(new { candidates = new[] { new { content = new { role = "model", parts = new[] {
                new { functionCall = new { id, name, args = arguments }, thoughtSignature = "fixture-private-signature" } } }, finishReason = "STOP" } } });
        return route.Transport == ModelTransport.Responses
            ? NativeLanguageModelTests.Data(new { type = "response.completed", response = new { status = "completed", output = new[] { new { type = "function_call", call_id = id, name, arguments = args } } } })
            : NativeLanguageModelTests.Data(new { choices = new[] { new { delta = new { tool_calls = new[] { new { index = 0, id, type = "function", function = new { name, arguments = args } } } }, finish_reason = "tool_calls" } } }) + "data: [DONE]\n\n";
    }
}
