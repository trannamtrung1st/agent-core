using AgentCore.Application.Ports;
using AgentCore.Infrastructure.Providers;
using AgentCore.Infrastructure.Providers.SemanticResponses;
using AgentCore.Infrastructure.Providers.Synthetic;

namespace AgentCore.Infrastructure.Tests;

public sealed class SemanticResponseLanguageModelTests
{
    private static readonly ModelResponseContract Contract = new(SpeechWillBeUsed: false);
    private static readonly ModelRequest Bare = new(Guid.NewGuid(), [new ModelMessage(ModelRole.User, "Hi")]);
    private static readonly ModelRequest Contracted = Bare with { ResponseContract = Contract };

    [Fact]
    public async Task Null_contract_passes_text_delta_through()
    {
        var inner = new ScriptedInner([new ModelTextDelta("raw"), new ModelCompleted(ModelStopReason.Completed)]);
        var events = await CollectAsync(new SemanticResponseLanguageModel(inner), Bare);
        Assert.Equal(["raw"], events.OfType<ModelTextDelta>().Select(item => item.Text).ToArray());
        Assert.DoesNotContain(events, item => item is ModelDisplayDelta or ModelSemanticResponseReady);
        Assert.DoesNotContain(events, item => item is ModelFailed failed && failed.Failure.Code == ProviderErrorCode.InvalidResponse);
        Assert.Null(inner.LastRequest!.ResponseContract);
    }

    [Fact]
    public async Task Native_valid_json_becomes_semantic_ready_without_display_delta()
    {
        var json = """{"displayText":"Shown","speech":{"mode":"same"},"blocks":[]}""";
        var inner = new ScriptedInner(
            [new ModelTextDelta(json[..10]), new ModelTextDelta(json[10..]), new ModelCompleted(ModelStopReason.Completed)],
            structured: true);
        var events = await CollectAsync(new SemanticResponseLanguageModel(inner), Contracted);
        Assert.DoesNotContain(events, item => item is ModelTextDelta);
        Assert.DoesNotContain(events, item => item is ModelDisplayDelta);
        var ready = Assert.Single(events.OfType<ModelSemanticResponseReady>()).Response;
        Assert.Equal("Shown", ready.DisplayText);
        Assert.Equal(ModelSpeechMode.Same, ready.Speech.Mode);
        Assert.DoesNotContain("{", string.Join(string.Empty, events.OfType<ModelDisplayDelta>().Select(item => item.Text)));
    }

    [Theory]
    [InlineData("""{"speech":{"mode":"same"}}""")]
    [InlineData("""{"displayText":"","speech":{"mode":"same"}}""")]
    [InlineData("""{"displayText":"Shown","speech":{"mode":"maybe"}}""")]
    [InlineData("""{"displayText":"Shown","speech":{"mode":"custom"}}""")]
    [InlineData("""{"displayText":"Shown","speech":{"mode":"none","text":"no"}}""")]
    [InlineData("""{"displayText":"Shown","speech":{"mode":"same"},"blocks":[{"kind":"widget"}]}""")]
    [InlineData("not-json")]
    public async Task Native_invalid_payload_is_invalid_response(string json)
    {
        var inner = new ScriptedInner([new ModelTextDelta(json), new ModelCompleted(ModelStopReason.Completed)], structured: true);
        var events = await CollectAsync(new SemanticResponseLanguageModel(inner), Contracted);
        var failed = Assert.Single(events.OfType<ModelFailed>());
        Assert.Equal(ProviderErrorCode.InvalidResponse, failed.Failure.Code);
        Assert.DoesNotContain(json, failed.Failure.SafeMessage, StringComparison.Ordinal);
        Assert.DoesNotContain(events, item => item is ModelDisplayDelta or ModelSemanticResponseReady);
    }

    [Fact]
    public async Task Native_oversized_display_is_invalid_response()
    {
        var json = "{\"displayText\":\"" + new string('a', AssistantResponseSchema.MaxDisplayCharacters + 1)
            + "\",\"speech\":{\"mode\":\"same\"}}";
        var inner = new ScriptedInner([new ModelTextDelta(json), new ModelCompleted(ModelStopReason.Completed)], structured: true);
        var events = await CollectAsync(new SemanticResponseLanguageModel(inner), Contracted);
        Assert.Equal(ProviderErrorCode.InvalidResponse, Assert.Single(events.OfType<ModelFailed>()).Failure.Code);
    }

