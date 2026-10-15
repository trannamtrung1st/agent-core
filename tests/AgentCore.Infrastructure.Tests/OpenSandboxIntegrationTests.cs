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
    public OpenSandboxFactAttribute(bool requiresOrdinaryImage = false, bool requiresAlternateImage = false)
    {
        if (Environment.GetEnvironmentVariable("AGENTCORE_OPENSANDBOX_TESTS") != "1") Skip = "Opt-in real local OpenSandbox/Docker acceptance.";
        else if (requiresOrdinaryImage && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AGENTCORE_OSB_ORDINARY_IMAGE")))
            Skip = "Set AGENTCORE_OSB_ORDINARY_IMAGE to an installed identity-compatible .NET SDK image for the focused compatibility regression.";
        else if (requiresAlternateImage && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AGENTCORE_OSB_ALTERNATE_IMAGE")))
            Skip = "Set AGENTCORE_OSB_ALTERNATE_IMAGE to a third installed identity-compatible Linux image.";
    }
}

public sealed class OpenSandboxIntegrationTests
{
    [OpenSandboxFact]
    public async Task Canonical_workspace_and_shell_share_cwd_and_explicit_shell_cwd_is_one_call_only()
    {
        await using var f = await Fixture.Create();
        await f.Home.StructureAsync(f.Session.AgentInstanceId, [new("mkdir", Path:"/home/project")]);
        var definition = f.Definition with { Environment = new(ToolAllowlist:[ToolCatalog.WorkspaceCwd, ToolCatalog.WorkspaceWrite, ToolCatalog.SandboxExec]),
            ExecutionSandboxProfile = f.Profile, ExecutionSandboxProfiles = [f.Profile] };
        var executor = f.Executor();
        var admission = new ToolExecutionAdmission(false, TriggerKind.UserTurn, AgentInstanceId:f.Session.AgentInstanceId,
            AgentRunId:f.Run.AgentRunId, OwnedSessionId:f.Session.SessionId, WorkspaceCwd:"/home");
        var changed = await executor.ExecuteAsync(definition, f.Session.SessionId, new("cwd", ToolCatalog.WorkspaceCwd,
            """{"operation":"set","path":"/home/project"}"""), 8192, admission:admission);
        Assert.Equal("/home/project", changed.WorkspaceCwd);
        admission = admission with { WorkspaceCwd = changed.WorkspaceCwd };
        async Task<string> Call(string name, string arguments)
        {
            var result = await executor.ExecuteAsync(definition, f.Session.SessionId, new(Guid.NewGuid().ToString("N"), name, arguments),8192,admission:admission);
            Assert.DoesNotContain("\"error\":\"",result.Text); return result.Text;
        }
        Assert.Contains("/home/project", await Call(ToolCatalog.SandboxExec,"""{"command":"pwd"}"""));
        Assert.Contains("/working", await Call(ToolCatalog.SandboxExec,"""{"command":"pwd","cwd":"/working"}"""));
        Assert.Contains("/home/project", await Call(ToolCatalog.SandboxExec,"""{"command":"pwd"}"""));
        await Call(ToolCatalog.WorkspaceWrite,"""{"path":"via-tool.txt","content":"same-directory"}""");
        Assert.Contains("same-directory", await Call(ToolCatalog.SandboxExec,"""{"command":"cat via-tool.txt"}"""));
        Assert.Equal("same-directory",Encoding.UTF8.GetString((await f.Home.ReadAsync(f.Session.AgentInstanceId,"/home/project/via-tool.txt")).Bytes));
    }

    [OpenSandboxFact]
    public async Task Canonical_home_is_shared_live_while_session_working_and_git_remain_private()
    {
        await using var f = await Fixture.Create();
        await f.Home.WriteFileAsync(f.Session.AgentInstanceId, "/home/shared.txt", "core"u8.ToArray());
        var sibling = await f.SiblingAccess();
        try
        {
            var first = await f.Exec("set -eu; test \"$(cat /home/shared.txt)\" = core; printf shell > /home/shared.txt; printf private > private.txt; mkdir project; cd project; git init -q; printf project > README.md; git -c user.name=Fixture -c user.email=fixture@example.invalid add README.md; git -c user.name=Fixture -c user.email=fixture@example.invalid commit -qm initial; git checkout -qb next; git clone -q . clone; test -f clone/README.md; cd /working; git init -q; git -c user.name=Fixture -c user.email=fixture@example.invalid add private.txt; git -c user.name=Fixture -c user.email=fixture@example.invalid commit -qm root; git checkout -qb root-proof; git clone -q . root-clone; test -f root-clone/private.txt");
            Assert.True(first.ExitCode == 0, first.Stdout + first.Stderr);
            Assert.Equal("shell", Encoding.UTF8.GetString((await f.Home.ReadAsync(f.Session.AgentInstanceId, "/home/shared.txt")).Bytes));
            var observed = await f.Service.ExecuteAsync(sibling, new("test ! -e /working/private.txt && cat /home/shared.txt; printf sibling > /home/from-sibling.txt"));
            Assert.Equal("shell", observed.Stdout); Assert.Equal(0, observed.ExitCode);
            await f.Home.WriteFileAsync(f.Session.AgentInstanceId, "/home/shared.txt", "tool-change"u8.ToArray());
            Assert.Equal("tool-change", (await f.Exec("cat /home/shared.txt")).Stdout);
            Assert.Equal("sibling", Encoding.UTF8.GetString((await f.Home.ReadAsync(f.Session.AgentInstanceId, "/home/from-sibling.txt")).Bytes));
            Assert.Equal("ref: refs/heads/next\n", await f.Read("project/.git/HEAD"));
            Assert.DoesNotContain(Directory.EnumerateFileSystemEntries(f.WorkspaceRoot), path => Path.GetFileName(path).Contains("staging", StringComparison.Ordinal));
        }
        finally { await f.Service.StopSessionAsync(sibling.SessionId); }
    }

    [OpenSandboxFact]
    public async Task Canonical_live_policy_reduction_retires_rw_processes_before_ro_or_disabled_reuse()
    {
        await using var f = await Fixture.Create();
        await f.Home.WriteFileAsync(f.Session.AgentInstanceId, "/home/retained.txt", "private-home"u8.ToArray());
        var background = await f.Service.ExecuteAsync(f.Access(), new("while :; do printf writer > /home/heartbeat; sleep 1; done", Background:true));
        Assert.Equal("Running", background.Status);
        await f.ChangeHome(HomeAccess.ReadOnly);
        Assert.Equal("EnforcementPending", (await f.Service.InspectAsync(f.Session.SessionId))!.State);
        await f.Service.ReconcileAsync();
        Assert.Equal("Terminated", (await f.Service.InspectAsync(f.Session.SessionId))!.State);
        var readOnly = await f.Exec("test \"$HOME\" = /home; cat /home/retained.txt; if printf denied > /home/denied 2>/dev/null; then exit 70; fi; printf allowed > /working/allowed");
        Assert.Equal(0, readOnly.ExitCode); Assert.Equal("private-home", readOnly.Stdout); Assert.NotEqual(background.Generation, readOnly.Generation);
        await f.ChangeHome(HomeAccess.Disabled); await f.Service.ReconcileAsync();
        var disabled = await f.Exec("test \"$HOME\" = /working; test ! -e /home/retained.txt; test ! -e /home/heartbeat; cat /working/allowed");
        Assert.Equal(0, disabled.ExitCode); Assert.Equal("allowed", disabled.Stdout); Assert.NotEqual(readOnly.Generation, disabled.Generation);
        Assert.Equal("private-home", Encoding.UTF8.GetString((await f.Home.ReadAsync(f.Session.AgentInstanceId, "/home/retained.txt")).Bytes));
    }

    [OpenSandboxFact]
    public async Task Canonical_background_writes_are_visible_before_exit_and_failed_writes_survive_reset()
    {
        await using var f = await Fixture.Create();
        var background = await f.Service.ExecuteAsync(f.Access(),new("printf started > /working/live.txt; sleep 120", Background:true));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (true)
        {
            try { Assert.Equal("started", await f.Read("live.txt")); break; }
            catch (AgentCoreException error) when (error.Code == "NotFound") { await Task.Delay(50, deadline.Token); }
        }
        await f.Workspace.WriteAsync(f.Session.SessionId, "/working/from-tool.txt", "concurrent"u8.ToArray());
        await f.Service.StopProcessAsync(f.Session.SessionId, background.ExecutionId);
        var failure = await f.Exec("cat from-tool.txt; printf partial > failed.txt; printf home-partial > /home/failed.txt; exit 7");
        Assert.Equal(7, failure.ExitCode); Assert.Equal("concurrent", failure.Stdout);
        await f.Service.StopSessionAsync(f.Session.SessionId); f.Reopen();
        Assert.Equal("partial", await f.Read("failed.txt"));
        Assert.Equal("home-partial", Encoding.UTF8.GetString((await f.Home.ReadAsync(f.Session.AgentInstanceId,"/home/failed.txt")).Bytes));
        Assert.Equal("started", await f.Read("live.txt"));
    }

    [OpenSandboxFact]
    public async Task Completed_inspection_failure_retires_owned_generation_and_retains_exact_visible_files()
    {
        await using var f = await Fixture.Create();
        f.Service.CompletedProcessInspection = _ => throw new IOException("Injected process inspection transport failure");
        var result = await f.Exec("printf recovered-exact > inspection.txt; exit 7");
        Assert.Equal("Completed",result.Status);Assert.Equal(7,result.ExitCode);Assert.True(result.FilesystemEffectsMayHaveOccurred);
        Assert.True(result.GenerationRecycled);Assert.True(result.RemoteTerminationConfirmed);Assert.True(result.TerminationConfirmed);
        Assert.Equal("recovered-exact",await f.Read("inspection.txt"));
        f.Service.CompletedProcessInspection = null;
        var fresh = await f.Exec("cat inspection.txt");
        Assert.Equal("recovered-exact",fresh.Stdout);Assert.NotEqual(result.Generation,fresh.Generation);
    }

    [OpenSandboxFact]
    public async Task Opaque_python_and_node_children_retire_generically_without_affecting_a_sibling_session()
    {
        await using var f = await Fixture.Create();
        var sibling = await f.SiblingAccess();
        try
        {
            var foreign = await f.Service.ExecuteAsync(sibling, new("printf foreign > sibling.txt"));
            foreach (var (command, file, expected) in new[] {
                ("python -c \"import subprocess,pathlib; pathlib.Path('python.txt').write_text('python-exact'); subprocess.Popen(['sleep','120'],start_new_session=True,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)\"", "python.txt", "python-exact"),
                ("node -e \"require('fs').writeFileSync('node.txt','node-exact');require('child_process').spawn('sleep',['120'],{detached:true,stdio:'ignore'}).unref()\"", "node.txt", "node-exact") })
            {
                var result = await f.Exec(command);
                Assert.Equal("Completed",result.Status);Assert.Equal(0,result.ExitCode);Assert.True(result.GenerationRecycled);Assert.Equal(expected,await f.Read(file));
                Assert.Equal(foreign.Generation,(await f.Service.InspectAsync(sibling.SessionId))!.Generation);
                Assert.Equal("Ready",(await f.Service.InspectAsync(sibling.SessionId))!.State);
            }
            var stillOwned = await f.Service.ExecuteAsync(sibling with { ToolCallId="after-other-retirements" },new("cat sibling.txt"));
            Assert.Equal("foreign",stillOwned.Stdout); Assert.Equal(foreign.Generation,stillOwned.Generation);
        }
        finally { await f.Service.StopSessionAsync(sibling.SessionId); }
    }

    [OpenSandboxFact]
    public async Task Generic_retirement_crash_boundaries_reconcile_processes_once_without_shell_replay()
    {
        foreach (var point in new[] { "commandCompleted", "retirementRequested", "remoteTerminated" })
        {
            await using var f = await Fixture.Create();
            var access = f.Access("crash-" + point);
            var command = new SandboxCommandRequest("printf x >> once.txt; sleep 120 >/dev/null 2>&1 &");
            f.Service.FinalizationCheckpoint = stage => { if (stage == point) throw new OpenSandboxService.FinalizationCrash(); };
            await Assert.ThrowsAsync<OpenSandboxService.FinalizationCrash>(() => f.Service.ExecuteAsync(access, command).AsTask());
            var pending = (await f.Store.GetOperationAsync(f.Session.SessionId, f.Run.AgentRunId, access.ToolCallId))!;
            Assert.Equal("Completed", pending.State.ToString()); Assert.Equal(0, pending.ExitCode);
            f.Reopen(); await f.Service.ReconcileAsync();
            var receipt = await f.Service.InspectProcessAsync(f.Session.SessionId, pending.Id);
            Assert.True(receipt.FilesystemEffectsMayHaveOccurred); Assert.True(receipt.GenerationRecycled); Assert.True(receipt.TerminationConfirmed);
            Assert.Equal("x", await f.Read("once.txt"));
            Assert.Equal(receipt.ExecutionId, (await f.Service.ExecuteAsync(access, command)).ExecutionId);
            Assert.Equal("x", await f.Read("once.txt"));
        }
    }

    [OpenSandboxFact]
    public async Task Generic_residual_processes_recycle_only_the_owned_generation_and_preserve_completed_files()
    {
        await using var f = await Fixture.Create();
        var normal = await f.Exec("printf initial > ordinary.txt");
        Assert.Equal("Completed", normal.Status); Assert.False(normal.GenerationRecycled);
        var reused = await f.Exec("cat ordinary.txt"); Assert.Equal(normal.Generation, reused.Generation);
        var selected = await f.AddProfile("selected-opaque", f.Profile.ImageReference);
        foreach (var child in new[] { "sleep 120 >/dev/null 2>&1 &", "setsid /bin/sh -c 'trap \"\" TERM; while :; do sleep 120; done' </dev/null >/dev/null 2>&1 &" })
        {
            var access = f.Access();
            var command = new SandboxCommandRequest("printf retained > recycled.txt; " + child + " exit 7", ProfileId:selected.Id);
            var completed = await f.Service.ExecuteAsync(access, command);
            Assert.Equal("Completed", completed.Status); Assert.Equal(7, completed.ExitCode);
            Assert.True(completed.FilesystemEffectsMayHaveOccurred); Assert.True(completed.GenerationRecycled); Assert.True(completed.RemoteTerminationConfirmed);
            Assert.True(completed.TerminationConfirmed); Assert.Equal("Terminated", (await f.Service.InspectAsync(f.Session.SessionId))!.State);
            Assert.Equal("retained", await f.Read("recycled.txt"));
            Assert.Equal(completed.ExecutionId, (await f.Service.ExecuteAsync(access, command)).ExecutionId);
            var fresh = await f.Exec("cat recycled.txt");
            Assert.Equal("retained", fresh.Stdout); Assert.NotEqual(completed.Generation, fresh.Generation);
            Assert.Equal(selected.Id, fresh.ProfileId);
        }
        var switched = await f.Service.ExecuteAsync(f.Access(),new("cat recycled.txt",ProfileId:f.Profile.Id));
        Assert.Equal(f.Profile.Id,switched.ProfileId);Assert.Equal("retained",switched.Stdout);
    }

    [OpenSandboxFact]
    public async Task Tool_timeout_defaults_to_the_explicit_or_retained_profile_and_explicit_limits_still_clamp()
    {
        await using var f = await Fixture.Create(maxCommand: 1);
        var alternate = await f.AddProfile("longer", f.Profile.ImageReference, maxCommandSeconds: 4);
        var definition = f.Definition with { Environment = new(ToolAllowlist: [ToolCatalog.SandboxExec]) };
        var admission = new ToolExecutionAdmission(false, TriggerKind.UserTurn, AgentInstanceId:f.Session.AgentInstanceId, AgentRunId:f.Run.AgentRunId, OwnedSessionId:f.Session.SessionId);
        foreach (var (arguments, seconds) in new[] {
            ("{\"command\":\"printf default\"}", 1),
            ("{\"command\":\"sleep 2; printf alternate\",\"profileId\":\"longer\"}", 4),
            ("{\"command\":\"sleep 2; printf retained\"}", 4),
            ("{\"command\":\"printf clamped\",\"timeoutSeconds\":180}", 4),
            ("{\"command\":\"printf explicit\",\"timeoutSeconds\":1}", 1) })
        {
            var call = Guid.NewGuid().ToString("N");
            var result = await f.Executor().ExecuteAsync(definition, f.Session.SessionId, new(call, ToolCatalog.SandboxExec, arguments), 8192, admission:admission);
            Assert.True(result.EffectObserved, result.Text);
            using var parsed = System.Text.Json.JsonDocument.Parse(result.Text);
            Assert.True(parsed.RootElement.TryGetProperty("exitCode", out var exit) && exit.GetInt32() == 0, result.Text);
            var receipt = (await f.Store.GetOperationAsync(f.Session.SessionId, f.Run.AgentRunId, call))!;
            Assert.InRange((receipt.DeadlineAt!.Value - receipt.StartedAt).TotalSeconds, seconds, seconds + .1);
            if (seconds == 4) Assert.Equal(alternate.Id, receipt.ProfileId);
            f.Reopen();
        }
    }

    [OpenSandboxFact]
    public async Task A_new_blocked_tool_call_reports_prior_evidence_without_claiming_an_effect()
    {
        await using var f = await Fixture.Create();
        var definition = f.Definition with { Environment = new(ToolAllowlist: [ToolCatalog.SandboxExec]), ExecutionSandboxProfile=f.Profile, ExecutionSandboxProfiles=[f.Profile] };
        var admission = new ToolExecutionAdmission(false, TriggerKind.UserTurn, AgentInstanceId:f.Session.AgentInstanceId, AgentRunId:f.Run.AgentRunId, OwnedSessionId:f.Session.SessionId);
        var access = f.Access("background-once");
        var command = new SandboxCommandRequest("sleep 120",Background:true);
        var active = await f.Service.ExecuteAsync(access,command);
        Assert.Equal("Running",active.Status);
        Assert.Equal(active,await f.Service.ExecuteAsync(access,command));
        var blocked=await f.Executor().ExecuteAsync(definition,f.Session.SessionId,new("blocked-effect",ToolCatalog.SandboxExec,"{\"command\":\"printf never\"}"),8192,admission:admission);
        Assert.True(blocked.RecoveryRequired,blocked.Text);Assert.False(blocked.EffectObserved,blocked.Text);
    }

    [OpenSandboxFact(requiresOrdinaryImage: true)]
    public async Task Ordinary_dotnet_build_and_run_without_server_flags_retain_each_command()
    {
        await using var f = await Fixture.Create();
        await f.UseImage(Environment.GetEnvironmentVariable("AGENTCORE_OSB_ORDINARY_IMAGE")!);
        var setup = await f.Exec("printf '<configuration><packageSources><clear /></packageSources></configuration>' > NuGet.Config; dotnet new console -o app --no-restore");
        Assert.True(setup.Status == "Completed", setup.Status + ": " + setup.Stderr);
        var build = await f.Exec("dotnet build app");
        Assert.True(build.Status == "Completed", build.Status + ": " + build.Stderr + "\n" + build.Stdout);
        Assert.Equal(0, build.ExitCode); Assert.True(build.FilesystemEffectsMayHaveOccurred);
        var run = await f.Exec("dotnet run --project app");
        Assert.True(run.Status == "Completed", run.Status + ": " + run.Stderr);
        Assert.Equal(0, run.ExitCode); Assert.Contains("Hello, World!", run.Stdout);
        Assert.Contains("Console.WriteLine", await f.Read("app/Program.cs"));

    }

    [OpenSandboxFact(requiresOrdinaryImage: true)]
    public async Task Interrupted_selection_termination_and_provisioning_intents_reconcile_without_replaying_commands()
    {
        foreach(var stage in new[]{"selection","termination","provisioning"})
        {
            await using var f=await Fixture.Create();
            var target=await f.AddProfile("dotnet",Environment.GetEnvironmentVariable("AGENTCORE_OSB_ORDINARY_IMAGE")!);
            Assert.Equal(0,(await f.Exec("printf once > transition.txt")).ExitCode);
            if(stage!="selection")await f.Service.StopSessionAsync(f.Session.SessionId);
            var original=(await f.Store.GetLeaseAsync(f.Session.SessionId))!;
            var interrupted=original with {Revision=original.Revision+1,TransitionProfileId=target.Id,TransitionProfileRevision=target.Revision,
                TransitionPhase=stage=="selection"?"termination":"provisioning",CleanupReason="profileSwitch",
                State=stage=="selection"?SandboxLeaseState.Terminating:stage=="termination"?SandboxLeaseState.Terminated:SandboxLeaseState.Provisioning};
            if(stage=="provisioning")interrupted=interrupted with {Generation=Guid.NewGuid(),Profile=target,ProviderId=null,Initialization=new("Pending",target.Id,target.Revision,null)};
            await f.Store.SaveLeaseAsync(interrupted,original.Revision);
            var denied=f.Access("must-not-dispatch-"+stage);
            if(stage!="termination")Assert.Equal("recovery_required",(await f.Service.ExecuteAsync(denied,new("printf duplicate >> transition.txt",ProfileId:target.Id))).Error);
            Assert.Null(await f.Store.GetOperationAsync(f.Session.SessionId,f.Run.AgentRunId,denied.ToolCallId));
            f.Reopen();await f.Service.ReconcileAsync();
            Assert.Equal("once",await f.Read("transition.txt"));
            Assert.Equal("Terminated",(await f.Service.InspectAsync(f.Session.SessionId))!.State);
            Assert.Equal(target.Id,(await f.Exec("cat transition.txt")).ProfileId);
            Assert.Equal("once",await f.Read("transition.txt"));
        }
    }

    [OpenSandboxFact]
    public async Task Sampled_canonical_storage_guard_terminates_excessive_writes_before_a_long_command_finishes()
    {
        await using var f = await Fixture.Create();
        await f.LimitStorage(32);
        var started=DateTimeOffset.UtcNow;
        var result=await f.Exec("dd if=/dev/zero of=oversize bs=4096 count=1 2>/dev/null; sleep 120");
        Assert.Equal("storageLimit",result.FailureCategory);Assert.True(result.TerminationConfirmed);
        Assert.True(result.FilesystemEffectsMayHaveOccurred);Assert.InRange((DateTimeOffset.UtcNow-started).TotalSeconds,0,30);
        Assert.True(new FileInfo(Path.Combine(await f.Workspace.PhysicalWorkingDirectoryAsync(f.Session.SessionId),"oversize")).Length > 32);
        Assert.Equal("Terminated",(await f.Service.InspectAsync(f.Session.SessionId))!.State);
    }

    [OpenSandboxFact(requiresOrdinaryImage: true, requiresAlternateImage:true)]
    public async Task Authorized_profiles_switch_once_preserve_files_and_remain_session_scoped_after_reopen()
    {
        await using var f = await Fixture.Create();
        var dotnet = await f.AddProfile("dotnet", Environment.GetEnvironmentVariable("AGENTCORE_OSB_ORDINARY_IMAGE")!);
        var minimal = await f.AddProfile("minimal", Environment.GetEnvironmentVariable("AGENTCORE_OSB_ALTERNATE_IMAGE")!);
        Assert.Equal(0, (await f.Exec("printf exact > switch.txt")).ExitCode);
        var initial = (await f.Service.InspectAsync(f.Session.SessionId))!;
        foreach (var id in new[] { dotnet.Id, minimal.Id, f.Profile.Id })
        {
            var access = f.Access("switch-" + id);
            var command = new SandboxCommandRequest("test $(cat switch.txt) = exact; printf x >> once.txt", ProfileId: id);
            var result = await f.Service.ExecuteAsync(access, command);
            Assert.Equal("Completed", result.Status); Assert.Equal(0, result.ExitCode);
            Assert.Equal(id, result.ProfileId); Assert.True(result.FilesystemEffectsMayHaveOccurred);
            Assert.Equal(result, await f.Service.ExecuteAsync(access, command));
            Assert.Equal("exact", await f.Read("switch.txt"));
        }
        Assert.Equal("xxx", await f.Read("once.txt"));
        var activeCommand=f.Exec("sleep 2").AsTask();
        for(var attempt=0;attempt<200 && (await f.Service.InspectAsync(f.Session.SessionId))?.ActiveExecutionId is null;attempt++)await Task.Delay(10);
        var whileActive=DateTimeOffset.UtcNow;
        var noQueue=await f.Service.ExecuteAsync(f.Access(),new("printf should-not-run",ProfileId:dotnet.Id));
        Assert.Equal("recovery_required",noQueue.Error);Assert.True((DateTimeOffset.UtcNow-whileActive).TotalSeconds<1);
        Assert.Equal("Completed",(await activeCommand).Status);
        Assert.Equal(f.Profile.Id,(await f.Service.InspectAsync(f.Session.SessionId))!.ProfileId);
        Assert.NotEqual(initial.Generation, (await f.Service.InspectAsync(f.Session.SessionId))!.Generation);
        Assert.Equal(0, (await f.Service.ExecuteAsync(f.Access(), new("dotnet --version", ProfileId: dotnet.Id))).ExitCode);
        f.Reopen();
        var retained = await f.Exec("dotnet --version"); Assert.Equal(dotnet.Id, retained.ProfileId); Assert.StartsWith("10.", retained.Stdout);
        var sibling = await f.SiblingAccess();
        var independent = await f.Service.ExecuteAsync(sibling, new("printf sibling > independent.txt", ProfileId: minimal.Id));
        Assert.Equal(minimal.Id, independent.ProfileId);
        Assert.Equal(dotnet.Id, (await f.Service.InspectAsync(f.Session.SessionId))!.ProfileId);
        Assert.Equal(minimal.Id, (await f.Service.InspectAsync(sibling.SessionId))!.ProfileId);
        await f.Service.StopSessionAsync(sibling.SessionId);
        var active = (await f.Service.InspectAsync(f.Session.SessionId))!;
        await Assert.ThrowsAsync<AgentCoreException>(() => f.Service.ExecuteAsync(f.Access(), new("true", ProfileId: "forbidden")).AsTask());
        Assert.Equal(active.Generation, (await f.Service.InspectAsync(f.Session.SessionId))!.Generation);
        await f.Service.StopSessionAsync(f.Session.SessionId, expectedGeneration: active.Generation);
        Assert.Equal(f.Profile.Id, (await f.Exec("cat switch.txt")).ProfileId); // Explicit reset returns to default.
    }

    [OpenSandboxFact(requiresOrdinaryImage: true)]
    public async Task Unavailable_default_does_not_revoke_an_independently_verified_alternate_and_blocked_generation_cannot_be_bypassed()
    {
        await using var f = await Fixture.Create();
        var alternate = await f.AddProfile("dotnet", Environment.GetEnvironmentVariable("AGENTCORE_OSB_ORDINARY_IMAGE")!);
        await f.Store.SaveProfileAsync(f.Profile with { Revision = 2, Enabled = false }, 1);
        Assert.Equal(0, (await f.Service.ExecuteAsync(f.Access(), new("dotnet --version", ProfileId: alternate.Id))).ExitCode);
        var background = await f.Service.ExecuteAsync(f.Access(), new("sleep 120", Background: true));
        var blocked = await f.Service.ExecuteAsync(f.Access(), new("true", ProfileId: f.Profile.Id));
        Assert.Equal("recovery_required", blocked.Error); Assert.Equal(background.ExecutionId, blocked.ExecutionId);
        Assert.Equal(alternate.Id, (await f.Service.InspectAsync(f.Session.SessionId))!.ProfileId);
        var lease = (await f.Store.GetLeaseAsync(f.Session.SessionId))!;
        await f.Store.SaveLeaseAsync(lease with { Revision = lease.Revision + 1, State = SandboxLeaseState.NeedsReconciliation }, lease.Revision);
        Assert.Equal("recovery_required", (await f.Service.ExecuteAsync(f.Access(), new("true", ProfileId: alternate.Id))).Error);
    }

    [OpenSandboxFact]
    public async Task Confirmed_foreground_cancellation_retains_receipt_and_allows_a_new_independent_command_without_replay()
    {
        await using var f = await Fixture.Create();
        Assert.Equal(0, (await f.Exec("printf safe > retained.txt")).ExitCode);
        using var canceled = new CancellationTokenSource();
        var access = f.Access("cancelled-once");
        var pending = f.Service.ExecuteAsync(access, new("printf unsafe > retained.txt; sleep 120"), canceled.Token).AsTask();
        using var written = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (await f.Read("retained.txt") != "unsafe") await Task.Delay(50, written.Token);
        canceled.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        var environment = (await f.Service.InspectAsync(f.Session.SessionId))!;
        Assert.Equal("Terminated", environment.State); Assert.Null(environment.ActiveExecutionId);
        var receipt = await f.Service.InspectProcessAsync(f.Session.SessionId, environment.LastExecutionId!.Value);
        Assert.Equal("Canceled", receipt.Status); Assert.True(receipt.FilesystemEffectsMayHaveOccurred);
        Assert.Equal("cancellation", receipt.FailureCategory); Assert.True(receipt.TerminationConfirmed);
        f.Reopen(); await f.Service.ReconcileAsync();
        var fresh = await f.Exec("cat retained.txt"); Assert.Equal("unsafe", fresh.Stdout);
        Assert.NotEqual(environment.Generation, fresh.Generation);
        Assert.Equal(receipt, await f.Service.ExecuteAsync(access, new("printf unsafe > retained.txt; sleep 120")));
    }

    [OpenSandboxFact(requiresOrdinaryImage: true)]
    public async Task Ordinary_dotnet_image_verifies_without_python_and_commands_modify_canonical_files()
    {
        await using var f = await Fixture.Create("printf compatible");
        var ordinary = f.Profile with { ImageReference = Environment.GetEnvironmentVariable("AGENTCORE_OSB_ORDINARY_IMAGE")! };
        foreach (var imageOnly in new[] { true, false })
        {
            var checkedImage = await f.Service.VerifyProfileAsync(ordinary, CancellationToken.None, imageOnly);
            Assert.True(checkedImage.Passed, checkedImage.Message);
            Assert.Equal(imageOnly ? "" : "compatible", checkedImage.Stdout);
        }
        await f.UseImage(ordinary.ImageReference);
        var executed = await f.Exec("! command -v python && dotnet --version && printf normal-image > result.txt");
        Assert.Equal("Completed", executed.Status); Assert.Equal(0, executed.ExitCode);
        Assert.StartsWith("10.", executed.Stdout); Assert.Equal("normal-image", await f.Read("result.txt"));
        var repeated = await f.Exec("cat result.txt && printf changed > result.txt");
        Assert.Equal(0, repeated.ExitCode); Assert.Equal("normal-image", repeated.Stdout);
        Assert.Equal("changed", await f.Read("result.txt"));
        var compiled = await f.Exec("export DOTNET_CLI_USE_MSBUILD_SERVER=0 MSBUILDDISABLENODEREUSE=1; mkdir -p offline-feed; dotnet new console -o app --no-restore && dotnet restore app --source /working/offline-feed -p:UseSharedCompilation=false -nodeReuse:false && dotnet run --project app --no-restore -p:UseSharedCompilation=false");
        Assert.True(compiled.Status == "Completed", $"{compiled.Status}/{compiled.FailureCategory}/{compiled.Phase}: {compiled.Stderr}"); Assert.Equal(0, compiled.ExitCode);
        Assert.Contains("Hello, World!", compiled.Stdout); Assert.Contains("Console.WriteLine", await f.Read("app/Program.cs"));
        var workbench = await f.Service.VerifyProfileAsync(f.Profile with { ImageReference = Environment.GetEnvironmentVariable("AGENTCORE_OSB_PROOF_IMAGE")! }, CancellationToken.None);
        Assert.True(workbench.Passed, workbench.Message);
    }

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
    public async Task Instance_setup_runs_after_profile_once_with_observable_bounded_output_and_visible_partial_writes_on_failure()
    {
        await using var f = await Fixture.Create("printf Profile; printf ProfileErr >&2; printf P > setup-order.txt");
        f.SetInstanceSetup("test $(cat setup-order.txt) = P; printf Instance; printf InstanceErr >&2; printf I >> setup-order.txt",7);
        Assert.Equal(0,(await f.Exec("cat setup-order.txt")).ExitCode);
        var first=(await f.Service.InspectAsync(f.Session.SessionId))!;
        Assert.Equal("Succeeded",first.Initialization!.State);Assert.Equal("ProfileInstance",first.Initialization.Stdout);
        Assert.Equal("ProfileErrInstanceErr",first.Initialization.Stderr);Assert.Equal(7,first.Initialization.InstanceRevision);Assert.Equal("PI",await f.Read("setup-order.txt"));
        var pinnedAccess = f.Access();
        f.Reopen();f.SetInstanceSetup("printf changed",8);Assert.Equal(0,(await f.Service.ExecuteAsync(pinnedAccess,new("true"))).ExitCode);
        Assert.Equal(first.Generation,(await f.Service.InspectAsync(f.Session.SessionId))!.Generation);
        Assert.Equal(7,(await f.Service.InspectAsync(f.Session.SessionId))!.Initialization!.InstanceRevision);Assert.Equal("PI",await f.Read("setup-order.txt"));
        var verified=await f.Service.VerifyProfileAsync(f.Profile,CancellationToken.None);Assert.True(verified.Passed,verified.Message);Assert.Contains("changed",verified.Stdout);
        var image=await f.Service.VerifyProfileAsync(f.Profile with {InstanceInitializationScript="exit 7"},CancellationToken.None,imageOnly:true);
        Assert.True(image.Passed);Assert.Empty(image.Stdout);
        await f.Service.StopSessionAsync(f.Session.SessionId,expectedGeneration:first.Generation);
        f.SetInstanceSetup("printf broken > partial.txt; echo failed >&2; exit 7",9);

        Assert.Equal("recovery_required",(await f.Exec("true")).Error);
        Assert.Equal("broken",await f.Read("partial.txt"));
        var failed=(await f.Service.InspectAsync(f.Session.SessionId))!;Assert.Equal("Failed",failed.Initialization!.State);Assert.Equal(7,failed.Initialization.ExitCode);Assert.Contains("failed",failed.Initialization.Stderr);
        Assert.Equal("recovery_required",(await f.Exec("true")).Error);
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
                Assert.True(lease.ExpiresAt > DateTimeOffset.UtcNow.AddSeconds(90));
                await Task.Delay(68000);
                await f.Service.ReconcileAsync();
                result = await f.Service.InspectProcessAsync(f.Session.SessionId, result.ExecutionId);
            }
            Assert.Equal("Completed", result.Status);
            Assert.Equal(0, result.ExitCode);
            Assert.True(result.FilesystemEffectsMayHaveOccurred);
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
        Assert.False(File.Exists(Path.Combine(await f.Workspace.PhysicalWorkingDirectoryAsync(f.Session.SessionId),"forbidden.txt")));
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
        Assert.True(invalid.Passed, invalid.Message);
        var privateOutput = await f.Service.VerifyProfileAsync(f.Profile with { InitializationScript = "printf secret-sentinel >&2; exit 9" }, CancellationToken.None);
        Assert.False(privateOutput.Passed); Assert.DoesNotContain("secret-sentinel", privateOutput.Message);
        Assert.Null(await f.Store.GetLeaseAsync(f.Session.SessionId));
    }

    [OpenSandboxFact]
    public async Task Real_sdk_deadline_quarantines_the_generation_while_retaining_partial_files()
    {
        await using var f = await Fixture.Create();
        Assert.Equal(0,(await f.Exec("printf safe > retained.txt")).ExitCode);

        var result=await f.Service.ExecuteAsync(f.Access(),new("printf partial > retained.txt; sleep 5",TimeoutSeconds:1));
        Assert.Equal("Indeterminate",result.Status);Assert.True(result.FilesystemEffectsMayHaveOccurred);
        Assert.Equal("partial",await f.Read("retained.txt"));
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
    public async Task Retired_tool_never_dispatches_and_modern_arguments_remain_strict()
    {
        await using var f = await Fixture.Create();
        var executor = f.Executor();
        var definition = f.Definition with { Environment = new(ToolAllowlist:["sandbox.run",ToolCatalog.SandboxExec]), ExecutionSandboxProfile=f.Profile };
        var admission = new ToolExecutionAdmission(false, TriggerKind.UserTurn, AgentInstanceId:f.Session.AgentInstanceId, AgentRunId:f.Run.AgentRunId, OwnedSessionId:f.Session.SessionId);
        var legacy = await executor.ExecuteAsync(definition,f.Session.SessionId,new("legacy-once","sandbox.run","""{"verb":"echo","arguments":["legacy-real"]}"""),8192,admission:admission);
        Assert.DoesNotContain("legacy-real",legacy.Text); Assert.DoesNotContain("\"ok\":true",legacy.Text);
        Assert.Null(await f.Store.GetLeaseAsync(f.Session.SessionId));
        foreach (var arguments in new[] { """{"command":"true","timeoutSeconds":"120"}""", """{"command":"true","background":"false"}""", """{"command":123}""" })
            Assert.Contains("invalid",(await executor.ExecuteAsync(definition,f.Session.SessionId,new(Guid.NewGuid().ToString(),ToolCatalog.SandboxExec,arguments),8192,admission:admission)).Text);
        var linked = await executor.ExecuteAsync(definition,f.Session.SessionId,new("link-clipped",ToolCatalog.SandboxExec,"""{"command":"ln -s /etc/passwd link"}"""),64,admission:admission);
        Assert.False(linked.OutcomeUnknown); Assert.InRange(Encoding.UTF8.GetByteCount(linked.Text),0,64);
        await Assert.ThrowsAsync<AgentCoreException>(() => f.Workspace.ReadAsync(f.Session.SessionId, f.Definition, "/working/link").AsTask());
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
    public async Task Real_initialization_is_generation_pinned_and_expiration_reuses_canonical_files_without_replaying_commands()
    {
        await using var f = await Fixture.Create("printf I >> init-count.txt");
        Assert.Equal(0,(await f.Exec("true")).ExitCode); Assert.Equal("I",await f.Read("init-count.txt"));
        var first = (await f.Store.GetLeaseAsync(f.Session.SessionId))!;
        f.Reopen(); Assert.Equal(0,(await f.Exec("true")).ExitCode); Assert.Equal("I",await f.Read("init-count.txt"));
        var pinnedBeforeChange=f.Access();
        await f.ReviseScript("printf N >> init-count.txt");
        Assert.Equal(0,(await f.Service.ExecuteAsync(pinnedBeforeChange,new("true"))).ExitCode); Assert.Equal("I",await f.Read("init-count.txt"));
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
        Assert.True(failed.FilesystemEffectsMayHaveOccurred); Assert.Equal("durable", await f.Read("retained.txt"));
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
        Assert.Equal("recovery_required", (await f.Exec("true")).Error);
        var stopped = await f.Service.StopProcessAsync(f.Session.SessionId, background.ExecutionId);
        Assert.Equal("Canceled", stopped.Status); Assert.True(stopped.FilesystemEffectsMayHaveOccurred);
        Assert.Equal("started", await f.Read("background.txt"));
        using var cancel = new CancellationTokenSource(); var cancelAccess = f.Access();
        var running = f.Service.ExecuteAsync(cancelAccess, new("printf partial > canceled.txt; sleep 120"), cancel.Token).AsTask();
        using var written = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (true)
        {
            try { if (await f.Read("canceled.txt") == "partial") break; }
            catch (AgentCoreException error) when (error.Code == "NotFound") { }
            await Task.Delay(50, written.Token);
        }
        cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.Equal("Terminated", (await f.Service.InspectAsync(f.Session.SessionId))!.State);
        Assert.Equal("partial", await f.Read("canceled.txt"));
        await f.Service.StopSessionAsync(f.Session.SessionId);
        Assert.Equal(0, (await f.Exec("test $(cat canceled.txt) = partial && cat retained.txt")).ExitCode);
        Assert.NotEqual(generation, (await f.Service.InspectAsync(f.Session.SessionId))!.Generation);
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
            var bytes = (await f.Workspace.ReadAsync(f.Session.SessionId, f.Definition, "/working/" + name)).Bytes;
            Assert.True(bytes.Length > 1000); Assert.Equal(new byte[] { 80,75 }, bytes[..2]);
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "agent-core-osb-" + Guid.NewGuid().ToString("N"));
        public AgentDefinition Definition { get; private set; } = null!; public SessionSnapshot Session { get; private set; } = null!;
        public AgentRun Run { get; private set; } = null!; public OpenSandboxService Service { get; private set; } = null!;
        public SandboxProfile Profile => _profile;
        public SessionToolExecutor Executor(IArtifactStore? artifacts = null) => new(workspace:Workspace,artifacts:artifacts,profiles:_memory,emailRuns:_runs,sandboxService:Service,sandboxStore:Store,
            agentWorkspace:new AgentCore.Application.Workspaces.AgentInstanceWorkspaceService(Instances,Home,_memory,Workspace,new AgentCore.Application.Admin.AdminLifecycleCoordinator()));
        public FileSessionWorkspace Workspace { get; private set; } = null!; public ISandboxStore Store { get; private set; } = null!;
        private Factory _factory = null!; private SqliteMemoryStore _memory = null!; private SqliteAgentRunStore _runs = null!;
        public SqliteAgentInstanceStore Instances { get; private set; } = null!;
        public FileAgentInstanceWorkspaceStore Home { get; private set; } = null!;
        public string WorkspaceRoot { get; private set; } = "";
        private OpenSandboxOptions _options = null!; private SandboxProfile _profile = null!;
        private readonly ILoggerFactory _logs = LoggerFactory.Create(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Warning));
        public static async Task<Fixture> Create(string? initializationScript = null, int idleTtl = 300, int maxCommand = 120, int maxLifetime = 900)
        {
            var f = new Fixture(); Directory.CreateDirectory(f.Root);
            f._factory = new(new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite("Data Source=" + Path.Combine(f.Root,"core.db"))
                .AddInterceptors(new SqlitePragmaInterceptor(5000)).Options);
            f._memory = new(f._factory, TimeProvider.System); await f._memory.EnsureCreatedAsync();
            f._options = new() { Enabled = true, AllowLocalImageIds = true, Endpoint = Environment.GetEnvironmentVariable("AGENTCORE_OSB_PROOF_URL") ?? "http://127.0.0.1:18080",
                ApiKey = Environment.GetEnvironmentVariable("AGENTCORE_OSB_PROOF_KEY"), CanonicalWorkspaceRoot = f.WorkspaceRoot = Path.Combine(Environment.GetEnvironmentVariable("AGENTCORE_OSB_PROOF_MOUNT_ROOT") ?? Path.Combine(f.Root,"mounts"), Guid.NewGuid().ToString("N")) };
            f._options.WorkspaceDockerHostRoot = f.WorkspaceRoot;
            f.Instances = new(f._factory,new SystemIdGenerator(TimeProvider.System));
            f._profile = new("general-workbench",1,"Workbench","Test",true,Environment.GetEnvironmentVariable("AGENTCORE_OSB_PROOF_IMAGE")!,InitializationScript:initializationScript,IdleTtlSeconds:idleTtl,MaxCommandSeconds:maxCommand,MaxLifetimeSeconds:maxLifetime);
            f.Definition = new(2,"examiner",1,new("Alex","Examiner","Practice","Calm"),["Practice"],"Instructions",new("acknowledgeThenContinue",true,true),new("concise",true,"en",256),new(false,8000,30000,1,["longSilence"]),new(false,"default",1),new("synthetic",null,null),new Dictionary<string,string>(), SandboxPolicy: new("general-workbench",["general-workbench"]));
            var now = DateTimeOffset.UtcNow; var owner = new AgentRunOwner(Guid.NewGuid(),Guid.NewGuid());
            await f.Instances.InsertAsync(new(owner.AgentInstanceId,f.Definition.Id,f.Definition.Version,f.Definition.Identity,AgentInstanceLifecycle.Active,now,now));
            f.Session = new(1,Guid.NewGuid(),1,f.Definition,SessionMode.Text,null,SessionStatus.Created,[],"",0,null,owner.ProfileId,now,now,owner.AgentInstanceId,PinnedPersona:f.Definition.Identity);
            await f._memory.SaveAsync(f.Session,0);
            f._runs = new(f._factory,f._memory,new SystemDiagnosticIdSource());
            var activation = new Activation(Guid.NewGuid(),f.Session.SessionId,ActivationKind.Initiative,[],Guid.NewGuid(),null,null,null,"sandbox-test",now);
            var run = AgentRun.Create(Guid.NewGuid(),owner,new(activation,f.Definition.Id,1,f.Definition.Identity,Guid.NewGuid(),AgentRunOutputContract.ConversationResponse),new("synthetic","synthetic","synthetic",null),3,now);
            await f._runs.AdmitAsync(f.Session with {Revision=2},1,run);
            f.Run = await f._runs.ApplyAsync(owner,run.AgentRunId,new AgentRunCommand.Claim(1,now,Guid.NewGuid(),now.AddMinutes(15)));
            f.Reopen(); await f.Store.SaveProfileAsync(f._profile,0);
            await f.Store.SaveVerificationAsync(new(f._profile.Id,f._profile.Revision,DateTimeOffset.UtcNow,true,"Lifecycle fixture admission",ContractVersion:SandboxProfileVerification.CurrentContractVersion)); return f;
        }
        public void Reopen()
        {
            Store = new SqliteSandboxStore(_factory); Workspace = new(WorkspaceRoot,Path.Combine(Root,"templates"),sessions:_memory,instances:Instances,sandboxes:Store);
            Home = new(WorkspaceRoot,Instances,Store);
            Service = new(_options,Store,Workspace,_memory,_runs,TimeProvider.System,instances:Instances,logger:_logs.CreateLogger<OpenSandboxService>());
        }
        public SandboxAccess Access(string? call = null) => new(Session.SessionId,Session.AgentInstanceId,Run.AgentRunId,Run.Claim!.Generation,call ?? Guid.NewGuid().ToString("N"),_profile,Definition);
        public async Task<SandboxProfile> AddProfile(string id, string image, int? maxCommandSeconds = null)
        {
            var profile = _profile with { Id=id, Name=id, ImageReference=image, InitializationScript=null, MaxCommandSeconds=maxCommandSeconds??_profile.MaxCommandSeconds };
            await Store.SaveProfileAsync(profile, 0);
            var verification = await Service.VerifyProfileAsync(profile, default);
            Assert.True(verification.Passed, verification.Message); await Store.SaveVerificationAsync(verification);
            var all = (Definition.ExecutionSandboxProfiles ?? [_profile]).Append(profile).ToArray();
            Definition = Definition with { SandboxPolicy = new(_profile.Id, all.Select(p=>p.Id).ToArray()), ExecutionSandboxProfiles = all, ExecutionSandboxProfile = _profile };
            return profile;
        }
        public async Task<SandboxAccess> SiblingAccess()
        {
            var now=DateTimeOffset.UtcNow;
            var sibling = Session with { SessionId=Guid.NewGuid(), Revision=1, CreatedAt=now, UpdatedAt=now };
            await _memory.SaveAsync(sibling,0);
            var owner=new AgentRunOwner(Session.AgentInstanceId,Session.ProfileId!.Value);
            var activation=new Activation(Guid.NewGuid(),sibling.SessionId,ActivationKind.Initiative,[],Guid.NewGuid(),null,null,null,"sandbox-test",now);
            var run=AgentRun.Create(Guid.NewGuid(),owner,new(activation,Definition.Id,1,Definition.Identity,Guid.NewGuid(),AgentRunOutputContract.ConversationResponse),new("synthetic","synthetic","synthetic",null),3,now);
            await _runs.AdmitAsync(sibling with {Revision=2},1,run);
            run=await _runs.ApplyAsync(owner,run.AgentRunId,new AgentRunCommand.Claim(1,now,Guid.NewGuid(),now.AddMinutes(15)));
            return new(sibling.SessionId,sibling.AgentInstanceId,run.AgentRunId,run.Claim!.Generation,Guid.NewGuid().ToString("N"),_profile,Definition);
        }
        public async Task UseImage(string image)
        {
            var next = _profile with { ImageReference = image, Revision = _profile.Revision + 1 };
            await Store.SaveProfileAsync(next, _profile.Revision); _profile = next;
            var verified = await Service.VerifyProfileAsync(next, CancellationToken.None);
            Assert.True(verified.Passed, verified.Message); await Store.SaveVerificationAsync(verified);
        }
        public void SetInstanceSetup(string script,long revision) => _profile = _profile with {InstanceInitializationScript=script,InstanceRevision=revision};
        public async Task ReviseScript(string script) { var next = _profile with { Revision=_profile.Revision+1,InitializationScript=script }; await Store.SaveProfileAsync(next,_profile.Revision); _profile=next; await Store.SaveVerificationAsync(new(next.Id,next.Revision,DateTimeOffset.UtcNow,true,"Lifecycle fixture admission",ContractVersion:SandboxProfileVerification.CurrentContractVersion)); }
        public async Task LimitStorage(long bytes) { var next=_profile with { Revision=_profile.Revision+1,StorageBytes=bytes };await Store.SaveProfileAsync(next,_profile.Revision);_profile=next;await Store.SaveVerificationAsync(new(next.Id,next.Revision,DateTimeOffset.UtcNow,true,"Lifecycle fixture admission",ContractVersion:SandboxProfileVerification.CurrentContractVersion)); }
        public async ValueTask CancelRun()
        {
            var owner = new AgentRunOwner(Session.AgentInstanceId, Session.ProfileId!.Value);
            var current = (await _runs.GetAsync(owner, Run.AgentRunId))!;
            Run = await _runs.ApplyAsync(owner, Run.AgentRunId, new AgentRunCommand.RequestCancellation(current.Revision, DateTimeOffset.UtcNow, null));
        }
        public async Task ChangeHome(HomeAccess access)
        {
            var owner = (await Instances.FindAsync(Session.AgentInstanceId))!;
            await Instances.UpdateWithExpectedRevisionAsync(new(owner.InstanceId, owner.Revision, SetSettingsOverrides:true,
                SettingsOverrides:(owner.SettingsOverrides ?? new()) with { HomeAccess = new(access.ToString()) }), DateTimeOffset.UtcNow);
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
        public async ValueTask<SandboxCommandResult> Exec(string command)
        {
            try { return await Service.ExecuteAsync(Access(),new(command)); }
            catch (AgentCoreException error) when (error.Code == "SessionPersistenceUnavailable")
            {
                var environment = await Service.InspectAsync(Session.SessionId);
                throw new InvalidOperationException($"{error.Code}: {environment?.FailurePhase}: {environment?.Message}", error);
            }
        }
        public async Task<string> Read(string name) => Encoding.UTF8.GetString((await Workspace.ReadAsync(Session.SessionId,Definition,"/working/"+name)).Bytes);
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
                if (cleaned) { Directory.Delete(Root, true); if(Directory.Exists(WorkspaceRoot))Directory.Delete(WorkspaceRoot,true); }
            }
        }
    }
    private sealed class Factory(DbContextOptions<AgentCoreDbContext> options) : IDbContextFactory<AgentCoreDbContext> { public AgentCoreDbContext CreateDbContext() => new(options); }
}
