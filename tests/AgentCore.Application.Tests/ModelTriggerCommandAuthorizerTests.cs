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
            "another at 8:52",
            "en",
            TriggerCommandAction.Create);
        Assert.Equal(expected, decision);
    }

    [Theory]
    [InlineData("{\"decision\":42}")]
    [InlineData("{\"decision\":{}}")]
    [InlineData("[]")]
    [InlineData("{\"decision\":\"maybe\"}")]
    public async Task Invalid_classifier_shape_fails_closed(string output)
    {
        var model = new StubLanguageModel(output);
        var authorizer = new ModelTriggerCommandAuthorizer(model, new());
        Assert.Equal(TriggerCommandAuthorizationDecision.ClassifierInvalidOutput,
            await authorizer.AuthorizeCurrentTurnAsync("maybe greet me later", "en", TriggerCommandAction.Create));
        Assert.Equal(1, model.Calls);
    }

    [Theory]
    [InlineData("maybe greet me every minute", "ambiguous", TriggerCommandAuthorizationDecision.Ambiguous)]
    [InlineData("say hello to me every minute if I return", "ambiguous", TriggerCommandAuthorizationDecision.Ambiguous)]
    [InlineData("say \"hello\" to me every minute", "ambiguous", TriggerCommandAuthorizationDecision.Ambiguous)]
    [InlineData("the kettle is blue", "deny", TriggerCommandAuthorizationDecision.Deny)]
    [InlineData("another at 8:52", "allow", TriggerCommandAuthorizationDecision.Allow)]
    public async Task Genuine_ambiguity_still_uses_classifier(string text, string token, TriggerCommandAuthorizationDecision expected)
    {
        var model = new StubLanguageModel("{\"decision\":\"" + token + "\"}");
        Assert.Equal(expected, await new ModelTriggerCommandAuthorizer(model, new())
            .AuthorizeCurrentTurnAsync(text, "en", TriggerCommandAction.Create));
        Assert.Equal(1, model.Calls);
    }

    [Fact]
    public async Task Oversized_timed_command_cannot_fall_through_directive_match()
    {
        var model = new StubLanguageModel("{\"decision\":\"ambiguous\"}");
        Assert.Equal(TriggerCommandAuthorizationDecision.Ambiguous,
            await new ModelTriggerCommandAuthorizer(model, new()).AuthorizeCurrentTurnAsync(
                "say hello to me " + new string('x', 512) + " every minute", "en", TriggerCommandAction.Create));
        Assert.Equal(1, model.Calls);
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
        Assert.Equal(TriggerCommandAuthorizationDecision.ClassifierInvalidOutput, decision);
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
    [InlineData("check the oven in 1 minute for me", TriggerCommandAuthorizationDecision.Allow)]
    [InlineData("in 1 minute, check the oven for me", TriggerCommandAuthorizationDecision.Allow)]
    [InlineData("greet me every 60 seconds", TriggerCommandAuthorizationDecision.Allow)]
    [InlineData("every 60 seconds, greet me", TriggerCommandAuthorizationDecision.Allow)]
    [InlineData("please greet me every minute", TriggerCommandAuthorizationDecision.Allow)]
    [InlineData("say hello to me every 60s", TriggerCommandAuthorizationDecision.Allow)]
    [InlineData("don't greet me every 60 seconds", TriggerCommandAuthorizationDecision.Deny)]
    [InlineData("can you greet me every 60 seconds?", TriggerCommandAuthorizationDecision.Deny)]
    [InlineData("yesterday I asked you to greet me every 60 seconds", TriggerCommandAuthorizationDecision.Deny)]
    [InlineData("greet me every minute, please", TriggerCommandAuthorizationDecision.Allow)]
    [InlineData("every minute, please greet me", TriggerCommandAuthorizationDecision.Allow)]
    [InlineData("every minute please greet me", TriggerCommandAuthorizationDecision.Allow)]
    [InlineData("i want you to greet me every 60 seconds", TriggerCommandAuthorizationDecision.Allow)]
    [InlineData("don’t greet me every minute", TriggerCommandAuthorizationDecision.Deny)]
    [InlineData("please do not greet me every minute", TriggerCommandAuthorizationDecision.Deny)]
    [InlineData("why did you say hello to me every 60s?", TriggerCommandAuthorizationDecision.Deny)]
    [InlineData("previously, greet me every 60 seconds was my request", TriggerCommandAuthorizationDecision.Deny)]
    [InlineData("say hello in 15s", TriggerCommandAuthorizationDecision.Allow)]
    [InlineData("in 15 seconds, say hello", TriggerCommandAuthorizationDecision.Allow)]
    [InlineData("every 60s, wake me up", TriggerCommandAuthorizationDecision.Allow)]
    [InlineData("don't say hello in 15s", TriggerCommandAuthorizationDecision.Deny)]
    [InlineData("do not wake me every 60s", TriggerCommandAuthorizationDecision.Deny)]
    [InlineData("never create an automation", TriggerCommandAuthorizationDecision.Deny)]
    public async Task Clear_requests_and_negation_do_not_call_unavailable_classifier(string text, TriggerCommandAuthorizationDecision expected)
    {
        var model = new FailingLanguageModel();
        var authorizer = new ModelTriggerCommandAuthorizer(model, new());
        Assert.Equal(expected, await authorizer.AuthorizeCurrentTurnAsync(text, "en", TriggerCommandAction.Create));
        Assert.Equal(0, model.Calls);
    }

    [Fact]
    public async Task Failed_stream_cannot_authorize_from_partial_json()
    {
        var authorizer = new ModelTriggerCommandAuthorizer(new PartiallyFailedModel(), new());
        Assert.Equal(TriggerCommandAuthorizationDecision.ClassifierUnavailable,
            await authorizer.AuthorizeCurrentTurnAsync("another at 8:52", "en", TriggerCommandAction.Create));
    }

    [Theory]
    [InlineData("missing-terminal", TriggerCommandAuthorizationDecision.ClassifierInvalidOutput)]
    [InlineData("truncated", TriggerCommandAuthorizationDecision.ClassifierInvalidOutput)]
    [InlineData("invalid-response", TriggerCommandAuthorizationDecision.ClassifierInvalidOutput)]
    [InlineData("oversized", TriggerCommandAuthorizationDecision.ClassifierInvalidOutput)]
    [InlineData("unavailable", TriggerCommandAuthorizationDecision.ClassifierUnavailable)]
    public async Task Incomplete_or_failed_classification_cannot_authorize(string mode, TriggerCommandAuthorizationDecision expected)
    {
        Assert.Equal(expected, await new ModelTriggerCommandAuthorizer(new InvalidStreamModel(mode), new())
            .AuthorizeCurrentTurnAsync("maybe greet me later", "en", TriggerCommandAction.Create));
    }

    private sealed class InvalidStreamModel(string mode) : ILanguageModel
    {
        public ModelCapabilities Capabilities { get; } = new(true, true);
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield return new ModelTextDelta(mode == "oversized" ? new string(' ', 4097) : "{\"decision\":\"allow\"}");
            if (mode == "missing-terminal") yield break;
            if (mode is "invalid-response" or "unavailable")
                yield return new ModelFailed(new(mode == "invalid-response" ? ProviderErrorCode.InvalidResponse : ProviderErrorCode.Unavailable, "Safe fixture failure."));
            else yield return new ModelCompleted(mode == "truncated" ? ModelStopReason.LengthLimit : ModelStopReason.Completed);
        }
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
        public int Calls { get; private set; }
        public ModelCapabilities Capabilities { get; } = new(StreamingText: true, Cancellation: true, Tools: false);

        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Calls++;
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ModelTextDelta(text);
            yield return new ModelCompleted(ModelStopReason.Completed);
        }
    }

    private sealed class FailingLanguageModel : ILanguageModel
    {
        public int Calls { get; private set; }
        public ModelCapabilities Capabilities { get; } = new(StreamingText: true, Cancellation: true, Tools: false);

        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            Calls++;
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
