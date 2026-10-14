using System.Text;
using AgentCore.Application.Ports;
using AgentCore.Application.Execution;
using AgentCore.Application.Sessions;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Sandbox;
using AgentCore.Infrastructure.Workspaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OpenSandbox;
using OpenSandbox.Config;

namespace AgentCore.Infrastructure.Tests;

public sealed class OpenSandboxFactAttribute : FactAttribute
{
    public OpenSandboxFactAttribute()
    { if (Environment.GetEnvironmentVariable("AGENTCORE_OPENSANDBOX_TESTS") != "1") Skip = "Opt-in real local OpenSandbox/Docker acceptance."; }
}

public sealed class OpenSandboxIntegrationTests
{
    [OpenSandboxFact]
    public async Task One_second_combined_setup_budget_can_execute_short_scripts_and_verify_without_rounding_it_to_zero()
    {
        await using var f=await Fixture.Create("printf Profile",maxCommand:1);
        f.SetInstanceSetup("printf Instance",1);
        var result=await f.Service.ExecuteAsync(f.Access(),new("true",TimeoutSeconds:1));
        Assert.Equal(0,result.ExitCode);
        Assert.Equal("ProfileInstance",(await f.Service.InspectAsync(f.Session.SessionId))!.Initialization!.Stdout);
        var verification=await f.Service.VerifyProfileAsync(f.Profile,CancellationToken.None);
        Assert.True(verification.Passed,verification.Message);Assert.Equal("ProfileInstance",verification.Stdout);
    }

    [OpenSandboxFact]
    public async Task Instance_setup_runs_after_profile_once_with_observable_bounded_output_and_no_partial_commit_on_failure()
    {
        await using var f = await Fixture.Create("printf Profile; printf ProfileErr >&2; printf P > setup-order.txt");
        f.SetInstanceSetup("test $(cat setup-order.txt) = P; printf Instance; printf InstanceErr >&2; printf I >> setup-order.txt",7);
        Assert.Equal(0,(await f.Exec("cat setup-order.txt")).ExitCode);
        var first=(await f.Service.InspectAsync(f.Session.SessionId))!;
        Assert.Equal("Succeeded",first.Initialization!.State);Assert.Equal("ProfileInstance",first.Initialization.Stdout);
        Assert.Equal("ProfileErrInstanceErr",first.Initialization.Stderr);Assert.Equal(7,first.Initialization.InstanceRevision);Assert.Equal("PI",await f.Read("setup-order.txt"));
        f.Reopen();f.SetInstanceSetup("printf changed",8);Assert.Equal(0,(await f.Exec("true")).ExitCode);
        Assert.Equal(first.Generation,(await f.Service.InspectAsync(f.Session.SessionId))!.Generation);
        Assert.Equal(7,(await f.Service.InspectAsync(f.Session.SessionId))!.Initialization!.InstanceRevision);Assert.Equal("PI",await f.Read("setup-order.txt"));
        var verified=await f.Service.VerifyProfileAsync(f.Profile,CancellationToken.None);Assert.True(verified.Passed,verified.Message);Assert.Contains("changed",verified.Stdout);
        var image=await f.Service.VerifyProfileAsync(f.Profile with {InstanceInitializationScript="exit 7"},CancellationToken.None,imageOnly:true);
        Assert.True(image.Passed);Assert.Empty(image.Stdout);
        await f.Service.StopSessionAsync(f.Session.SessionId,expectedGeneration:first.Generation);
        f.SetInstanceSetup("printf broken > partial.txt; echo failed >&2; exit 7",9);
        var before=(await f.Workspace.CaptureScratchAsync(f.Session.SessionId,f.Definition)).Fingerprint;
        await Assert.ThrowsAsync<AgentCoreException>(async()=>await f.Exec("true"));
        Assert.Equal(before,(await f.Workspace.CaptureScratchAsync(f.Session.SessionId,f.Definition)).Fingerprint);
        var failed=(await f.Service.InspectAsync(f.Session.SessionId))!;Assert.Equal("Failed",failed.Initialization!.State);Assert.Equal(7,failed.Initialization.ExitCode);Assert.Contains("failed",failed.Initialization.Stderr);
        await Assert.ThrowsAsync<AgentCoreException>(async()=>await f.Exec("true"));
        Assert.Equal(failed.Generation,(await f.Service.InspectAsync(f.Session.SessionId))!.Generation);
    }

