using System.Diagnostics;
using AgentCore.Application.Sessions;
using AgentCore.Application.Execution;
using AgentCore.Infrastructure.Identity;
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
    [InlineData(204)]
    [InlineData(404)]
    public async Task Remote_absence_does_not_hide_local_cleanup_failure_and_a_later_reset_recovers(int status)
    {
        await using var host = await Host.Create(context => { context.Response.StatusCode = status; return Task.CompletedTask; });
        var (service, store, lease, logs) = await host.Service();
        lease = lease with { Revision = 2, MountContractVersion = 1 };
        await store.SaveLeaseAsync(lease, 1);
        var path = new SandboxMountFiles(new() { WorkspaceMountRoot = host.MountRoot }).Prepare(lease.Generation).Host!.Path;
        // Excessive workload nesting is retained safely instead of recursing without a bound.
        for (var i = 0; i < 66; i++) path = Path.Combine(path, "nested");
        Directory.CreateDirectory(path);
        var error = await Assert.ThrowsAsync<AgentCoreException>(() => service.StopSessionAsync(lease.SessionId).AsTask());
        Assert.Contains("cleanup is pending", error.Message);
        Assert.Equal(SandboxLeaseState.Terminating, (await store.GetLeaseAsync(lease.SessionId))!.State);
        Assert.Single(logs.Warnings); Assert.Empty(logs.Exceptions);
        Directory.Delete(host.MountRoot, true);
        await service.StopSessionAsync(lease.SessionId);
        Assert.Equal(SandboxLeaseState.Terminated, (await store.GetLeaseAsync(lease.SessionId))!.State);
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

    [Fact]
    public async Task Verification_lost_create_reconciles_exact_metadata_before_removing_private_staging()
    {
        var creates=0; var lists=0;
        await using var host=await Host.Create(_=>Task.CompletedTask, async context=>
        {
            creates++; context.Response.StatusCode=500; await context.Response.WriteAsync("private-upstream-sentinel");
        }, async context=>
        {
            lists++; Assert.Contains("agent-core-image-verification",Uri.UnescapeDataString(context.Request.QueryString.Value!));
            await context.Response.WriteAsJsonAsync(new {items=Array.Empty<object>(),pagination=new {page=1,pageSize=100,totalItems=0,totalPages=0}});
        });
        var service=new OpenSandboxService(new() {Enabled=true,Endpoint=host.Endpoint,ApiKey="private-fixture-key",AllowLocalImageIds=true,WorkspaceMountRoot=host.MountRoot},new InMemorySandboxStore(),null!,null!,null!,TimeProvider.System);
        var observed=await service.VerifyProfileAsync(new("fixture",1,"Fixture","",true,"sha256:"+new string('a',64)),default);
        Assert.False(observed.Passed); Assert.DoesNotContain("private-upstream-sentinel",observed.Message);
        Assert.Equal(1,creates); Assert.Equal(1,lists); Assert.Empty(Directory.EnumerateDirectories(host.MountRoot));
    }

    [Fact]
    public async Task Unknown_create_keeps_safe_cause_and_reconciles_without_replaying_provisioning()
    {
        var creates = 0; var lists = 0; string? generation = null;
        await using var host = await Host.Create(_ => Task.CompletedTask, async context =>
        {
            creates++;
            context.Response.StatusCode = 500;
            await context.Response.WriteAsync("private-upstream-body");
        }, async context =>
        {
            lists++;
            Assert.Contains(generation!, Uri.UnescapeDataString(context.Request.QueryString.Value!));
            await context.Response.WriteAsJsonAsync(new { items = Array.Empty<object>(), pagination = new { page = 1, pageSize = 100, totalItems = 0, totalPages = 0 } });
        });
        var now = DateTimeOffset.UtcNow;
        var owner = new AgentRunOwner(Guid.NewGuid(), Guid.NewGuid());
        var definition = new AgentDefinition(1,"examiner",1,new("Alex","Examiner","Practice","Calm"),["Practice"],"Instructions",new("acknowledgeThenContinue",true,true),new("concise",true,"en",256),new(false,8000,30000,1,["longSilence"]),new(false,"default",1),new("synthetic",null,null),new Dictionary<string,string>(), SandboxPolicy: new("fixture",["fixture"]));
        var snapshot = new SessionSnapshot(1,Guid.NewGuid(),1,definition,SessionMode.Text,null,SessionStatus.Created,[],"",0,null,owner.ProfileId,now,now,owner.AgentInstanceId);
        var memory = new InMemoryMemoryStore(); await memory.SaveAsync(snapshot, 0);
        var runs = new InMemoryAgentRunStore(memory, new SystemDiagnosticIdSource());
        var activation = new Activation(Guid.NewGuid(),snapshot.SessionId,ActivationKind.Initiative,[],Guid.NewGuid(),null,null,null,"sandbox-test",now);
        var run = AgentRun.Create(Guid.NewGuid(),owner,new(activation,definition.Id,1,definition.Identity,Guid.NewGuid(),AgentRunOutputContract.ConversationResponse),new("synthetic","synthetic","synthetic",null),3,now);
        await runs.AdmitAsync(snapshot with { Revision = 2 }, 1, run);
        run = await runs.ApplyAsync(owner,run.AgentRunId,new AgentRunCommand.Claim(1,now,Guid.NewGuid(),now.AddMinutes(15)));
        var store = new InMemorySandboxStore(); var image = new Uri(host.Endpoint).Authority + "/fixture/workbench@sha256:" + new string('a', 64);
        var profile = new SandboxProfile("fixture",1,"Fixture","",true,image); await store.SaveProfileAsync(profile, 0);
        await store.SaveVerificationAsync(new(profile.Id,profile.Revision,now,true,"Fixture admission",ContractVersion:2));
        var logs = new Logs();
        var service = new OpenSandboxService(new() { Enabled = true, Endpoint = host.Endpoint, ApiKey = "private-fixture-key", WorkspaceMountRoot = host.MountRoot, AllowedImagePrefixes=[new Uri(host.Endpoint).Authority+"/fixture"], InsecureRegistryHosts=[new Uri(host.Endpoint).Authority] },store,null!,memory,runs,TimeProvider.System,logger:logs);
        await Assert.ThrowsAsync<AgentCoreException>(() => service.ExecuteAsync(new(snapshot.SessionId,owner.AgentInstanceId,run.AgentRunId,run.Claim!.Generation,"create-once",profile,definition),new("true")).AsTask());
        var lease = (await store.GetLeaseAsync(snapshot.SessionId))!;
        Assert.Null(lease.ProviderId); Assert.Equal(SandboxLeaseState.NeedsReconciliation, lease.State);
        generation = lease.Generation.ToString("N");
        var warning = Assert.Single(logs.Warnings);
        Assert.Contains("Failure=http", warning); Assert.Contains("StatusCode=500", warning); Assert.Contains("ProviderLinked=False", warning);
        Assert.DoesNotContain("private-upstream-body", warning); Assert.DoesNotContain("private-fixture-key", warning);
        Assert.Empty(logs.Exceptions);
        await service.ReconcileAsync();
        Assert.Equal(SandboxLeaseState.Terminated, (await store.GetLeaseAsync(snapshot.SessionId))!.State);
        Assert.Equal(1, creates); Assert.Equal(1, lists);
    }

    [Fact]
    public async Task Explicit_generation_fenced_reset_clears_initialization_retry_fence_after_cleanup_without_remote_replay()
    {
        var deletes=0;
        await using var host=await Host.Create(context => { deletes++;context.Response.StatusCode=204;return Task.CompletedTask; });
        var (service,store,lease,_)=await host.Service();
        lease=lease with { Revision=2,State=SandboxLeaseState.Terminated,CleanupReason="initializationUncertain",
            Initialization=new("Running",lease.Profile.Id,lease.Profile.Revision,null) };
        await store.SaveLeaseAsync(lease,1);
        await Assert.ThrowsAsync<AgentCoreException>(()=>service.StopSessionAsync(lease.SessionId,expectedGeneration:Guid.NewGuid()).AsTask());
        await service.StopSessionAsync(lease.SessionId,expectedGeneration:lease.Generation);
        Assert.Equal("reset",(await store.GetLeaseAsync(lease.SessionId))!.CleanupReason);
        Assert.Equal(0,deletes);Assert.Equal("Running",(await store.GetLeaseAsync(lease.SessionId))!.Initialization!.State);
    }

    private sealed class Host(WebApplication app, string endpoint) : IAsyncDisposable
    {
        public string Endpoint => endpoint;
        public string MountRoot { get; } = Path.Combine(Path.GetTempPath(), "sandbox-cleanup-mounts-" + Guid.NewGuid().ToString("N"));
        public static async Task<Host> Create(RequestDelegate delete, RequestDelegate? create = null, RequestDelegate? list = null)
        {
            var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var app = builder.Build(); app.MapDelete("/v1/sandboxes/{id}", delete);
            if (create is not null) app.MapPost("/v1/sandboxes", create);
            if (list is not null) app.MapGet("/v1/sandboxes", list);
            app.MapMethods("/v2/{**path}",["HEAD"],() => Results.Ok());
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
            var service = new OpenSandboxService(new() { Endpoint = endpoint, ApiKey = "private-fixture-key", WorkspaceMountRoot = MountRoot },
                store, null!, new InMemoryMemoryStore(), null!, TimeProvider.System, logger: logs);
            return (service, store, lease, logs);
        }
        public async ValueTask DisposeAsync() { await app.StopAsync(); await app.DisposeAsync(); if (Directory.Exists(MountRoot)) Directory.Delete(MountRoot, true); }
    }
    private sealed class Logs : ILogger<OpenSandboxService>
    {
        public List<string> Warnings { get; } = [];
        public List<Exception> Exceptions { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        { if (exception is not null) Exceptions.Add(exception); if (level == LogLevel.Warning) Warnings.Add(formatter(state, exception)); }
    }
}
