using System.Net.Http.Json;
using AgentCore.Api.Realtime;
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
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed class SqliteHostRecoveryTests
{
    [Fact(Timeout = 30_000)]
    public async Task Lost_text_ack_retry_after_host_reconstruction_keeps_one_user_entry()
    {
        var db = Path.Combine(Path.GetTempPath(), $"agent-core-host-{Guid.NewGuid():N}.db");
        var backup = Path.Combine(Path.GetTempPath(), $"agent-core-host-bak-{Guid.NewGuid():N}.db");
        var eventId = Guid.NewGuid().ToString();
        try
        {
            string sessionId;
            await using (var first = new DurableSqliteHostFactory(db))
            {
                var host = first.Services.GetRequiredService<SessionHost>();
                host.AfterUserTextPersisted = ct => Task.Delay(Timeout.InfiniteTimeSpan, ct);
                var client = first.CreateClient();
                TestOwnerCapability.Apply(client, first.Services);
                var created = await client.PostAsJsonAsync("/api/v1/sessions", new CreateSessionRequest("examiner", 1, "text"));
                created.EnsureSuccessStatusCode();
                var session = (await created.Content.ReadFromJsonAsync<SessionViewResponse>())!;
                sessionId = session.SessionId;
                await using var hub = await ConnectFactoryAsync(first);
                var ready = ReadyWaiter(hub);
                var attached = await hub.InvokeAsync<CommandAck>("Attach", Attach(sessionId));
                Assert.True(attached.Accepted, attached.Error?.Message);
                var attachment = await ready;
                var send = hub.InvokeAsync<CommandAck>(
                    "SendText",
                    Text(sessionId, 1, attachment, "Hello", eventId));
                var committed = false;
                for (var attempt = 0; attempt < 80; attempt++)
                {
                    var page = await client.GetFromJsonAsync<HistoryPageResponse>($"/api/v1/sessions/{sessionId}/messages?after=0");
                    if (page?.Items.Any(item => item.Role == "user" && item.SourceEventId == eventId) == true)
                    {
                        committed = true;
                        break;
                    }

                    await Task.Delay(50);
                }

                Assert.True(committed, "user turn was not persisted before host reconstruction");
                Assert.False(send.IsCompletedSuccessfully);
                var store = first.Services.GetRequiredService<IMemoryStore>();
                Assert.IsType<SqliteMemoryStore>(store);
                await ((SqliteMemoryStore)store).BackupToAsync(backup);
            }

            await using var second = new DurableSqliteHostFactory(backup);
            var retryClient = second.CreateClient();
            TestOwnerCapability.Apply(retryClient, second.Services);
            await using var retryHub = await ConnectFactoryAsync(second);
            var retryReady = ReadyWaiter(retryHub);
            await ReopenIfPausedAsync(retryClient, sessionId);
            var retriedAttach = await retryHub.InvokeAsync<CommandAck>("Attach", Attach(sessionId));
            Assert.True(retriedAttach.Accepted, retriedAttach.Error?.Message);
            var retryAttachment = await retryReady;
            var retry = await retryHub.InvokeAsync<CommandAck>(
                "SendText",
                Text(sessionId, 1, retryAttachment, "Hello", eventId));
            Assert.True(retry.Accepted, retry.Error?.Message);
            var history = await retryClient.GetFromJsonAsync<HistoryPageResponse>($"/api/v1/sessions/{sessionId}/messages?after=0");
            Assert.Equal(1, history!.Items.Count(item => item.Role == "user"));
            Assert.Equal(eventId, history.Items.Single(item => item.Role == "user").SourceEventId);
        }
        finally
        {
            using (var connection = new SqliteConnection($"Data Source={db}"))
            {
                SqliteConnection.ClearPool(connection);
            }
            using (var connection = new SqliteConnection($"Data Source={backup}"))
            {
                SqliteConnection.ClearPool(connection);
            }
            foreach (var path in new[] { db, db + "-wal", db + "-shm", backup, backup + "-wal", backup + "-shm" })
            {
                try
                {
                    File.Delete(path);
                }
                catch
                {
                    // ignored
                }
            }
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task SendText_killed_before_persist_is_not_silently_dropped()
    {
        var db = Path.Combine(Path.GetTempPath(), $"agent-core-text-{Guid.NewGuid():N}.db");
        var eventId = Guid.NewGuid().ToString();
        string sessionId;
        try
        {
            await using (var first = new GatedUserTurnSqliteFactory(db))
            {
                var client = first.CreateClient();
                TestOwnerCapability.Apply(client, first.Services);
                var created = await client.PostAsJsonAsync("/api/v1/sessions", new CreateSessionRequest("examiner", 1, "text"));
                created.EnsureSuccessStatusCode();
                var session = (await created.Content.ReadFromJsonAsync<SessionViewResponse>())!;
                sessionId = session.SessionId;
                await using var hub = await ConnectFactoryAsync(first);
                var ready = ReadyWaiter(hub);
                var attached = await hub.InvokeAsync<CommandAck>("Attach", Attach(sessionId));
                Assert.True(attached.Accepted, attached.Error?.Message);
                var attachment = await ready;
                var send = hub.InvokeAsync<CommandAck>("SendText", Text(sessionId, 1, attachment, "Hello", eventId));
                await first.Store.SaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.False(send.IsCompleted);
            }

            await using var second = new DurableSqliteHostFactory(db);
            var retryClient = second.CreateClient();
            TestOwnerCapability.Apply(retryClient, second.Services);
            await using var retryHub = await ConnectFactoryAsync(second);
            var retryReady = ReadyWaiter(retryHub);
            await ReopenIfPausedAsync(retryClient, sessionId);
            var retriedAttach = await retryHub.InvokeAsync<CommandAck>("Attach", Attach(sessionId));
            Assert.True(retriedAttach.Accepted, retriedAttach.Error?.Message);
            var retryAttachment = await retryReady;
            var missing = await retryClient.GetFromJsonAsync<HistoryPageResponse>($"/api/v1/sessions/{sessionId}/messages?after=0");
            Assert.DoesNotContain(missing!.Items, item => item.Role == "user");
            var retry = await retryHub.InvokeAsync<CommandAck>(
                "SendText",
                Text(sessionId, 1, retryAttachment, "Hello", eventId));
            Assert.True(retry.Accepted, retry.Error?.Message);
            var history = await retryClient.GetFromJsonAsync<HistoryPageResponse>($"/api/v1/sessions/{sessionId}/messages?after=0");
            Assert.Equal(1, history!.Items.Count(item => item.Role == "user"));
            Assert.Equal(eventId, history.Items.Single(item => item.Role == "user").SourceEventId);
        }
        finally
        {
            using (var connection = new SqliteConnection($"Data Source={db}"))
            {
                SqliteConnection.ClearPool(connection);
            }
            foreach (var path in new[] { db, db + "-wal", db + "-shm" })
            {
                try
                {
                    File.Delete(path);
                }
                catch
                {
                    // ignored
                }
            }
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task Cancelled_terminate_extracts_ending_runtime()
    {
        await using var factory = new GatedEndSqliteFactory();
        var host = factory.Services.GetRequiredService<SessionHost>();
        var client = factory.CreateClient();
        TestOwnerCapability.Apply(client, factory.Services);
        var created = await client.PostAsJsonAsync("/api/v1/sessions", new CreateSessionRequest("examiner", 1, "text"));
        created.EnsureSuccessStatusCode();
        var session = (await created.Content.ReadFromJsonAsync<SessionViewResponse>())!;
        var sessionId = Guid.Parse(session.SessionId);
        await using var hub = await ConnectFactoryAsync(factory);
        var ready = ReadyWaiter(hub);
        var attached = await hub.InvokeAsync<CommandAck>("Attach", Attach(session.SessionId));
        Assert.True(attached.Accepted, attached.Error?.Message);
        await ready;
        using var cts = new CancellationTokenSource();
        var terminate = host.TerminateAsync(sessionId, cts.Token);
        await factory.Store.EndedSaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotNull(host.LiveSnapshot(sessionId));
        cts.Cancel();
        await terminate.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null(host.LiveSnapshot(sessionId));
    }

    [Fact(Timeout = 30_000)]
    public async Task Failed_end_save_does_not_accept_or_persist_ended()
    {
        await using var factory = new FailingEndSqliteFactory();
        var client = factory.CreateClient();
        TestOwnerCapability.Apply(client, factory.Services);
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
                    TestOwnerCapability.Apply(options, factory.Services);
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
            }).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.False(ended.Accepted);
        Assert.Equal("SessionPersistenceUnavailable", ended.Error?.Code);
        var view = await client.GetFromJsonAsync<SessionViewResponse>($"/api/v1/sessions/{session.SessionId}");
        Assert.NotEqual("ended", view!.Status);
    }

    [Fact]
    public async Task Failed_user_persist_invalidates_attachment()
    {
        await using var factory = new FailingUserTurnSqliteFactory();
        var host = factory.Services.GetRequiredService<SessionHost>();
        var client = factory.CreateClient();
        TestOwnerCapability.Apply(client, factory.Services);
        var created = await client.PostAsJsonAsync("/api/v1/sessions", new CreateSessionRequest("examiner", 1, "text"));
        created.EnsureSuccessStatusCode();
        var session = (await created.Content.ReadFromJsonAsync<SessionViewResponse>())!;
        var sessionId = Guid.Parse(session.SessionId);
        await using var hub = new HubConnectionBuilder()
            .WithUrl(
                new Uri(factory.Server.BaseAddress!, "/hubs/session"),
                options =>
                {
                    options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                    options.Transports = HttpTransportType.LongPolling;
                    TestOwnerCapability.Apply(options, factory.Services);
                })
            .AddMessagePackProtocol()
            .Build();
        await hub.StartAsync();
        var persisted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        hub.On<ServerEvent>("SessionEvent", evt =>
        {
            if (evt.Type == "error"
                && evt.Payload.TryGetValue("code", out var code)
                && string.Equals(Convert.ToString(code), "SessionPersistenceUnavailable", StringComparison.Ordinal))
            {
                persisted.TrySetResult();
            }
        });
        var ready = ReadyWaiter(hub);
        var attached = await hub.InvokeAsync<CommandAck>("Attach", Attach(session.SessionId));
        Assert.True(attached.Accepted, attached.Error?.Message);
        var attachment = await ready;
        var lease = host.LiveAttachmentId(sessionId);
        _ = hub.InvokeAsync<CommandAck>("SendText", Text(session.SessionId, 1, attachment, "Hello", Guid.NewGuid().ToString()));
        await persisted.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(SessionStatus.Paused, host.LiveSnapshot(sessionId)?.Status);
        Assert.NotEqual(lease, host.LiveAttachmentId(sessionId));
        var followUp = await hub.InvokeAsync<CommandAck>(
            "SendText",
            Text(session.SessionId, 2, attachment, "Again", Guid.NewGuid().ToString()));
        Assert.False(followUp.Accepted);
        Assert.Equal("NotFound", followUp.Error?.Code);
        await WaitForCatalogStatusAsync(client, session.SessionId, "paused", TimeSpan.FromSeconds(10));
        await ReopenIfPausedAsync(client, session.SessionId);
        var reconnectReady = ReadyWaiter(hub);
        var reconnect = await hub.InvokeAsync<CommandAck>("Attach", Attach(session.SessionId));
        Assert.True(reconnect.Accepted, reconnect.Error?.Message);
        var nextAttachment = await reconnectReady;
        Assert.NotEqual(attachment, nextAttachment);
    }

    [Fact(Timeout = 30_000)]
    public async Task Later_pause_save_failure_keeps_ended_and_rejects_attach()
    {
        var db = Path.Combine(Path.GetTempPath(), $"agent-core-pause-end-{Guid.NewGuid():N}.db");
        string sessionId;
        try
        {
            await using (var factory = new PauseAfterEndSqliteFactory(db))
            {
                var client = factory.CreateClient();
                TestOwnerCapability.Apply(client, factory.Services);
                var created = await client.PostAsJsonAsync("/api/v1/sessions", new CreateSessionRequest("examiner", 1, "text"));
                created.EnsureSuccessStatusCode();
                var session = (await created.Content.ReadFromJsonAsync<SessionViewResponse>())!;
                sessionId = session.SessionId;
                await using var hub = new HubConnectionBuilder()
                    .WithUrl(
                        new Uri(factory.Server.BaseAddress!, "/hubs/session"),
                        options =>
                        {
                            options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                            options.Transports = HttpTransportType.LongPolling;
                            TestOwnerCapability.Apply(options, factory.Services);
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
                Assert.True(ended.Accepted, ended.Error?.Message);
                Assert.Equal(0, factory.Store.PauseAfterEnd);
                var view = await client.GetFromJsonAsync<SessionViewResponse>($"/api/v1/sessions/{session.SessionId}");
                Assert.Equal("ended", view!.Status);
                await hub.StopAsync();
            }

            await using var restarted = await SqliteKestrelProcess.StartAsync(db);
            await using var retry = await ConnectAsync(restarted.BaseAddress);
            var rejected = await retry.InvokeAsync<CommandAck>("Attach", Attach(sessionId));
            Assert.False(rejected.Accepted);
            Assert.Equal("NotFound", rejected.Error?.Code);
        }
        finally
        {
            try
            {
                File.Delete(db);
            }
            catch
            {
                // ignored
            }
        }
    }

    private static async Task<HubConnection> ConnectFactoryAsync(WebApplicationFactory<Program> factory)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(
                new Uri(factory.Server.BaseAddress!, "/hubs/session"),
                options =>
                {
                    options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                    options.Transports = HttpTransportType.LongPolling;
                    TestOwnerCapability.Apply(options, factory.Services);
                })
            .AddMessagePackProtocol()
            .Build();
        await connection.StartAsync();
        return connection;
    }

    private static async Task<HubConnection> ConnectAsync(string baseAddress)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var connection = new HubConnectionBuilder()
                .WithUrl($"{baseAddress}/hubs/session", options =>
                {
                    options.Transports = HttpTransportType.LongPolling;
                    options.Headers[OwnerCapabilityHeaders.Name] = IssueOwnerHttp(baseAddress);
                })
                .AddMessagePackProtocol()
                .Build();
            try
            {
                await connection.StartAsync();
                return connection;
            }
            catch (Exception ex) when (attempt < 4)
            {
                last = ex;
                await connection.DisposeAsync();
                await Task.Delay(TimeSpan.FromMilliseconds(250 * (attempt + 1)));
            }
        }

        throw new InvalidOperationException("Failed to connect to restarted host.", last);
    }

    private static string IssueOwnerHttp(string baseAddress)
    {
        using var http = new HttpClient { BaseAddress = new Uri(baseAddress) };
        var issued = http.PostAsync("/api/v1/local/owner-capability", null).GetAwaiter().GetResult();
        issued.EnsureSuccessStatusCode();
        return issued.Content.ReadFromJsonAsync<OwnerCapabilityResponse>().GetAwaiter().GetResult()!.Token;
    }

    private static async Task<string> CreateSessionAsync(string baseAddress)
    {
        using var http = new HttpClient { BaseAddress = new Uri(baseAddress) };
        http.DefaultRequestHeaders.TryAddWithoutValidation(OwnerCapabilityHeaders.Name, IssueOwnerHttp(baseAddress));
        var created = await http.PostAsJsonAsync("/api/v1/sessions", new CreateSessionRequest("examiner", 1, "text"));
        created.EnsureSuccessStatusCode();
        var session = await created.Content.ReadFromJsonAsync<SessionViewResponse>();
        return session!.SessionId;
    }

    private static async Task WaitForCatalogStatusAsync(
        HttpClient client,
        string sessionId,
        string status,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var view = await client.GetFromJsonAsync<SessionViewResponse>($"/api/v2/sessions/{sessionId}");
            if (string.Equals(view?.Status, status, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            await Task.Delay(50);
        }

        var final = await client.GetFromJsonAsync<SessionViewResponse>($"/api/v2/sessions/{sessionId}");
        Assert.Equal(status, final?.Status);
    }

    private static async Task ReopenIfPausedAsync(HttpClient client, string sessionId)
    {
        var view = await client.GetFromJsonAsync<SessionViewResponse>($"/api/v2/sessions/{sessionId}");
        if (view is null
            || !string.Equals(view.Status, "paused", StringComparison.OrdinalIgnoreCase)
            || !SessionPauseSemantics.RequiresExplicitResume(view.PauseReason))
        {
            return;
        }

        var reopen = await client.PostAsync($"/api/v2/sessions/{sessionId}/reopen", null);
        if (reopen.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            return;
        }

        reopen.EnsureSuccessStatusCode();
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

internal sealed class DurableSqliteHostFactory(string dbPath) : WebApplicationFactory<Program>
{
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
                ["Persistence:ConnectionString"] = $"Data Source={dbPath}"
            });
        });
        builder.ConfigureTestServices(services =>
        {
            var persistence = new PersistenceOptions
            {
                Provider = "Sqlite",
                ConnectionString = $"Data Source={dbPath}"
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
                var sqlite = new SqliteMemoryStore(
                    provider.GetRequiredService<IDbContextFactory<AgentCoreDbContext>>(),
                    provider.GetRequiredService<TimeProvider>());
                sqlite.EnsureCreatedAsync().AsTask().GetAwaiter().GetResult();
                return sqlite;
            });
        });
        TestHttpDefaults.UseLoopbackCaller(builder);
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