    [OpenSandboxFact]
    public async Task Short_idle_ttl_does_not_kill_foreground_background_or_initialization_within_permitted_deadlines()
    {
        async Task Exercise(bool background, bool initialization)
        {
            await using var f = await Fixture.Create(initialization ? "sleep 65; printf initialized > initialized.txt" : null, idleTtl: 60, maxCommand: 90);
            var access = f.Access();
            var result = await f.Service.ExecuteAsync(access, new(initialization ? "cat initialized.txt" : "sleep 65; printf completed > result.txt", TimeoutSeconds: 90, Background: background));
            if (background)
            {
                var lease = (await f.Store.GetLeaseAsync(f.Session.SessionId))!;
                Assert.True(lease.ExpiresAt > DateTimeOffset.UtcNow.AddSeconds(120));
                await Task.Delay(68000);
                await f.Service.ReconcileAsync();
                result = await f.Service.InspectProcessAsync(f.Session.SessionId, result.ExecutionId);
            }
            Assert.Equal("Completed", result.Status);
            Assert.Equal(0, result.ExitCode);
            Assert.Equal("committed", result.WorkspaceSyncStatus);
            Assert.Equal(initialization ? "initialized" : "completed", await f.Read(initialization ? "initialized.txt" : "result.txt"));
            var current = (await f.Store.GetLeaseAsync(f.Session.SessionId))!;
            Assert.True(current.ExpiresAt <= current.CreatedAt.AddSeconds(current.Profile.MaxLifetimeSeconds));
        }
        await Task.WhenAll(Exercise(false, false), Exercise(true, false), Exercise(false, true));
    }

    [OpenSandboxFact]
    public async Task Hard_maximum_lifetime_rejects_dispatch_without_replay_or_canonical_changes()
    {
        await using var f = await Fixture.Create(idleTtl: 60, maxLifetime: 60);
        var access = f.Access("too-long");
        await Assert.ThrowsAsync<AgentCoreException>(async () => await f.Service.ExecuteAsync(access, new("printf forbidden > forbidden.txt", TimeoutSeconds: 120)));
        Assert.Null(await f.Store.GetOperationAsync(f.Session.SessionId, f.Run.AgentRunId, access.ToolCallId));
        Assert.Empty((await f.Workspace.CaptureScratchAsync(f.Session.SessionId, f.Definition)).Transfer.Entries.Skip(1));
    }

    [OpenSandboxFact]
    public async Task Crash_after_swap_and_lost_terminal_receipt_recovers_commit_evidence_without_replaying_or_overwriting_newer_files()
    {
        await using var f = await Fixture.Create();
        var access = f.Access("commit-crash");
        f.Workspace.ScratchCommitFault = point => { if (point == "afterSwap") throw new IOException("Injected crash after tree replacement"); };
        f.Service.ExecutionReceiptFault = receipt => { if (receipt.WorkspaceSyncStatus == "committed") throw new IOException("Injected terminal persistence failure"); };
        var command = new SandboxCommandRequest("printf once > once.txt");
        var result = await f.Service.ExecuteAsync(access, command);
        Assert.Equal("committed", result.WorkspaceSyncStatus);
        Assert.Equal("committing", (await f.Store.GetExecutionAsync(f.Session.SessionId, result.ExecutionId))!.WorkspaceSyncStatus);
        f.Reopen();
        Assert.Equal("once", await f.Read("once.txt"));
        await f.Workspace.WriteAsync(f.Session.SessionId, "/workspace/working/later.txt", "later"u8.ToArray());
        await f.Service.ReconcileAsync();
        var recovered = await f.Service.InspectProcessAsync(f.Session.SessionId, result.ExecutionId);
        Assert.Equal("Indeterminate", recovered.Status);
        Assert.Equal("committed", recovered.WorkspaceSyncStatus);
        Assert.Equal(recovered, await f.Service.ExecuteAsync(access, command));
        Assert.Equal("once", await f.Read("once.txt"));
        Assert.Equal("later", await f.Read("later.txt"));
    }

    [OpenSandboxFact]
    public async Task Verification_executes_initialization_rejects_broken_scripts_and_does_not_change_profile_configuration()
    {
        await using var f = await Fixture.Create("printf harmless > initialization.txt; exit 7");
        var broken = await f.Service.VerifyProfileAsync(f.Profile, CancellationToken.None);
        Assert.False(broken.Passed); Assert.Equal("initialization", broken.FailureCode); Assert.Equal(7, broken.ExitCode);
        await f.Store.SaveVerificationAsync(broken);
        Assert.Equal(f.Profile, await f.Store.GetProfileAsync(f.Profile.Id, f.Profile.Revision));
        var passed = await f.Service.VerifyProfileAsync(f.Profile with { InitializationScript = "printf initialized > initialization.txt" }, CancellationToken.None);
        Assert.True(passed.Passed, passed.Message);
        var invalid = await f.Service.VerifyProfileAsync(f.Profile with { InitializationScript = "touch .env" }, CancellationToken.None);
        Assert.False(invalid.Passed); Assert.Equal("validation", invalid.FailureCode);
        var privateOutput = await f.Service.VerifyProfileAsync(f.Profile with { InitializationScript = "printf secret-sentinel >&2; exit 9" }, CancellationToken.None);
        Assert.False(privateOutput.Passed); Assert.DoesNotContain("secret-sentinel", privateOutput.Message);
        Assert.Null(await f.Store.GetLeaseAsync(f.Session.SessionId));
    }

