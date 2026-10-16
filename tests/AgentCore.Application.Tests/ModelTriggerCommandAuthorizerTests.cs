using System.Runtime.CompilerServices;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Application.Triggers;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Application.Tests;

public sealed class ModelTriggerCommandAuthorizerTests
{
    private static AutomationAuthorizationRequest Proposed(string text = "每周五上午九点复盘", string instructions = "Review experience")
    {
        var exact = JsonSerializer.SerializeToElement(new { tool = ToolCatalog.AutomationCreate, arguments = new { instructions, kind = "weekly", weekdays = new[] { "Friday" }, localTime = "09:00", timeZone = "Asia/Ho_Chi_Minh", executionTarget = "backgroundSession", reportBack = false } });
        return new(text, TriggerCommandAction.Create, ToolCatalog.AutomationCreate, exact, ToolActionHash.Compute(ToolCatalog.AutomationCreate, exact));
    }

    [Theory]
    [InlineData("everyday at 9am, think about how you can consolidate your memory, experience", "allow")]
    [InlineData("every week, think about improving your harness", "ambiguous")]
    [InlineData("Hãy xem lại kinh nghiệm mỗi thứ Sáu lúc 9 giờ", "allow")]
    [InlineData("毎週金曜日の午前9時に振り返って", "allow")]
    [InlineData("هل تستطيع جدولة التذكيرات؟", "deny")]
    [InlineData("do not schedule anything", "deny")]
    [InlineData("What if you reviewed every week?", "deny")]
    [InlineData("Yesterday I asked for a reminder", "deny")]
    [InlineData("\"Create a schedule\" was quoted data", "deny")]
    [InlineData("enable my existing automation", "deny")]
    public async Task Every_language_and_intent_reaches_the_independent_exact_action_verifier(string user, string decision)
    {
        var model = new FixtureModel(decision);
        var request = Proposed(user);
        var result = await new ModelTriggerCommandAuthorizer(model).AuthorizeAsync(request);
        Assert.Equal(Enum.Parse<TriggerCommandAuthorizationDecision>(decision, true), result.Decision);
        var sent = Assert.Single(model.Requests);
        Assert.Contains(sent.Messages, m => m.Role == ModelRole.User && m.Text == user);
        Assert.Contains(sent.Messages, m => m.Text.Contains(request.ProposedAction.GetRawText(), StringComparison.Ordinal));
        Assert.Contains(sent.Messages, m => m.Role == ModelRole.Developer && m.Text.Contains(request.ActionHash));
    }

    [Theory]
    [InlineData("allow", TriggerCommandAuthorizationDecision.Allow)]
    [InlineData("deny", TriggerCommandAuthorizationDecision.Deny)]
    [InlineData("ambiguous", TriggerCommandAuthorizationDecision.Ambiguous)]
    [InlineData("truncated", TriggerCommandAuthorizationDecision.ClassifierInvalidOutput)]
    [InlineData("malformed", TriggerCommandAuthorizationDecision.ClassifierInvalidOutput)]
    public async Task Only_confirmed_length_limit_retries_with_fresh_output_and_identical_exact_context(string second, TriggerCommandAuthorizationDecision expected)
    {
        var model = new FixtureModel(second, firstTruncated: true);
        var result = await new ModelTriggerCommandAuthorizer(model, "low").AuthorizeAsync(Proposed());
        Assert.Equal(expected, result.Decision);
        Assert.Equal(2, model.Requests.Count);
        var first = model.Requests[0]; var retry = model.Requests[1];
        Assert.Equal(512, first.MaxOutputTokens); Assert.Equal(1024, retry.MaxOutputTokens);
        Assert.Equal(first, retry with { MaxOutputTokens = first.MaxOutputTokens });
        Assert.Equal("low", retry.ReasoningEffort); Assert.Null(retry.Tools);
    }

