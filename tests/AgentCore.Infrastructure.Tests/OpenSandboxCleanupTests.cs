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
    [Fact]
    public async Task Reset_retains_unknown_file_evidence_and_staging_even_without_an_active_execution_pointer()
    {
        await using var host = await Host.Create(context => { context.Response.StatusCode=204; return Task.CompletedTask; });
        var (service,store,lease,_) = await host.Service();
        var execution = new SandboxExecution(Guid.NewGuid(),lease.SessionId,lease.AgentInstanceId,lease.Generation,Guid.NewGuid(),Guid.NewGuid(),"once","hash",
            SandboxExecutionState.Completed,DateTimeOffset.UtcNow,ExitCode:7,WorkspaceSyncStatus:"unknown",Phase:"workspaceCommit");
        await store.SaveExecutionAsync(execution,true);
        var staging = new SandboxMountFiles(new() {WorkspaceMountRoot=host.MountRoot}).Prepare(lease.Generation).Host!.Path;
        await File.WriteAllTextAsync(Path.Combine(staging,"retained.txt"),"unresolved");
        await store.SaveLeaseAsync(lease with {Revision=2,State=SandboxLeaseState.NeedsResync,MountContractVersion=1,LastExecutionId=execution.Id},1);
        await Assert.ThrowsAsync<AgentCoreException>(()=>service.StopSessionAsync(lease.SessionId,expectedGeneration:lease.Generation).AsTask());
        Assert.True(File.Exists(Path.Combine(staging,"retained.txt")));
        var retained = await service.InspectProcessAsync(lease.SessionId,execution.Id);
        Assert.Equal("unknown",retained.WorkspaceSyncStatus);Assert.Equal(7,retained.ExitCode);
        Assert.True(retained.RemoteTerminationConfirmed);Assert.False(retained.TerminationConfirmed);
        Assert.Equal("NeedsResync",(await service.InspectAsync(lease.SessionId))!.State);
    }

    [Theory]
    [InlineData("complete")]
    [InlineData("cancelRun")]
    [InlineData("ownerReset")]
    [InlineData("resetIntent")]
    [InlineData("narrowedBackground")]
    public async Task Pending_retirement_preserves_staging_and_reconciliation_imports_only_after_confirmed_owned_absence(string resolution)
    {
        var allow = false; var deletes = 0;
        await using var host = await Host.Create(context => { deletes++; context.Response.StatusCode = allow ? 204 : 403; return Task.CompletedTask; });
        var (_, store, lease, _) = await host.Service();
        var memory = new InMemoryMemoryStore(); var now = DateTimeOffset.UtcNow; var owner = new AgentRunOwner(lease.AgentInstanceId, Guid.NewGuid());
        var definition = new AgentDefinition(1,"examiner",1,new("Alex","Examiner","Practice","Calm"),["Practice"],"Instructions",new("acknowledgeThenContinue",true,true),new("concise",true,"en",256),new(false,8000,30000,1,["longSilence"]),new(false,"default",1),new("synthetic",null,null),new Dictionary<string,string>(), SandboxPolicy: new("fixture",["fixture"]));
        var session = new SessionSnapshot(1,lease.SessionId,1,definition,SessionMode.Text,null,SessionStatus.Created,[],"",0,null,owner.ProfileId,now,now,owner.AgentInstanceId);
        await memory.SaveAsync(session,0);
        var runs = new InMemoryAgentRunStore(memory,new SystemDiagnosticIdSource());
        var activation = new Activation(Guid.NewGuid(),session.SessionId,ActivationKind.Initiative,[],Guid.NewGuid(),null,null,null,"retirement-fixture",now);
        var run = AgentRun.Create(Guid.NewGuid(),owner,new(activation,definition.Id,1,definition.Identity,Guid.NewGuid(),AgentRunOutputContract.ConversationResponse),new("synthetic","synthetic","synthetic",null),3,now);
        await runs.AdmitAsync(session with {Revision=2},1,run);
        run = await runs.ApplyAsync(owner,run.AgentRunId,new AgentRunCommand.Claim(1,now,Guid.NewGuid(),now.AddMinutes(5)));
        await store.SaveProfileAsync(lease.Profile,0);
        await store.SaveVerificationAsync(new(lease.Profile.Id,1,now,true,"Fixture",ContractVersion:2));
        var options = new OpenSandboxOptions {Enabled=true,AllowLocalImageIds=true,Endpoint=host.Endpoint,ApiKey="private-fixture-key",WorkspaceMountRoot=host.MountRoot};
        var canonical = host.MountRoot + "-canonical";
        try
        {
            var workspace = new FileSessionWorkspace(canonical,canonical+"-templates",sessions:memory);
            var scratch = await workspace.CaptureScratchAsync(session.SessionId,definition);
            var path = new SandboxMountFiles(options).Prepare(lease.Generation).Host!.Path;
            await File.WriteAllTextAsync(Path.Combine(path,"retained.txt"),"exact");
            var execution = new SandboxExecution(Guid.NewGuid(),session.SessionId,owner.AgentInstanceId,lease.Generation,run.AgentRunId,run.Claim!.Generation,"once","hash",SandboxExecutionState.Completed,now,
                FinishedAt:now,ExitCode:7,WorkspaceSyncStatus:"notCommitted",Phase:"retirementRequested",Background:resolution == "narrowedBackground");
            await store.SaveExecutionAsync(execution,true);
            await store.SaveLeaseAsync(lease with {Revision=2,State=SandboxLeaseState.NeedsReconciliation,MountContractVersion=1,ActiveExecutionId=execution.Id,LastExecutionId=execution.Id,ScratchFingerprint=scratch.Fingerprint},1);
            var service = new OpenSandboxService(options,store,workspace,memory,runs,TimeProvider.System);
            await service.ReconcileAsync();
            Assert.True(File.Exists(Path.Combine(path,"retained.txt"))); Assert.Single((await workspace.CaptureScratchAsync(session.SessionId,definition)).Transfer.Entries);
            Assert.Equal(SandboxLeaseState.NeedsReconciliation,(await store.GetLeaseAsync(session.SessionId))!.State);
            if(resolution == "cancelRun") await runs.ApplyAsync(owner,run.AgentRunId,new AgentRunCommand.RequestCancellation(run.Revision,DateTimeOffset.UtcNow,null));
            if(resolution == "narrowedBackground")
            {
                var instances = new InMemoryAgentInstanceStore();
                await instances.InsertAsync(new(lease.AgentInstanceId,definition.Id,definition.Version,definition.Identity,AgentInstanceLifecycle.Active,now,now,
                    SettingsOverrides:new(SandboxAllowedProfileIds:new(["another-profile"]))));
                service = new(options,store,workspace,memory,runs,TimeProvider.System,instances:instances);
            }
            if(resolution == "resetIntent")
            {
                var current = (await store.GetLeaseAsync(session.SessionId))!;
                await store.SaveLeaseAsync(current with {Revision=current.Revision+1, State=SandboxLeaseState.Terminating, CleanupReason="reset"}, current.Revision);
            }
            allow=true;
            if(resolution == "ownerReset") await service.StopSessionAsync(session.SessionId, expectedGeneration:lease.Generation);
            else await service.ReconcileAsync();
            var receipt = await service.InspectProcessAsync(session.SessionId,execution.Id);
            Assert.Equal(resolution == "cancelRun"?"Canceled":"Completed",receipt.Status); Assert.Equal(7,receipt.ExitCode); Assert.Equal(resolution == "complete"?"committed":"notCommitted",receipt.WorkspaceSyncStatus);
            Assert.Equal(resolution == "complete",receipt.GenerationRecycled); Assert.True(receipt.TerminationConfirmed); Assert.False(Directory.Exists(path)); Assert.Equal(2,deletes);
            var files=(await workspace.CaptureScratchAsync(session.SessionId,definition)).Transfer.Entries;
            if(resolution != "complete")Assert.Single(files);
            else Assert.Equal("exact",System.Text.Encoding.UTF8.GetString(files.Single(e=>e.RelativePath=="retained.txt").Bytes));
        }
        finally { if(Directory.Exists(canonical))Directory.Delete(canonical,true);if(Directory.Exists(canonical+"-templates"))Directory.Delete(canonical+"-templates",true); }
    }

    [Fact]
    public async Task Credential_rotation_retires_an_idle_generation_even_when_the_profile_revision_is_unchanged()
    {
        var deletes = 0;
        await using var host = await Host.Create(context => { deletes++; context.Response.StatusCode = 204; return Task.CompletedTask; });
        var (_, store, lease, _) = await host.Service();
        var owners = new InMemoryAgentInstanceStore();
        var credentials = new InMemoryCredentialStore(owners);
        var protector = new LocalCredentialProtector(Path.Combine(host.MountRoot, "keys"));
        var credentialService = new CredentialService(credentials, credentials, protector, owners, new SystemIdGenerator(TimeProvider.System), TimeProvider.System);
        var credential = await credentialService.CreateContainerRegistryAsync("Fixture", "Token", new("ghcr.io", "owner", ["ghcr.io/acme"]), "fixture-token");
        var profile = lease.Profile with { ImageReference = "ghcr.io/acme/tool@sha256:" + new string('a', 64), RegistryCredentialId = credential.CredentialId,
            RegistryCredentialRevision = credential.Revision, RegistrySecretRevision = 0 };
        await store.SaveProfileAsync(profile, 0);
        var memory = new InMemoryMemoryStore(); var now = DateTimeOffset.UtcNow; var owner = new AgentRunOwner(lease.AgentInstanceId, Guid.NewGuid());
        var definition = new AgentDefinition(1,"examiner",1,new("Alex","Examiner","Practice","Calm"),["Practice"],"Instructions",new("acknowledgeThenContinue",true,true),new("concise",true,"en",256),new(false,8000,30000,1,["longSilence"]),new(false,"default",1),new("synthetic",null,null),new Dictionary<string,string>(), SandboxPolicy: new("fixture",["fixture"]));
        await memory.SaveAsync(new SessionSnapshot(1,lease.SessionId,1,definition,SessionMode.Text,null,SessionStatus.Created,[],"",0,null,owner.ProfileId,now,now,owner.AgentInstanceId),0);
        await store.SaveLeaseAsync(lease with { Revision = 2, Profile = profile, SelectedProfileId = profile.Id }, 1);
        var options = new OpenSandboxOptions { Enabled = true, Endpoint = host.Endpoint, ApiKey = "private-fixture-key", WorkspaceMountRoot = host.MountRoot };
        var authority = new SandboxImageAuthority(options, store, TimeProvider.System, credentials, protector);
        var service = new OpenSandboxService(options, store, null!, memory, null!, TimeProvider.System, imageAuthority: authority);
        await service.ReconcileAsync();
        Assert.Equal(0, deletes); Assert.Equal("Ready", (await service.InspectAsync(lease.SessionId))!.State);
        await credentialService.ReplaceAsync(credential.CredentialId, credential.Revision, "rotated-fixture-token");
        await service.ReconcileAsync();
        Assert.Equal(1, deletes);
        var result = (await service.InspectAsync(lease.SessionId))!;
        Assert.Equal("Terminated", result.State); Assert.Equal(profile.Id, result.SelectedProfileId);
        Assert.Equal("authorityRevoked", result.CleanupReason);
    }

    [Theory]
    [InlineData("notCommitted", SandboxLeaseState.Terminated)]
    [InlineData("committed", SandboxLeaseState.Terminated)]
    [InlineData("unknown", SandboxLeaseState.NeedsResync)]
    public async Task Reconciliation_after_confirmed_cancellation_releases_only_known_file_outcomes(string sync, SandboxLeaseState expected)
    {
        var deletes=0;
        await using var host=await Host.Create(context=>{deletes++;context.Response.StatusCode=404;return Task.CompletedTask;});
        var (service,store,lease,_)=await host.Service();
        var execution=new SandboxExecution(Guid.NewGuid(),lease.SessionId,lease.AgentInstanceId,lease.Generation,Guid.NewGuid(),Guid.NewGuid(),"once","hash",
            SandboxExecutionState.Canceled,DateTimeOffset.UtcNow,FinishedAt:DateTimeOffset.UtcNow,Stdout:"retained-output",WorkspaceSyncStatus:sync,
            FailureCategory:"cancellation",CancellationReason:"userStop",TerminationConfirmed:true);
        await store.SaveExecutionAsync(execution,true);
        var staging = new SandboxMountFiles(new() { WorkspaceMountRoot = host.MountRoot }).Prepare(lease.Generation).Host!.Path;
        await File.WriteAllTextAsync(Path.Combine(staging,"retained.txt"),"private-staging");
        await store.SaveLeaseAsync(lease with {Revision=2,MountContractVersion=1,State=SandboxLeaseState.NeedsResync,LastExecutionId=execution.Id,ActiveExecutionId=execution.Id},1);
        await service.ReconcileAsync();
        var recovered=(await store.GetLeaseAsync(lease.SessionId))!;
        Assert.Equal(expected,recovered.State);Assert.Equal(sync=="unknown"?execution.Id:(Guid?)null,recovered.ActiveExecutionId);Assert.Equal(1,deletes);
        Assert.Equal(sync=="unknown",File.Exists(Path.Combine(staging,"retained.txt")));
        var retained=await service.InspectProcessAsync(lease.SessionId,execution.Id);
        Assert.Equal("Canceled",retained.Status);Assert.Equal(sync,retained.WorkspaceSyncStatus);Assert.Equal("userStop",retained.CancellationReason);Assert.Equal("retained-output",retained.Stdout);
    }

    [Fact]
    public async Task Durable_inspection_during_provider_outage_never_contacts_the_provider_or_changes_evidence()
    {
        var contacts=0;
        await using var host=await Host.Create(context=>{contacts++;context.Response.StatusCode=503;return Task.CompletedTask;});
        var (service,store,lease,_)=await host.Service();
        var execution=new SandboxExecution(Guid.NewGuid(),lease.SessionId,lease.AgentInstanceId,lease.Generation,Guid.NewGuid(),Guid.NewGuid(),"once","hash",SandboxExecutionState.Indeterminate,
            DateTimeOffset.UtcNow,WorkspaceSyncStatus:"unknown",FailureCategory:"processInspectionFailure",Phase:"processInspection");
        await store.SaveExecutionAsync(execution,true);
        var before=(await store.GetLeaseAsync(lease.SessionId))!;
        Assert.Equal("Ready",(await service.InspectAsync(lease.SessionId))!.State);
        Assert.Equal("processInspectionFailure",(await service.InspectProcessAsync(lease.SessionId,execution.Id)).FailureCategory);
        Assert.Equal(before,await store.GetLeaseAsync(lease.SessionId));Assert.Equal(0,contacts);
        await Assert.ThrowsAsync<AgentCoreException>(()=>service.StopSessionAsync(lease.SessionId,default,lease.Generation,execution.Id,true).AsTask());
        Assert.Equal(0,contacts); // stale execution confirmation fails before DELETE.
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
            var host = new Host(app, app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
            Directory.CreateDirectory(host.MountRoot); return host;
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
