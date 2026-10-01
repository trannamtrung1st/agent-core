using AgentCore.Application.Execution;
using AgentCore.Application.Memory;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
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
        Assert.False(string.IsNullOrEmpty(failed.Failure.FailureReason));
        Assert.Equal(ProviderResponseChannel.StructuredOutput, failed.Failure.ResponseChannel);
        Assert.DoesNotContain(events, item => item is ModelDisplayDelta or ModelSemanticResponseReady);
    }

    [Fact]
    public async Task Native_length_limit_without_an_envelope_is_output_limit()
    {
        var inner = new ScriptedInner(
            [new ModelCompleted(ModelStopReason.LengthLimit)],
            structured: true);
        var events = await CollectAsync(new SemanticResponseLanguageModel(inner), Contracted);
        var failed = Assert.Single(events.OfType<ModelFailed>());
        Assert.Equal(ProviderFailureReason.OutputLimit, failed.Failure.FailureReason);
        Assert.Equal(ProviderResponseChannel.StructuredOutput, failed.Failure.ResponseChannel);
        Assert.Equal("The model output was cut off before a complete response.", failed.Failure.SafeMessage);
        Assert.DoesNotContain("Malformed", failed.Failure.SafeMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Native_length_limit_with_broken_json_is_output_limit()
    {
        var inner = new ScriptedInner(
            [new ModelTextDelta("{\"displayText\":"), new ModelCompleted(ModelStopReason.LengthLimit)],
            structured: true);
        var events = await CollectAsync(new SemanticResponseLanguageModel(inner), Contracted);
        var failed = Assert.Single(events.OfType<ModelFailed>());
        Assert.Equal(ProviderFailureReason.OutputLimit, failed.Failure.FailureReason);
        Assert.DoesNotContain("displayText", failed.Failure.SafeMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Native_length_limit_keeps_a_parsed_malformed_envelope()
    {
        var json = """{"speech":{"mode":"same"}}""";
        var inner = new ScriptedInner(
            [new ModelTextDelta(json), new ModelCompleted(ModelStopReason.LengthLimit)],
            structured: true);
        var events = await CollectAsync(new SemanticResponseLanguageModel(inner), Contracted);
        var failed = Assert.Single(events.OfType<ModelFailed>());
        Assert.Equal(ProviderFailureReason.MissingDisplayText, failed.Failure.FailureReason);
        Assert.Equal("Malformed assistant envelope.", failed.Failure.SafeMessage);
        Assert.DoesNotContain(json, failed.Failure.SafeMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Marker_length_limit_without_display_is_output_limit()
    {
        var inner = new ScriptedInner(
            [new ModelCompleted(ModelStopReason.LengthLimit)],
            tools: false);
        var events = await CollectAsync(new SemanticResponseLanguageModel(inner), Contracted);
        var failed = Assert.Single(events.OfType<ModelFailed>());
        Assert.Equal(ProviderFailureReason.OutputLimit, failed.Failure.FailureReason);
        Assert.Equal(ProviderResponseChannel.MarkerCompatibility, failed.Failure.ResponseChannel);
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
    public async Task Length_limit_without_a_semantic_payload_is_output_limit()
    {
        var native = new ScriptedInner(
            [new ModelCompleted(ModelStopReason.LengthLimit)],
            structured: true);
        var nativeFailed = Assert.Single(
            (await CollectAsync(new SemanticResponseLanguageModel(native), Contracted)).OfType<ModelFailed>());
        Assert.Equal(ProviderFailureReason.OutputLimit, nativeFailed.Failure.FailureReason);
        Assert.Equal(ProviderResponseChannel.StructuredOutput, nativeFailed.Failure.ResponseChannel);
        Assert.DoesNotContain("Malformed assistant envelope.", nativeFailed.Failure.SafeMessage, StringComparison.Ordinal);
        Assert.NotEqual(ProviderFailureReason.MissingDisplayText, nativeFailed.Failure.FailureReason);

        var marker = new ScriptedInner(
            [new ModelCompleted(ModelStopReason.LengthLimit)],
            tools: false);
        var markerFailed = Assert.Single(
            (await CollectAsync(new SemanticResponseLanguageModel(marker), Contracted)).OfType<ModelFailed>());
        Assert.Equal(ProviderFailureReason.OutputLimit, markerFailed.Failure.FailureReason);
        Assert.Equal(ProviderResponseChannel.MarkerCompatibility, markerFailed.Failure.ResponseChannel);
        Assert.DoesNotContain("Malformed assistant envelope.", markerFailed.Failure.SafeMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Length_limit_keeps_a_parsed_malformed_envelope()
    {
        var json = """{"speech":{"mode":"same"}}""";
        var inner = new ScriptedInner(
            [new ModelTextDelta(json), new ModelCompleted(ModelStopReason.LengthLimit)],
            structured: true);
        var failed = Assert.Single(
            (await CollectAsync(new SemanticResponseLanguageModel(inner), Contracted)).OfType<ModelFailed>());
        Assert.Equal(ProviderFailureReason.MissingDisplayText, failed.Failure.FailureReason);
        Assert.Contains("Malformed assistant envelope.", failed.Failure.SafeMessage, StringComparison.Ordinal);
        Assert.DoesNotContain(json, failed.Failure.SafeMessage, StringComparison.Ordinal);
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
        var inner = new ScriptedInner(
            [new ModelTextDelta("OK."), new ModelCompleted(ModelStopReason.Completed)],
            tools: false);
        var voice = Contracted with { ResponseContract = new ModelResponseContract(SpeechWillBeUsed: true) };
        _ = await CollectAsync(new SemanticResponseLanguageModel(inner), voice);
        var sent = inner.LastRequest!;
        Assert.Equal(2, sent.Messages.Count);
        Assert.Equal("Hi", sent.Messages[0].Text);
        var instruction = sent.Messages[1];
        Assert.Equal(ModelRole.System, instruction.Role);
        Assert.StartsWith(AssistantResponseSchema.CompatibilityInstructionPrefix, instruction.Text, StringComparison.Ordinal);
        Assert.Contains("[[speech:", instruction.Text, StringComparison.Ordinal);
        Assert.Contains("Complete with one chat.respond", instruction.Text, StringComparison.Ordinal);
        Assert.Contains("Do not emit JSON", instruction.Text, StringComparison.Ordinal);
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
    public async Task Function_channel_normalizes_the_response_tool_and_hides_it()
    {
        var json = """
            {"displayText":"Noted.","speech":{"mode":"same","text":null},"blocks":[],"memory":[{"operation":"upsert","kind":"fact","subject":"editor","content":"Rider","scopeHint":null,"source":"userExplicit"}]}
            """;
        var inner = new ScriptedInner(
        [
            new ModelToolCallEvent(new ModelToolCall("call-1", AssistantResponseSchema.ResponseFunctionName, json)),
            new ModelCompleted(ModelStopReason.ToolCalls)
        ]);
        var events = await CollectAsync(new SemanticResponseLanguageModel(inner), Contracted);
        var ready = Assert.Single(events.OfType<ModelSemanticResponseReady>()).Response;
        Assert.Equal("Rider", Assert.Single(ready.Memory!).Content);
        Assert.DoesNotContain(events, item => item is ModelToolCallEvent);
        Assert.Equal(ModelStopReason.Completed, Assert.Single(events.OfType<ModelCompleted>()).Reason);
        Assert.Contains(inner.LastRequest!.Tools!, tool => tool.Name == AssistantResponseSchema.ResponseFunctionName);
        Assert.Equal(ModelToolChoice.Named, inner.LastRequest.ToolChoice);
        Assert.Equal(AssistantResponseSchema.ResponseFunctionName, inner.LastRequest.ToolChoiceName);
    }

    [Fact]
    public async Task Function_channel_forwards_continuation_tools_without_treating_them_as_the_response()
    {
        var inner = new ScriptedInner(
        [
            new ModelToolCallEvent(new ModelToolCall("m1", ToolCatalog.AppMessageSend, """{"text":"Still checking the order"}""")),
            new ModelToolCallEvent(new ModelToolCall("load-1", ToolCatalog.SkillsLoad, """{"ids":["order.lookup"]}""")),
            new ModelCompleted(ModelStopReason.ToolCalls)
        ]);
        var request = Contracted with
        {
            Tools =
            [
                ToolRegistry.Get(ToolCatalog.AppMessageSend).ModelDefinition,
                ToolRegistry.Get(ToolCatalog.SkillsLoad).ModelDefinition
            ]
        };
        var events = await CollectAsync(new SemanticResponseLanguageModel(inner), request);
        Assert.Equal(
            [ToolCatalog.AppMessageSend, ToolCatalog.SkillsLoad],
            events.OfType<ModelToolCallEvent>().Select(item => item.Call.Name).ToArray());
        Assert.Equal(ModelStopReason.ToolCalls, Assert.Single(events.OfType<ModelCompleted>()).Reason);
        Assert.DoesNotContain(events, item => item is ModelSemanticResponseReady);
        Assert.Contains(inner.LastRequest!.Tools!, tool => tool.Name == ToolCatalog.AppMessageSend);
        Assert.Contains(inner.LastRequest.Tools!, tool => tool.Name == ToolCatalog.SkillsLoad);
        Assert.Contains(inner.LastRequest.Tools!, tool => tool.Name == AssistantResponseSchema.ResponseFunctionName);
        Assert.Equal(ModelToolChoice.Required, inner.LastRequest.ToolChoice);
    }

    [Fact]
    public async Task Function_channel_rejects_chat_respond_with_empty_display_text()
    {
        var json = """
            {"disposition":"Complete","action":{"kind":"chat.respond"},"displayText":"","speech":{"mode":"same","text":null},"blocks":[],"memory":[]}
            """;
        var inner = new ScriptedInner(
        [
            new ModelToolCallEvent(new ModelToolCall("call-1", AssistantResponseSchema.ResponseFunctionName, json)),
            new ModelCompleted(ModelStopReason.ToolCalls)
        ]);
        var events = await CollectAsync(new SemanticResponseLanguageModel(inner), Contracted);
        var failed = Assert.Single(events.OfType<ModelFailed>());
        Assert.Equal(ProviderFailureReason.MissingDisplayText, failed.Failure.FailureReason);
        Assert.Equal(ProviderResponseChannel.ResponseFunction, failed.Failure.ResponseChannel);
        Assert.DoesNotContain(json, failed.Failure.SafeMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Function_channel_invalid_memory_proposal_carries_bounded_failure_detail()
    {
        var json = """{"displayText":"Shown","speech":{"mode":"same"},"blocks":[],"memory":[{"operation":"upsert","kind":"fact","subject":"token"}]}""";
        var inner = new ScriptedInner(
        [
            new ModelToolCallEvent(new ModelToolCall("call-1", AssistantResponseSchema.ResponseFunctionName, json)),
            new ModelCompleted(ModelStopReason.ToolCalls)
        ]);
        var events = await CollectAsync(new SemanticResponseLanguageModel(inner), Contracted);
        var failed = Assert.Single(events.OfType<ModelFailed>());
        Assert.Equal(ProviderFailureReason.InvalidMemoryProposal, failed.Failure.FailureReason);
        Assert.Equal(ProviderResponseChannel.ResponseFunction, failed.Failure.ResponseChannel);
        Assert.DoesNotContain("token", failed.Failure.SafeMessage, StringComparison.Ordinal);
        Assert.DoesNotContain(json, failed.Failure.SafeMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Plain_text_model_does_not_receive_the_response_function()
    {
        var inner = new ScriptedInner(
            [new ModelTextDelta("OK."), new ModelCompleted(ModelStopReason.Completed)],
            tools: false);
        var events = await CollectAsync(new SemanticResponseLanguageModel(inner), Contracted);
        Assert.Equal("OK.", Assert.Single(events.OfType<ModelSemanticResponseReady>()).Response.DisplayText);
        Assert.Null(inner.LastRequest!.Tools);
        Assert.Equal(ModelToolChoice.Auto, inner.LastRequest.ToolChoice);
    }

    [Fact]
    public async Task Model_output_drops_application_and_admin_memory_sources()
    {
        var marker = """
            Noted. [[memory:[{"operation":"upsert","kind":"fact","subject":"rank","content":"hidden","source":"admin"},{"operation":"upsert","kind":"fact","subject":"tone","content":"warm","source":"userExplicit"}] ]]
            """;
        var inner = new ScriptedInner(
            [new ModelTextDelta(marker), new ModelCompleted(ModelStopReason.Completed)]);
        var events = await CollectAsync(new SemanticResponseLanguageModel(inner), Contracted);
        var ready = Assert.Single(events.OfType<ModelSemanticResponseReady>()).Response;
        var proposal = Assert.Single(ready.Memory!);
        Assert.Equal(MemoryProposalSource.UserExplicit, proposal.Source);
        Assert.Equal("tone", proposal.Subject);
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

    [Fact]
    public void Strict_schema_requires_agent_step_fields_and_rejects_extra_properties()
    {
        using var schema = System.Text.Json.JsonDocument.Parse(AssistantResponseSchema.JsonSchemaJson);
        var root = schema.RootElement;
        Assert.False(root.GetProperty("additionalProperties").GetBoolean());
        var required = root.GetProperty("required").EnumerateArray().Select(item => item.GetString()!).ToArray();
        Assert.Equal(
            ["disposition", "action", "displayText", "speech", "blocks", "memory"],
            required);
        var properties = root.GetProperty("properties");
        Assert.Equal(
            ["Continue", "Wait", "Complete", "Blocked"],
            properties.GetProperty("disposition").GetProperty("enum").EnumerateArray().Select(item => item.GetString()!).ToArray());
        var action = properties.GetProperty("action");
        var actionObject = action.GetProperty("anyOf").EnumerateArray()
            .Single(item => item.TryGetProperty("type", out var type) && type.GetString() == "object");
        Assert.False(actionObject.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(
            "chat.respond",
            actionObject.GetProperty("properties").GetProperty("kind").GetProperty("enum")[0].GetString());
        Assert.Contains("disposition", AssistantResponseSchema.ResponseFunction.ParametersJson, StringComparison.Ordinal);
        Assert.Equal(AssistantResponseSchema.JsonSchemaJson, AssistantResponseSchema.ResponseFunction.ParametersJson);
        Assert.DoesNotContain("sessionId", AssistantResponseSchema.JsonSchemaJson, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Contract_complete_chat_respond_normalizes_one_chat_action(bool native)
    {
        var json = """
            {"disposition":"Complete","action":{"kind":"chat.respond"},"displayText":"Shown","speech":{"mode":"same","text":null},"blocks":[],"memory":[]}
            """;
        var ready = await ReadyAsync(json, native);
        Assert.Equal("Complete", ready.Disposition);
        Assert.Equal(AgentStepNormalizer.ChatRespondKind, ready.ActionKind);
        Assert.True(ready.ActionSpecified);
        var step = Step(ready);
        Assert.Equal(AgentStepDisposition.Complete, step.Disposition);
        var chat = Assert.IsType<ChatRespondAction>(Assert.Single(step.Actions));
        Assert.Equal("Shown", chat.DisplayText);
        Assert.False(AgentStepController.Decide(step).ScheduleAnotherGeneration);
        Assert.True(AgentStepController.Decide(step).ExecuteChat);
    }

    [Theory]
    [InlineData(true, "Wait")]
    [InlineData(false, "Wait")]
    [InlineData(true, "Blocked")]
    [InlineData(false, "Blocked")]
    public async Task Contract_return_control_dispositions_do_not_invent_chat(bool native, string disposition)
    {
        var json = $$"""
            {"disposition":"{{disposition}}","action":null,"displayText":"","speech":{"mode":"none","text":null},"blocks":[],"memory":[]}
            """;
        var ready = await ReadyAsync(json, native);
        Assert.Equal(disposition, ready.Disposition);
        Assert.Null(ready.ActionKind);
        Assert.True(ready.ActionSpecified);
        var step = Step(ready);
        Assert.Equal(Enum.Parse<AgentStepDisposition>(disposition), step.Disposition);
        Assert.Empty(step.Actions);
        var decision = AgentStepController.Decide(step);
        Assert.False(decision.ExecuteChat);
        Assert.False(decision.ScheduleAnotherGeneration);
        Assert.False(decision.StageMemoryAfterChatSuccess);
        Assert.Equal(
            disposition == "Wait" ? AgentStepEffect.WaitForExternalInput : AgentStepEffect.BlockActivation,
            decision.Effect);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Contract_invalid_action_is_invalid_response(bool native)
    {
        var json = """
            {"disposition":"Complete","action":{"kind":"teams.reply"},"displayText":"Shown","speech":{"mode":"same","text":null},"blocks":[],"memory":[]}
            """;
        var events = await CollectAsync(Channel(json, native), Contracted);
        var failed = Assert.Single(events.OfType<ModelFailed>());
        Assert.Equal(ProviderErrorCode.InvalidResponse, failed.Failure.Code);
        Assert.Equal(ProviderFailureReason.UnknownAction, failed.Failure.FailureReason);
        Assert.Equal(
            native ? ProviderResponseChannel.StructuredOutput : ProviderResponseChannel.ResponseFunction,
            failed.Failure.ResponseChannel);
        Assert.DoesNotContain(events, item => item is ModelSemanticResponseReady or ModelDisplayDelta);
        Assert.DoesNotContain("teams.reply", failed.Failure.SafeMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Omitted_step_fields_default_to_complete_chat_respond()
    {
        var json = """{"displayText":"Shown","speech":{"mode":"same"},"blocks":[]}""";
        var ready = await ReadyAsync(json, native: true);
        Assert.Null(ready.Disposition);
        Assert.Null(ready.ActionKind);
        Assert.False(ready.ActionSpecified);
        var step = Step(ready);
        Assert.Equal(AgentStepDisposition.Complete, step.Disposition);
        Assert.Equal("Shown", Assert.IsType<ChatRespondAction>(Assert.Single(step.Actions)).DisplayText);
    }

    [Fact]
    public async Task Compatibility_plain_text_defaults_to_complete_chat_respond()
    {
        var inner = new ScriptedInner(
            [new ModelTextDelta("Hello."), new ModelCompleted(ModelStopReason.Completed)]);
        var events = await CollectAsync(new SemanticResponseLanguageModel(inner), Contracted);
        var ready = Assert.Single(events.OfType<ModelSemanticResponseReady>()).Response;
        Assert.Equal("Hello.", ready.DisplayText);
        Assert.Null(ready.Disposition);
        Assert.False(ready.ActionSpecified);
        var step = Step(ready);
        Assert.Equal(AgentStepDisposition.Complete, step.Disposition);
        Assert.IsType<ChatRespondAction>(Assert.Single(step.Actions));
        Assert.Contains("Complete with one chat.respond", inner.LastRequest!.Messages[^1].Text, StringComparison.Ordinal);
    }

    private static async Task<ModelSemanticResponse> ReadyAsync(string json, bool native)
    {
        var events = await CollectAsync(Channel(json, native), Contracted);
        return Assert.Single(events.OfType<ModelSemanticResponseReady>()).Response;
    }

    private static SemanticResponseLanguageModel Channel(string json, bool native) =>
        native
            ? new SemanticResponseLanguageModel(new ScriptedInner(
                [new ModelTextDelta(json), new ModelCompleted(ModelStopReason.Completed)],
                structured: true))
            : new SemanticResponseLanguageModel(new ScriptedInner(
            [
                new ModelToolCallEvent(new ModelToolCall("call-1", AssistantResponseSchema.ResponseFunctionName, json)),
                new ModelCompleted(ModelStopReason.ToolCalls)
            ]));

    private static AgentStep Step(ModelSemanticResponse response) =>
        Assert.IsType<AgentStepAccepted>(
            AgentStepNormalizer.Normalize(AgentStepNormalizer.FromSemanticResponse(response))).Step;

    private static async Task<List<ModelGenerationEvent>> CollectAsync(ILanguageModel model, ModelRequest request)
    {
        var listed = new List<ModelGenerationEvent>();
        await foreach (var item in model.GenerateAsync(request))
        {
            listed.Add(item);
        }

        return listed;
    }

    private sealed class ScriptedInner(
        IReadOnlyList<ModelGenerationEvent> events,
        bool structured = false,
        bool tools = true) : ILanguageModel
    {
        public ModelCapabilities Capabilities { get; } = new(
            StreamingText: true,
            Cancellation: true,
            Tools: tools,
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
