using System.Runtime.CompilerServices;
using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Testing;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Providers.OpenAICompatible;
using AgentCore.Infrastructure.Providers.SemanticResponses;
using AgentCore.Infrastructure.Providers.Synthetic;
using Xunit.Abstractions;

namespace AgentCore.Application.Tests;

public sealed partial class MessagingSkillJourneyTests
{
    private readonly ITestOutputHelper liveEvidence;

    public MessagingSkillJourneyTests(ITestOutputHelper liveEvidence) => this.liveEvidence = liveEvidence;

    [SkillContentLiveFact]
    public async Task Hosted_skill_only_turn_and_compacted_history_retain_meaning_without_reactivation()
    {
        var modelId = Environment.GetEnvironmentVariable("AGENTCORE_LLM_MODEL")!;
        using var http = new HttpClient();
        var model = new OpenAICompatibleLanguageModel(http, new()
        {
            Adapter = "OpenAICompatible", BaseUrl = "https://openrouter.ai/api/v1/",
            ApiKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY"), DefaultModel = modelId,
            Tools = true, StructuredOutput = false,
            Timeouts = new() { SetupSeconds = 20, StreamIdleSeconds = 45, TotalSeconds = 60 }
        });
        await VerifySkillContentUatAsync(model, modelId);
    }

    [Fact]
    public Task Skill_content_UAT_fixture_compacts_and_reopens_offline() =>
        VerifySkillContentUatAsync(new SkillContentReplayModel(), "scripted-skill-uat");

