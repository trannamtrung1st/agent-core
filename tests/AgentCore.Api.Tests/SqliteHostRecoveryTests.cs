using System.Net.Http.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Contracts.Http;
using AgentCore.Contracts.Realtime;
using AgentCore.Domain.Conversation;
using AgentCore.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed class SqliteHostRecoveryTests
{
    [Fact]
    public async Task Lost_text_ack_retry_after_host_reconstruction_keeps_one_user_entry()
    {
        var db = Path.Combine(Path.GetTempPath(), $"agent-core-host-{Guid.NewGuid():N}.db");
        await using var first = await SqliteKestrelProcess.StartAsync(db);
        var eventId = Guid.NewGuid().ToString();
        string sessionId;
        await using (var hub = await ConnectAsync(first.BaseAddress))
        {
            sessionId = await CreateSessionAsync(first.BaseAddress);
            var ready = ReadyWaiter(hub);
            var completed = EventWaiter(hub, "agent.response.completed");
            var attached = await hub.InvokeAsync<CommandAck>("Attach", Attach(sessionId));
            Assert.True(attached.Accepted, attached.Error?.Message);
            var attachment = await ready;
            var sent = await hub.InvokeAsync<CommandAck>(
                "SendText",
                Text(sessionId, 1, attachment, "Hello", eventId));
            Assert.True(sent.Accepted, sent.Error?.Message);
            await completed;
        }

        await first.RestartAsync();
        await using (var hub = await ConnectAsync(first.BaseAddress))
        {
            var ready = ReadyWaiter(hub);
            var attached = await hub.InvokeAsync<CommandAck>("Attach", Attach(sessionId));
            Assert.True(attached.Accepted, attached.Error?.Message);
            var attachment = await ready;
            var retry = await hub.InvokeAsync<CommandAck>(
                "SendText",
                Text(sessionId, 1, attachment, "Hello", eventId));
            Assert.True(retry.Accepted, retry.Error?.Message);
        }

        using var http = new HttpClient { BaseAddress = new Uri(first.BaseAddress) };
        var history = await http.GetFromJsonAsync<HistoryPageResponse>($"/api/v1/sessions/{sessionId}/messages?after=0");
        Assert.Equal(1, history!.Items.Count(item => item.Role == "user"));
        Assert.Equal(eventId, history.Items.Single(item => item.Role == "user").SourceEventId);
    }

    [Fact]
    public async Task Failed_end_save_does_not_accept_or_persist_ended()
    {
        await using var factory = new FailingEndSqliteFactory();
        var client = factory.CreateClient();
        var created = await client.PostAsJsonAsync("/api/v1/sessions", new CreateSessionRequest("examiner", 1, "text"));
        created.EnsureSuccessStatusCode();
        var session = (await created.Content.ReadFromJsonAsync<SessionViewResponse>())!;
        await using var hub = new HubConnectionBuilder()
            .WithUrl(
                new Uri(factory.Server.BaseAddress!, "/hubs/session"),
                options =>
                {
                    options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                    options.Transports = HttpTransportType.LongPolling;
                })
            .AddMessagePackProtocol()
            .Build();
        await hub.StartAsync();
        var ready = ReadyWaiter(hub);
        var attached = await hub.InvokeAsync<CommandAck>("Attach", Attach(session.SessionId));
        Assert.True(attached.Accepted, attached.Error?.Message);
        var attachment = await ready;
        var ended = await hub.InvokeAsync<CommandAck>(
            "EndSession",
            new ClientCommand<EndPayload>
            {
                ProtocolVersion = 1,
                SessionId = session.SessionId,
                EventId = Guid.NewGuid().ToString(),
                Sequence = 1,
                Timestamp = DateTimeOffset.UtcNow.ToString("o"),
                AttachmentId = attachment,
                Type = "session.end",
                Payload = new EndPayload { Reason = "userEnded" }
            });
        Assert.False(ended.Accepted);
        Assert.Equal("SessionPersistenceUnavailable", ended.Error?.Code);
        var view = await client.GetFromJsonAsync<SessionViewResponse>($"/api/v1/sessions/{session.SessionId}");
        Assert.NotEqual("ended", view!.Status);
    }

    private static async Task<HubConnection> ConnectAsync(string baseAddress)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl($"{baseAddress}/hubs/session", options =>
            {
                options.Transports = HttpTransportType.WebSockets;
                options.SkipNegotiation = true;
            })
            .AddMessagePackProtocol()
            .Build();
        await connection.StartAsync();
        return connection;
    }

    private static async Task<string> CreateSessionAsync(string baseAddress)
    {
        using var http = new HttpClient { BaseAddress = new Uri(baseAddress) };
        var created = await http.PostAsJsonAsync("/api/v1/sessions", new CreateSessionRequest("examiner", 1, "text"));
        created.EnsureSuccessStatusCode();
        var session = await created.Content.ReadFromJsonAsync<SessionViewResponse>();
        return session!.SessionId;
    }

    private static ClientCommand<AttachPayload> Attach(string sessionId) =>
        new()
        {
            ProtocolVersion = 1,
            SessionId = sessionId,
            EventId = Guid.NewGuid().ToString(),
            Sequence = 0,
            Timestamp = DateTimeOffset.UtcNow.ToString("o"),
            Type = "session.attach",
            Payload = new AttachPayload()
        };

    private static ClientCommand<UserTextPayload> Text(
        string sessionId,
        long sequence,
        string attachmentId,
        string text,
        string eventId) =>
        new()
        {
            ProtocolVersion = 1,
            SessionId = sessionId,
            EventId = eventId,
            Sequence = sequence,
            Timestamp = DateTimeOffset.UtcNow.ToString("o"),
            AttachmentId = attachmentId,
            Type = "user.text",
            Payload = new UserTextPayload { Text = text }
        };

    private static Task<string> ReadyWaiter(HubConnection hub)
    {
        var ready = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        hub.On<ServerEvent>("SessionEvent", evt =>
        {
            if (evt.Type == "session.ready" && evt.AttachmentId is not null)
            {
                ready.TrySetResult(evt.AttachmentId);
            }
        });
        return ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }

    private static Task EventWaiter(HubConnection hub, string type)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        hub.On<ServerEvent>("SessionEvent", evt =>
        {
            if (evt.Type == type)
            {
                done.TrySetResult();
            }
        });
        return done.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }
}

