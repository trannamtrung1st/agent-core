using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Providers.Synthetic;

namespace AgentCore.Application.Tests;

public sealed class CompletionEvaluatorTests
{
    [Fact]
    public void Ongoing_and_disabled_goal_sessions_skip_evaluation()
    {
        var now = new DateTimeOffset(2026, 9, 19, 5, 0, 0, TimeSpan.Zero);
        var ongoing = Snapshot(now);
        Assert.False(CompletionEvaluator.ShouldEvaluate(ongoing));

        var disabledGoal = Snapshot(
            now,
            new SessionPurpose(SessionPurposeKind.Goal, "Finish the briefing"),
            SessionCompletionPolicy.Default);
        Assert.Equal(AgentCompletionAuthority.Disabled, disabledGoal.CompletionPolicy!.AgentCompletion);
        Assert.False(CompletionEvaluator.ShouldEvaluate(disabledGoal));
    }

    [Fact]
    public async Task Continue_and_requestComplete_parse_from_scripted_json()
    {
        var now = new DateTimeOffset(2026, 9, 19, 5, 0, 0, TimeSpan.Zero);
        var snapshot = Snapshot(
            now,
            new SessionPurpose(SessionPurposeKind.Goal, "Wrap up"),
            new SessionCompletionPolicy(AgentCompletionAuthority.Allowed, true, true));
        var continueDecision = await CompletionEvaluator.EvaluateAsync(
            new ScriptedLanguageModel(completionDecision: """{"decision":"continue","reason":"still open"}"""),
            snapshot,
            now,
            CancellationToken.None);
        Assert.Equal("still open", Assert.IsType<ContinueSession>(continueDecision).Reason);

        var complete = await CompletionEvaluator.EvaluateAsync(
            new ScriptedLanguageModel(completionDecision: """{"decision":"requestComplete","reason":"goal met"}"""),
            snapshot,
            now,
            CancellationToken.None);
        Assert.Equal("goal met", Assert.IsType<RequestComplete>(complete).Reason);
    }

    [Fact]
    public async Task Malformed_or_provider_failed_evaluation_continues()
    {
        var now = new DateTimeOffset(2026, 9, 19, 5, 0, 0, TimeSpan.Zero);
        var snapshot = Snapshot(
            now,
            new SessionPurpose(SessionPurposeKind.Goal),
            new SessionCompletionPolicy(AgentCompletionAuthority.Allowed, true, true));
        var malformed = await CompletionEvaluator.EvaluateAsync(
            new ScriptedLanguageModel(completionDecision: "not-json"),
            snapshot,
            now,
            CancellationToken.None);
        Assert.IsType<ContinueSession>(malformed);

        var failed = await CompletionEvaluator.EvaluateAsync(
            new ScriptedLanguageModel(completionProviderFailed: true),
            snapshot,
            now,
            CancellationToken.None);
        Assert.Contains("provider", Assert.IsType<ContinueSession>(failed).Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Evaluation_request_is_marked_and_omits_user_visible_instructions()
    {
        var now = new DateTimeOffset(2026, 9, 19, 5, 0, 0, TimeSpan.Zero);
        var request = CompletionEvaluator.CreateEvaluationRequest(
            Snapshot(
                now,
                new SessionPurpose(SessionPurposeKind.Goal, "Collect a name"),
                new SessionCompletionPolicy(AgentCompletionAuthority.Advisory, true, true)),
            now);
        var system = request.Messages.First(message => message.Role == ModelRole.System).Text;
        Assert.Contains(CompletionEvaluator.Marker, system, StringComparison.Ordinal);
        Assert.Contains("requestComplete", system, StringComparison.Ordinal);
        Assert.DoesNotContain("[[speech:", system, StringComparison.Ordinal);
    }

    private static SessionSnapshot Snapshot(
        DateTimeOffset now,
        SessionPurpose? purpose = null,
        SessionCompletionPolicy? policy = null) =>
        new(
            1,
            Guid.Parse("873f07d1-e264-4c81-a31b-7e59e940b842"),
            1,
            SampleDefinitions.Examiner,
            SessionMode.Text,
            null,
            SessionStatus.Attached,
            [
                new ConversationEntry(
                    Guid.NewGuid(),
                    1,
                    Guid.NewGuid(),
                    ConversationRole.User,
                    "Hello",
                    null,
                    EntryStatus.Completed,
                    SessionMode.Text,
                    5,
                    5,
                    now)
            ],
            string.Empty,
            0,
            null,
            null,
            now,
            now,
            Purpose: purpose,
            CompletionPolicy: policy);
}