    [Fact]
    public async Task Native_too_many_blocks_is_invalid_response()
    {
        var blocks = string.Join(',', Enumerable.Range(0, AssistantResponseSchema.MaxBlocks + 1)
            .Select(_ => """{"kind":"markdown","text":"x"}"""));
        var json = """{"displayText":"Shown","speech":{"mode":"same"},"blocks":[""" + blocks + "]}";
        var inner = new ScriptedInner([new ModelTextDelta(json), new ModelCompleted(ModelStopReason.Completed)], structured: true);
        var events = await CollectAsync(new SemanticResponseLanguageModel(inner), Contracted);
        Assert.Equal(ProviderErrorCode.InvalidResponse, Assert.Single(events.OfType<ModelFailed>()).Failure.Code);
    }

    [Fact]
    public async Task Native_reasoning_passes_through_separately()
    {
        var json = """{"displayText":"Shown","speech":{"mode":"same"}}""";
        var inner = new ScriptedInner(
            [
                new ModelReasoningDelta("hidden"),
                new ModelTextDelta(json),
                new ModelCompleted(ModelStopReason.Completed)
            ],
            structured: true);
        var events = await CollectAsync(new SemanticResponseLanguageModel(inner), Contracted);
        Assert.Equal("hidden", Assert.Single(events.OfType<ModelReasoningDelta>()).Text);
        Assert.Equal("Shown", Assert.Single(events.OfType<ModelSemanticResponseReady>()).Response.DisplayText);
    }

    [Fact]
    public async Task Native_tool_round_does_not_require_envelope()
    {
        var call = new ModelToolCall("1", "knowledge.retrieve", "{}");
        var inner = new ScriptedInner(
            [new ModelToolCallEvent(call), new ModelCompleted(ModelStopReason.ToolCalls)],
            structured: true);
        var events = await CollectAsync(new SemanticResponseLanguageModel(inner), Contracted);
        Assert.Single(events.OfType<ModelToolCallEvent>());
        Assert.Equal(ModelStopReason.ToolCalls, Assert.Single(events.OfType<ModelCompleted>()).Reason);
        Assert.DoesNotContain(events, item => item is ModelSemanticResponseReady or ModelFailed);
    }

    [Fact]
    public async Task Native_post_tool_round_requires_envelope()
    {
        var json = """{"displayText":"After tools","speech":{"mode":"same"}}""";
        var inner = new ScriptedInner(
            [new ModelTextDelta(json), new ModelCompleted(ModelStopReason.Completed)],
            structured: true);
        var events = await CollectAsync(new SemanticResponseLanguageModel(inner), Contracted);
        Assert.Equal("After tools", Assert.Single(events.OfType<ModelSemanticResponseReady>()).Response.DisplayText);
    }

    [Fact]
    public async Task Compatibility_plain_display_streams_and_matches_ready()
    {
        var inner = new ScriptedInner(
        [
            new ModelTextDelta("Hello"),
            new ModelTextDelta(" world."),
            new ModelCompleted(ModelStopReason.Completed)
        ]);
        var events = await CollectAsync(new SemanticResponseLanguageModel(inner), Contracted);
        var streamed = string.Concat(events.OfType<ModelDisplayDelta>().Select(item => item.Text));
        var ready = Assert.Single(events.OfType<ModelSemanticResponseReady>()).Response;
        Assert.Equal("Hello world.", streamed);
        Assert.Equal(streamed, ready.DisplayText);
        Assert.Equal(ModelSpeechMode.Same, ready.Speech.Mode);
        Assert.DoesNotContain(events, item => item is ModelTextDelta);
    }

    [Fact]
    public async Task Compatibility_speech_only_without_display_is_invalid_response()
    {
        var inner = new ScriptedInner(
        [
            new ModelTextDelta("[[speech:Spoken only]]"),
            new ModelCompleted(ModelStopReason.Completed)
        ]);
        var events = await CollectAsync(new SemanticResponseLanguageModel(inner), Contracted);
        Assert.Equal(ProviderErrorCode.InvalidResponse, Assert.Single(events.OfType<ModelFailed>()).Failure.Code);
        Assert.DoesNotContain(events, item => item is ModelSemanticResponseReady);
    }

    [Fact]
    public async Task Compatibility_none_marker_becomes_none()
    {
        var inner = new ScriptedInner(
        [
            new ModelTextDelta("Chart only.[[speech:none]]"),
            new ModelCompleted(ModelStopReason.Completed)
        ]);
        var events = await CollectAsync(new SemanticResponseLanguageModel(inner), Contracted);
        var ready = Assert.Single(events.OfType<ModelSemanticResponseReady>()).Response;
        Assert.Equal("Chart only.", ready.DisplayText);
        Assert.Equal(ModelSpeechMode.None, ready.Speech.Mode);
    }