    [Theory]
    [InlineData("malformed", TriggerAuthorizationFailureReason.MalformedJson)]
    [InlineData("invalid", TriggerAuthorizationFailureReason.InvalidDecision)]
    [InlineData("duplicate", TriggerAuthorizationFailureReason.InvalidDecision)]
    [InlineData("missing-terminal", TriggerAuthorizationFailureReason.IncompleteOutput)]
    [InlineData("invalid-response", TriggerAuthorizationFailureReason.ProviderInvalidResponse)]
    [InlineData("unavailable", TriggerAuthorizationFailureReason.ProviderFailure)]
    [InlineData("oversized", TriggerAuthorizationFailureReason.OversizedOutput)]
    [InlineData("tool-channel", TriggerAuthorizationFailureReason.InvalidResponseChannel)]
    [InlineData("filtered", TriggerAuthorizationFailureReason.InvalidResponseChannel)]
    public async Task Other_failures_never_retry_or_accept_partial_allow(string mode, TriggerAuthorizationFailureReason reason)
    {
        var model = new FixtureModel(mode);
        var result = await new ModelTriggerCommandAuthorizer(model).AuthorizeAsync(Proposed());
        Assert.Equal(reason, result.FailureReason); Assert.NotEqual(TriggerCommandAuthorizationDecision.Allow, result.Decision);
        Assert.Single(model.Requests);
    }

    [Theory]
    [InlineData(31, 1)]
    [InlineData(25, 2)]
    public async Task One_shared_deadline_bounds_both_attempts(int firstSeconds, int expected)
    {
        var time = new FakeTimeProvider(); var calls = 0;
        var model = new FixtureModel("allow", true, () => time.Advance(TimeSpan.FromSeconds(++calls == 1 ? firstSeconds : 6)));
        var verdict = await new ModelTriggerCommandAuthorizer(model, timeProvider: time).AuthorizeAsync(Proposed());
        Assert.Equal(TriggerCommandAuthorizationDecision.ClassifierUnavailable, verdict.Decision);
        Assert.Equal(expected, model.Requests.Count);
    }

    [Fact]
    public async Task Cancellation_prevents_internal_recovery()
    {
        using var cts = new CancellationTokenSource();
        var model = new FixtureModel("allow", true, cts.Cancel);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ModelTriggerCommandAuthorizer(model).AuthorizeAsync(Proposed(), cts.Token).AsTask());
        Assert.Single(model.Requests);
    }

    private sealed class FixtureModel(string outcome, bool firstTruncated = false, Action? beforeTerminal = null) : ILanguageModel
    {
        public ModelCapabilities Capabilities { get; } = new(true, true);
        public List<ModelRequest> Requests { get; } = [];
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask; ct.ThrowIfCancellationRequested(); Requests.Add(request);
            if (outcome == "tool-channel") { yield return new ModelToolCallEvent(new("unexpected", "automation.create", "{}")); yield break; }
            var truncated = outcome == "truncated" || firstTruncated && Requests.Count == 1;
            yield return new ModelTextDelta(truncated ? "{\"decision\":\"allow\"}" : outcome switch
            { "malformed" => "broken", "invalid" => "{\"decision\":42}", "duplicate" => "{\"decision\":\"deny\",\"decision\":\"allow\"}", "oversized" => new string('x',4097), _ => JsonSerializer.Serialize(new { decision = outcome }) });
            if (outcome == "missing-terminal") yield break;
            if (outcome is "invalid-response" or "unavailable")
            { yield return new ModelFailed(new(outcome == "invalid-response" ? ProviderErrorCode.InvalidResponse : ProviderErrorCode.Unavailable, "Fixture failure")); yield break; }
            beforeTerminal?.Invoke();
            yield return new ModelCompleted(truncated ? ModelStopReason.LengthLimit : outcome == "filtered" ? ModelStopReason.ContentFiltered : ModelStopReason.Completed);
        }
    }
}
