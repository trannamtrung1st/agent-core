using System.Net;
using System.Text;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Infrastructure.Providers.OpenAICompatible;

namespace AgentCore.Infrastructure.Tests;

public sealed class ProviderArgumentTypeTests
{
    public static IEnumerable<object[]> MalformedTypes()
    {
        foreach (var transport in new[] { ModelInferenceTransport.ChatCompletions, ModelInferenceTransport.Responses })
        foreach (var tool in new[] { ToolCatalog.BrowserClose, ToolCatalog.SkillsLoad })
        foreach (var value in new[] { "{}", "{\"ids\":[\"sensitive-procedure\"]}", "[]", "null", "true", "42" })
        foreach (var prefix in transport == ModelInferenceTransport.ChatCompletions ? new[] { false, true } : new[] { false })
            yield return [transport, tool, value, prefix];
    }

    [Theory]
    [MemberData(nameof(MalformedTypes))]
    public async Task Non_string_transport_arguments_cannot_become_executable_or_absent(
        ModelInferenceTransport transport, string tool, string raw, bool prefix)
    {
        using var value = JsonDocument.Parse(raw);
        var name = OpenAiCompatibleToolNames.ToWireName(tool);
        var item = new { type = "function_call", call_id = "typed-call", name, arguments = value.RootElement };
        var payload = transport == ModelInferenceTransport.Responses
            ? Event(new { type = "response.completed", response = new { status = "completed", output = new[] { item } } })
            : Event(new { choices = new[] { new { delta = new { tool_calls = new[] { new { index = 0, id = "typed-call", type = "function", function = new { name, arguments = value.RootElement } } } } } } })
                // A later well-typed fragment must not repair a malformed transport fragment.
                + Event(new { choices = new[] { new { delta = new { tool_calls = new[] { new { index = 0, function = new { arguments = prefix ? "\"]}" : "{}" } } } }, finish_reason = "tool_calls" } } }) + "data: [DONE]\n\n";
        if (prefix)
            payload = Event(new { choices = new[] { new { delta = new { tool_calls = new[] { new { index = 0, id = "typed-call", function = new { name, arguments = "{\"ids\":[\"" } } } } } } }) + payload;
        using var http = new HttpClient(new Handler(payload));
        var model = new OpenAICompatibleLanguageModel(http, new()
        { BaseUrl = "http://127.0.0.1/v1/", DefaultModel = "fixture", Tools = true, Transport = transport });
        var events = new List<ModelGenerationEvent>();
        await foreach (var e in model.GenerateAsync(new(Guid.NewGuid(), [], Tools: [ToolRegistry.Get(tool).ModelDefinition]))) events.Add(e);
        Assert.Empty(events.OfType<ModelFailed>());
        var call = Assert.Single(events.OfType<ModelToolCallEvent>()).Call;
        Assert.Equal("typed-call", call.Id);
        Assert.Equal(tool, call.Name);
        Assert.False(string.IsNullOrWhiteSpace(call.ArgumentsJson));
        if (tool == ToolCatalog.BrowserClose)
            Assert.False(BrowserToolArguments.TryValidateClose(call.ArgumentsJson, out _));
        Assert.ThrowsAny<JsonException>(() => JsonDocument.Parse(call.ArgumentsJson));
        Assert.DoesNotContain("sensitive-procedure", call.ArgumentsJson, StringComparison.Ordinal);
        Assert.Equal(ModelStopReason.ToolCalls, Assert.Single(events.OfType<ModelCompleted>()).Reason);
    }

    private static string Event(object payload) => "data: " + JsonSerializer.Serialize(payload) + "\n\n";
    private sealed class Handler(string payload) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(payload, Encoding.UTF8, "text/event-stream") });
    }
}