internal sealed class GatedUserTurnSqliteFactory(string dbPath) : WebApplicationFactory<Program>
{
    public GatedUserTurnStore Store { get; private set; } = null!;

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
                ["Persistence:ConnectionString"] = $"Data Source={dbPath}"
            });
        });
        builder.ConfigureTestServices(services =>
        {
            var persistence = new PersistenceOptions
            {
                Provider = "Sqlite",
                ConnectionString = $"Data Source={dbPath}"
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
                var sqlite = new SqliteMemoryStore(
                    provider.GetRequiredService<IDbContextFactory<AgentCoreDbContext>>(),
                    provider.GetRequiredService<TimeProvider>());
                sqlite.EnsureCreatedAsync().AsTask().GetAwaiter().GetResult();
                Store = new GatedUserTurnStore(sqlite);
                return Store;
            });
        });
        TestHttpDefaults.UseLoopbackCaller(builder);
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

internal sealed class GatedUserTurnStore(IMemoryStore inner) : IMemoryStore
{
    public TaskCompletionSource SaveStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ValueTask<SessionSnapshot?> LoadAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
        inner.LoadAsync(sessionId, cancellationToken);

    public async ValueTask SaveAsync(
        SessionSnapshot snapshot,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        if (snapshot.Entries.Any(entry => entry.Role == ConversationRole.User))
        {
            SaveStarted.TrySetResult();
            await Gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        await inner.SaveAsync(snapshot, expectedRevision, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<IReadOnlyList<ConversationEntry>> ReadHistoryAsync(
        Guid sessionId,
        long afterEntrySequence,
        int limit,
        CancellationToken cancellationToken = default) =>
        inner.ReadHistoryAsync(sessionId, afterEntrySequence, limit, cancellationToken);

    public ValueTask<UserProfile?> LoadProfileAsync(Guid profileId, CancellationToken cancellationToken = default) =>
        inner.LoadProfileAsync(profileId, cancellationToken);

    public ValueTask SaveProfileAsync(
        UserProfile profile,
        long expectedRevision,
        CancellationToken cancellationToken = default) =>
        inner.SaveProfileAsync(profile, expectedRevision, cancellationToken);

    public ValueTask RecoverCrashedSessionsAsync(CancellationToken cancellationToken = default) =>
        inner.RecoverCrashedSessionsAsync(cancellationToken);
}

internal sealed class GatedEndSqliteFactory : WebApplicationFactory<Program>
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"agent-core-gated-end-{Guid.NewGuid():N}.db");

    public GatedEndStore Store { get; private set; } = null!;

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
                Store = new GatedEndStore(inner);
                return Store;
            });
        });
        TestHttpDefaults.UseLoopbackCaller(builder);
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

