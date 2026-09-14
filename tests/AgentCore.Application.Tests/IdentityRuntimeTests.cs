using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Testing;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Providers.Synthetic;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Application.Tests;

public sealed class RecordingLanguageModel : ILanguageModel
{
    private readonly ILanguageModel _inner;

    public RecordingLanguageModel(ILanguageModel inner) => _inner = inner;

    public ModelCapabilities Capabilities => _inner.Capabilities;

    public ModelRequest? LastRequest { get; private set; }

    public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(
        ModelRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        LastRequest = request;
        await foreach (var item in _inner.GenerateAsync(request, cancellationToken))
        {
            yield return item;
        }
    }
}

public sealed class IdentityRuntimeTests
{
    [Fact]
    public void Prompt_sections_are_exact_and_identities_differ_only_by_definition()
    {
        var builder = new PromptContextBuilder();
        var examiner = Context(Load("examiner"), "Please explain.");
        var support = Context(Load("customer-support"), "Please explain.");
        var examinerSections = builder.BuildSections(examiner);
        var supportSections = builder.BuildSections(support);

        Assert.Equal(PromptContextBuilder.BuildIdentitySystem(examiner.Definition), examinerSections.IdentitySystem);
        Assert.Equal(PromptContextBuilder.BuildIdentitySystem(support.Definition), supportSections.IdentitySystem);
        Assert.Contains("Alex", examinerSections.IdentitySystem, StringComparison.Ordinal);
        Assert.Contains("Sam", supportSections.IdentitySystem, StringComparison.Ordinal);
        Assert.DoesNotContain("Sam", examinerSections.IdentitySystem, StringComparison.Ordinal);
        Assert.Equal(examinerSections.ModeSystem, supportSections.ModeSystem);
        Assert.Equal(1, examinerSections.TurnMessages.Count(message => message.Role == ModelRole.User && message.Text == "Please explain."));
        Assert.Equal(1, examinerSections.TurnMessages.Count(message => message.Role == ModelRole.User));
    }

    [Fact]
    public async Task Default_brain_returns_stay_silent_when_idle_help_is_not_useful()
    {
        var brain = new DefaultAgentBrain(new PromptContextBuilder());
        var context = Context(Load("examiner"), "Hello") with
        {
            Trigger = new AgentTrigger(Guid.NewGuid(), TriggerKind.LongSilence, null)
        };
        var decision = await brain.DecideAsync(context, Guid.NewGuid());
        Assert.IsType<StaySilent>(decision);
    }

    [Fact]
    public async Task Two_sessions_are_independent_and_revisions_are_deterministic()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero));
        var store = new InMemoryMemoryStore();
        var examinerModel = new RecordingLanguageModel(new ScriptedLanguageModel());
        var supportModel = new RecordingLanguageModel(new ScriptedLanguageModel());
        var examiner = await StartAsync(Load("examiner"), examinerModel, store, time, sessionIndex: 1);
        var support = await StartAsync(Load("customer-support"), supportModel, store, time, sessionIndex: 2);
        await using var examinerRuntime = examiner.Runtime;
        await using var supportRuntime = support.Runtime;

        await examinerRuntime.SubmitUserTextAsync("Hello");
        await supportRuntime.SubmitUserTextAsync("thanks");
        await examinerRuntime.WaitUntilIdleAsync();
        await supportRuntime.WaitUntilIdleAsync();

        var examinerSnap = await store.LoadAsync(examiner.SessionId);
        var supportSnap = await store.LoadAsync(support.SessionId);
        Assert.NotEqual(examiner.SessionId, support.SessionId);
        Assert.Equal("examiner", examinerSnap!.Definition.Id);
        Assert.Equal("customer-support", supportSnap!.Definition.Id);
        Assert.Contains(examinerSnap.Entries, entry => entry.Role == ConversationRole.User && entry.Text == "Hello");
        Assert.DoesNotContain(examinerSnap.Entries, entry => entry.Text == "thanks");
        Assert.Contains(supportSnap.Entries, entry => entry.Text == "thanks");
        Assert.Equal(4, examinerSnap.Revision);
        Assert.Equal(4, supportSnap.Revision);
        Assert.Contains("Alex", examinerModel.LastRequest!.Messages[0].Text, StringComparison.Ordinal);
        Assert.Contains("Sam", supportModel.LastRequest!.Messages[0].Text, StringComparison.Ordinal);
        Assert.Equal(1, examinerModel.LastRequest.Messages.Count(message => message.Role == ModelRole.User));
    }

    [Fact]
    public void Application_and_domain_do_not_reference_provider_dtos()
    {
        var root = FindRepoRoot();
        foreach (var path in Directory.GetFiles(Path.Combine(root, "src", "AgentCore.Application"), "*.cs", SearchOption.AllDirectories)
                     .Concat(Directory.GetFiles(Path.Combine(root, "src", "AgentCore.Domain"), "*.cs", SearchOption.AllDirectories)))
        {
            var text = File.ReadAllText(path);
            Assert.DoesNotContain("OpenAI.", text, StringComparison.Ordinal);
            Assert.DoesNotContain("ChatCompletion", text, StringComparison.Ordinal);
            Assert.DoesNotContain("openrouter", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static AgentContext Context(AgentDefinition definition, string userText)
    {
        var now = new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero);
        var user = new ConversationEntry(
            Guid.Parse("019944af-0000-7000-8000-000000000010"),
            1,
            Guid.Parse("019944af-0000-7000-8000-000000000010"),
            ConversationRole.User,
            userText,
            null,
            EntryStatus.Completed,
            SessionMode.Text,
            userText.Length,
            userText.Length,
            now);
        return new AgentContext(
            definition,
            [user],
            string.Empty,
            null,
            SessionMode.Text,
            null,
            false,
            null,
            new AgentTrigger(user.EntryId, TriggerKind.UserTurn, userText));
    }

    private static async Task<(SessionRuntime Runtime, Guid SessionId)> StartAsync(
        AgentDefinition definition,
        ILanguageModel model,
        InMemoryMemoryStore store,
        FakeTimeProvider time,
        int sessionIndex)
    {
        var ids = new DeterministicIdGenerator(
            Enumerable.Range(1, 64).Select(index => Guid.Parse($"019944af-000{sessionIndex}-7000-8000-{index:D12}")),
            [Guid.Parse($"873f07d1-e264-4c81-a31b-7e59e940b84{sessionIndex}")]);
        var now = time.GetUtcNow();
        var snapshot = new SessionSnapshot(
            1,
            ids.NewSessionId(),
            1,
            definition,
            SessionMode.Text,
            null,
            SessionStatus.Created,
            [],
            string.Empty,
            0,
            null,
            null,
            now,
            now);
        await store.SaveAsync(snapshot, 0);
        var runtime = new SessionRuntime(
            snapshot,
            model,
            new DefaultAgentBrain(new PromptContextBuilder()),
            store,
            new CapturingSessionOutput(),
            ids,
            time,
            NullLogger<SessionRuntime>.Instance);
        return (runtime, snapshot.SessionId);
    }

    private static AgentDefinition Load(string id)
    {
        var store = new Infrastructure.Definitions.FileAgentDefinitionStore(FindAgents(), SyntheticProviderAliases.Default);
        return store.GetAsync(id).AsTask().GetAwaiter().GetResult()
               ?? throw new InvalidOperationException(id);
    }

    private static string FindAgents()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var agents = Path.Combine(dir.FullName, "agents");
            if (Directory.Exists(agents))
            {
                return agents;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException();
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "AgentCore.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException();
    }
}
