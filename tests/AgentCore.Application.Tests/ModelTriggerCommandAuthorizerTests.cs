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
        Assert.Equal(TriggerCommandAuthorizationDecision.ClassifierUnavailable, decision);
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
            authorizer.AuthorizeCurrentTurnAsync("remind me", "en", TriggerCommandAction.Create, null, null, cts.Token).AsTask());
    }

    [Theory]
    [InlineData("say hello in 15s", TriggerCommandAuthorizationDecision.Allow)]
    [InlineData("in 15 seconds, say hello", TriggerCommandAuthorizationDecision.Allow)]
    [InlineData("every 60s, wake me up", TriggerCommandAuthorizationDecision.Allow)]
    [InlineData("don't say hello in 15s", TriggerCommandAuthorizationDecision.Deny)]
    [InlineData("do not wake me every 60s", TriggerCommandAuthorizationDecision.Deny)]
    [InlineData("never create an automation", TriggerCommandAuthorizationDecision.Deny)]
    public async Task Clear_requests_and_negation_do_not_call_unavailable_classifier(string text, TriggerCommandAuthorizationDecision expected)
    {
        var authorizer = new ModelTriggerCommandAuthorizer(new FailingLanguageModel(), new());
        Assert.Equal(expected, await authorizer.AuthorizeCurrentTurnAsync(text, "en", TriggerCommandAction.Create));
    }

    [Fact]
    public async Task Failed_stream_cannot_authorize_from_partial_json()
    {
        var authorizer = new ModelTriggerCommandAuthorizer(new PartiallyFailedModel(), new());
        Assert.Equal(TriggerCommandAuthorizationDecision.ClassifierUnavailable,
            await authorizer.AuthorizeCurrentTurnAsync("another at 8:52", "en", TriggerCommandAction.Create));
    }

    private sealed class PartiallyFailedModel : ILanguageModel
    {
        public ModelCapabilities Capabilities { get; } = new(true, true);
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield return new ModelTextDelta("{\"decision\":\"allow\"}");
            yield return new ModelFailed(new(ProviderErrorCode.Unavailable, "Provider unavailable."));
        }
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
