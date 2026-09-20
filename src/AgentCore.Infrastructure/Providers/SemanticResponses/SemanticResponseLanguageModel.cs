using System.Runtime.CompilerServices;
using System.Text;
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

        var forwarded = Capabilities.StructuredOutput
            ? request
            : WithCompatibilityInstruction(request, request.ResponseContract);
        if (Capabilities.StructuredOutput)
        {
            await foreach (var item in GenerateNativeAsync(forwarded, cancellationToken).ConfigureAwait(false))
            {
                yield return item;
            }
        }
        else
        {
            await foreach (var item in GenerateCompatibilityAsync(forwarded, cancellationToken).ConfigureAwait(false))
            {
                yield return item;
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
                        yield return Fail();
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

                    if (NativeSemanticResponseParser.TryParse(buffer.ToString(), out var response, out _))
                    {
                        yield return new ModelSemanticResponseReady(response!);
                        yield return completed;
                        yield break;
                    }

                    yield return Fail();
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

                    if (!parser.TryFinish(out var response, out _))
                    {
                        yield return Fail();
                        yield break;
                    }

                    var remainder = parser.FinishDisplayRemainder(response!);
                    if (remainder.Length > 0)
                    {
                        yield return new ModelDisplayDelta(remainder);
                    }

                    if (peeked is null
                        || peeked.DisplayText != response!.DisplayText
                        || peeked.Blocks.Count != response.Blocks.Count)
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

    private static ModelRequest WithCompatibilityInstruction(ModelRequest request, ModelResponseContract contract)
    {
        var instruction = AssistantResponseSchema.CompatibilityInstruction(contract);
        var messages = request.Messages.ToList();
        messages.Add(new ModelMessage(ModelRole.System, instruction));
        return request with { Messages = messages };
    }

    private static ModelFailed Fail() =>
        new(new ProviderFailure(ProviderErrorCode.InvalidResponse, "Malformed assistant envelope."));
}
