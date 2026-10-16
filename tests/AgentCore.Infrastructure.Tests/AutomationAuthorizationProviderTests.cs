using System.Net;
using System.Text;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Triggers;
using AgentCore.Infrastructure.Providers;
using AgentCore.Infrastructure.Providers.OpenAICompatible;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgentCore.Infrastructure.Tests;

public sealed class AutomationAuthorizationProviderTests
{
    [Theory]
    [InlineData(ModelInferenceTransport.ChatCompletions, "allow", TriggerCommandAuthorizationDecision.Allow)]
    [InlineData(ModelInferenceTransport.ChatCompletions, "deny", TriggerCommandAuthorizationDecision.Deny)]
    [InlineData(ModelInferenceTransport.ChatCompletions, "truncated", TriggerCommandAuthorizationDecision.ClassifierInvalidOutput)]
    [InlineData(ModelInferenceTransport.Responses, "allow", TriggerCommandAuthorizationDecision.Allow)]
    [InlineData(ModelInferenceTransport.Responses, "deny", TriggerCommandAuthorizationDecision.Deny)]
    [InlineData(ModelInferenceTransport.Responses, "truncated", TriggerCommandAuthorizationDecision.ClassifierInvalidOutput)]
    public async Task Output_limit_recovery_preserves_wire_context_and_records_only_safe_metadata(ModelInferenceTransport transport, string outcome, TriggerCommandAuthorizationDecision expected)
    {
        using var handler = new ClassifierHandler(outcome, transport, truncateFirst: true);
        using var http = new HttpClient(handler);
        using var logs = new ClassifierLogs();
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddProvider(logs));
        services.AddSingleton<IHttpClientFactory>(new HttpFactory(http));
        services.AddAgentCoreInfrastructure(FindAgents(), "Real", new()
        {
            Adapter = "OpenAICompatible", BaseUrl = "https://provider.example/v1/", ApiKey = "private-fixture-key", DefaultModel = "legacy-model"
        });
        var descriptor = ModelCatalogFactory.Real().Default with { Key = "custom", ModelId = "catalog-model", Transport = transport,
            DefaultReasoningEffort = "high", SupportedReasoningEfforts = ["high", "low"] };
        services.AddSingleton<IModelCatalog>(new ConfigurationModelCatalog("custom", [descriptor]));
        await using var provider = services.BuildServiceProvider();
        var verdict = await provider.GetRequiredService<ITriggerCommandAuthorizer>().AuthorizeAsync(Proposed("I would like daily reflection on private-user-content at 9am"));
        Assert.Equal(expected, verdict.Decision);
        if (outcome == "truncated") Assert.Equal(TriggerAuthorizationFailureReason.TruncatedOutput, verdict.FailureReason);
        Assert.Equal(2, handler.Payloads.Count);
        var budgetKey = transport == ModelInferenceTransport.Responses ? "max_output_tokens" : "max_tokens";
        var bodies = handler.Payloads.Select(payload => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(payload)!).ToArray();
        Assert.Equal(512, bodies[0][budgetKey].GetInt32()); Assert.Equal(1024, bodies[1][budgetKey].GetInt32());
        Assert.Equal("catalog-model", bodies[0]["model"].GetString());
        Assert.Equal("low", transport == ModelInferenceTransport.Responses ? bodies[0]["reasoning"].GetProperty("effort").GetString() : bodies[0]["reasoning_effort"].GetString());
        Assert.False(bodies[0].ContainsKey("tools"));
        foreach (var body in bodies) body.Remove(budgetKey);
        Assert.Equal(JsonSerializer.Serialize(bodies[0]), JsonSerializer.Serialize(bodies[1]));
        Assert.Equal(2, logs.Entries.Count);
        Assert.Contains("maxOutputTokens=512", logs.Entries[0]); Assert.Contains("maxOutputTokens=1024", logs.Entries[1]);
        Assert.Contains("terminal=LengthLimit", logs.Entries[0]); Assert.Contains("outputTokens=512", logs.Entries[0]);
        Assert.Contains("catalog=custom", logs.Entries[0]); Assert.Contains($"transport={transport}", logs.Entries[0]);
        Assert.All(logs.Entries, entry =>
        {
            Assert.DoesNotContain("private-user-content", entry); Assert.DoesNotContain("private-fixture-key", entry);
            Assert.DoesNotContain("decision\"", entry); Assert.DoesNotContain("private-reasoning", entry);
        });
        Assert.Contains(outcome == "truncated" ? "validDecision=False" : "validDecision=True", logs.Entries[1]);
    }

    [Theory]
    [InlineData(ModelInferenceTransport.ChatCompletions, false)]
    [InlineData(ModelInferenceTransport.Responses, false)]
    [InlineData(ModelInferenceTransport.ChatCompletions, true)]
    [InlineData(ModelInferenceTransport.Responses, true)]
    public async Task Authorization_binds_catalog_default_transport_and_effort_without_mutating_provider_options(ModelInferenceTransport transport, bool supportsNone)
    {
        using var handler = new ClassifierHandler("allow", transport);
        using var http = new HttpClient(handler);
        var primary = new LanguageModelProviderOptions
        {
            Adapter = "OpenAICompatible", BaseUrl = "https://provider.example/v1/", ApiKey = "offline-fixture", DefaultModel = "legacy-model"
        };
        var descriptor = ModelCatalogFactory.Real().Default with { Key = "custom", ModelId = "catalog-model", Transport = transport,
            SupportedReasoningEfforts = supportsNone ? ["high", "none", "low"] : ["low", "high"] };
        var services = new ServiceCollection();
        services.AddSingleton<IHttpClientFactory>(new HttpFactory(http));
        services.AddAgentCoreInfrastructure(FindAgents(), "Real", primary);
        services.AddSingleton<IModelCatalog>(new ConfigurationModelCatalog("custom", [descriptor]));
        await using var provider = services.BuildServiceProvider();
        Assert.Equal(TriggerCommandAuthorizationDecision.Allow, (await provider.GetRequiredService<ITriggerCommandAuthorizer>()
            .AuthorizeAsync(Proposed("I would like daily reflection on your memory at 9am"))).Decision);
        using var body = JsonDocument.Parse(Assert.Single(handler.Payloads));
        Assert.Equal("catalog-model", body.RootElement.GetProperty("model").GetString());
        Assert.EndsWith(transport == ModelInferenceTransport.Responses ? "/responses" : "/chat/completions", Assert.Single(handler.Paths));
        var messages = body.RootElement.GetProperty(transport == ModelInferenceTransport.Responses ? "input" : "messages").EnumerateArray();
        Assert.Contains(messages, message => message.GetProperty("role").GetString() == "developer");
        Assert.Equal(supportsNone ? "none" : "low", transport == ModelInferenceTransport.Responses
            ? body.RootElement.GetProperty("reasoning").GetProperty("effort").GetString()
            : body.RootElement.GetProperty("reasoning_effort").GetString());
        Assert.False(primary.DeveloperRoleSupport);
        Assert.Equal("legacy-model", primary.DefaultModel);
        Assert.Equal(ModelInferenceTransport.ChatCompletions, primary.Transport);
        Assert.Null(primary.ReasoningEffort);
    }

    [Theory]
    [InlineData("allow", TriggerCommandAuthorizationDecision.Allow)]
    [InlineData("deny", TriggerCommandAuthorizationDecision.Deny)]
    [InlineData("ambiguous", TriggerCommandAuthorizationDecision.Ambiguous)]
    [InlineData("invalid", TriggerCommandAuthorizationDecision.ClassifierInvalidOutput)]
    [InlineData("unavailable", TriggerCommandAuthorizationDecision.ClassifierUnavailable)]
    [InlineData("http400", TriggerCommandAuthorizationDecision.ClassifierUnavailable)]
    [InlineData("http401", TriggerCommandAuthorizationDecision.ClassifierUnavailable)]
    [InlineData("http429", TriggerCommandAuthorizationDecision.ClassifierUnavailable)]
    [InlineData("http500", TriggerCommandAuthorizationDecision.ClassifierUnavailable)]
    [InlineData("incomplete", TriggerCommandAuthorizationDecision.ClassifierUnavailable)]
    [InlineData("malformed-sse", TriggerCommandAuthorizationDecision.ClassifierUnavailable)]
    [InlineData("filtered", TriggerCommandAuthorizationDecision.ClassifierInvalidOutput)]
    public async Task Real_authorization_uses_catalog_hierarchy_and_preserves_classifier_outcomes(
        string outcome, TriggerCommandAuthorizationDecision expected)
    {
        using var handler = new ClassifierHandler(outcome);
        using var http = new HttpClient(handler);
        var services = new ServiceCollection();
        services.AddSingleton<IHttpClientFactory>(new HttpFactory(http));
        services.AddAgentCoreInfrastructure(FindAgents(), "Real", new LanguageModelProviderOptions
        {
            Adapter = "OpenAICompatible", BaseUrl = "https://provider.example/v1/", ApiKey = "offline-fixture",
            DefaultModel = ModelCatalogFactory.DeepSeekV41FlashModelId
            // Hierarchy/transport capabilities belong to the catalog, as in normal Real setup.
        });
        await using var provider = services.BuildServiceProvider();
        var verdict = await provider.GetRequiredService<ITriggerCommandAuthorizer>().AuthorizeAsync(Proposed("I would like daily reflection on how you can consolidate your memory, experience at 9am"));

        Assert.Equal(expected, verdict.Decision);
        using var body = JsonDocument.Parse(Assert.Single(handler.Payloads));
        Assert.Equal(ModelCatalogFactory.DeepSeekV41FlashModelId, body.RootElement.GetProperty("model").GetString());
        var messages = body.RootElement.GetProperty("messages").EnumerateArray().ToArray();
        Assert.Contains(messages, message => message.GetProperty("role").GetString() == "system");
        Assert.Contains(messages, message => message.GetProperty("role").GetString() == "developer"
            && message.GetProperty("content").GetString()!.Contains("Proposed operation: Create"));
        Assert.DoesNotContain(messages, message => message.GetProperty("content").GetString()!.Contains("offline-fixture"));
        Assert.Equal("I would like daily reflection on how you can consolidate your memory, experience at 9am",
            messages[^1].GetProperty("content").GetString());
    }

    private static AutomationAuthorizationRequest Proposed(string text)
    {
        var action = JsonSerializer.SerializeToElement(new { tool = "automation.create", arguments = new { instructions = "Reflect on experience", kind = "daily", localTime = "09:00", timeZone = "Asia/Ho_Chi_Minh", executionTarget = "backgroundSession", reportBack = false } });
        return new(text, TriggerCommandAction.Create, "automation.create", action, AgentCore.Application.Tools.ToolActionHash.Compute("automation.create", action));
    }

    private sealed class HttpFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class ClassifierHandler(string outcome, ModelInferenceTransport transport = ModelInferenceTransport.ChatCompletions, bool truncateFirst = false) : HttpMessageHandler
    {
        public List<string> Payloads { get; } = [];
        public List<string> Paths { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Payloads.Add(await request.Content!.ReadAsStringAsync(ct));
            Paths.Add(request.RequestUri!.AbsolutePath);
            if (outcome == "unavailable") return new(HttpStatusCode.ServiceUnavailable);
            if (outcome.StartsWith("http", StringComparison.Ordinal)) return new((HttpStatusCode)int.Parse(outcome[4..]));
            if (outcome == "malformed-sse") return new(HttpStatusCode.OK) { Content = new StringContent("data: {broken\n\n", Encoding.UTF8, "text/event-stream") };
            var limited = outcome == "truncated" || truncateFirst && Payloads.Count == 1;
            var text = outcome == "invalid" ? "invalid" : JsonSerializer.Serialize(new { decision = limited ? "allow" : outcome });
            if (transport == ModelInferenceTransport.Responses)
            {
                var responseDelta = JsonSerializer.Serialize(new { type = "response.output_text.delta", delta = text });
                var terminal = limited
                    ? JsonSerializer.Serialize(new { type = "response.incomplete", response = new { status = "incomplete", output = Array.Empty<object>(), incomplete_details = new { reason = "max_output_tokens" }, usage = new { output_tokens = 512 } } })
                    : "{\"type\":\"response.completed\",\"response\":{\"status\":\"completed\",\"output\":[],\"usage\":{\"output_tokens\":8}}}";
                return new(HttpStatusCode.OK) { Content = new StringContent(
                    $"data: {responseDelta}\n\ndata: {terminal}\n\n",
                    Encoding.UTF8, "text/event-stream") };
            }
            var delta = JsonSerializer.Serialize(new { choices = new[] { new { delta = new { content = text } } } });
            if (outcome == "incomplete") return new(HttpStatusCode.OK) { Content = new StringContent($"data: {delta}\n\n", Encoding.UTF8, "text/event-stream") };
            var reasoning = JsonSerializer.Serialize(new { choices = new[] { new { delta = new { reasoning_content = "private-reasoning" } } } });
            return new(HttpStatusCode.OK) { Content = new StringContent(
                $"data: {reasoning}\n\ndata: {delta}\n\ndata: {{\"choices\":[{{\"delta\":{{}},\"finish_reason\":\"{(limited ? "length" : outcome == "filtered" ? "content_filter" : "stop")}\"}}],\"usage\":{{\"completion_tokens\":{(limited ? 512 : 8)}}}}}\n\ndata: [DONE]\n\n",
                Encoding.UTF8, "text/event-stream") };
        }
    }

    private sealed class ClassifierLogs : ILoggerProvider
    {
        public List<string> Entries { get; } = [];
        public ILogger CreateLogger(string categoryName) => new Sink(categoryName, Entries);
        public void Dispose() { }
        private sealed class Sink(string category, List<string> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel level) => true;
            public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter)
            { if (category == typeof(ModelTriggerCommandAuthorizer).FullName) entries.Add(formatter(state, error)); }
        }
    }

    private static string FindAgents()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "agents"))) directory = directory.Parent;
        return Path.Combine(directory!.FullName, "agents");
    }
}