internal sealed class GatedEndStore(IMemoryStore inner) : IMemoryStore
{
    public TaskCompletionSource EndedSaveStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ValueTask<SessionSnapshot?> LoadAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
        inner.LoadAsync(sessionId, cancellationToken);

    public async ValueTask SaveAsync(
        SessionSnapshot snapshot,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        if (snapshot.Status == SessionStatus.Ended)
        {
            EndedSaveStarted.TrySetResult();
            await Gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        await inner.SaveAsync(snapshot, expectedRevision, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<IReadOnlyList<ConversationEntry>> ReadHistoryAsync(
        Guid sessionId,
        long afterEntrySequence,
        int limit,
        CancellationToken cancellationToken = default) =>
        inner.ReadHistoryAsync(sessionId, afterEntrySequence, limit, cancellationToken);

    public ValueTask<UserProfile?> LoadProfileAsync(Guid profileId, CancellationToken cancellationToken = default) =>
        inner.LoadProfileAsync(profileId, cancellationToken);

    public ValueTask SaveProfileAsync(
        UserProfile profile,
        long expectedRevision,
        CancellationToken cancellationToken = default) =>
        inner.SaveProfileAsync(profile, expectedRevision, cancellationToken);

    public ValueTask RecoverCrashedSessionsAsync(CancellationToken cancellationToken = default) =>
        inner.RecoverCrashedSessionsAsync(cancellationToken);
}

internal sealed class PauseAfterEndSqliteFactory : WebApplicationFactory<Program>
{
    private readonly string _db;
    private readonly bool _deleteOnDispose;

    public PauseAfterEndSqliteFactory(string? dbPath = null)
    {
        _db = dbPath ?? Path.Combine(Path.GetTempPath(), $"agent-core-pause-end-{Guid.NewGuid():N}.db");
        _deleteOnDispose = dbPath is null;
    }

    public string DatabasePath => _db;

    public PauseAfterEndStore Store { get; private set; } = null!;

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
                Store = new PauseAfterEndStore(inner);
                return Store;
            });
        });
        TestHttpDefaults.UseLoopbackCaller(builder);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!_deleteOnDispose)
        {
            return;
        }

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

