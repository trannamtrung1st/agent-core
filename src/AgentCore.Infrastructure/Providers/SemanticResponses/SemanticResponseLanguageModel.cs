using System.Runtime.CompilerServices;
using System.Text;
using AgentCore.Application.Memory;
using AgentCore.Application.Ports;

namespace AgentCore.Infrastructure.Providers.SemanticResponses;

public sealed class SemanticResponseLanguageModel(ILanguageModel inner) : ILanguageModel
{
    public ILanguageModel Inner { get; } = inner;

    public ModelCapabilities Capabilities => Inner.Capabilities;

    public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(
        ModelRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (request.ResponseContract is null)
        {
            await foreach (var item in Inner.GenerateAsync(request, cancellationToken).ConfigureAwait(false))
            {
                yield return item;
            }

            yield break;
        }

        if (Capabilities.StructuredOutput)
        {
            await foreach (var item in GenerateNativeAsync(request, cancellationToken).ConfigureAwait(false))
            {
                yield return item;
            }

            yield break;
        }

        if (Capabilities.Tools)
        {
            await foreach (var item in GenerateFunctionAsync(request, cancellationToken).ConfigureAwait(false))
            {
                yield return item;
            }

            yield break;
        }

        var forwarded = WithCompatibilityInstruction(request, request.ResponseContract, responseFunction: false);
        await foreach (var item in GenerateCompatibilityAsync(forwarded, cancellationToken).ConfigureAwait(false))
        {
            yield return item;
        }
    }

    private async IAsyncEnumerable<ModelGenerationEvent> GenerateFunctionAsync(
        ModelRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var forwarded = WithResponseFunction(request);
        var responseCalls = new List<ModelToolCall>();
        var otherCalls = new List<ModelToolCall>();
        await foreach (var item in GenerateCompatibilityAsync(forwarded, cancellationToken).ConfigureAwait(false))
        {
            switch (item)
            {
                case ModelToolCallEvent call when call.Call.Name == AssistantResponseSchema.ResponseFunctionName:
                    responseCalls.Add(call.Call);
                    continue;
                case ModelToolCallEvent call:
                    otherCalls.Add(call.Call);
                    continue;
                case ModelCompleted completed when completed.Reason == ModelStopReason.ToolCalls && otherCalls.Count > 0:
                    foreach (var other in otherCalls)
                    {
                        yield return new ModelToolCallEvent(other);
                    }

                    otherCalls.Clear();
                    responseCalls.Clear();
                    yield return completed;
                    continue;
                case ModelCompleted completed when completed.Reason == ModelStopReason.ToolCalls && responseCalls.Count > 0:
                    var arguments = responseCalls[^1].ArgumentsJson;
                    responseCalls.Clear();
                    if (!NativeSemanticResponseParser.TryParse(arguments, out var parsed, out var failureReason))
                    {
                        yield return Fail(failureReason, ProviderResponseChannel.ResponseFunction);
                        yield break;
                    }

                    yield return new ModelSemanticResponseReady(parsed!);
                    yield return new ModelCompleted(
                        ModelStopReason.Completed,
                        completed.InputTokens,
                        completed.OutputTokens);
                    yield break;
                default:
                    yield return item;
                    continue;
            }
        }
    }

    private async IAsyncEnumerable<ModelGenerationEvent> GenerateNativeAsync(
        ModelRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var buffer = new StringBuilder();
        var innerReady = false;
        await foreach (var item in Inner.GenerateAsync(request, cancellationToken).ConfigureAwait(false))
        {
            switch (item)
            {
                case ModelTextDelta delta:
                    if (buffer.Length + delta.Text.Length > AssistantResponseSchema.MaxJsonCharacters)
                    {
                        yield return Fail(
                            ProviderFailureReason.ResponseTooLarge,
                            ProviderResponseChannel.StructuredOutput);
                        yield break;
                    }

                    buffer.Append(delta.Text);
                    continue;
                case ModelDisplayDelta:
                    continue;
                case ModelSemanticResponseReady:
                    innerReady = true;
                    yield return item;
                    continue;
                case ModelReasoningDelta:
                case ModelToolCallEvent:
                    yield return item;
                    continue;
                case ModelFailed:
                    yield return item;
                    yield break;
                case ModelCompleted completed:
                    if (completed.Reason == ModelStopReason.ToolCalls)
                    {
                        yield return completed;
                        yield break;
                    }

                    if (innerReady)
                    {
                        yield return completed;
                        yield break;
                    }

                    if (NativeSemanticResponseParser.TryParse(buffer.ToString(), out var response, out var failureReason))
                    {
                        yield return new ModelSemanticResponseReady(response!);
                        yield return completed;
                        yield break;
                    }

                    yield return Fail(
                        ClassifyLength(completed.Reason, failureReason, native: true),
                        ProviderResponseChannel.StructuredOutput);
                    yield break;
                default:
                    yield return item;
                    continue;
            }
        }
    }

