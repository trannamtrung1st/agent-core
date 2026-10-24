using System.Security.Claims;
using AgentCore.Api.Realtime;
using AgentCore.Application.Testing;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed class SessionHubDiagnosticFilterTests
{
    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task Lifecycle_faults_keep_safe_cause_and_reference_without_adopting_unverified_caller_metadata(bool connecting)
    {
        var id = Guid.NewGuid(); var spare = Guid.NewGuid();
        var diagnostics = new QueueDiagnosticIdSource([id, spare]);
        var logs = new DiagnosticLogCapture<SessionHubDiagnosticFilter>();
        var filter = new SessionHubDiagnosticFilter(logs, diagnostics);
        using var provider = new ServiceCollection().BuildServiceProvider();
        var caller = new Caller(CancellationToken.None);
        caller.Items["TenantId"] = Guid.NewGuid(); caller.Items["ActorPrincipalId"] = Guid.NewGuid();
        using var hub = new TestHub { Context = caller };
        var lifetime = new HubLifetimeContext(caller, provider, hub);
        var original = new IOException("private-lifecycle-sentinel", new InvalidOperationException("private-lifecycle-sentinel"));
        var safe = await Assert.ThrowsAsync<HubException>(() => connecting
            ? filter.OnConnectedAsync(lifetime, _ => throw original)
            : filter.OnDisconnectedAsync(lifetime, original, (_, _) => throw original));
        Assert.Contains(id.ToString("D"), safe.Message);
        Assert.Null(safe.InnerException);
        Assert.DoesNotContain("private-lifecycle-sentinel", safe.ToString());
        var logged = Assert.Single(logs.Entries);
        Assert.Equal(connecting ? "hub.connect" : "hub.disconnect", logged.Properties["Operation"]);
        Assert.Equal(id, logged.Properties["DiagnosticId"]);
        Assert.False(logged.Properties.ContainsKey("TenantId")); Assert.False(logged.Properties.ContainsKey("ActorPrincipalId"));
        Assert.Contains(nameof(IOException), logged.Exception!.ToString());
        Assert.DoesNotContain("private-lifecycle-sentinel", logged.Exception.ToString());
        Assert.Equal(spare, diagnostics.NewId());
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task Aborted_lifecycle_cancellation_has_no_private_exception_or_failure_reference(bool connecting)
    {
        var id = Guid.NewGuid(); var diagnostics = new QueueDiagnosticIdSource([id]);
        var logs = new DiagnosticLogCapture<SessionHubDiagnosticFilter>();
        var filter = new SessionHubDiagnosticFilter(logs, diagnostics);
        using var provider = new ServiceCollection().BuildServiceProvider();
        using var stopped = new CancellationTokenSource(); await stopped.CancelAsync();
        using var hub = new TestHub { Context = new Caller(stopped.Token) };
        var lifetime = new HubLifetimeContext(hub.Context, provider, hub);
        var safe = await Assert.ThrowsAsync<HubException>(() => connecting
            ? filter.OnConnectedAsync(lifetime, _ => throw new OperationCanceledException("private-lifecycle-sentinel"))
            : filter.OnDisconnectedAsync(lifetime, null, (_, _) => throw new OperationCanceledException("private-lifecycle-sentinel")));
        Assert.Null(safe.InnerException); Assert.DoesNotContain("private-lifecycle-sentinel", safe.ToString());
        Assert.DoesNotContain("DiagnosticId", safe.Message); Assert.Empty(logs.Entries); Assert.Equal(id, diagnostics.NewId());
    }

    [Fact]
    public async Task Successful_teardown_does_not_log_the_private_transport_close_reason()
    {
        var id = Guid.NewGuid(); var diagnostics = new QueueDiagnosticIdSource([id]);
        var logs = new DiagnosticLogCapture<SessionHubDiagnosticFilter>(); var filter = new SessionHubDiagnosticFilter(logs, diagnostics);
        using var provider = new ServiceCollection().BuildServiceProvider();
        using var hub = new TestHub { Context = new Caller(CancellationToken.None) };
        var reason = new IOException("private-lifecycle-sentinel");
        await filter.OnDisconnectedAsync(new(hub.Context, provider, hub), reason, (_, observed) =>
        { Assert.Same(reason, observed); return Task.CompletedTask; });
        Assert.Empty(logs.Entries); Assert.Equal(id, diagnostics.NewId());
    }

    private sealed class TestHub : Hub { }
    private sealed class Caller(CancellationToken stopped) : HubCallerContext
    {
        public override string ConnectionId => "diagnostic-test";
        public override string? UserIdentifier => null;
        public override ClaimsPrincipal? User => new(new ClaimsIdentity([new Claim("TenantId", "private-lifecycle-sentinel")]));
        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
        public override IFeatureCollection Features { get; } = new FeatureCollection();
        public override CancellationToken ConnectionAborted => stopped;
        public override void Abort() { }
    }
}
