using System.Diagnostics;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Infrastructure.Providers;
using AgentCore.Infrastructure.Providers.OpenAICompatible;
using AgentCore.Infrastructure.Providers.SemanticResponses;
using Xunit.Abstractions;

namespace AgentCore.Infrastructure.Tests;

public sealed class ModelCatalogLiveCompatibilityTests(ITestOutputHelper output)
{
    [CatalogLiveTheory]
    [InlineData("gpt-6-luna")]
    [InlineData("claude-haiku-5.5")]
    [InlineData("gpt-6.1-sol")]
    [InlineData("deepseek-v41-flash")]
    public async Task Reasoning_tool_continuation_and_structured_final_are_independently_verified(string key)
    {
        var descriptor = ModelCatalogFactory.Real().Get(key)!;
        using var http = new HttpClient();
        ILanguageModel model = new SemanticResponseLanguageModel(new OpenAICompatibleLanguageModel(http, new()
        {
            Adapter = "OpenAICompatible", BaseUrl = "https://openrouter.ai/api/v1/", DefaultModel = descriptor.ModelId,
            ApiKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY"), Transport = descriptor.Transport,
            Tools = true, Vision = descriptor.Vision, StructuredOutput = descriptor.StructuredOutput,
            ReasoningObjectWire = true, Timeouts = new() { SetupSeconds = 30, StreamIdleSeconds = 60, TotalSeconds = 120 }
        }), preferResponseFunction: descriptor.PreferResponseFunction);
        var messages = new List<ModelMessage> { new(ModelRole.System, "Use lookup to retrieve the answer before replying. Retry lookup if it reports a transient error. Return the answer exactly as the tool reports it."), new(ModelRole.User, "What is the current fixture answer? You must call lookup first.") };
        var tools = new[] { new ModelToolDefinition("lookup", "Retrieve the current fixture answer", "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}") };
        var executed = 0; var input = 0; var tokens = 0; var timer = Stopwatch.StartNew(); var final = "";
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        for (var round = 0; round < 4; round++)
        {
            var calls = new List<ModelToolCall>();
            await foreach (var evt in model.GenerateAsync(new(Guid.NewGuid(), messages, MaxOutputTokens: 4096, Tools: tools,
                ReasoningEffort: descriptor.DefaultReasoningEffort, ResponseContract: new(false)), deadline.Token))
            {
                if (evt is ModelFailed failed) Assert.Fail($"Provider failed: {failed.Failure.Code}; {failed.Failure.FailureReason}; {failed.Failure.SafeMessage}");
                if (evt is ModelCompleted done) { input += done.InputTokens ?? 0; tokens += done.OutputTokens ?? 0; }
                if (evt is ModelToolCallEvent call) calls.Add(call.Call);
                if (evt is ModelSemanticResponseReady ready) final = ready.Response.DisplayText;
            }
            if (calls.Count == 0) break;
            messages.Add(new(ModelRole.Assistant, "", ToolCalls: calls));
            foreach (var call in calls)
            {
                Assert.Equal("lookup", call.Name);
                using var args = JsonDocument.Parse(call.ArgumentsJson); Assert.Equal(JsonValueKind.Object, args.RootElement.ValueKind);
                executed++;
                messages.Add(new(ModelRole.Tool, executed == 1 ? "{\"error\":\"transient_unavailable\",\"retryable\":true}" : "fixture-answer-7429", ToolCallId: call.Id, Name: call.Name));
            }
        }
        output.WriteLine(JsonSerializer.Serialize(new { key, transport = descriptor.Transport.ToString(), effort = descriptor.DefaultReasoningEffort,
            executedTools = executed, inputTokens = input, outputTokens = tokens, elapsedSeconds = timer.Elapsed.TotalSeconds, independentlyVerified = final.Contains("fixture-answer-7429") }));
        Assert.True(executed >= 2); Assert.Contains("fixture-answer-7429", final); Assert.True(input > 0); Assert.True(tokens > 0);
    }

    [CatalogLiveTheory]
    [InlineData("gpt-6-luna")]
    [InlineData("claude-haiku-5.5")]
    [InlineData("gpt-6.1-sol")]
    [InlineData("deepseek-v41-flash")]
    public async Task Image_input_and_structured_conversation_are_verified(string key)
    {
        var descriptor = ModelCatalogFactory.Real().Get(key)!;
        using var http = new HttpClient();
        var model = new SemanticResponseLanguageModel(new OpenAICompatibleLanguageModel(http, new()
        {
            Adapter = "OpenAICompatible", BaseUrl = "https://openrouter.ai/api/v1/", DefaultModel = descriptor.ModelId,
            ApiKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY"), Transport = descriptor.Transport,
            Tools = true, Vision = true, StructuredOutput = true, ReasoningObjectWire = true,
            Timeouts = new() { SetupSeconds = 30, StreamIdleSeconds = 60, TotalSeconds = 120 }
        }));
        // A deterministic 64x64 solid red PNG, generated locally, no private image content.
        var bytes = Convert.FromBase64String(RedPng);
        var final = "";
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await foreach (var evt in model.GenerateAsync(new(Guid.NewGuid(), [new(ModelRole.User, "What is the dominant color in this image? Reply with the color name.",
            Parts: [new ModelTextContent("What is the dominant color in this image? Reply with the color name."), new ModelImageContent("image/png", bytes, "fixture.png")])],
            MaxOutputTokens: 4096, ReasoningEffort: descriptor.DefaultReasoningEffort, ResponseContract: new(false)), deadline.Token))
        {
            if (evt is ModelFailed failed) Assert.Fail($"Image provider failure: {failed.Failure.Code}; {failed.Failure.FailureReason}");
            if (evt is ModelSemanticResponseReady ready) final = ready.Response.DisplayText;
        }
        Assert.Contains("red", final, StringComparison.OrdinalIgnoreCase);
        output.WriteLine($"{key}: image color independently verified; structured response accepted.");
    }

    private const string RedPng = "iVBORw0KGgoAAAANSUhEUgAAAEAAAABACAIAAAAlC+aJAAAAb0lEQVR4nO3PAQkAAAyEwO9feoshgnABdLep8QUNyPEFDcjxBQ3I8QUNyPEFDcjxBQ3I8QUNyPEFDcjxBQ3I8QUNyPEFDcjxBQ3I8QUNyPEFDcjxBQ3I8QUNyPEFDcjxBQ3I8QUNyPEFDcjxBQ3IPanc8OLDQitxAAAAAElFTkSuQmCC";

}

public sealed class CatalogLiveTheoryAttribute : TheoryAttribute
{
    public CatalogLiveTheoryAttribute()
    {
        if (Environment.GetEnvironmentVariable("AGENTCORE_MODEL_CATALOG_LIVE") != "1") Skip = "Explicit model catalog live opt-in required.";
        else if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENROUTER_API_KEY"))) Skip = "OpenRouter credential required.";
    }
}
