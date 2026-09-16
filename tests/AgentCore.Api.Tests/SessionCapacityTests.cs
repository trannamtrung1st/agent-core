using System.Net;
using System.Net.Http.Json;
using AgentCore.Api.Realtime;
using AgentCore.Contracts.Http;
using AgentCore.Contracts.Realtime;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed class SessionCapacityTests : IClassFixture<CapacityOneApiFactory>
{
    private readonly CapacityOneApiFactory _factory;

    public SessionCapacityTests(CapacityOneApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Http_delete_releases_live_capacity_and_rejects_reattach()
    {
        var host = _factory.Services.GetRequiredService<SessionHost>();
        var client = TestOwnerCapability.CreateOwnerClient(_factory);
        var firstCreated = await client.PostAsJsonAsync("/api/v1/sessions", new CreateSessionRequest("examiner", 1, "text"));
        firstCreated.EnsureSuccessStatusCode();
        var first = (await firstCreated.Content.ReadFromJsonAsync<SessionViewResponse>())!;
        var secondCreated = await client.PostAsJsonAsync("/api/v1/sessions", new CreateSessionRequest("examiner", 1, "text"));
        secondCreated.EnsureSuccessStatusCode();
        var second = (await secondCreated.Content.ReadFromJsonAsync<SessionViewResponse>())!;

        await using var hubA = await ConnectAsync();
        var readyA = ReadyWaiter(hubA);
        var attachedA = await hubA.InvokeAsync<CommandAck>("Attach", Attach(first.SessionId));
        Assert.True(attachedA.Accepted, attachedA.Error?.Message);
        await readyA;

        await using var hubB = await ConnectAsync();
        var rejected = await hubB.InvokeAsync<CommandAck>("Attach", Attach(second.SessionId));
        Assert.False(rejected.Accepted);
        Assert.Equal("SessionCapacityExceeded", rejected.Error?.Code);

        var deleted = await client.DeleteAsync($"/api/v1/sessions/{first.SessionId}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Null(host.LiveSnapshot(Guid.Parse(first.SessionId)));

        var readyB = ReadyWaiter(hubB);
        var attachedB = await hubB.InvokeAsync<CommandAck>("Attach", Attach(second.SessionId));
        Assert.True(attachedB.Accepted, attachedB.Error?.Message);
        await readyB;

        var reattach = await hubA.InvokeAsync<CommandAck>("Attach", Attach(first.SessionId));
        Assert.False(reattach.Accepted);
        Assert.Equal("NotFound", reattach.Error?.Code);

        var deletedAgain = await client.DeleteAsync($"/api/v1/sessions/{first.SessionId}");
        Assert.Equal(HttpStatusCode.NoContent, deletedAgain.StatusCode);
    }

    private async Task<HubConnection> ConnectAsync()
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(
                new Uri(_factory.Server.BaseAddress!, "/hubs/session"),
                options =>
                {
                    options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                    options.Transports = HttpTransportType.LongPolling;
                    TestOwnerCapability.Apply(options, _factory.Services);
                })
            .AddMessagePackProtocol()
            .Build();
        await connection.StartAsync();
        return connection;
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
}

public sealed class CapacityOneApiFactory : WebApplicationFactory<Program>
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
                ["AgentCore:MaxActiveSessions"] = "1"
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
