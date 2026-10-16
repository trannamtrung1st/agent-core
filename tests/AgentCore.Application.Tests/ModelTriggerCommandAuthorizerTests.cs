using System.Runtime.CompilerServices;
using AgentCore.Application.Ports;
using AgentCore.Application.Triggers;
using AgentCore.Domain.Triggers;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Application.Tests;

public sealed class ModelTriggerCommandAuthorizerTests
{
    [Theory]
    [InlineData("allow", TriggerCommandAuthorizationDecision.Allow)]
    [InlineData("deny", TriggerCommandAuthorizationDecision.Deny)]
    [InlineData("ambiguous", TriggerCommandAuthorizationDecision.Ambiguous)]
    [InlineData("truncated", TriggerCommandAuthorizationDecision.ClassifierInvalidOutput)]
    [InlineData("malformed", TriggerCommandAuthorizationDecision.ClassifierInvalidOutput)]
    public async Task Only_confirmed_length_limit_retries_with_fresh_output_and_identical_context(string second, TriggerCommandAuthorizationDecision expected)
    {
        var model = new BudgetModel(second);
        var result = await new ModelTriggerCommandAuthorizer(model, new(), "low")
            .AuthorizeWithDetailsAsync("I would like daily reflection on your memory at 9am", "en", TriggerCommandAction.Create,
                new(Guid.NewGuid(), 2, TriggerCommandAction.Update, "Original source", "Asia/Ho_Chi_Minh", TriggerScheduleKind.Daily, AutomationStatus.Cancelled, null),
                new("Retained draft", TriggerCommandAction.Create, "daily", null, "schedule_validation_failed", DateTimeOffset.UnixEpoch, "{\"reportBack\":false}"));
        Assert.Equal(expected, result.Decision);
        Assert.Equal(2, model.Requests.Count);
        var first = model.Requests[0]; var retry = model.Requests[1];
        Assert.Equal(512, first.MaxOutputTokens);
        Assert.Equal(1024, retry.MaxOutputTokens);
        Assert.Equal(first, retry with { MaxOutputTokens = first.MaxOutputTokens });
        Assert.Equal("low", retry.ReasoningEffort);
        Assert.Null(retry.Tools);
        if (second == "truncated") Assert.Equal(TriggerAuthorizationFailureReason.TruncatedOutput, result.FailureReason);
    }