    [Fact]
    public async Task Compatibility_chunked_custom_then_none_keeps_custom_speech()
    {
        var inner = new ScriptedInner(
        [
            new ModelTextDelta("Visible answer.[[speech:Speak this]]"),
            new ModelTextDelta("[[speech:none]]"),
            new ModelCompleted(ModelStopReason.Completed)
        ]);
        var events = await CollectAsync(new SemanticResponseLanguageModel(inner), Contracted);
        var ready = Assert.Single(events.OfType<ModelSemanticResponseReady>()).Response;
        Assert.Equal("Visible answer.", ready.DisplayText);
        Assert.Equal(ModelSpeechMode.Custom, ready.Speech.Mode);
        Assert.Equal("Speak this", ready.Speech.Text);
    }

    [Fact]
    public async Task Compatibility_chunked_none_then_custom_keeps_none_speech()
    {
        var inner = new ScriptedInner(
        [
            new ModelTextDelta("Visible answer.[[speech:none]]"),
            new ModelTextDelta("[[speech:Speak this]]"),
            new ModelCompleted(ModelStopReason.Completed)
        ]);
        var events = await CollectAsync(new SemanticResponseLanguageModel(inner), Contracted);
        var ready = Assert.Single(events.OfType<ModelSemanticResponseReady>()).Response;
        Assert.Equal("Visible answer.", ready.DisplayText);
        Assert.Equal(ModelSpeechMode.None, ready.Speech.Mode);
        Assert.DoesNotContain(events, item => item is ModelSemanticResponseReady readyEvent
            && readyEvent.Response.Speech.Mode == ModelSpeechMode.Custom);
    }