    [OpenSandboxFact]
    public async Task Status_is_read_only_and_reconciliation_fences_revocation_during_background_import()
    {
        await using var f = await Fixture.Create();
        var background = await f.Service.ExecuteAsync(f.Access(), new("printf background > background.txt", Background: true));
        await Task.Delay(1000);
        var firstLease = (await f.Store.GetLeaseAsync(f.Session.SessionId))!;
        Assert.Equal("Running", (await f.Service.InspectProcessAsync(f.Session.SessionId, background.ExecutionId)).Status);
        Assert.Equal(firstLease.Revision, (await f.Store.GetLeaseAsync(f.Session.SessionId))!.Revision);
        Assert.Empty((await f.Workspace.CaptureScratchAsync(f.Session.SessionId, f.Definition)).Transfer.Entries.Skip(1));
        await f.Service.ReconcileAsync();
        Assert.Equal("Completed", (await f.Service.InspectProcessAsync(f.Session.SessionId, background.ExecutionId)).Status);
        Assert.Equal("background", await f.Read("background.txt"));
        var canceled = await f.Service.ExecuteAsync(f.Access(), new("printf unintended > late.txt", Background: true));
        await Task.Delay(1000);
        f.Workspace.BeforeScratchCommitValidation = async _ => await f.CancelRun();
        await f.Service.ReconcileAsync();
        var receipt = await f.Service.InspectProcessAsync(f.Session.SessionId, canceled.ExecutionId);
        Assert.Equal("Indeterminate", receipt.Status); Assert.Equal("notCommitted", receipt.WorkspaceSyncStatus);
        Assert.DoesNotContain((await f.Workspace.CaptureScratchAsync(f.Session.SessionId, f.Definition)).Transfer.Entries, e => e.RelativePath == "late.txt");
    }

    [OpenSandboxFact]
    public async Task Real_sdk_deadline_quarantines_the_generation_without_importing_partial_files()
    {
        await using var f = await Fixture.Create();
        Assert.Equal(0,(await f.Exec("printf safe > retained.txt")).ExitCode);
        var before=(await f.Workspace.CaptureScratchAsync(f.Session.SessionId,f.Definition)).Fingerprint;
        var result=await f.Service.ExecuteAsync(f.Access(),new("printf partial > retained.txt; sleep 5",TimeoutSeconds:1));
        Assert.Equal("Indeterminate",result.Status);Assert.Equal("notCommitted",result.WorkspaceSyncStatus);
        Assert.Equal(before,(await f.Workspace.CaptureScratchAsync(f.Session.SessionId,f.Definition)).Fingerprint);
        Assert.Equal("safe",await f.Read("retained.txt"));
    }
    [OpenSandboxFact]
    public async Task Real_profile_quota_and_concurrent_canonical_write_conflicts_preserve_authoritative_bytes()
    {
        await using var f = await Fixture.Create();
        Assert.Equal(0,(await f.Exec("printf safe > retained.txt")).ExitCode);
        var pending = f.Exec("printf wrong > retained.txt; sleep 2").AsTask();
        for (var attempt=0; attempt<300 && (await f.Service.InspectAsync(f.Session.SessionId))?.ActiveExecutionId is null; attempt++) await Task.Delay(100);
        Assert.NotNull((await f.Service.InspectAsync(f.Session.SessionId))?.ActiveExecutionId);
        await f.Workspace.WriteAsync(f.Session.SessionId,"/workspace/working/core.txt","canonical"u8.ToArray());
        Assert.Equal("notCommitted",(await pending).WorkspaceSyncStatus); Assert.Equal("safe",await f.Read("retained.txt"));
        Assert.Equal("canonical",await f.Read("core.txt"));
        await f.Service.StopSessionAsync(f.Session.SessionId); await f.LimitStorage(16);
        var before = (await f.Workspace.CaptureScratchAsync(f.Session.SessionId,f.Definition)).Fingerprint;
        Assert.Equal("notCommitted",(await f.Exec("printf overflowing > extra.txt")).WorkspaceSyncStatus);
        Assert.Equal(before,(await f.Workspace.CaptureScratchAsync(f.Session.SessionId,f.Definition)).Fingerprint);
    }