    [Fact]
    public async Task Cancellation_at_the_truncated_terminal_prevents_the_internal_retry()
    {
        using var cts = new CancellationTokenSource();
        var model = new BudgetModel("allow", cts.Cancel);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ModelTriggerCommandAuthorizer(model, new())
            .AuthorizeWithDetailsAsync("another at 8:52", "en", TriggerCommandAction.Create, cancellationToken: cts.Token).AsTask());
        Assert.Single(model.Requests);
    }

    [Theory]
    [InlineData(31, 1)]
    [InlineData(25, 2)]
    public async Task Attempts_share_one_thirty_second_deadline_instead_of_renewing_it(int firstSeconds, int expectedCalls)
    {
        var time = new FakeTimeProvider();
        var calls = 0;
        var model = new BudgetModel("allow", () => time.Advance(TimeSpan.FromSeconds(++calls == 1 ? firstSeconds : 6)));
        var verdict = await new ModelTriggerCommandAuthorizer(model, new(), timeProvider: time)
            .AuthorizeWithDetailsAsync("another at 8:52", "en", TriggerCommandAction.Create);
        Assert.Equal(TriggerCommandAuthorizationDecision.ClassifierUnavailable, verdict.Decision);
        Assert.Equal(TriggerAuthorizationFailureReason.ProviderFailure, verdict.FailureReason);
        Assert.Equal(expectedCalls, model.Requests.Count);
    }

    private sealed class BudgetModel(string second, Action? beforeTerminal = null) : ILanguageModel
    {
        public ModelCapabilities Capabilities { get; } = new(true, true);
        public List<ModelRequest> Requests { get; } = [];
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            ct.ThrowIfCancellationRequested();
            Requests.Add(request);
            // A complete-looking Allow is still unusable on a truncated attempt.
            yield return new ModelTextDelta(Requests.Count == 1 || second == "truncated" ? "{\"decision\":\"allow\"}"
                : second == "malformed" ? "broken" : "{\"decision\":\"" + second + "\"}");
            beforeTerminal?.Invoke();
            yield return new ModelCompleted(Requests.Count == 1 || second == "truncated" ? ModelStopReason.LengthLimit : ModelStopReason.Completed, OutputTokens: request.MaxOutputTokens);
        }
    }

    [Theory]
    [InlineData("enable the 60s hello schedule", TriggerCommandAction.Enable)]
    [InlineData("enable the 60s hello schedule", TriggerCommandAction.List)]
    [InlineData("activate the hello automation", TriggerCommandAction.Enable)]
    [InlineData("enable core event review recent work", TriggerCommandAction.Enable)]
    [InlineData("enable the review recent work preset", TriggerCommandAction.Enable)]
    [InlineData("turn on the hello schedule", TriggerCommandAction.Enable)]
    [InlineData("pause the hello schedule", TriggerCommandAction.Disable)]
    [InlineData("turn off the hello automation", TriggerCommandAction.Disable)]
    [InlineData("disable the hello schedule", TriggerCommandAction.List)]
    [InlineData("update the hello schedule", TriggerCommandAction.List)]
    public async Task Explicit_management_and_supporting_reads_do_not_call_classifier(string text, TriggerCommandAction action)
    {
        var model = new StubLanguageModel("invalid classifier output");
        Assert.Equal(TriggerCommandAuthorizationDecision.Allow,
            await new ModelTriggerCommandAuthorizer(model, new()).AuthorizeCurrentTurnAsync(text, "en", action));
        Assert.Equal(0, model.Calls);
    }

    [Theory]
    [InlineData("do not enable the hello schedule")]
    [InlineData("never activate the hello automation")]
    [InlineData("could you enable the hello schedule")]
    [InlineData("yesterday I enabled the hello schedule")]
    public async Task Non_commands_cannot_gain_activation_authority_from_classifier(string text)
    {
        var model = new StubLanguageModel("{\"decision\":\"allow\"}");
        Assert.Equal(TriggerCommandAuthorizationDecision.Deny,
            await new ModelTriggerCommandAuthorizer(model, new()).AuthorizeCurrentTurnAsync(text, "en", TriggerCommandAction.Enable));
        Assert.Equal(0, model.Calls);
    }

    [Theory]
    [InlineData("enable the 60s hello schedule")]
    [InlineData("enable the hello automation")]
    [InlineData("disable this automation")]
    public async Task Managing_existing_automation_never_authorizes_creating_a_replacement(string text)
    {
        var model = new StubLanguageModel("{\"decision\":\"allow\"}");
        Assert.Equal(TriggerCommandAuthorizationDecision.Deny,
            await new ModelTriggerCommandAuthorizer(model, new()).AuthorizeCurrentTurnAsync(text, "en", TriggerCommandAction.Create));
        Assert.Equal(0, model.Calls);
    }

    [Theory]
    [InlineData("truncated", TriggerAuthorizationFailureReason.TruncatedOutput)]
    [InlineData("missing-terminal", TriggerAuthorizationFailureReason.IncompleteOutput)]
    [InlineData("invalid-response", TriggerAuthorizationFailureReason.ProviderInvalidResponse)]
    [InlineData("unavailable", TriggerAuthorizationFailureReason.ProviderFailure)]
    [InlineData("oversized", TriggerAuthorizationFailureReason.OversizedOutput)]
    [InlineData("tool-channel", TriggerAuthorizationFailureReason.InvalidResponseChannel)]
    public async Task Failure_details_are_bounded_and_distinguish_provider_and_output_failures(string mode, TriggerAuthorizationFailureReason reason)
    {
        var model = new InvalidStreamModel(mode);
        var result = await new ModelTriggerCommandAuthorizer(model, new())
            .AuthorizeWithDetailsAsync("maybe greet me later", "en", TriggerCommandAction.Create);
        Assert.Equal(reason, result.FailureReason);
        Assert.NotEqual(TriggerCommandAuthorizationDecision.Allow, result.Decision);
        Assert.Equal(mode == "truncated" ? 2 : 1, model.Calls);
    }

    [Theory]
    [InlineData("not json", TriggerAuthorizationFailureReason.MalformedJson)]
    [InlineData("{\"decision\":42}", TriggerAuthorizationFailureReason.InvalidDecision)]
    public async Task Json_failure_details_do_not_claim_a_provider_outage(string output, TriggerAuthorizationFailureReason reason)
    {
        var result = await new ModelTriggerCommandAuthorizer(new StubLanguageModel(output), new())
            .AuthorizeWithDetailsAsync("maybe greet me later", "en", TriggerCommandAction.Create);
        Assert.Equal(reason, result.FailureReason);
        Assert.Equal(TriggerCommandAuthorizationDecision.ClassifierInvalidOutput, result.Decision);
    }

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
    [InlineData("{\"decision\":\"deny\",\"decision\":\"allow\"}")]
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
    [InlineData("create an automation to greet me every minute if I return", "ambiguous", TriggerCommandAuthorizationDecision.Ambiguous)]
    [InlineData("if I return, create an automation to greet me every minute", "ambiguous", TriggerCommandAuthorizationDecision.Ambiguous)]
    [InlineData("create an automation if I return", "ambiguous", TriggerCommandAuthorizationDecision.Ambiguous)]
    [InlineData("we used to create an automation every minute", "deny", TriggerCommandAuthorizationDecision.Deny)]
    [InlineData("we used to create an automation", "deny", TriggerCommandAuthorizationDecision.Deny)]
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
    [InlineData("everyday at 9am, think about how you can consolidate your memory, experience", TriggerCommandAuthorizationDecision.Allow)]
    [InlineData("every week, think about how you can improve your harness for better support", TriggerCommandAuthorizationDecision.Allow)]
    [InlineData("please every day at 09:00, think about your memory", TriggerCommandAuthorizationDecision.Allow)]
    [InlineData("every week, please think about your harness", TriggerCommandAuthorizationDecision.Allow)]
    [InlineData("can you think about your memory every day?", TriggerCommandAuthorizationDecision.Deny)]
    [InlineData("I used to think about your memory every day", TriggerCommandAuthorizationDecision.Deny)]
    [InlineData("do not think about your memory every day", TriggerCommandAuthorizationDecision.Deny)]
    [InlineData("create an automation to greet me every minute", TriggerCommandAuthorizationDecision.Allow)]
    [InlineData("please configure an automation", TriggerCommandAuthorizationDecision.Allow)]
    [InlineData("enable core event review recent work", TriggerCommandAuthorizationDecision.Deny)]
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
        public int Calls { get; private set; }
        public ModelCapabilities Capabilities { get; } = new(true, true);
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            Calls++;
            if (mode == "tool-channel")
            {
                yield return new ModelToolCallEvent(new("unexpected", "automation.create", "{}"));
                yield return new ModelCompleted(ModelStopReason.ToolCalls);
                yield break;
            }
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