internal sealed class PauseAfterEndStore(IMemoryStore inner) : IMemoryStore
{
    private bool _ended;

    public int PauseAfterEnd { get; private set; }

    public ValueTask<SessionSnapshot?> LoadAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
        inner.LoadAsync(sessionId, cancellationToken);

    public ValueTask SaveAsync(SessionSnapshot snapshot, long expectedRevision, CancellationToken cancellationToken = default)
    {
        if (snapshot.Status == SessionStatus.Ended)
        {
            if (_ended)
            {
                throw AgentCoreErrors.Persistence("forced later end save failure");
            }

            _ended = true;
        }

        if (snapshot.Status == SessionStatus.Paused && _ended)
        {
            PauseAfterEnd++;
            throw AgentCoreErrors.Persistence("forced pause after end");
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
        TestHttpDefaults.UseLoopbackCaller(builder);
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

internal sealed class FailingUserTurnSqliteFactory : WebApplicationFactory<Program>
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"agent-core-fail-user-{Guid.NewGuid():N}.db");

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
                return new FailingUserTurnStore(inner);
            });
        });
        TestHttpDefaults.UseLoopbackCaller(builder);
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

internal sealed class FailingUserTurnStore(IMemoryStore inner) : IMemoryStore
{
    public ValueTask<SessionSnapshot?> LoadAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
        inner.LoadAsync(sessionId, cancellationToken);

    public ValueTask SaveAsync(SessionSnapshot snapshot, long expectedRevision, CancellationToken cancellationToken = default)
    {
        if (snapshot.Entries.Count > 0 && snapshot.Status is not SessionStatus.Ended and not SessionStatus.Ending)
        {
            throw AgentCoreErrors.Persistence("forced user persist failure");
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
    private readonly string _dbPath;

    private SqliteKestrelProcess(string dbPath)
    {
        _dbPath = dbPath;
    }

    public string BaseAddress { get; private set; } = "";

    public string DbPath => _dbPath;

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
