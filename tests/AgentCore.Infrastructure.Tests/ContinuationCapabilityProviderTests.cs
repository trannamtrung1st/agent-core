using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Infrastructure.Providers.OpenAICompatible;

namespace AgentCore.Infrastructure.Tests;

public sealed class ContinuationCapabilityProviderTests
{
    [Fact]
    public void Application_assembly_does_not_reference_infrastructure_or_a_vendor_sdk()
    {
        var referenced = typeof(ToolCatalog).Assembly.GetReferencedAssemblies().Select(name => name.Name).ToArray();
        Assert.DoesNotContain("AgentCore.Infrastructure", referenced);
        Assert.DoesNotContain(referenced, name => name?.Contains("OpenAI", StringComparison.OrdinalIgnoreCase) == true);
    }

    [Fact]
    public async Task OpenAI_compatible_maps_both_continuation_schemas_and_results_without_vendor_events()
    {
        var send = ToolRegistry.Get(ToolCatalog.AppMessageSend).ModelDefinition;
        var load = ToolRegistry.Get(ToolCatalog.SkillsLoad).ModelDefinition;
        var handler = new RecordingHandler(StopStream());
        var model = Create(handler, tools: true);
        var executionId = Guid.Parse("019944af-00ee-7000-8000-0000000000e1");
        Assert.True(ApplicationMessageAdmission.TryCreateEffectKey(executionId, "m1", out var effectKey));
        const string messageText = "Still checking the order";
        var budgetAfter = ApplicationMessageBudget.Fresh().AfterAdmit(messageText.Length);
        var request = new ModelRequest(
            Guid.NewGuid(),
            [
                new ModelMessage(ModelRole.User, "hello"),
                new ModelMessage(
                    ModelRole.Assistant,
                    string.Empty,
                    ToolCalls:
                    [
                        new ModelToolCall("m1", ToolCatalog.AppMessageSend, $$"""{"text":"{{messageText}}"}"""),
                        new ModelToolCall("load-1", ToolCatalog.SkillsLoad, """{"ids":["order.lookup"]}""")
                    ]),
                new ModelMessage(
                    ModelRole.Tool,
                    ApplicationMessageAdmission.Success(effectKey, budgetAfter),
                    ToolCallId: "m1",
                    Name: ToolCatalog.AppMessageSend),
                new ModelMessage(
                    ModelRole.Tool,
                    """{"admitted":["order.lookup"],"alreadyActive":[],"rejected":[]}""",
                    ToolCallId: "load-1",
                    Name: ToolCatalog.SkillsLoad)
            ],
            Tools: [send, load]);

        var events = await CollectAsync(model, request);
        Assert.IsType<ModelTextDelta>(events[0]);
        Assert.DoesNotContain(events, item => item is ModelToolCallEvent);

        var body = handler.LastBody;
        Assert.Contains("app_message_send", body, StringComparison.Ordinal);
        Assert.Contains("skills_load", body, StringComparison.Ordinal);
        Assert.DoesNotContain("app.message.send", body, StringComparison.Ordinal);
        Assert.DoesNotContain("skills.load", body, StringComparison.Ordinal);
        Assert.Contains("\"additionalProperties\":false", body, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(body);
        var tools = document.RootElement.GetProperty("tools");
        Assert.Equal(2, tools.GetArrayLength());
        Assert.All(tools.EnumerateArray(), tool => Assert.Equal("function", tool.GetProperty("type").GetString()));
        var results = document.RootElement.GetProperty("messages").EnumerateArray()
            .Where(message => message.GetProperty("role").GetString() == "tool")
            .Select(message => message.GetProperty("content").GetString())
            .ToArray();
        Assert.Equal(2, results.Length);
        using (var success = JsonDocument.Parse(results[0]!))
        {
            Assert.True(success.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal(effectKey, success.RootElement.GetProperty("effectId").GetString());
            Assert.Equal(11, success.RootElement.GetProperty("remainingMessages").GetInt32());
            Assert.Equal(
                ApplicationMessagePolicy.Default.MaxAggregateCharactersPerExecution - messageText.Length,
                success.RootElement.GetProperty("remainingCharacters").GetInt32());
        }

        Assert.All(results, result =>
        {
            Assert.DoesNotContain(messageText, result, StringComparison.Ordinal);
            Assert.DoesNotContain("ORDER_PROCEDURE", result, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Malformed_and_replayed_arguments_are_rejected_while_explicit_object_arguments_are_preserved()
    {
        var body =
            "data: {\"choices\":[{\"delta\":{\"tool_calls\":[" +
            "{\"index\":0,\"id\":\"m1\",\"type\":\"function\",\"function\":{\"name\":\"app_message_send\",\"arguments\":\"{\\\"text\\\":\\\"Hi\\\"}\"}}," +
            "{\"index\":1,\"id\":\"load-1\",\"type\":\"function\",\"function\":{\"name\":\"skills_load\",\"arguments\":\"{\\\"ids\\\":[\\\"order.lookup\\\"],\\\"sessionId\\\":\\\"secret-session\\\"}\"}}," +
            "{\"index\":2,\"id\":\"m1\",\"type\":\"function\",\"function\":{\"name\":\"app_message_send\",\"arguments\":\"{}\"}}" +
            "]}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"{\\\"text\\\":\\\"Hi\\\"}\"}}]}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":3,\"id\":\"obj-1\",\"type\":\"function\",\"function\":{\"name\":\"skills_load\",\"arguments\":{\"ids\":[\"order.lookup\"]}}}]}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"tool_calls\"}]}\n\n" +
            "data: [DONE]\n\n";
        var handler = new RecordingHandler([Encoding.UTF8.GetBytes(body)]);
        var model = Create(handler, tools: true);
        var tools = new[]
        {
            ToolRegistry.Get(ToolCatalog.AppMessageSend).ModelDefinition,
            ToolRegistry.Get(ToolCatalog.SkillsLoad).ModelDefinition
        };
        var events = await CollectAsync(
            model,
            new ModelRequest(Guid.NewGuid(), [new ModelMessage(ModelRole.User, "hello")], Tools: tools));
        var calls = events.OfType<ModelToolCallEvent>().Select(item => item.Call).ToArray();
        Assert.Equal(4, calls.Length);
        Assert.Equal(ToolCatalog.AppMessageSend, calls[0].Name);
        Assert.Equal("""{"text":"Hi"}{"text":"Hi"}""", calls[0].ArgumentsJson);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<JsonElement>(calls[0].ArgumentsJson));

        Assert.Equal(ToolCatalog.SkillsLoad, calls[1].Name);
        using var extra = JsonDocument.Parse(calls[1].ArgumentsJson);
        Assert.False(SkillLoadAdmission.TryParseIds(extra.RootElement, out _, out var loadError));
        Assert.Contains("only ids", loadError, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret-session", loadError, StringComparison.Ordinal);

        Assert.Equal("m1", calls[2].Id);
        Assert.Equal(calls[0].Id, calls[2].Id);
        var executionId = Guid.Parse("019944af-00ee-7000-8000-0000000000e1");
        Assert.True(ApplicationMessageAdmission.TryCreateEffectKey(executionId, calls[0].Id, out var firstKey));
        Assert.True(ApplicationMessageAdmission.TryCreateEffectKey(executionId, calls[2].Id, out var replayKey));
        Assert.Equal(firstKey, replayKey);

        var objectArguments = calls.Single(call => call.Id == "obj-1").ArgumentsJson;
        Assert.Equal("""{"ids":["order.lookup"]}""", objectArguments);
        using var preserved = JsonDocument.Parse(objectArguments);
        Assert.True(SkillLoadAdmission.TryParseIds(preserved.RootElement, out var objectIds, out _));
        Assert.Equal(new[] { "order.lookup" }, objectIds);
        Assert.Equal(ModelStopReason.ToolCalls, Assert.IsType<ModelCompleted>(events[^1]).Reason);
    }

    [Fact]
    public async Task No_tools_provider_refuses_continuation_tools_before_any_provider_call()
    {
        var handler = new RecordingHandler(StopStream());
        var model = Create(handler, tools: false);
        var events = await CollectAsync(
            model,
            new ModelRequest(
                Guid.NewGuid(),
                [new ModelMessage(ModelRole.User, "hello")],
                Tools: [ToolRegistry.Get(ToolCatalog.AppMessageSend).ModelDefinition]));
        var failed = Assert.IsType<ModelFailed>(Assert.Single(events));
        Assert.Equal(ProviderErrorCode.UnsupportedCapability, failed.Failure.Code);
        Assert.Equal(0, handler.PostCount);
    }

    private static byte[] StopStream() =>
        Encoding.UTF8.GetBytes(
            "data: {\"choices\":[{\"delta\":{\"content\":\"Shown\"},\"finish_reason\":\"stop\"}]}\n\n" +
            "data: [DONE]\n\n");

    private static OpenAICompatibleLanguageModel Create(HttpMessageHandler handler, bool tools) =>
        new(
            new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri("http://127.0.0.1/") },
            new LanguageModelProviderOptions
            {
                Adapter = "OpenAICompatible",
                BaseUrl = "http://127.0.0.1/v1/",
                DefaultModel = "local-model",
                ApiKey = "test-key",
                Tools = tools
            });

    private static async Task<List<ModelGenerationEvent>> CollectAsync(ILanguageModel model, ModelRequest request)
    {
        var events = new List<ModelGenerationEvent>();
        await foreach (var item in model.GenerateAsync(request))
        {
            events.Add(item);
        }

        return events;
    }

    private sealed class RecordingHandler(IReadOnlyList<byte[]> chunks) : HttpMessageHandler
    {
        public RecordingHandler(byte[] body)
            : this([body])
        {
        }

        public int PostCount { get; private set; }

        public string LastBody { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            PostCount++;
            LastBody = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new MemoryStream(chunks.SelectMany(chunk => chunk).ToArray()))
                {
                    Headers = { ContentType = new MediaTypeHeaderValue("text/event-stream") }
                }
            };
        }
    }
}
