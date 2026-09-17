using System.Text.Json;
using AgentCore.Application.Agents;
using AgentCore.Application.Observability;
using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Providers.Synthetic;

namespace AgentCore.Application.Tests;

public sealed class InitiativeEvaluatorTests
{
    [Fact]
    public void Evaluation_request_includes_agent_identity_and_policy()
    {
        var now = DateTimeOffset.UtcNow;
        var context = ExaminerContext(now, silenceSeconds: 45, assistantText: "Would you like to try again?");
        var request = InitiativeEvaluator.CreateEvaluationRequest(context, new PromptContextBuilder());
        var system = request.Messages.First(message => message.Role == ModelRole.System).Text;
        var userJson = request.Messages.Last(message => message.Role == ModelRole.User).Text;

        Assert.Contains(InitiativeEvaluator.Marker, system, StringComparison.Ordinal);
        Assert.Contains("Alex", system, StringComparison.Ordinal);
        Assert.Contains("Speaking examiner", system, StringComparison.Ordinal);
        Assert.Contains("meaningfully advance", system, StringComparison.Ordinal);

        using var doc = JsonDocument.Parse(userJson);
        var root = doc.RootElement;
        Assert.Equal("examiner", root.GetProperty("agent").GetProperty("id").GetString());
        Assert.True(root.GetProperty("silenceMs").GetDouble() >= 44_000);
        Assert.Equal("LongSilence", root.GetProperty("trigger").GetString());
    }

    [Fact]
    public async Task Synthetic_examiner_stays_silent_after_recent_question_with_moderate_silence()
    {
        var json = await RunSyntheticInitiativeAsync(
            SampleDefinitions.Examiner,
            silenceSeconds: 50,
            assistantText: "Would you like to try again?");
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("staySilent", doc.RootElement.GetProperty("decision").GetString());
    }

    [Fact]
    public async Task Synthetic_examiner_speaks_after_long_silence()
    {
        var json = await RunSyntheticInitiativeAsync(
            SampleDefinitions.Examiner,
            silenceSeconds: 95,
            assistantText: "Would you like to try again?");
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("speak", doc.RootElement.GetProperty("decision").GetString());
    }

    [Fact]
    public async Task Synthetic_examiner_avoids_second_nudge_in_same_period()
    {
        var json = await RunSyntheticInitiativeAsync(
            SampleDefinitions.Examiner,
            silenceSeconds: 120,
            assistantText: "You can start with your name.",
            speaksThisSilencePeriod: 1);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("staySilent", doc.RootElement.GetProperty("decision").GetString());
    }

    [Fact]
    public async Task Synthetic_support_stays_silent_when_user_closed_the_thread()
    {
        var support = SampleDefinitions.Support with
        {
            InitiativePolicy = new InitiativePolicy(
                true,
                10_000,
                30_000,
                2,
                ["longSilence"],
                MaxConsecutiveProactiveTurns: 2)
        };
        var json = await RunSyntheticInitiativeAsync(
            support,
            silenceSeconds: 50,
            userText: "Thanks, that's all.",
            assistantText: "Glad I could help.");
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("staySilent", doc.RootElement.GetProperty("decision").GetString());
    }

    [Fact]
    public async Task Synthetic_support_can_speak_twice_when_context_unresolved()
    {
        var support = SampleDefinitions.Support with
        {
            InitiativePolicy = new InitiativePolicy(
                true,
                10_000,
                30_000,
                2,
                ["longSilence"],
                MaxConsecutiveProactiveTurns: 2)
        };
        var first = await RunSyntheticInitiativeAsync(
            support,
            silenceSeconds: 50,
            userText: "My order is late.",
            assistantText: "I can help with that.");
        var second = await RunSyntheticInitiativeAsync(
            support,
            silenceSeconds: 50,
            userText: "My order is late.",
            assistantText: "I can help with that.",
            speaksThisSilencePeriod: 1);
        using var firstDoc = JsonDocument.Parse(first);
        using var secondDoc = JsonDocument.Parse(second);
        Assert.Equal("speak", firstDoc.RootElement.GetProperty("decision").GetString());
        Assert.Equal("speak", secondDoc.RootElement.GetProperty("decision").GetString());
    }

