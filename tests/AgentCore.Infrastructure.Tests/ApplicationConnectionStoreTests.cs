using AgentCore.Application.Connections;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Connections;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Browser;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AgentCore.Infrastructure.Tests;

public sealed class ApplicationConnectionStoreTests
{
    [Fact]
    public async Task Sqlite_reopen_keeps_connected_and_rows_stay_separate()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agent-core-connection-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite($"Data Source={path}").Options;
        var factory = new SqliteFactory(options);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        try
        {
            var memory = new SqliteMemoryStore(factory, TimeProvider.System);
            await memory.EnsureCreatedAsync();
            var store = new SqliteApplicationConnectionStore(factory);
            var service = new ApplicationConnectionService(
                store,
                new SystemIdGenerator(TimeProvider.System),
                TimeProvider.System,
                browser: new ConnectedBrowser());
            await service.ConnectAsync(first, "Store", "http://127.0.0.1:5088/");
            await service.ConnectAsync(second, "Other", "http://127.0.0.1:5099/");

            var reopened = new SqliteApplicationConnectionStore(factory);
            var loaded = await reopened.GetByAgentAsync(first);
            var other = await reopened.GetByAgentAsync(second);
            Assert.NotNull(loaded);
            Assert.Equal(ApplicationConnectionStatus.Connected, loaded!.Status);
            Assert.Equal(first, loaded.ProfileKey);
            Assert.Equal("http://127.0.0.1:5088", loaded.BaseUrl);
            Assert.Equal(["http://127.0.0.1:5088"], loaded.TrustedOrigins);
            Assert.NotEqual(loaded.ConnectionId, other!.ConnectionId);
            Assert.Equal(second, other.AgentInstanceId);
        }
        finally
        {
            SqliteConnectionClear(path);
        }
    }

    [Fact]
    public async Task Reset_deletes_only_the_targeted_profile_and_close_keeps_it()
    {
        var root = Path.Combine(Path.GetTempPath(), "agent-core-profiles-" + Guid.NewGuid().ToString("N"));
        var messages = new List<string>();
        using var logs = LoggerFactory.Create(builder => builder.AddProvider(new CollectingLoggerProvider(messages)));
        var session = new PlaywrightBrowserSession(
            new BrowserOptions
            {
                Enabled = false,
                ProfileMode = nameof(BrowserProfileMode.PersistentAgent),
                ProfileRoot = root
            },
            logs);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var firstDirectory = Path.Combine(root, first.ToString("D"));
        var secondDirectory = Path.Combine(root, second.ToString("D"));
        Directory.CreateDirectory(firstDirectory);
        Directory.CreateDirectory(secondDirectory);
        var lockFile = firstDirectory + ".lock";
        await File.WriteAllTextAsync(lockFile, "lock");
        var sessionId = Guid.NewGuid();
        session.BindSession(sessionId, first);

        var closed = await session.CloseAsync(sessionId);
        Assert.Equal("provider_unavailable", closed.Status);
        Assert.True(Directory.Exists(firstDirectory));
        Assert.True(File.Exists(lockFile));

        await session.ResetPersistentProfileAsync(first);

        Assert.False(Directory.Exists(firstDirectory));
        Assert.False(File.Exists(lockFile));
        Assert.True(Directory.Exists(secondDirectory));
        var rendered = string.Join('\n', messages);
        Assert.DoesNotContain(root, rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(first.ToString("D"), rendered, StringComparison.Ordinal);
        Assert.Contains(messages, message => message == "Browser profile reset.");
    }

    [Fact]
    public async Task Revoke_keeps_the_profile_directory_and_denies_later_browser_use()
    {
        var root = Path.Combine(Path.GetTempPath(), "agent-core-profiles-" + Guid.NewGuid().ToString("N"));
        var instanceId = Guid.NewGuid();
        var directory = Path.Combine(root, instanceId.ToString("D"));
        Directory.CreateDirectory(directory);
        var marker = Path.Combine(directory, "sign-in");
        await File.WriteAllTextAsync(marker, "saved-sign-in");
        try
        {
            var store = new InMemoryApplicationConnectionStore();
            var connectBrowser = new ConnectedBrowser();
            var connections = new ApplicationConnectionService(
                store,
                new SystemIdGenerator(TimeProvider.System),
                TimeProvider.System,
                browser: connectBrowser);
            await connections.ConnectAsync(instanceId, "Store", "http://127.0.0.1:5088/");
            Assert.Equal(new Uri("http://127.0.0.1:5088/admin"), connectBrowser.Navigated);

            var browser = new CountingBrowser();
            var executor = new SessionToolExecutor(
                browser: browser,
                configurationGate: ToolConfigurationGates.AllowAll,
                applicationConnections: store);
            var admission = new ToolExecutionAdmission(false, TriggerKind.UserTurn, AgentInstanceId: instanceId);
            var allowed = await executor.ExecuteAsync(
                BrowserDefinition(),
                Guid.NewGuid(),
                new ModelToolCall("navigate", ToolCatalog.BrowserNavigate, """{"url":"http://127.0.0.1:5088/admin"}"""),
                ToolLimits.MaxOutputBytes,
                admission: admission);
            Assert.DoesNotContain("cannot be used", allowed.Text, StringComparison.Ordinal);
            Assert.Equal(1, browser.NavigateCalls);
            Assert.Equal(instanceId, browser.BoundAgent);

            await connections.RevokeAsync(instanceId);
            Assert.Equal(ApplicationConnectionStatus.NotConnected, (await store.GetByAgentAsync(instanceId))!.Status);
            Assert.True(File.Exists(marker));

            foreach (var call in new[]
            {
                new ModelToolCall("navigate", ToolCatalog.BrowserNavigate, """{"url":"http://127.0.0.1:5088/admin"}"""),
                new ModelToolCall("observe", ToolCatalog.BrowserObserve, "{}"),
                new ModelToolCall("act", ToolCatalog.BrowserAct, """{"operation":"click","ref":"el_aaaaaaaaaaaaaaaaaaaaaa"}""")
            })
            {
                var denied = await executor.ExecuteAsync(
                    BrowserDefinition(),
                    Guid.NewGuid(),
                    call,
                    ToolLimits.MaxOutputBytes,
                    admission: admission);
                Assert.Contains("This application connection cannot be used.", denied.Text, StringComparison.Ordinal);
            }

            Assert.Equal(1, browser.NavigateCalls);
            Assert.Equal(0, browser.ObserveCalls);
            Assert.Equal(0, browser.ActCalls);
            Assert.Null(browser.BoundAgent);
            Assert.True(Directory.Exists(directory));

            var unbound = Guid.NewGuid();
            var missing = await executor.ExecuteAsync(
                BrowserDefinition(),
                Guid.NewGuid(),
                new ModelToolCall("navigate", ToolCatalog.BrowserNavigate, """{"url":"http://127.0.0.1:5088/admin"}"""),
                ToolLimits.MaxOutputBytes,
                admission: admission with { AgentInstanceId = unbound });
            Assert.DoesNotContain("cannot be used", missing.Text, StringComparison.Ordinal);
            Assert.Equal(2, browser.NavigateCalls);
            Assert.Equal(unbound, browser.BoundAgent);

            var revoked = (await store.GetByAgentAsync(instanceId))!;
            await store.SaveAsync(
                revoked with
                {
                    Status = ApplicationConnectionStatus.NeedsReauthentication,
                    StatusDetail = ApplicationConnectionDetails.LoginWall,
                    Revision = revoked.Revision + 1
                },
                revoked.Revision);
            var needsSignIn = await executor.ExecuteAsync(
                BrowserDefinition(),
                Guid.NewGuid(),
                new ModelToolCall("navigate", ToolCatalog.BrowserNavigate, """{"url":"http://127.0.0.1:5088/admin"}"""),
                ToolLimits.MaxOutputBytes,
                admission: admission);
            Assert.Contains("This application connection cannot be used.", needsSignIn.Text, StringComparison.Ordinal);
            Assert.Equal(2, browser.NavigateCalls);
            Assert.Null(browser.BoundAgent);
            Assert.True(File.Exists(marker));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static AgentDefinition BrowserDefinition() =>
        new(
            1,
            "secretary",
            1,
            new AgentIdentity("Morgan", "Secretary", "desc", "Tone"),
            [],
            "instructions",
            new BehaviorPolicy("answerNewTurn", true, true),
            new ConversationPolicy("balanced", false, "en", 2048),
            new InitiativePolicy(false, 60_000, 120_000, 1, ["longSilence"], 0),
            new VoiceConfiguration(false, "default", 1.0),
            new ProviderPreferences("primary-llm", "primary-stt", "primary-tts"),
            new Dictionary<string, string>(StringComparer.Ordinal),
            new RoleEnvironment(ToolAllowlist:
            [
                ToolCatalog.BrowserNavigate,
                ToolCatalog.BrowserObserve,
                ToolCatalog.BrowserAct
            ]));

    private static void SqliteConnectionClear(string path)
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private sealed class SqliteFactory(DbContextOptions<AgentCoreDbContext> options) : IDbContextFactory<AgentCoreDbContext>
    {
        public AgentCoreDbContext CreateDbContext() => new(options);

        public Task<AgentCoreDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed class ConnectedBrowser : IBrowserSession
    {
        public bool IsAvailable => true;

        public BrowserHostPolicy HostPolicy { get; } = new(true, true, BrowserInteractionMode.InteractiveDemo, []);

        public ValueTask<Uri?> GetCurrentUrlAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<Uri?>(new Uri("http://127.0.0.1:5088/admin"));

        public Uri? Navigated { get; private set; }

        public ValueTask<BrowserOperationResult> NavigateAsync(
            BrowserNavigateRequest request,
            CancellationToken cancellationToken = default)
        {
            Navigated = request.Url;
            return ValueTask.FromResult(new BrowserOperationResult(null, Observed(request.Url!)));
        }

        public ValueTask<BrowserOperationResult> ObserveAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new BrowserOperationResult(null, Observed(new Uri("http://127.0.0.1:5088/admin"))));

        public ValueTask<BrowserOperationResult> ActAsync(BrowserActRequest request, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new BrowserOperationResult("unsupported_operation", null));

        private static BrowserObservation Observed(Uri url) =>
            new(url.GetLeftPart(UriPartial.Authority) + "/admin", "admin", string.Empty, false, []);
    }

    private sealed class CountingBrowser : IBrowserSession, IBrowserProfileBinding
    {
        public int NavigateCalls { get; private set; }

        public int ObserveCalls { get; private set; }

        public int ActCalls { get; private set; }

        public Guid? BoundAgent { get; private set; }

        public bool IsAvailable => true;

        public BrowserHostPolicy HostPolicy { get; } = new(
            true,
            true,
            BrowserInteractionMode.InteractiveDemo,
            [],
            PolicyMode: BrowserPolicyMode.OpenWeb);

        public void BindSession(Guid sessionId, Guid? agentInstanceId) => BoundAgent = agentInstanceId;

        public ValueTask<Uri?> GetCurrentUrlAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<Uri?>(new Uri("http://127.0.0.1:5088/admin"));

        public ValueTask<BrowserOperationResult> NavigateAsync(
            BrowserNavigateRequest request,
            CancellationToken cancellationToken = default)
        {
            NavigateCalls++;
            return ValueTask.FromResult(new BrowserOperationResult(
                null,
                new BrowserObservation(request.Url!.AbsoluteUri, "admin", string.Empty, false, [])));
        }

        public ValueTask<BrowserOperationResult> ObserveAsync(Guid sessionId, CancellationToken cancellationToken = default)
        {
            ObserveCalls++;
            return ValueTask.FromResult(new BrowserOperationResult(
                null,
                new BrowserObservation("http://127.0.0.1:5088/admin", "admin", string.Empty, false, [])));
        }

        public ValueTask<BrowserOperationResult> ActAsync(BrowserActRequest request, CancellationToken cancellationToken = default)
        {
            ActCalls++;
            return ValueTask.FromResult(new BrowserOperationResult(
                null,
                new BrowserObservation("http://127.0.0.1:5088/admin", "admin", string.Empty, false, [])));
        }
    }

    private sealed class CollectingLoggerProvider(List<string> messages) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new CollectingLogger(messages);

        public void Dispose()
        {
        }

        private sealed class CollectingLogger(List<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                messages.Add(formatter(state, exception));
            }
        }
    }
}
