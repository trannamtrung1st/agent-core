using System.Runtime.CompilerServices;
using AgentCore.Application.Agents;
using AgentCore.Application.Memory;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Testing;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Memory;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Providers.Synthetic;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Application.Tests;

public sealed class ExplicitUserMemoryTests
{
    private const string CodenameFact = "Atlas";

    private static readonly DateTimeOffset Now = new(2026, 9, 24, 8, 0, 0, TimeSpan.Zero);
    private static readonly Guid Instance = Guid.Parse("019944af-0020-7000-8000-0000000000a1");
    private static readonly Guid SessionOne = Guid.Parse("019944af-0020-7000-8000-0000000000b1");
    private static readonly Guid SessionTwo = Guid.Parse("019944af-0020-7000-8000-0000000000b2");
    private static readonly Guid ProfileId = LocalUserProfile.Id;

    [Theory]
    [InlineData("Please remember that my project codename is Atlas.", "project codename", CodenameFact)]
    [InlineData("remember P4A_LONG_FACT for later.", "Remembered item", "P4A_LONG_FACT")]
    [InlineData("Please remember P4A_LONG_FACT for later.", "Remembered item", "P4A_LONG_FACT")]
    public void TryParse_recognizes_explicit_remember_phrases(string text, string subject, string content)
    {
        Assert.True(ExplicitUserMemoryRequests.TryParse(text, out var parsed));
        Assert.NotNull(parsed);
        Assert.Equal(MemoryKind.Fact, parsed!.Kind);
        Assert.Equal(subject, parsed.Subject, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(content, parsed.Content, StringComparer.Ordinal);
    }

    [Theory]
    [InlineData("What is the code word?")]
    [InlineData("Remember to call John tomorrow.")]
    public void TryParse_ignores_non_explicit_requests(string text)
    {
        Assert.False(ExplicitUserMemoryRequests.TryParse(text, out _));
    }

    [Fact]
    public async Task New_session_on_same_instance_receives_promoted_identity_memory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agent-core-explicit-memory-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite($"Data Source={path}").Options;
        var clock = new FakeTimeProvider(Now);
        var factory = new SqliteFactory(options);
        var sessions = new SqliteMemoryStore(factory, clock);
        var memoryStore = new SqliteStructuredMemoryStore(factory);
        try
        {
            await sessions.EnsureCreatedAsync();
            await sessions.SaveProfileAsync(Profile(), 0);
            var definition = Enabled();
            await sessions.SaveAsync(Snapshot(SessionOne, definition), 0);
            await sessions.SaveAsync(Snapshot(SessionTwo, definition), 0);
            var memories = Service(memoryStore, clock, 24);
            var captureModel = new RecordingLanguageModel(new ScriptedLanguageModel());

            await using (var runtime = Runtime(
                (await sessions.LoadAsync(SessionOne))!,
                sessions,
                memories,
                captureModel,
                clock))
            {
                await runtime.AttachAsync();
                Assert.True(await runtime.SubmitPersistedUserTextAsync(
                    "Please remember that my project codename is Atlas.",
                    Guid.Parse("019944af-0020-7000-8000-0000000000e1")));
                await runtime.WaitUntilIdleAsync();
            }

            var admission = Admission();
            var identityOwner = new TrustedIdentityUserOwner(Instance, ProfileId);
            var stored = await memories.SearchIdentityUserAsync(
                identityOwner,
                new MemorySearchQuery("codename", null),
                retrievalAllowed: true,
                admission);
            Assert.Contains(stored, item => item.Content == CodenameFact);

            var recallModel = new RecordingLanguageModel(new ScriptedLanguageModel());
            await using (var runtime = Runtime(
                (await sessions.LoadAsync(SessionTwo))!,
                sessions,
                memories,
                recallModel,
                clock))
            {
                await runtime.AttachAsync();
                Assert.True(await runtime.SubmitPersistedUserTextAsync(
                    "What is my project codename?",
                    Guid.Parse("019944af-0020-7000-8000-0000000000e2")));
                await runtime.WaitUntilIdleAsync();
            }

            var request = recallModel.LastRequest;
            Assert.NotNull(request);
            Assert.Contains(
                PromptContextBuilder.BuildMemoryCapability(definition.MemoryPolicy),
                request.Messages[2].Text,
                StringComparison.Ordinal);
            var learned = request.Messages.Single(message =>
                message.Text.StartsWith(SessionMemoryPrompt.LearnedDataLabel, StringComparison.Ordinal)).Text;
            Assert.Contains("project codename", learned, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(CodenameFact, learned, StringComparison.Ordinal);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static SessionRuntime Runtime(
        SessionSnapshot snapshot,
        IMemoryStore sessions,
        IStructuredMemoryService memories,
        ILanguageModel model,
        TimeProvider time) =>
        new(
            snapshot,
            model,
            new DefaultAgentBrain(new PromptContextBuilder()),
            sessions,
            new CapturingSessionOutput(),
            Ids(32, "019944af-0021-7000-8000-"),
            time,
            NullLogger<SessionRuntime>.Instance,
            structuredMemory: memories);

    private static StructuredMemoryService Service(IStructuredMemoryStore store, TimeProvider time, int count) =>
        new(store, Ids(count, "019944af-0022-7000-8000-"), time);

    private static DeterministicIdGenerator Ids(int count, string prefix) =>
        new(
            Enumerable.Range(1, count).Select(index => Guid.Parse($"{prefix}{index:D12}")),
            [Guid.Parse("019944af-0020-7000-8000-0000000000ff")]);

    private static AgentDefinition Enabled() =>
        SampleDefinitions.Support with
        {
            MemoryPolicy = new MemoryPolicy(
                SessionMemory: true,
                IdentityUserPromotion: true,
                IdentityUserRetrieval: true)
        };

    private static UserProfile Profile() =>
        new(
            ProfileId,
            1,
            new Dictionary<string, UserProfileValue>(StringComparer.Ordinal)
            {
                ["language"] = LocalUserProfile.ApplicationProfileValue("en", Now)
            },
            Now);

    private static SessionSnapshot Snapshot(Guid sessionId, AgentDefinition definition) =>
        new(
            1,
            sessionId,
            1,
            definition,
            SessionMode.Text,
            null,
            SessionStatus.Created,
            [],
            string.Empty,
            0,
            null,
            ProfileId,
            Now,
            Now,
            AgentInstanceId: Instance);

    private static MemoryAdmissionContext Admission() =>
        new("application", [], new HashSet<string>(StringComparer.Ordinal));

    private sealed class RecordingLanguageModel(ILanguageModel inner) : ILanguageModel
    {
        public ModelRequest? LastRequest { get; private set; }

        public ModelCapabilities Capabilities => inner.Capabilities;

        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (!request.Messages.Any(message => message.Text.Contains(ConversationCompactor.Marker, StringComparison.Ordinal)))
            {
                LastRequest = request;
            }

            await foreach (var item in inner.GenerateAsync(request, cancellationToken).ConfigureAwait(false))
            {
                yield return item;
            }
        }
    }

    private sealed class SqliteFactory(DbContextOptions<AgentCoreDbContext> options) : IDbContextFactory<AgentCoreDbContext>
    {
        public AgentCoreDbContext CreateDbContext() => new(options);

        public Task<AgentCoreDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