    [Fact]
    public async Task Evaluation_records_structured_initiative_telemetry()
    {
        RuntimeTelemetry.Reset();
        var model = new ScriptedLanguageModel();
        var builder = new PromptContextBuilder();
        var now = DateTimeOffset.UtcNow;
        var context = ExaminerContext(now, silenceSeconds: 95, assistantText: "Would you like to try again?");
        _ = await InitiativeEvaluator.EvaluateAsync(model, builder, context, Guid.NewGuid(), CancellationToken.None);
        var timeline = RuntimeTelemetry.SnapshotTimeline();
        var entry = Assert.Single(timeline, item => item.Stage == "initiative_eval");
        Assert.False(string.IsNullOrEmpty(entry.Detail));
        Assert.Contains("examiner", entry.Detail!, StringComparison.Ordinal);
        Assert.Contains("evaluated", entry.Detail!, StringComparison.Ordinal);
        Assert.Contains("reasonCode", entry.Detail!, StringComparison.Ordinal);
        Assert.DoesNotContain("reasonDetail", entry.Detail!, StringComparison.Ordinal);
    }

    private static AgentContext ExaminerContext(
        DateTimeOffset now,
        double silenceSeconds,
        string assistantText,
        string userText = "Hello",
        int speaksThisSilencePeriod = 0) =>
        new(
            SampleDefinitions.Examiner,
            [
                new ConversationEntry(
                    Guid.NewGuid(),
                    1,
                    Guid.NewGuid(),
                    ConversationRole.User,
                    userText,
                    null,
                    EntryStatus.Completed,
                    SessionMode.Text,
                    userText.Length,
                    userText.Length,
                    now.AddSeconds(-silenceSeconds - 5)),
                new ConversationEntry(
                    Guid.NewGuid(),
                    2,
                    Guid.NewGuid(),
                    ConversationRole.Assistant,
                    assistantText,
                    Guid.NewGuid(),
                    EntryStatus.Completed,
                    SessionMode.Text,
                    assistantText.Length,
                    assistantText.Length,
                    now.AddSeconds(-silenceSeconds / 2))
            ],
            string.Empty,
            null,
            SessionMode.Text,
            null,
            false,
            null,
            new AgentTrigger(Guid.NewGuid(), TriggerKind.LongSilence, null),
            SpeaksThisSilencePeriod: speaksThisSilencePeriod,
            UtcNow: now,
            LastUserActivityAt: now.AddSeconds(-silenceSeconds));

    private static async Task<string> RunSyntheticInitiativeAsync(
        AgentDefinition definition,
        double silenceSeconds,
        string assistantText,
        string userText = "Hello",
        int speaksThisSilencePeriod = 0)
    {
        var now = DateTimeOffset.UtcNow;
        var context = new AgentContext(
            definition,
            [
                new ConversationEntry(
                    Guid.NewGuid(),
                    1,
                    Guid.NewGuid(),
                    ConversationRole.User,
                    userText,
                    null,
                    EntryStatus.Completed,
                    SessionMode.Text,
                    userText.Length,
                    userText.Length,
                    now.AddSeconds(-silenceSeconds - 5)),
                new ConversationEntry(
                    Guid.NewGuid(),
                    2,
                    Guid.NewGuid(),
                    ConversationRole.Assistant,
                    assistantText,
                    Guid.NewGuid(),
                    EntryStatus.Completed,
                    SessionMode.Text,
                    assistantText.Length,
                    assistantText.Length,
                    now.AddSeconds(-Math.Max(1, silenceSeconds / 2)))
            ],
            string.Empty,
            null,
            SessionMode.Text,
            null,
            false,
            null,
            new AgentTrigger(Guid.NewGuid(), TriggerKind.LongSilence, null),
            SpeaksThisSilencePeriod: speaksThisSilencePeriod,
            UtcNow: now,
            LastUserActivityAt: now.AddSeconds(-silenceSeconds));
        var request = InitiativeEvaluator.CreateEvaluationRequest(context, new PromptContextBuilder());
        var model = new ScriptedLanguageModel();
        var builder = new System.Text.StringBuilder();
        await foreach (var evt in model.GenerateAsync(request))
        {
            if (evt is ModelTextDelta delta)
            {
                builder.Append(delta.Text);
            }
        }

        return builder.ToString();
    }
}