    [OpenSandboxFact]
    public async Task Provisioning_orphan_reconciliation_kills_only_its_durable_generation_without_adopting_it()
    {
        await using var f = await Fixture.Create();
        Assert.Equal(0,(await f.Exec("printf retained > orphan.txt")).ExitCode);
        var lease = (await f.Store.GetLeaseAsync(f.Session.SessionId))!;
        await f.Store.SaveLeaseAsync(lease with { Revision=lease.Revision+1,State=SandboxLeaseState.Provisioning,ProviderId=null },lease.Revision);
        f.Reopen(); await f.Service.ReconcileAsync();
        Assert.Equal("Terminated",(await f.Service.InspectAsync(f.Session.SessionId))!.State);
        Assert.Equal(0,(await f.Exec("cat orphan.txt")).ExitCode);
        Assert.NotEqual(lease.Generation,(await f.Service.InspectAsync(f.Session.SessionId))!.Generation);
        Assert.Equal("retained",await f.Read("orphan.txt"));
    }
    [OpenSandboxFact]
    public async Task Legacy_tool_uses_the_owned_sdk_and_rejects_mutated_grammar_and_malformed_modern_arguments()
    {
        await using var f = await Fixture.Create();
        var executor = f.Executor();
        var definition = f.Definition with { Environment = new(ToolAllowlist:[ToolCatalog.SandboxRun,ToolCatalog.SandboxExec]), ExecutionSandboxProfile=f.Profile };
        var admission = new ToolExecutionAdmission(false, TriggerKind.UserTurn, AgentInstanceId:f.Session.AgentInstanceId, AgentRunId:f.Run.AgentRunId, OwnedSessionId:f.Session.SessionId);
        var legacy = await executor.ExecuteAsync(definition,f.Session.SessionId,new("legacy-once",ToolCatalog.SandboxRun,"""{"verb":"echo","arguments":["legacy-real"]}"""),8192,admission:admission);
        Assert.Contains("\"ok\":true",legacy.Text); Assert.Contains("legacy-real",legacy.Text);
        Assert.Equal(0,(await f.Exec("printf canonical > legacy.txt")).ExitCode);
        var artifacts = new InMemoryArtifactStore(TimeProvider.System);
        var exported = await f.Executor(artifacts).ExecuteAsync(definition,f.Session.SessionId,new("legacy-export",ToolCatalog.SandboxRun,"""{"verb":"cat","arguments":["legacy.txt"],"exportPath":"legacy.txt"}"""),8192,admission:admission);
        Assert.Contains("\"ok\":true",exported.Text); Assert.Contains("\"artifactId\":",exported.Text); Assert.Contains("canonical",exported.Text);
        Assert.Single(await artifacts.ListAsync(f.Session.SessionId));
        foreach (var arguments in new[] { """{"verb":"sh","arguments":["-c","true"]}""", """{"verb":"echo","arguments":["$(true)"]}""" })
            Assert.Contains("forbidden",(await executor.ExecuteAsync(definition,f.Session.SessionId,new(Guid.NewGuid().ToString(),ToolCatalog.SandboxRun,arguments),8192,admission:admission)).Text);
        foreach (var arguments in new[] { """{"command":"true","timeoutSeconds":"120"}""", """{"command":"true","background":"false"}""", """{"command":123}""" })
            Assert.Contains("invalid",(await executor.ExecuteAsync(definition,f.Session.SessionId,new(Guid.NewGuid().ToString(),ToolCatalog.SandboxExec,arguments),8192,admission:admission)).Text);
        var unknown = await executor.ExecuteAsync(definition,f.Session.SessionId,new("unknown-clipped",ToolCatalog.SandboxExec,"""{"command":"ln -s /etc/passwd link"}"""),64,admission:admission);
        Assert.True(unknown.OutcomeUnknown); Assert.InRange(Encoding.UTF8.GetByteCount(unknown.Text),0,64);
    }