internal sealed class FailingEndSqliteFactory : WebApplicationFactory<Program>
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"agent-core-fail-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        var repo = FindRepoRoot();
        builder.UseContentRoot(Path.Combine(repo, "src", "AgentCore.Api"));
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AgentCore:Profile"] = "Synthetic",
                ["AgentCore:AgentDirectory"] = Path.Combine(repo, "agents"),
                ["Persistence:Provider"] = "Sqlite",
                ["Persistence:ConnectionString"] = $"Data Source={_db}"
            });
        });
        builder.ConfigureTestServices(services =>
        {
            var persistence = new PersistenceOptions
            {
                Provider = "Sqlite",
                ConnectionString = $"Data Source={_db}"
            };
            foreach (var option in services.Where(item => item.ServiceType == typeof(PersistenceOptions)).ToArray())
            {
                services.Remove(option);
            }

            services.AddSingleton(persistence);
            if (services.All(item => item.ServiceType != typeof(IDbContextFactory<AgentCoreDbContext>)))
            {
                services.AddDbContextFactory<AgentCoreDbContext>(options => options.UseSqlite(persistence.ConnectionString));
            }

            foreach (var store in services.Where(item => item.ServiceType == typeof(IMemoryStore)).ToArray())
            {
                services.Remove(store);
            }

            services.AddSingleton<IMemoryStore>(provider =>
            {
                var inner = new SqliteMemoryStore(
                    provider.GetRequiredService<IDbContextFactory<AgentCoreDbContext>>(),
                    provider.GetRequiredService<TimeProvider>());
                inner.EnsureCreatedAsync().AsTask().GetAwaiter().GetResult();
                return new FailingEndStore(inner);
            });
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try
        {
            File.Delete(_db);
        }
        catch
        {
            // ignored
        }
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

internal sealed class FailingEndStore(IMemoryStore inner) : IMemoryStore
{
    public ValueTask<SessionSnapshot?> LoadAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
        inner.LoadAsync(sessionId, cancellationToken);

    public ValueTask SaveAsync(SessionSnapshot snapshot, long expectedRevision, CancellationToken cancellationToken = default)
    {
        if (snapshot.Status == SessionStatus.Ended)
        {
            throw AgentCoreErrors.Persistence("forced end save failure");
        }

        return inner.SaveAsync(snapshot, expectedRevision, cancellationToken);
    }

    public ValueTask<IReadOnlyList<ConversationEntry>> ReadHistoryAsync(
        Guid sessionId,
        long afterEntrySequence,
        int limit,
        CancellationToken cancellationToken = default) =>
        inner.ReadHistoryAsync(sessionId, afterEntrySequence, limit, cancellationToken);

    public ValueTask<UserProfile?> LoadProfileAsync(Guid profileId, CancellationToken cancellationToken = default) =>
        inner.LoadProfileAsync(profileId, cancellationToken);

    public ValueTask SaveProfileAsync(UserProfile profile, long expectedRevision, CancellationToken cancellationToken = default) =>
        inner.SaveProfileAsync(profile, expectedRevision, cancellationToken);

    public ValueTask RecoverCrashedSessionsAsync(CancellationToken cancellationToken = default) =>
        inner.RecoverCrashedSessionsAsync(cancellationToken);
}

internal sealed class SqliteKestrelProcess : IAsyncDisposable
{
    private System.Diagnostics.Process? _process;

    private SqliteKestrelProcess(string dbPath)
    {
        DbPath = dbPath;
    }

    public string BaseAddress { get; private set; } = "";

    public string DbPath { get; }

    public static async Task<SqliteKestrelProcess> StartAsync(string dbPath)
    {
        var host = new SqliteKestrelProcess(dbPath);
        await host.StartProcessAsync();
        return host;
    }

    public async Task RestartAsync()
    {
        await StopProcessAsync();
        await StartProcessAsync();
    }

    public async ValueTask DisposeAsync() => await StopProcessAsync();

    private async Task StartProcessAsync()
    {
        var root = FindRepoRoot();
        var port = GetFreePort();
        BaseAddress = $"http://127.0.0.1:{port}";
        Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);
        var start = new System.Diagnostics.ProcessStartInfo(
            "dotnet",
            $"run --project \"{Path.Combine(root, "src", "AgentCore.Api", "AgentCore.Api.csproj")}\" --no-build --no-launch-profile --urls {BaseAddress}")
        {
            WorkingDirectory = Path.Combine(root, "src", "AgentCore.Api"),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        start.Environment["AgentCore__Profile"] = "Synthetic";
        start.Environment["AgentCore__AgentDirectory"] = Path.Combine(root, "agents");
        start.Environment["Persistence__Provider"] = "Sqlite";
        start.Environment["Persistence__ConnectionString"] = $"Data Source={DbPath}";
        start.Environment["Hosting__BindUrl"] = BaseAddress;
        var logs = new System.Text.StringBuilder();
        _process = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("Failed to start API.");
        void Append(string? line)
        {
            if (!string.IsNullOrEmpty(line))
            {
                lock (logs)
                {
                    logs.AppendLine(line);
                }
            }
        }

        _process.OutputDataReceived += (_, args) => Append(args.Data);
        _process.ErrorDataReceived += (_, args) => Append(args.Data);
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        for (var attempt = 0; attempt < 80; attempt++)
        {
            if (_process.HasExited)
            {
                throw new InvalidOperationException($"API exited during startup with code {_process.ExitCode}. Output:\n{logs}");
            }

            try
            {
                var health = await client.GetAsync($"{BaseAddress}/health");
                if (health.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (Exception) when (attempt < 79)
            {
            }

            await Task.Delay(250);
        }

        throw new InvalidOperationException($"Health endpoint never became ready. Output:\n{logs}");
    }

    private async Task StopProcessAsync()
    {
        if (_process is null)
        {
            return;
        }

        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync();
        }

        _process.Dispose();
        _process = null;
    }

    private static int GetFreePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
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