    private async Task VerifySkillContentUatAsync(ILanguageModel model, string modelId)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var bounded = new BoundedLiveModel(model);
        var recording = new RecordingModel(bounded);
        var definition = Definition(Skill("say", "For this invocation only, briefly confirm readiness to work on the AI engineering project. Do not call other tools.")) with
        {
            Identity = new("UAT assistant", "Project assistant", "Disposable Skill content validation", "Concise"),
            Goals = ["Help the user with their project."],
            SystemInstructions = "Answer the user's request concisely. Treat prior Skill usage as history, not a new invocation.",
            InitiativePolicy = SampleDefinitions.Examiner.InitiativePolicy with { Enabled = false },
            ConversationPolicy = SampleDefinitions.Examiner.ConversationPolicy with { MaxOutputTokens = 1200 }
        };
        var turns = new RuntimeAgentRunStore();
        var memory = new InMemoryMemoryStore();
        await using var template = Create(new ScriptedLanguageModel(), new(), new(), new(), definition);
        var initial = template.Snapshot with
        {
            ModelSelection = new("live-skill-uat", "primary-llm", modelId, ModelSelectionSource.SystemDefault, null)
        };
        SessionSnapshot saved;
        ConversationEntry firstAnswer;
        await using (var runtime = Create(new SemanticResponseLanguageModel(recording), new(), turns, memory, definition, initial))
        {
            await runtime.AttachAsync();
            UserMessagePart[] parts = [new("invocation", InvocationKind: "skill", SkillKey: "definition:say")];
            Assert.True(await runtime.SubmitUserTextAsync(UserMessageContent.DisplayText(parts), parts: parts));
            await runtime.WaitUntilIdleAsync(deadline.Token);
            var user = Assert.Single(runtime.Snapshot.Entries, e => e.Role == ConversationRole.User);
            Assert.Equal(parts, user.Parts);
            Assert.Equal("/say", runtime.Snapshot.Title);
            firstAnswer = Assert.Single(runtime.Snapshot.Entries, e => e.Role == ConversationRole.Assistant && e.Status == EntryStatus.Completed);
            Assert.Contains("AI engineering", firstAnswer.Text, StringComparison.OrdinalIgnoreCase);
            Assert.True(await runtime.SubmitReceiptAsync(firstAnswer.ResponseId!.Value, firstAnswer.Text.Length, deadline.Token));
            var originalRun = (await turns.ForSourceAsync(runtime.SessionId, user.SourceEventId!.Value))!;
            Assert.Equal(["definition:say"], originalRun.ActiveSkillKeys);
            Assert.Equal(0, originalRun.SkillLoadCount);

            var before = recording.Requests.Count;
            Assert.True(await runtime.SubmitUserTextAsync("For AI engineering, we chose .NET 10. Confirm that decision briefly without invoking any Skill."));
            await runtime.WaitUntilIdleAsync(deadline.Token);
            var firstRequest = recording.Requests[0];
            Assert.Contains("Explicitly invoked Skill:", Text(firstRequest));
            Assert.Contains(definition.SkillList[0].Procedure, Text(firstRequest));
            var plainRequest = recording.Requests[before];
            Assert.Contains("Previously invoked Skill (historical, not activated)", Text(plainRequest));
            Assert.DoesNotContain(definition.SkillList[0].Procedure, Text(plainRequest));
            Assert.Equal(2, runtime.Snapshot.Entries.Count(e => e.Role == ConversationRole.Assistant && e.Status == EntryStatus.Completed));
            var ordinaryAnswer = runtime.Snapshot.Entries.Last(e => e.Role == ConversationRole.Assistant);
            Assert.True(await runtime.SubmitReceiptAsync(ordinaryAnswer.ResponseId!.Value, ordinaryAnswer.Text.Length, deadline.Token));
            await runtime.WaitUntilIdleAsync(deadline.Token);

            var ordinaryUser = runtime.Snapshot.Entries.Last(e => e.Role == ConversationRole.User);
            Assert.Empty((await turns.ForSourceAsync(runtime.SessionId, ordinaryUser.SourceEventId!.Value))!.ActiveSkillKeys);

            // Seed ordinary durable fixture turns to cross the real compaction threshold
            // without spending provider calls on a long artificial conversation.
            saved = (await memory.LoadAsync(runtime.SessionId))!;
        }
        var history = saved.Entries.ToList();
        var sequence = saved.LastEntrySequence;
        while (history.Count(e => e.Role is ConversationRole.User or ConversationRole.Assistant) < CompactionPolicy.TriggerEligibleEntries)
        {
            var role = history.Count(e => e.Role is ConversationRole.User or ConversationRole.Assistant) % 2 == 0
                ? ConversationRole.User : ConversationRole.Assistant;
            var text = role == ConversationRole.User ? "Continue the project planning." : "The project plan remains unchanged.";
            history.Add(new(Guid.NewGuid(), ++sequence, Guid.NewGuid(), role, text,
                role == ConversationRole.Assistant ? Guid.NewGuid() : null, EntryStatus.Completed,
                SessionMode.Text, 0, text.Length, saved.UpdatedAt));
        }
        var seeded = saved with { Revision = saved.Revision + 1, Entries = history, LastEntrySequence = sequence };
        await memory.SaveAsync(seeded, saved.Revision);
        var outcome = await new ConversationCompactor(memory).TryCompactAsync(new(saved.SessionId, "", 0,
            SummaryFormats.Legacy, bounded, DateTimeOffset.UtcNow,
            new("skill-content-uat", "primary-llm", modelId, null)), deadline.Token);
        Assert.True(outcome is CompactionAccepted, $"Compaction rejected: {(outcome as CompactionRejected)?.Reason}");
        var accepted = Assert.IsType<CompactionAccepted>(outcome).Candidate;
        Assert.Contains("say", accepted.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("AI engineering", accepted.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(".NET 10", accepted.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("definition:say", accepted.Summary);
        Assert.Equal(history, await memory.ReadHistoryAsync(saved.SessionId, 0, 100));

        var compacted = seeded with { Revision = seeded.Revision + 1, Summary = accepted.Summary,
            SummarizedThroughEntrySequence = accepted.ThroughEntrySequence, SummaryFormatVersion = accepted.FormatVersion,
            SummaryGeneratedAt = accepted.GeneratedAt, SummaryModel = accepted.Model };
        await memory.SaveAsync(compacted, seeded.Revision);
        await using var reopened = Create(new SemanticResponseLanguageModel(recording), new(), turns, memory, definition, compacted, idStart: 201);
        await reopened.AttachAsync();
        var recallStart = recording.Requests.Count;
        Assert.True(await reopened.SubmitUserTextAsync("What project and framework did we choose, and which Skill did I previously use? Answer from history without invoking a Skill."));
        await reopened.WaitUntilIdleAsync(deadline.Token);
        var recalled = reopened.Snapshot.Entries.Last(e => e.Role == ConversationRole.Assistant && e.Status == EntryStatus.Completed).Text;
        Assert.Contains("AI engineering", recalled, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(".NET 10", recalled, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("say", recalled, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(accepted.Summary, Text(recording.Requests[recallStart]));
        Assert.DoesNotContain(definition.SkillList[0].Procedure, Text(recording.Requests[recallStart]));
        var lastUser = reopened.Snapshot.Entries.Last(e => e.Role == ConversationRole.User);
        Assert.Empty((await turns.ForSourceAsync(saved.SessionId, lastUser.SourceEventId!.Value))!.ActiveSkillKeys);
        Assert.Equal("/say", reopened.Snapshot.Title);
        Assert.Equal(4, bounded.Calls);
        liveEvidence.WriteLine($"Model: {modelId}; generation calls: {bounded.Calls}/4; seeded eligible history: 40; summarized through: {accepted.ThroughEntrySequence}");
        liveEvidence.WriteLine($"Skill-only answer: {firstAnswer.Text}\nSummary: {accepted.Summary}\nRecall answer: {recalled}");
    }

    // This replay proves fixture mechanics, not hosted-model summary quality.
    private sealed class SkillContentReplayModel : ILanguageModel
    {
        public ModelCapabilities Capabilities { get; } = new(true, true, Tools: true);
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.CompletedTask;
            const string text = "The user invoked Say for the AI engineering project and chose .NET 10.";
            if (request.Messages.Any(m => m.Text.Contains(ConversationCompactor.Marker)))
            {
                yield return new ModelTextDelta(text);
                yield return new ModelCompleted(ModelStopReason.Completed);
            }
            else
            {
                var response = System.Text.Json.JsonSerializer.Serialize(new
                {
                    disposition = "Complete", action = "chat.respond", displayText = text,
                    speech = new { mode = "none", text = (string?)null }, blocks = Array.Empty<object>(), memory = Array.Empty<object>()
                });
                yield return new ModelToolCallEvent(new(Guid.NewGuid().ToString("N"), "agent_core_respond", response));
                yield return new ModelCompleted(ModelStopReason.ToolCalls);
            }
        }
    }

    private sealed class BoundedLiveModel(ILanguageModel inner) : ILanguageModel
    {
        public int Calls { get; private set; }
        public ModelCapabilities Capabilities => inner.Capabilities;
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (++Calls > 4) throw new InvalidOperationException("Live Skill UAT exceeded its four-generation budget.");
            await foreach (var item in inner.GenerateAsync(request with { MaxOutputTokens = Math.Min(request.MaxOutputTokens, 1200) }, cancellationToken))
                yield return item;
        }
    }
}

public sealed class SkillContentLiveFactAttribute : FactAttribute
{
    public SkillContentLiveFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("AGENTCORE_SKILL_CONTENT_LIVE") != "1")
            Skip = "Explicit Skill content live UAT opt-in required.";
        else if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENROUTER_API_KEY"))
            || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AGENTCORE_LLM_MODEL")))
            Skip = "Configured model and provider credential required.";
    }
}
