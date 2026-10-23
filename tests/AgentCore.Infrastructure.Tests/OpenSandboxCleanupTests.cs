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
using AgentCore.Application.Credentials;
using AgentCore.Infrastructure.Credentials;
using AgentCore.Infrastructure.Workspaces;

namespace AgentCore.Infrastructure.Tests;

public sealed class OpenSandboxCleanupTests
{
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Unlinked_historical_cleanup_checks_each_remote_generation_and_never_adopts_or_replays(bool foreign)
    {
        var deletes = 0; var lists = 0; var creates = 0; var generation = Guid.Empty;
        var foreignMetadata = foreign;
        await using var host = await Host.Create(context => { deletes++; context.Response.StatusCode = 204; return Task.CompletedTask; },
            context => { creates++; context.Response.StatusCode = 500; return Task.CompletedTask; }, async context =>
            {
                lists++; Assert.Contains(generation.ToString("N"), Uri.UnescapeDataString(context.Request.QueryString.Value!));
                await context.Response.WriteAsJsonAsync(new { items = new[] { new { id = "owned-provider", entrypoint = new[] { "/bin/sh" }, status = new { state = "Running" },
                    createdAt = DateTimeOffset.UtcNow, metadata = new Dictionary<string, string> { ["agent-core-generation"] = generation.ToString("N"),
                        ["agent-core-tenant"] = foreignMetadata ? Guid.NewGuid().ToString("N") : "" } } },
                    pagination = new { page = 1, pageSize = 100, totalItems = 1, totalPages = 1 } });
            });
        var (service, store, lease, _) = await host.Service(); generation = lease.Generation;
        await store.SaveLeaseAsync(lease with { Revision = 2, ProviderId = null, State = SandboxLeaseState.Provisioning }, 1);
        await service.ReconcileAsync();
        Assert.Equal(foreign ? 0 : 1, deletes); Assert.Equal(0, creates);
        Assert.Equal(foreign ? SandboxLeaseState.Terminating : SandboxLeaseState.Terminated, (await store.GetLeaseAsync(lease.SessionId))!.State);
        if (foreign) { foreignMetadata = false; await service.ReconcileAsync(); }
        var retired = (await store.GetLeaseAsync(lease.SessionId))!;
        Assert.Null(retired.ProviderId); Assert.Equal(SandboxLeaseState.Terminated, retired.State);
        Assert.Equal(foreign ? 2 : 1, lists); Assert.Equal(1, deletes); Assert.Equal(0, creates);
        await service.ReconcileAsync(); Assert.Equal(foreign ? 2 : 1, lists);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)]
    [InlineData(true, false)] [InlineData(true, true)]
    public async Task Unowned_generations_are_cleanup_only_and_cannot_retire_a_tenant_generation(bool background, bool foreign)
    {
        var deletes = 0; var creates = 0;
        await using var host = await Host.Create(context => { deletes++; context.Response.StatusCode = 204; return Task.CompletedTask; },
            context => { creates++; context.Response.StatusCode = 500; return Task.CompletedTask; });
        var (service, store, lease, _) = await host.Service();
        var canonicalFile = Path.Combine(host.MountRoot, "retained.txt");
        await File.WriteAllTextAsync(canonicalFile, "retained-native-writes");
        var receipt = new SandboxExecution(Guid.NewGuid(), lease.SessionId, lease.AgentInstanceId, lease.Generation,
            Guid.NewGuid(), Guid.NewGuid(), "historical-command", "hash", background ? SandboxExecutionState.Running : SandboxExecutionState.Completed,
            DateTimeOffset.UtcNow, ExitCode: background ? null : 7, Stdout: "retained-output", Background: background,
            FilesystemEffectsMayHaveOccurred: true, Phase: "execution", DeadlineAt: DateTimeOffset.UtcNow.AddMinutes(5));
        await store.SaveExecutionAsync(receipt, true);
        await store.SaveLeaseAsync(lease with { Revision = 2, State = background ? SandboxLeaseState.Executing : SandboxLeaseState.Ready,
            ActiveExecutionId = background ? receipt.Id : null, LastExecutionId = receipt.Id }, 1);
        if (foreign) host.RemoteTenant = Guid.NewGuid();
        await service.ReconcileAsync();
        var observed = (await store.GetLeaseAsync(lease.SessionId))!;
        Assert.Equal("legacyAuthorityRetired", observed.CleanupReason);
        Assert.Equal(foreign ? SandboxLeaseState.Terminating : SandboxLeaseState.Terminated, observed.State);
        Assert.Equal(foreign ? 0 : 1, deletes); Assert.Equal(0, creates);
        if (foreign)
        {
            Assert.Equal(receipt, await store.GetExecutionAsync(lease.SessionId, receipt.Id));
            host.RemoteTenant = null; await service.ReconcileAsync();
        }
        var retained = (await store.GetExecutionAsync(lease.SessionId, receipt.Id))!;
        Assert.Equal(background ? SandboxExecutionState.Indeterminate : SandboxExecutionState.Completed, retained.State);
        Assert.Equal(receipt.ExitCode, retained.ExitCode); Assert.Equal("retained-output", retained.Stdout);
        Assert.True(retained.TerminationConfirmed); Assert.True(retained.RemoteTerminationConfirmed);
        Assert.Equal("retained-native-writes", await File.ReadAllTextAsync(canonicalFile));
        await service.ReconcileAsync(); Assert.Equal(1, deletes); Assert.Equal(0, creates);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Policy_generation_retirement_preserves_selection_and_only_explicit_reset_clears_it(bool pendingCleanup)
    {
        var allowed = !pendingCleanup;
        await using var host = await Host.Create(context => { context.Response.StatusCode = allowed ? 404 : 403; return Task.CompletedTask; });
        var (service, store, lease, _) = await host.Service();
        var execution = new SandboxExecution(Guid.NewGuid(), lease.SessionId, lease.AgentInstanceId, lease.Generation,
            Guid.NewGuid(), Guid.NewGuid(), "once", "hash", SandboxExecutionState.Running, DateTimeOffset.UtcNow, Background:true);
        await store.SaveExecutionAsync(execution, true);
        await store.SaveLeaseAsync(lease with { Revision=2, SelectedProfileId="alternate", ActiveExecutionId=execution.Id, LastExecutionId=execution.Id }, 1);
        var enforcement = new AgentCore.Application.Admin.SandboxPolicyEnforcement(store, service);
        Assert.Equal(!pendingCleanup, await enforcement.EnforceAsync(lease.AgentInstanceId));
        allowed = true;
        if (pendingCleanup) await service.ReconcileAsync();
        var retired = (await store.GetLeaseAsync(lease.SessionId))!;
        Assert.Equal(SandboxLeaseState.Terminated, retired.State);
        Assert.Equal("alternate", retired.SelectedProfileId);
        Assert.Equal("policyChanged", retired.CleanupReason);
        var receipt = (await store.GetExecutionAsync(lease.SessionId, execution.Id))!;
        Assert.Equal(SandboxExecutionState.Canceled, receipt.State);
        Assert.Equal("policyChanged", receipt.CancellationReason);
        await service.StopSessionAsync(lease.SessionId, expectedGeneration:lease.Generation); // CAS alone is lifecycle intent.
        Assert.Equal("alternate", (await store.GetLeaseAsync(lease.SessionId))!.SelectedProfileId);
        await service.StopSessionAsync(lease.SessionId, expectedGeneration:lease.Generation,
            intent:AgentCore.Application.Ports.SandboxStopIntent.OwnerReset);
        Assert.Null((await store.GetLeaseAsync(lease.SessionId))!.SelectedProfileId);
        Assert.Equal("reset", (await store.GetLeaseAsync(lease.SessionId))!.CleanupReason);
        Assert.Equal(receipt, await store.GetExecutionAsync(lease.SessionId, execution.Id));
    }

    [Theory]
    [InlineData(SandboxExecutionState.Completed, 7)]
    [InlineData(SandboxExecutionState.Canceled, null)]
    [InlineData(SandboxExecutionState.Indeterminate, null)]
    public async Task Reset_and_reconciliation_retain_canonical_partial_writes_and_original_receipts(SandboxExecutionState state, int? exit)
    {
        var allowed = false; var deletes = 0;
        await using var host = await Host.Create(context => { deletes++; context.Response.StatusCode = allowed ? 404 : 403; return Task.CompletedTask; });
        var (service, store, lease, _) = await host.Service();
        var canonicalFile = Path.Combine(host.MountRoot, "retained.txt");
        await File.WriteAllTextAsync(canonicalFile, "partial-and-visible");
        var execution = new SandboxExecution(Guid.NewGuid(), lease.SessionId, lease.AgentInstanceId, lease.Generation, Guid.NewGuid(), Guid.NewGuid(), "once", "hash", state,
            DateTimeOffset.UtcNow, ExitCode: exit, Stdout: "original-output", FilesystemEffectsMayHaveOccurred:true, Phase:"processInspection");
        await store.SaveExecutionAsync(execution, true);
        await store.SaveLeaseAsync(lease with { Revision=2, State=SandboxLeaseState.NeedsReconciliation, LastExecutionId=execution.Id, ActiveExecutionId=execution.Id }, 1);
        await Assert.ThrowsAsync<AgentCoreException>(() => service.StopSessionAsync(lease.SessionId, expectedGeneration:lease.Generation, intent: AgentCore.Application.Ports.SandboxStopIntent.OwnerReset).AsTask());
        Assert.Equal("partial-and-visible", await File.ReadAllTextAsync(canonicalFile));
        allowed = true;
        await service.ReconcileAsync();
        var retained = (await store.GetExecutionAsync(lease.SessionId, execution.Id))!;
        Assert.Equal(state, retained.State); Assert.Equal(exit, retained.ExitCode);
        Assert.Equal("original-output", retained.Stdout); Assert.True(retained.FilesystemEffectsMayHaveOccurred);
        Assert.True(retained.RemoteTerminationConfirmed); Assert.True(retained.TerminationConfirmed);
        Assert.Equal(SandboxLeaseState.Terminated, (await store.GetLeaseAsync(lease.SessionId))!.State);
        Assert.Equal("partial-and-visible", await File.ReadAllTextAsync(canonicalFile));
        Assert.Equal(2, deletes);
    }

    [Fact]
    public async Task Durable_inspection_during_provider_outage_reads_receipts_without_contact_or_mutation()
    {
        var contacts = 0;
        await using var host = await Host.Create(context => { contacts++; context.Response.StatusCode=503; return Task.CompletedTask; });
        var (service, store, lease, _) = await host.Service();
        var execution = new SandboxExecution(Guid.NewGuid(),lease.SessionId,lease.AgentInstanceId,lease.Generation,Guid.NewGuid(),Guid.NewGuid(),"once","hash",SandboxExecutionState.Indeterminate,
            DateTimeOffset.UtcNow, FilesystemEffectsMayHaveOccurred:true, FailureCategory:"processInspectionFailure");
        await store.SaveExecutionAsync(execution,true);
        var before = await store.GetLeaseAsync(lease.SessionId);
        Assert.Equal(before, await store.GetLeaseAsync(lease.SessionId));
        Assert.Equal("processInspectionFailure", (await store.GetExecutionAsync(lease.SessionId, execution.Id))!.FailureCategory);
        Assert.Equal(before, await store.GetLeaseAsync(lease.SessionId)); Assert.Equal(0, contacts);
        await Assert.ThrowsAsync<AgentCoreException>(() => service.StopSessionAsync(lease.SessionId, default, lease.Generation, execution.Id, true).AsTask());
        Assert.Equal(0, contacts);
    }

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
        if (terminated) await service.StopSessionAsync(lease.SessionId, expectedGeneration: lease.Generation, intent: AgentCore.Application.Ports.SandboxStopIntent.OwnerReset);
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

    [Fact]
    public async Task Verification_lost_create_reconciles_exact_metadata_before_removing_disposable_probe_files()
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
        var service=new OpenSandboxService(new() {Enabled=true,Endpoint=host.Endpoint,ApiKey="private-fixture-key",AllowLocalImageIds=true,CanonicalWorkspaceRoot=host.MountRoot,WorkspaceDockerHostRoot=host.MountRoot},new InMemorySandboxStore(),null!,null!,TimeProvider.System);
        var observed=await service.VerifyProfileAsync(new("fixture",1,"Fixture","",true,"sha256:"+new string('a',64)),default);
        Assert.False(observed.Passed); Assert.DoesNotContain("private-upstream-sentinel",observed.Message);
        Assert.Equal(1,creates); Assert.Equal(1,lists); Assert.Empty(Directory.EnumerateDirectories(Path.Combine(host.MountRoot,".verification")));
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
        await Assert.ThrowsAsync<AgentCoreException>(()=>service.StopSessionAsync(lease.SessionId,expectedGeneration:Guid.NewGuid(), intent: AgentCore.Application.Ports.SandboxStopIntent.OwnerReset).AsTask());
        await service.StopSessionAsync(lease.SessionId,expectedGeneration:lease.Generation, intent: AgentCore.Application.Ports.SandboxStopIntent.OwnerReset);
        Assert.Equal("reset",(await store.GetLeaseAsync(lease.SessionId))!.CleanupReason);
        Assert.Equal(0,deletes);Assert.Equal("Running",(await store.GetLeaseAsync(lease.SessionId))!.Initialization!.State);
    }

    private sealed class Host(WebApplication app, string endpoint) : IAsyncDisposable
    {
        public string Endpoint => endpoint;
        private Guid generation;
        public Guid? RemoteTenant { get; set; }
        public string MountRoot { get; } = Path.Combine(Path.GetTempPath(), "sandbox-cleanup-mounts-" + Guid.NewGuid().ToString("N"));
        public static async Task<Host> Create(RequestDelegate delete, RequestDelegate? create = null, RequestDelegate? list = null)
        {
            var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            Host? current = null;
            var app = builder.Build();
            app.MapGet("/v1/sandboxes/{id}", () => Results.Json(new { id = "owned-provider", entrypoint = new[] { "/bin/sh" }, status = new { state = "Running" },
                createdAt = DateTimeOffset.UtcNow, metadata = new Dictionary<string, string> { ["agent-core-generation"] = current!.generation.ToString("N"),
                    ["agent-core-tenant"] = current.RemoteTenant?.ToString("N") ?? "" } }));
            app.MapDelete("/v1/sandboxes/{id}", delete);
            if (create is not null) app.MapPost("/v1/sandboxes", create);
            if (list is not null) app.MapGet("/v1/sandboxes", list);
            app.MapMethods("/v2/{**path}",["HEAD"],() => Results.Ok());
            await app.StartAsync();
            var host = new Host(app, app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
            current = host; Directory.CreateDirectory(host.MountRoot); return host;
        }
        public async Task<(OpenSandboxService Service, InMemorySandboxStore Store, SandboxLease Lease, Logs Logs)> Service()
        {
            var store = new InMemorySandboxStore(); var now = DateTimeOffset.UtcNow;
            var profile = new SandboxProfile("fixture", 1, "Fixture", "", true, "sha256:" + new string('a', 64));
            var lease = new SandboxLease(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, profile,
                SandboxLeaseState.Ready, "owned-provider", now, now.AddMinutes(5));
            generation = lease.Generation;
            await store.SaveLeaseAsync(lease, 0); var logs = new Logs();
            // StopSession touches only the durable sandbox store and SDK lifecycle.
            var service = new OpenSandboxService(new() { Endpoint = endpoint, ApiKey = "private-fixture-key", CanonicalWorkspaceRoot = MountRoot, WorkspaceDockerHostRoot = MountRoot },                store,null!,new InMemoryMemoryStore(),TimeProvider.System,logger: logs);
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