    private async IAsyncEnumerable<ModelGenerationEvent> GenerateCompatibilityAsync(
        ModelRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var parser = new MarkerSemanticResponseParser();
        var innerReady = false;
        var speechReadyEmitted = false;
        ModelSemanticResponse? peeked = null;
        await foreach (var item in Inner.GenerateAsync(request, cancellationToken).ConfigureAwait(false))
        {
            switch (item)
            {
                case ModelTextDelta delta:
                {
                    var display = parser.Feed(delta.Text);
                    if (display.Length > 0)
                    {
                        yield return new ModelDisplayDelta(display);
                    }

                    if (!speechReadyEmitted && parser.TryPeekSpeechReady(out var speechReady))
                    {
                        speechReadyEmitted = true;
                        peeked = speechReady;
                        yield return new ModelSemanticResponseReady(speechReady!);
                    }

                    continue;
                }
                case ModelDisplayDelta delta:
                    yield return delta;
                    continue;
                case ModelSemanticResponseReady:
                    innerReady = true;
                    yield return item;
                    continue;
                case ModelReasoningDelta:
                case ModelToolCallEvent:
                    yield return item;
                    continue;
                case ModelFailed:
                    yield return item;
                    yield break;
                case ModelCompleted completed:
                    if (completed.Reason == ModelStopReason.ToolCalls)
                    {
                        yield return completed;
                        yield break;
                    }

                    if (innerReady)
                    {
                        yield return completed;
                        yield break;
                    }

                    if (!parser.TryFinish(out var response, out var failureReason))
                    {
                        var reason = string.IsNullOrEmpty(failureReason)
                            ? ProviderFailureReason.InvalidMarkerEnvelope
                            : failureReason;
                        yield return Fail(
                            ClassifyLength(completed.Reason, reason, native: false),
                            ProviderResponseChannel.MarkerCompatibility);
                        yield break;
                    }

                    var remainder = parser.FinishDisplayRemainder(response!);
                    if (remainder.Length > 0)
                    {
                        yield return new ModelDisplayDelta(remainder);
                    }

                    if (peeked is null || !SemanticMatches(peeked, response!))
                    {
                        yield return new ModelSemanticResponseReady(response!);
                    }

                    yield return completed;
                    yield break;
                default:
                    yield return item;
                    continue;
            }
        }
    }

    private static ModelRequest WithResponseFunction(ModelRequest request)
    {
        var instructed = WithCompatibilityInstruction(request, request.ResponseContract!, responseFunction: true);
        var tools = instructed.Tools?.ToList() ?? [];
        if (!tools.Any(tool => tool.Name == AssistantResponseSchema.ResponseFunctionName))
        {
            tools.Add(AssistantResponseSchema.ResponseFunction(request.ResponseContract!));
        }

        var sessionTools = request.Tools is { Count: > 0 };
        return instructed with
        {
            Tools = tools,
            ToolChoice = sessionTools ? ModelToolChoice.Required : ModelToolChoice.Named,
            ToolChoiceName = sessionTools ? null : AssistantResponseSchema.ResponseFunctionName
        };
    }

    private static ModelRequest WithCompatibilityInstruction(
        ModelRequest request,
        ModelResponseContract contract,
        bool responseFunction)
    {
        var instruction = AssistantResponseSchema.CompatibilityInstruction(contract, responseFunction);
        var messages = request.Messages.ToList();
        messages.Add(new ModelMessage(ModelRole.System, instruction));
        return request with { Messages = messages };
    }

    private static bool SemanticMatches(ModelSemanticResponse peeked, ModelSemanticResponse final) =>
        string.Equals(peeked.DisplayText, final.DisplayText, StringComparison.Ordinal)
        && peeked.Blocks.Count == final.Blocks.Count
        && peeked.Speech.Mode == final.Speech.Mode
        && string.Equals(peeked.Speech.Text, final.Speech.Text, StringComparison.Ordinal)
        && MemoryMatches(peeked.Memory, final.Memory);

    private static bool MemoryMatches(IReadOnlyList<MemoryProposal>? left, IReadOnlyList<MemoryProposal>? right)
    {
        var first = left ?? [];
        var second = right ?? [];
        if (first.Count != second.Count)
        {
            return false;
        }

        for (var index = 0; index < first.Count; index++)
        {
            if (first[index] != second[index])
            {
                return false;
            }
        }

        return true;
    }

    private static string ClassifyLength(ModelStopReason reason, string failureReason, bool native)
    {
        if (reason != ModelStopReason.LengthLimit)
        {
            return failureReason;
        }

        var truncated = native
            ? failureReason is ProviderFailureReason.InvalidJson
                or ProviderFailureReason.ResponseFunctionArgumentsInvalid
            : failureReason is ProviderFailureReason.MissingDisplayText
                or ProviderFailureReason.InvalidMarkerEnvelope;
        return truncated ? ProviderFailureReason.OutputLimit : failureReason;
    }

    private static ModelFailed Fail(string failureReason, string responseChannel) =>
        new(new ProviderFailure(
            ProviderErrorCode.InvalidResponse,
            failureReason switch
            {
                ProviderFailureReason.OutputLimit =>
                    "The model output was cut off before a complete response.",
                ProviderFailureReason.ToolCallTruncated =>
                    "The model's tool call was cut off before it finished.",
                _ => "Malformed assistant envelope."
            },
            FailureReason: failureReason,
            ResponseChannel: responseChannel));
}
