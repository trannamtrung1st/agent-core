using System.Diagnostics;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Sandbox;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgentCore.Infrastructure.Tests;

public sealed class OpenSandboxCleanupTests
{
    [Theory]
    [InlineData(500, 204, 2, true)]
    [InlineData(503, 404, 2, true)]
    [InlineData(403, 204, 1, false)]
    [InlineData(500, 500, 3, false)]
    public async Task Exact_owned_delete_recovers_transient_failure_but_preserves_denied_or_pending_cleanup(
        int first, int next, int expectedRequests, bool terminated)
    {
        var requests = 0; var authorized = true; var exactIdentity = true;
        await using var host = await Host.Create(async context =>
        {
            authorized &= context.Request.Headers["OPEN-SANDBOX-API-KEY"] == "private-fixture-key";
            exactIdentity &= context.Request.RouteValues["id"]?.ToString() == "owned-provider";
            var code = Interlocked.Increment(ref requests) == 1 ? first : next;
            context.Response.StatusCode = code;
            if (code != 204) await context.Response.WriteAsJsonAsync(new { code = "fixture", message = "private-upstream-body" });
        });
        var (service, store, lease, logs) = await host.Service();
        if (terminated) await service.StopSessionAsync(lease.SessionId, expectedGeneration: lease.Generation);
        else await Assert.ThrowsAsync<AgentCoreException>(() => service.StopSessionAsync(lease.SessionId).AsTask());
        Assert.Equal(expectedRequests, requests); Assert.True(authorized); Assert.True(exactIdentity);
        Assert.Equal(terminated ? SandboxLeaseState.Terminated : SandboxLeaseState.Terminating,
            (await store.GetLeaseAsync(lease.SessionId))!.State);
        if (!terminated)
        {
            var warning = Assert.Single(logs.Warnings);
            Assert.Contains("Failure=http", warning); Assert.Contains("StatusCode=" + (first == 403 ? 403 : 500), warning);
            Assert.DoesNotContain("private-upstream-body", warning); Assert.DoesNotContain("private-fixture-key", warning);
            Assert.DoesNotContain("owned-provider", warning);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Lost_delete_acknowledgement_can_confirm_absence_and_unresponsive_cleanup_stays_bounded(bool alwaysHang)
    {
        var requests = 0;
        await using var host = await Host.Create(async context =>
        {
            if (Interlocked.Increment(ref requests) == 1 || alwaysHang)
            {
                try { await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted); }
                catch (OperationCanceledException) { }
            }
            else context.Response.StatusCode = 404;
        });
        var (service, store, lease, logs) = await host.Service();
        var elapsed = Stopwatch.StartNew();
        if (alwaysHang) await Assert.ThrowsAsync<AgentCoreException>(() => service.StopSessionAsync(lease.SessionId).AsTask());
        else await service.StopSessionAsync(lease.SessionId);
        Assert.InRange(elapsed.Elapsed.TotalSeconds, 4, 21);
        Assert.Equal(alwaysHang ? 3 : 2, requests);
        Assert.Equal(alwaysHang ? SandboxLeaseState.Terminating : SandboxLeaseState.Terminated,
            (await store.GetLeaseAsync(lease.SessionId))!.State);
        if (alwaysHang) Assert.Contains("Failure=timeout", Assert.Single(logs.Warnings));
    }

    private sealed class Host(WebApplication app, string endpoint) : IAsyncDisposable
    {
        public static async Task<Host> Create(RequestDelegate delete)
        {
            var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var app = builder.Build(); app.MapDelete("/v1/sandboxes/{id}", delete);
            await app.StartAsync();
            return new(app, app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
        }
        public async Task<(OpenSandboxService Service, InMemorySandboxStore Store, SandboxLease Lease, Logs Logs)> Service()
        {
            var store = new InMemorySandboxStore(); var now = DateTimeOffset.UtcNow;
            var profile = new SandboxProfile("fixture", 1, "Fixture", "", true, "sha256:" + new string('a', 64));
            var lease = new SandboxLease(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, profile,
                SandboxLeaseState.Ready, "owned-provider", now, now.AddMinutes(5));
            await store.SaveLeaseAsync(lease, 0); var logs = new Logs();
            // StopSession touches only the durable sandbox store and SDK lifecycle.
            var service = new OpenSandboxService(new() { Endpoint = endpoint, ApiKey = "private-fixture-key" },
                store, null!, null!, null!, TimeProvider.System, logger: logs);
            return (service, store, lease, logs);
        }
        public async ValueTask DisposeAsync() { await app.StopAsync(); await app.DisposeAsync(); }
    }
    private sealed class Logs : ILogger<OpenSandboxService>
    {
        public List<string> Warnings { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        { if (level == LogLevel.Warning) Warnings.Add(formatter(state, exception)); }
    }
}