    [OpenSandboxFact]
    public async Task Utf8_output_is_bounded_and_unsafe_files_or_revoked_authority_never_import_partial_scratch()
    {
        await using var f = await Fixture.Create();
        var output = await f.Service.ExecuteAsync(f.Access(),new(null,["python","-I","-c","import sys; sys.stdout.write('🚀'*20000); sys.stderr.write('é'*40000)"]));
        Assert.Equal("Completed",output.Status); Assert.True(output.Truncated);
        Assert.InRange(Encoding.UTF8.GetByteCount(output.Stdout),1,32768); Assert.InRange(Encoding.UTF8.GetByteCount(output.Stderr),1,32768);
        Assert.DoesNotContain('\uFFFD',output.Stdout); Assert.DoesNotContain('\uFFFD',output.Stderr);
        Assert.Equal(0,(await f.Exec("printf safe > retained.txt")).ExitCode);
        foreach (var command in new[] { "ln -s /etc/passwd link", "mkfifo pipe", "printf secret > .env", "printf a > file; printf b > FILE", "python -I -c \"import socket; socket.socket(socket.AF_UNIX).bind('socket')\"" })
        {
            var before = (await f.Workspace.CaptureScratchAsync(f.Session.SessionId,f.Definition)).Fingerprint;
            Assert.Equal("notCommitted",(await f.Exec(command)).WorkspaceSyncStatus);
            Assert.Equal(before,(await f.Workspace.CaptureScratchAsync(f.Session.SessionId,f.Definition)).Fingerprint);
            await f.Service.StopSessionAsync(f.Session.SessionId);
        }
        var pending = f.Exec("printf changed > retained.txt; sleep 2").AsTask();
        for (var attempt=0; attempt<300 && (await f.Service.InspectAsync(f.Session.SessionId))?.ActiveExecutionId is null; attempt++) await Task.Delay(100);
        Assert.NotNull((await f.Service.InspectAsync(f.Session.SessionId))?.ActiveExecutionId);
        await f.Pause("manual"); Assert.Equal("notCommitted",(await pending).WorkspaceSyncStatus);
        Assert.Equal("safe",await f.Read("retained.txt"));
    }
    [OpenSandboxFact]
    public async Task Idle_transport_pause_preserves_generation_but_explicit_deactivation_terminates_it()
    {
        await using var f = await Fixture.Create("printf I >> init-count.txt");
        Assert.Equal(0, (await f.Exec("true")).ExitCode);
        var generation = (await f.Service.InspectAsync(f.Session.SessionId))!.Generation;
        await f.Pause("disconnected"); f.Reopen(); await f.Service.ReconcileAsync();
        Assert.Equal("Ready", (await f.Service.InspectAsync(f.Session.SessionId))!.State);
        await f.Resume(); Assert.Equal(0, (await f.Exec("true")).ExitCode);
        Assert.Equal(generation, (await f.Service.InspectAsync(f.Session.SessionId))!.Generation);
        Assert.Equal("I", await f.Read("init-count.txt"));
        await f.Pause("manual"); await f.Service.ReconcileAsync();
        Assert.Equal("Terminated", (await f.Service.InspectAsync(f.Session.SessionId))!.State);
    }
    [OpenSandboxFact]
    public async Task Real_initialization_is_generation_pinned_and_expiration_rehydrates_without_replaying_commands()
    {
        await using var f = await Fixture.Create("printf I >> init-count.txt");
        Assert.Equal(0,(await f.Exec("true")).ExitCode); Assert.Equal("I",await f.Read("init-count.txt"));
        var first = (await f.Store.GetLeaseAsync(f.Session.SessionId))!;
        f.Reopen(); Assert.Equal(0,(await f.Exec("true")).ExitCode); Assert.Equal("I",await f.Read("init-count.txt"));
        await f.ReviseScript("printf N >> init-count.txt");
        Assert.Equal(0,(await f.Exec("true")).ExitCode); Assert.Equal("I",await f.Read("init-count.txt"));
        Assert.Equal(1,(await f.Store.GetLeaseAsync(f.Session.SessionId))!.Profile.Revision);
        await Assert.ThrowsAsync<AgentCoreException>(async () => await f.Service.StopSessionAsync(f.Session.SessionId,expectedGeneration:Guid.NewGuid()));
        await f.Service.StopSessionAsync(f.Session.SessionId,expectedGeneration:first.Generation);
        Assert.Equal(0,(await f.Exec("true")).ExitCode); Assert.Equal("IN",await f.Read("init-count.txt"));
        var second = (await f.Store.GetLeaseAsync(f.Session.SessionId))!; Assert.NotEqual(first.Generation,second.Generation); Assert.Equal(2,second.Profile.Revision);
        var uri = new Uri(Environment.GetEnvironmentVariable("AGENTCORE_OSB_PROOF_URL") ?? "http://127.0.0.1:18080");
        await using (var sdk = await OpenSandbox.Sandbox.ConnectAsync(new SandboxConnectOptions { SandboxId=second.ProviderId!,ConnectionConfig=new(new ConnectionConfigOptions { Domain=uri.Authority,Protocol=ConnectionProtocol.Http,ApiKey=Environment.GetEnvironmentVariable("AGENTCORE_OSB_PROOF_KEY"),UseServerProxy=true,DisableMetrics=true }) }))
            await sdk.RenewAsync(1);
        await Task.Delay(3000);
        Assert.Equal(0,(await f.Exec("true")).ExitCode); Assert.Equal("INN",await f.Read("init-count.txt"));
        Assert.NotEqual(second.Generation,(await f.Store.GetLeaseAsync(f.Session.SessionId))!.Generation);
    }

    [OpenSandboxFact]
    public async Task Reset_preserves_terminal_receipts_and_profile_disable_cleans_owned_generations()
    {
        await using var f = await Fixture.Create();
        var background = await f.Service.ExecuteAsync(f.Access(),new("sleep 120",Background:true));
        var stopped = await f.Service.StopProcessAsync(f.Session.SessionId,background.ExecutionId);
        await f.Service.StopSessionAsync(f.Session.SessionId);
        Assert.Equal(stopped,await f.Service.InspectProcessAsync(f.Session.SessionId,background.ExecutionId));
        Assert.Equal(0,(await f.Exec("true")).ExitCode);
        var latest = await f.Store.GetProfileAsync("general-workbench");
        await f.Store.SaveProfileAsync(latest! with { Revision=latest.Revision+1,Enabled=false },latest.Revision);
        await Assert.ThrowsAsync<AgentCoreException>(async () => await f.Exec("true"));
        await f.Service.ReconcileAsync(); Assert.Equal("Terminated",(await f.Service.InspectAsync(f.Session.SessionId))!.State);
    }

