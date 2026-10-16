using System.Net;
using System.Text;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Triggers;
using AgentCore.Infrastructure.Providers;
using AgentCore.Infrastructure.Providers.OpenAICompatible;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Infrastructure.Tests;

public sealed class AutomationAuthorizationProviderTests
{
    [Theory]
    [InlineData(ModelInferenceTransport.ChatCompletions)]
    [InlineData(ModelInferenceTransport.Responses)]
    public async Task Authorization_binds_catalog_default_transport_and_effort_without_mutating_provider_options(ModelInferenceTransport transport)
    {
        using var handler = new ClassifierHandler("allow", transport);
        using var http = new HttpClient(handler);
        var primary = new LanguageModelProviderOptions
        {
            Adapter = "OpenAICompatible", BaseUrl = "https://provider.example/v1/", ApiKey = "offline-fixture", DefaultModel = "legacy-model"
        };
        var descriptor = ModelCatalogFactory.Real().Default with { Key = "custom", ModelId = "catalog-model", Transport = transport };
        var services = new ServiceCollection();
        services.AddSingleton<IHttpClientFactory>(new HttpFactory(http));
        services.AddAgentCoreInfrastructure(FindAgents(), "Real", primary);
        services.AddSingleton<IModelCatalog>(new ConfigurationModelCatalog("custom", [descriptor]));
        await using var provider = services.BuildServiceProvider();
        Assert.Equal(TriggerCommandAuthorizationDecision.Allow, await provider.GetRequiredService<ITriggerCommandAuthorizer>()
            .AuthorizeCurrentTurnAsync("everyday at 9am, think about your memory", "en", TriggerCommandAction.Create));
        using var body = JsonDocument.Parse(Assert.Single(handler.Payloads));
        Assert.Equal("catalog-model", body.RootElement.GetProperty("model").GetString());
        Assert.EndsWith(transport == ModelInferenceTransport.Responses ? "/responses" : "/chat/completions", Assert.Single(handler.Paths));
        var messages = body.RootElement.GetProperty(transport == ModelInferenceTransport.Responses ? "input" : "messages").EnumerateArray();
        Assert.Contains(messages, message => message.GetProperty("role").GetString() == "developer");
        Assert.Equal(descriptor.DefaultReasoningEffort, transport == ModelInferenceTransport.Responses
            ? body.RootElement.GetProperty("reasoning").GetProperty("effort").GetString()
            : body.RootElement.GetProperty("reasoning_effort").GetString());
        Assert.False(primary.DeveloperRoleSupport);
        Assert.Equal("legacy-model", primary.DefaultModel);
        Assert.Equal(ModelInferenceTransport.ChatCompletions, primary.Transport);
    }

    [Theory]
    [InlineData("allow", TriggerCommandAuthorizationDecision.Allow)]
    [InlineData("deny", TriggerCommandAuthorizationDecision.Deny)]
    [InlineData("ambiguous", TriggerCommandAuthorizationDecision.Ambiguous)]
    [InlineData("invalid", TriggerCommandAuthorizationDecision.ClassifierInvalidOutput)]
    [InlineData("unavailable", TriggerCommandAuthorizationDecision.ClassifierUnavailable)]
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
        var verdict = await provider.GetRequiredService<ITriggerCommandAuthorizer>().AuthorizeWithDetailsAsync(
            "everyday at 9am, think about how you can consolidate your memory, experience", "en", TriggerCommandAction.Create);

        Assert.Equal(expected, verdict.Decision);
        using var body = JsonDocument.Parse(Assert.Single(handler.Payloads));
        Assert.Equal(ModelCatalogFactory.DeepSeekV41FlashModelId, body.RootElement.GetProperty("model").GetString());
        var messages = body.RootElement.GetProperty("messages").EnumerateArray().ToArray();
        Assert.Contains(messages, message => message.GetProperty("role").GetString() == "system");
        Assert.Contains(messages, message => message.GetProperty("role").GetString() == "developer"
            && message.GetProperty("content").GetString()!.Contains("Proposed action: Create"));
        Assert.DoesNotContain(messages, message => message.GetProperty("content").GetString()!.Contains("offline-fixture"));
        Assert.Equal("everyday at 9am, think about how you can consolidate your memory, experience",
            messages[^1].GetProperty("content").GetString());
    }

    private sealed class HttpFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class ClassifierHandler(string outcome, ModelInferenceTransport transport = ModelInferenceTransport.ChatCompletions) : HttpMessageHandler
    {
        public List<string> Payloads { get; } = [];
        public List<string> Paths { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Payloads.Add(await request.Content!.ReadAsStringAsync(ct));
            Paths.Add(request.RequestUri!.AbsolutePath);
            if (outcome == "unavailable") return new(HttpStatusCode.ServiceUnavailable);
            var text = outcome == "invalid" ? "invalid" : JsonSerializer.Serialize(new { decision = outcome });
            if (transport == ModelInferenceTransport.Responses)
            {
                var responseDelta = JsonSerializer.Serialize(new { type = "response.output_text.delta", delta = text });
                return new(HttpStatusCode.OK) { Content = new StringContent(
                    $"event: response.output_text.delta\ndata: {responseDelta}\n\nevent: response.completed\ndata: {{\"type\":\"response.completed\",\"response\":{{\"status\":\"completed\",\"output\":[]}}}}\n\n",
                    Encoding.UTF8, "text/event-stream") };
            }
            var delta = JsonSerializer.Serialize(new { choices = new[] { new { delta = new { content = text } } } });
            return new(HttpStatusCode.OK) { Content = new StringContent(
                $"data: {delta}\n\ndata: {{\"choices\":[{{\"delta\":{{}},\"finish_reason\":\"stop\"}}]}}\n\ndata: [DONE]\n\n",
                Encoding.UTF8, "text/event-stream") };
        }
    }

    private static string FindAgents()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "agents"))) directory = directory.Parent;
        return Path.Combine(directory!.FullName, "agents");
    }
}
