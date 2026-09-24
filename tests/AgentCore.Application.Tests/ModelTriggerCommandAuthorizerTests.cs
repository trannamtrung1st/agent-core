using System.Runtime.CompilerServices;
using AgentCore.Application.Ports;
using AgentCore.Application.Triggers;

namespace AgentCore.Application.Tests;

public sealed class ModelTriggerCommandAuthorizerTests
{
    [Theory]
    [InlineData("""{"decision":"allow"}""", TriggerCommandAuthorizationDecision.Allow)]
    [InlineData("""prefix {"decision":"deny"} suffix""", TriggerCommandAuthorizationDecision.Deny)]
    [InlineData("""{"decision":"ambiguous"}""", TriggerCommandAuthorizationDecision.Ambiguous)]
    public async Task Parses_bounded_classifier_responses(string modelText, TriggerCommandAuthorizationDecision expected)
    {
        var authorizer = new ModelTriggerCommandAuthorizer(
            new StubLanguageModel(modelText),
            new HeuristicTriggerCommandAuthorizer());
        var decision = await authorizer.AuthorizeCurrentTurnAsync(
            expected == TriggerCommandAuthorizationDecision.Allow
                ? "remind me tomorrow at 9"
                : "another at 8:52",
            "en",
            TriggerCommandAction.Create);
        Assert.Equal(expected, decision);
    }

    [Fact]
    public async Task Malformed_json_denies_without_claiming_user_ambiguity()
    {
        var authorizer = new ModelTriggerCommandAuthorizer(
            new StubLanguageModel("not json"),
            new HeuristicTriggerCommandAuthorizer());
        var decision = await authorizer.AuthorizeCurrentTurnAsync(
            "another at 8:52",
            "en",
            TriggerCommandAction.Create);
        Assert.Equal(TriggerCommandAuthorizationDecision.Deny, decision);
    }

    [Fact]
    public async Task Provider_failure_propagates()
    {
        var authorizer = new ModelTriggerCommandAuthorizer(
            new FailingLanguageModel(),
            new HeuristicTriggerCommandAuthorizer());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            authorizer.AuthorizeCurrentTurnAsync("another at 8:52", "en", TriggerCommandAction.Create).AsTask());
    }

    [Fact]
    public async Task Cancellation_is_observed()
    {
        var authorizer = new ModelTriggerCommandAuthorizer(
            new CancellingLanguageModel(),
            new HeuristicTriggerCommandAuthorizer());
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            authorizer.AuthorizeCurrentTurnAsync("remind me", "en", TriggerCommandAction.Create, null, cts.Token).AsTask());
    }

    private sealed class StubLanguageModel(string text) : ILanguageModel
    {
        public ModelCapabilities Capabilities { get; } = new(StreamingText: true, Cancellation: true, Tools: false);

        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ModelTextDelta(text);
            yield return new ModelCompleted(ModelStopReason.Completed);
        }
    }

    private sealed class FailingLanguageModel : ILanguageModel
    {
        public ModelCapabilities Capabilities { get; } = new(StreamingText: true, Cancellation: true, Tools: false);

        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            throw new InvalidOperationException("provider down");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }
    }

    private sealed class CancellingLanguageModel : ILanguageModel
    {
        public ModelCapabilities Capabilities { get; } = new(StreamingText: true, Cancellation: true, Tools: false);

        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield break;
        }
    }
}