    [OpenSandboxFact]
    public async Task Real_sdk_persists_exact_scratch_reconnects_fences_replay_and_stops_owned_processes()
    {
        await using var f = await Fixture.Create();
        var failed = await f.Exec("printf stdout; printf stderr >&2; printf durable > retained.txt; exit 7");
        Assert.Equal("Completed", failed.Status); Assert.Equal(7, failed.ExitCode); Assert.Equal("stdout",failed.Stdout); Assert.Equal("stderr",failed.Stderr);
        Assert.Equal("committed",failed.WorkspaceSyncStatus); Assert.Equal("durable", await f.Read("retained.txt"));
        var generation = (await f.Service.InspectAsync(f.Session.SessionId))!.Generation;
        f.Reopen();
        var retained = await f.Exec("cat retained.txt"); Assert.Equal("durable", retained.Stdout);
        Assert.Equal(generation, (await f.Service.InspectAsync(f.Session.SessionId))!.Generation);
        var access = f.Access("once"); var command = new SandboxCommandRequest("printf x >> once.txt");
        var first = await f.Service.ExecuteAsync(access, command);
        Assert.Equal(first, await f.Service.ExecuteAsync(access, command)); Assert.Equal("x", await f.Read("once.txt"));
        await Assert.ThrowsAsync<AgentCoreException>(async () => await f.Service.ExecuteAsync(access, command with { Command = "printf y >> once.txt" }));
        await Assert.ThrowsAsync<AgentCoreException>(async () => await f.Service.ExecuteAsync(f.Access() with { AgentInstanceId = Guid.NewGuid() }, new("true")));
        var background = await f.Service.ExecuteAsync(f.Access(), new("printf started > background.txt; sleep 120", Background: true));
        Assert.Equal("Running", background.Status);
        Assert.Equal("Running", (await f.Service.InspectProcessAsync(f.Session.SessionId, background.ExecutionId)).Status);
        await Assert.ThrowsAsync<AgentCoreException>(async () => await f.Service.InspectProcessAsync(Guid.NewGuid(), background.ExecutionId));
        await Assert.ThrowsAsync<AgentCoreException>(async () => await f.Exec("true"));
        var stopped = await f.Service.StopProcessAsync(f.Session.SessionId, background.ExecutionId);
        Assert.Equal("Canceled", stopped.Status); Assert.Equal("committed", stopped.WorkspaceSyncStatus);
        Assert.Equal("started", await f.Read("background.txt"));
        using var cancel = new CancellationTokenSource(); var cancelAccess = f.Access();
        var running = f.Service.ExecuteAsync(cancelAccess, new("printf partial > canceled.txt; sleep 120"), cancel.Token).AsTask();
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var receipt = await f.Store.GetOperationAsync(f.Session.SessionId, f.Run.AgentRunId, cancelAccess.ToolCallId);
            if (receipt?.ProviderCommandId is not null) break;
            await Task.Delay(50);
        }
        cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.Equal("NeedsResync", (await f.Service.InspectAsync(f.Session.SessionId))!.State);
        await Assert.ThrowsAsync<AgentCoreException>(async () => await f.Workspace.ReadAsync(f.Session.SessionId, f.Definition, "/workspace/working/canceled.txt"));
        await f.Service.StopSessionAsync(f.Session.SessionId);
        Assert.Equal(0, (await f.Exec("test ! -e canceled.txt && cat retained.txt")).ExitCode);
        Assert.NotEqual(generation, (await f.Service.InspectAsync(f.Session.SessionId))!.Generation);
    }

    [OpenSandboxFact]
    public async Task Real_sdk_rejects_links_and_untracked_children_without_corrupting_canonical_scratch()
    {
        await using var f = await Fixture.Create();
        Assert.Equal(0, (await f.Exec("printf safe > source.txt")).ExitCode);
        var before = (await f.Workspace.CaptureScratchAsync(f.Session.SessionId, f.Definition)).Fingerprint;
        var linked = await f.Exec("ln source.txt hardlink.txt");
        Assert.Equal("Indeterminate",linked.Status); Assert.Equal("notCommitted",linked.WorkspaceSyncStatus);
        Assert.Equal(before, (await f.Workspace.CaptureScratchAsync(f.Session.SessionId, f.Definition)).Fingerprint);
        await f.Service.StopSessionAsync(f.Session.SessionId);
        var detached = await f.Exec("sleep 120 >/tmp/detached.log 2>&1 &");
        Assert.Equal("Indeterminate", detached.Status); Assert.Equal("notCommitted", detached.WorkspaceSyncStatus);
        Assert.Equal(before, (await f.Workspace.CaptureScratchAsync(f.Session.SessionId, f.Definition)).Fingerprint);
    }

    [OpenSandboxFact]
    public async Task Real_workbench_fixes_python_tests_runs_node_and_creates_office_bytes()
    {
        await using var f = await Fixture.Create();
        Assert.Equal(0, (await f.Exec("python --version && node --version && tsc --version && libreoffice --version")).ExitCode);
        var failure = await f.Exec("printf 'def add(a,b): return a-b\n' > maths.py; printf 'from maths import add\ndef test_add(): assert add(2,3)==5\n' > test_maths.py; python -m pytest -q");
        Assert.True(failure.Status == "Completed", failure.Status + ": " + failure.Stderr);
        Assert.NotEqual(0, failure.ExitCode); Assert.Contains("failed",failure.Stdout);
        var fixedResult = await f.Exec("printf 'def add(a,b): return a+b\n' > maths.py; python -m pytest -q");
        Assert.Equal(0, fixedResult.ExitCode); Assert.Contains("passed",fixedResult.Stdout);
        Assert.Equal(0, (await f.Exec("mkdir node-project; printf '{\"scripts\":{\"test\":\"node --test\"}}' > node-project/package.json; printf 'const test=require(\"node:test\"),assert=require(\"node:assert/strict\");test(\"sum\",()=>assert.equal(2+3,5));' > node-project/sum.test.js; cd node-project && npm test")).ExitCode);
        const string office = "from openpyxl import Workbook,load_workbook; from docx import Document; w=Workbook(); w.active.append(['Total',5]); w.save('report.xlsx'); d=Document(); d.add_heading('Report',0); d.add_paragraph('Total: 5'); d.save('report.docx'); assert load_workbook('report.xlsx').active['B1'].value==5; assert Document('report.docx').paragraphs[1].text=='Total: 5'";
        Assert.Equal(0, (await f.Service.ExecuteAsync(f.Access(), new(null, ["python", "-I", "-c", office]))).ExitCode);
        foreach (var name in new[] { "report.xlsx", "report.docx" })
        {
            var bytes = (await f.Workspace.ReadAsync(f.Session.SessionId, f.Definition, "/workspace/working/" + name)).Bytes;
            Assert.True(bytes.Length > 1000); Assert.Equal(new byte[] { 80,75 }, bytes[..2]);
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "agent-core-osb-" + Guid.NewGuid().ToString("N"));
        public AgentDefinition Definition { get; private set; } = null!; public SessionSnapshot Session { get; private set; } = null!;
        public AgentRun Run { get; private set; } = null!; public OpenSandboxService Service { get; private set; } = null!;
        public SandboxProfile Profile => _profile;
        public SessionToolExecutor Executor(IArtifactStore? artifacts = null) => new(workspace:Workspace,artifacts:artifacts,profiles:_memory,emailRuns:_runs,sandboxService:Service,sandboxStore:Store);
        public FileSessionWorkspace Workspace { get; private set; } = null!; public ISandboxStore Store { get; private set; } = null!;
        private Factory _factory = null!; private SqliteMemoryStore _memory = null!; private SqliteAgentRunStore _runs = null!;
        private OpenSandboxOptions _options = null!; private SandboxProfile _profile = null!;
        private readonly ILoggerFactory _logs = LoggerFactory.Create(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Warning));
        public static async Task<Fixture> Create(string? initializationScript = null, int idleTtl = 300, int maxCommand = 120, int maxLifetime = 900)
        {
            var f = new Fixture(); Directory.CreateDirectory(f.Root);
            f._factory = new(new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite("Data Source=" + Path.Combine(f.Root,"core.db"))
                .AddInterceptors(new SqlitePragmaInterceptor(5000)).Options);
            f._memory = new(f._factory, TimeProvider.System); await f._memory.EnsureCreatedAsync();
            f._options = new() { Enabled = true, AllowLocalImageIds = true, Endpoint = Environment.GetEnvironmentVariable("AGENTCORE_OSB_PROOF_URL") ?? "http://127.0.0.1:18080",
                ApiKey = Environment.GetEnvironmentVariable("AGENTCORE_OSB_PROOF_KEY"), WorkbenchImage = Environment.GetEnvironmentVariable("AGENTCORE_OSB_PROOF_IMAGE") };
            f._profile = new("general-workbench",1,"Workbench","Test",true,f._options.WorkbenchImage!,InitializationScript:initializationScript,IdleTtlSeconds:idleTtl,MaxCommandSeconds:maxCommand,MaxLifetimeSeconds:maxLifetime);
            f.Definition = new(1,"examiner",1,new("Alex","Examiner","Practice","Calm"),["Practice"],"Instructions",new("acknowledgeThenContinue",true,true),new("concise",true,"en",256),new(false,8000,30000,1,["longSilence"]),new(false,"default",1),new("synthetic",null,null),new Dictionary<string,string>(), SandboxPolicy: new("general-workbench",["general-workbench"]));
            var now = DateTimeOffset.UtcNow; var owner = new AgentRunOwner(Guid.NewGuid(),Guid.NewGuid());
            f.Session = new(1,Guid.NewGuid(),1,f.Definition,SessionMode.Text,null,SessionStatus.Created,[],"",0,null,owner.ProfileId,now,now,owner.AgentInstanceId,PinnedPersona:f.Definition.Identity);
            await f._memory.SaveAsync(f.Session,0);
            f._runs = new(f._factory,f._memory,new SystemDiagnosticIdSource());
            var activation = new Activation(Guid.NewGuid(),f.Session.SessionId,ActivationKind.Initiative,[],Guid.NewGuid(),null,null,null,"sandbox-test",now);
            var run = AgentRun.Create(Guid.NewGuid(),owner,new(activation,f.Definition.Id,1,f.Definition.Identity,Guid.NewGuid(),AgentRunOutputContract.ConversationResponse),new("synthetic","synthetic","synthetic",null),3,now);
            await f._runs.AdmitAsync(f.Session with {Revision=2},1,run);
            f.Run = await f._runs.ApplyAsync(owner,run.AgentRunId,new AgentRunCommand.Claim(1,now,Guid.NewGuid(),now.AddMinutes(15)));
            f.Reopen(); await f.Store.SaveProfileAsync(f._profile,0); return f;
        }
        public void Reopen()
        {
            Store = new SqliteSandboxStore(_factory); Workspace = new(Path.Combine(Root,"workspaces"),Path.Combine(Root,"templates"),sessions:_memory);
            Service = new(_options,Store,Workspace,_memory,_runs,TimeProvider.System,logger:_logs.CreateLogger<OpenSandboxService>());
        }
        public SandboxAccess Access(string? call = null) => new(Session.SessionId,Session.AgentInstanceId,Run.AgentRunId,Run.Claim!.Generation,call ?? Guid.NewGuid().ToString("N"),_profile,Definition);
        public void SetInstanceSetup(string script,long revision) => _profile = _profile with {InstanceInitializationScript=script,InstanceRevision=revision};
        public async Task ReviseScript(string script) { var next = _profile with { Revision=_profile.Revision+1,InitializationScript=script }; await Store.SaveProfileAsync(next,_profile.Revision); _profile=next; }
        public async Task LimitStorage(long bytes) { var next=_profile with { Revision=_profile.Revision+1,StorageBytes=bytes };await Store.SaveProfileAsync(next,_profile.Revision);_profile=next; }
        public async ValueTask CancelRun()
        {
            var owner = new AgentRunOwner(Session.AgentInstanceId, Session.ProfileId!.Value);
            var current = (await _runs.GetAsync(owner, Run.AgentRunId))!;
            Run = await _runs.ApplyAsync(owner, Run.AgentRunId, new AgentRunCommand.RequestCancellation(current.Revision, DateTimeOffset.UtcNow, null));
        }
        public async Task Pause(string reason)
        {
            var current = (await _memory.LoadAsync(Session.SessionId))!;
            await _memory.SaveAsync(current with { Revision=current.Revision+1, LifecycleStatus=SessionLifecycleStatus.Paused, LifecycleReason=reason, Status=SessionStatus.Paused }, current.Revision);
        }
        public async Task Resume()
        {
            var current = (await _memory.LoadAsync(Session.SessionId))!;
            await _memory.SaveAsync(current with { Revision=current.Revision+1, LifecycleStatus=SessionLifecycleStatus.Active, LifecycleReason=null, Status=SessionStatus.Attached }, current.Revision);
        }
        public ValueTask<SandboxCommandResult> Exec(string command) => Service.ExecuteAsync(Access(),new(command));
        public async Task<string> Read(string name) => Encoding.UTF8.GetString((await Workspace.ReadAsync(Session.SessionId,Definition,"/workspace/working/"+name)).Bytes);
        public async ValueTask DisposeAsync()
        {
            var cleaned = false;
            try
            {
                // Create may fail before returning an ID. Use the production
                // generation-metadata reconciliation path; adopt/replay nothing.
                if (await Store.GetLeaseAsync(Session.SessionId) is { ProviderId: null, State: not SandboxLeaseState.Terminated })
                    await Service.ReconcileAsync();
                await Service.StopSessionAsync(Session.SessionId);
                cleaned = true;
            }
            finally
            {
                _logs.Dispose();
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                // A pending cleanup must retain the durable evidence for inspection.
                if (cleaned) Directory.Delete(Root, true);
            }
        }
    }
    private sealed class Factory(DbContextOptions<AgentCoreDbContext> options) : IDbContextFactory<AgentCoreDbContext> { public AgentCoreDbContext CreateDbContext() => new(options); }
}