    [Fact]
    public async Task Compatibility_speech_marker_becomes_custom()
    {
        var inner = new ScriptedInner(
        [
            new ModelTextDelta("Shown.[[speech:Spoken]]"),
            new ModelCompleted(ModelStopReason.Completed)
        ]);
        var events = await CollectAsync(new SemanticResponseLanguageModel(inner), Contracted);
        var ready = Assert.Single(events.OfType<ModelSemanticResponseReady>()).Response;
        Assert.Equal("Shown.", ready.DisplayText);
        Assert.Equal(ModelSpeechMode.Custom, ready.Speech.Mode);
        Assert.Equal("Spoken", ready.Speech.Text);
        Assert.DoesNotContain(events, item => item is ModelDisplayDelta delta && delta.Text.Contains("[[", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Compatibility_rich_blocks_normalize()
    {
        var inner = new ScriptedInner(
        [
            new ModelTextDelta("Intro.[[md:**Delayed**]][[attachment:notes.txt]][[artifact:a1]]"),
            new ModelCompleted(ModelStopReason.Completed)
        ]);
        var events = await CollectAsync(new SemanticResponseLanguageModel(inner), Contracted);
        var ready = Assert.Single(events.OfType<ModelSemanticResponseReady>()).Response;
        Assert.Equal("Intro.", ready.DisplayText);
        Assert.Equal(
            [
                ModelResponseBlockKind.Markdown,
                ModelResponseBlockKind.AttachmentReference,
                ModelResponseBlockKind.ArtifactReference
            ],
            ready.Blocks.Select(block => block.Kind).ToArray());
    }

    [Fact]
    public async Task Compatibility_incomplete_marker_is_withheld()
    {
        var inner = new ScriptedInner(
        [
            new ModelTextDelta("Hello [[md:**Del"),
            new ModelTextDelta("ayed**]] done"),
            new ModelCompleted(ModelStopReason.Completed)
        ]);
        var events = await CollectAsync(new SemanticResponseLanguageModel(inner), Contracted);
        Assert.Equal("Hello  done", string.Concat(events.OfType<ModelDisplayDelta>().Select(item => item.Text)));
        Assert.DoesNotContain(events, item => item is ModelDisplayDelta delta && delta.Text.Contains("[[", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Compatibility_malformed_marker_does_not_enter_speech()
    {
        var inner = new ScriptedInner(
        [
            new ModelTextDelta("Hello [[speech:secret"),
            new ModelCompleted(ModelStopReason.Completed)
        ]);
        var events = await CollectAsync(new SemanticResponseLanguageModel(inner), Contracted);
        var ready = Assert.Single(events.OfType<ModelSemanticResponseReady>()).Response;
        Assert.Equal("Hello ", ready.DisplayText);
        Assert.Equal(ModelSpeechMode.Same, ready.Speech.Mode);
        Assert.Null(ready.Speech.Text);
        Assert.DoesNotContain("secret", ready.Speech.Text ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("[[", ready.DisplayText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Compatibility_reasoning_and_tools_still_work()
    {
        var call = new ModelToolCall("1", "attachments.read", "{}");
        var inner = new ScriptedInner(
        [
            new ModelReasoningDelta("think"),
            new ModelToolCallEvent(call),
            new ModelCompleted(ModelStopReason.ToolCalls)
        ]);
        var events = await CollectAsync(new SemanticResponseLanguageModel(inner), Contracted);
        Assert.Equal("think", Assert.Single(events.OfType<ModelReasoningDelta>()).Text);
        Assert.Single(events.OfType<ModelToolCallEvent>());
        Assert.DoesNotContain(events, item => item is ModelSemanticResponseReady);
    }

    [Fact]
    public async Task Compatibility_injects_one_bounded_instruction_when_unstructured()
    {
        var inner = new ScriptedInner([new ModelTextDelta("OK."), new ModelCompleted(ModelStopReason.Completed)]);
        var voice = Contracted with { ResponseContract = new ModelResponseContract(SpeechWillBeUsed: true) };
        _ = await CollectAsync(new SemanticResponseLanguageModel(inner), voice);
        var sent = inner.LastRequest!;
        Assert.Equal(2, sent.Messages.Count);
        Assert.Equal("Hi", sent.Messages[0].Text);
        var instruction = sent.Messages[1];
        Assert.Equal(ModelRole.System, instruction.Role);
        Assert.StartsWith(AssistantResponseSchema.CompatibilityInstructionPrefix, instruction.Text, StringComparison.Ordinal);
        Assert.Contains("[[speech:", instruction.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("You are", instruction.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("examiner", instruction.Text, StringComparison.OrdinalIgnoreCase);
        Assert.True(instruction.Text.Length < 800);
    }

    [Fact]
    public async Task Native_does_not_inject_compatibility_instruction()
    {
        var json = """{"displayText":"Shown","speech":{"mode":"same"}}""";
        var inner = new ScriptedInner(
            [new ModelTextDelta(json), new ModelCompleted(ModelStopReason.Completed)],
            structured: true);
        _ = await CollectAsync(new SemanticResponseLanguageModel(inner), Contracted);
        Assert.Single(inner.LastRequest!.Messages);
        Assert.Equal("Hi", inner.LastRequest.Messages[0].Text);
    }

    [Fact]
    public async Task Synthetic_scripted_path_uses_the_decorator()
    {
        var model = new SemanticResponseLanguageModel(new ScriptedLanguageModel(ScriptedLanguageModel.ShortChunks));
        var events = await CollectAsync(model, Contracted);
        var streamed = string.Concat(events.OfType<ModelDisplayDelta>().Select(item => item.Text));
        Assert.Equal("OK.", streamed);
        Assert.Equal(streamed, Assert.Single(events.OfType<ModelSemanticResponseReady>()).Response.DisplayText);
        Assert.DoesNotContain(events, item => item is ModelTextDelta);
    }

    [Fact]
    public async Task Catalog_bound_scripted_native_json_becomes_semantic_ready()
    {
        var inner = new CatalogBoundLanguageModel(
            new ScriptedLanguageModel(),
            new ModelCapabilities(StreamingText: true, Cancellation: true, Tools: true, StructuredOutput: true));
        var events = await CollectAsync(new SemanticResponseLanguageModel(inner), Contracted);
        var ready = Assert.Single(events.OfType<ModelSemanticResponseReady>()).Response;
        Assert.Equal("Hello from synthetic.", ready.DisplayText);
        Assert.Equal(ModelSpeechMode.Same, ready.Speech.Mode);
        Assert.DoesNotContain(events, item => item is ModelTextDelta);
        Assert.DoesNotContain(events, item => item is ModelDisplayDelta);
    }

    [Fact]
    public async Task Catalog_bound_scripted_speech_none_omits_speech_text()
    {
        var inner = new CatalogBoundLanguageModel(
            new ScriptedLanguageModel(),
            new ModelCapabilities(StreamingText: true, Cancellation: true, Tools: true, StructuredOutput: true));
        var request = new ModelRequest(
            Guid.NewGuid(),
            [new ModelMessage(ModelRole.User, "[test:speech-none]")],
            ResponseContract: Contract);
        var events = await CollectAsync(new SemanticResponseLanguageModel(inner), request);
        var ready = Assert.Single(events.OfType<ModelSemanticResponseReady>()).Response;
        Assert.Equal("Shown only.", ready.DisplayText);
        Assert.Equal(ModelSpeechMode.None, ready.Speech.Mode);
        Assert.Null(ready.Speech.Text);
    }

    private static async Task<List<ModelGenerationEvent>> CollectAsync(ILanguageModel model, ModelRequest request)
    {
        var listed = new List<ModelGenerationEvent>();
        await foreach (var item in model.GenerateAsync(request))
        {
            listed.Add(item);
        }

        return listed;
    }

    private sealed class ScriptedInner(IReadOnlyList<ModelGenerationEvent> events, bool structured = false) : ILanguageModel
    {
        public ModelCapabilities Capabilities { get; } = new(
            StreamingText: true,
            Cancellation: true,
            Tools: true,
            StructuredOutput: structured);

        public ModelRequest? LastRequest { get; private set; }

        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(
            ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            await Task.Yield();
            foreach (var item in events)
            {
                yield return item;
            }
        }
    }
}
