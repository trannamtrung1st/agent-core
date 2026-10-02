using AgentCore.Application.Connections;
using AgentCore.Application.Ports;
using AgentCore.Domain.Connections;
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

        public ValueTask<BrowserOperationResult> NavigateAsync(
            BrowserNavigateRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new BrowserOperationResult(null, Observed(request.Url)));

        public ValueTask<BrowserOperationResult> ObserveAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new BrowserOperationResult(null, Observed(new Uri("http://127.0.0.1:5088/admin"))));

        public ValueTask<BrowserOperationResult> ActAsync(BrowserActRequest request, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new BrowserOperationResult("unsupported_operation", null));

        private static BrowserObservation Observed(Uri url) =>
            new(url.GetLeftPart(UriPartial.Authority) + "/admin", "admin", string.Empty, false, []);
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
