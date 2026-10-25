using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Infrastructure.Sandbox;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantDefinitionIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Membership_removal_waits_for_exact_Instance_mount_retirement_without_partial_changes(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var s = await PrepareRuntimeSandbox(f);
        var storage = (ITenantSandboxRuntimeStore)f.Iam; var spaces = (ITenantSpaceStore)f.Iam;
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision;
        var directory = new TenantSpaceService(f.Iam, spaces, (ITenantAgentStore)f.Iam, f.Authorizer, s.Resolver,
            (ITenantCredentialBindingStore)f.Iam, (ITenantCredentialStore)f.Iam, f.Ids, TimeProvider.System);
        var shared = await directory.CreateAsync(s.Human.PrincipalId, s.Tenant, f.Ids.NewId(), "Mounted membership", "", "", await Revision());
        var member = await directory.SetMemberAsync(s.Human.PrincipalId, s.Tenant, shared.SpaceId, s.Session.AgentInstanceId,
            "Reviewer", "Keep until retirement", [], [], await Revision(), shared.Revision, 0);
        shared = (await spaces.FindSpaceAsync(s.Tenant, shared.SpaceId))!;
        // Unknown mount provenance cannot prove that this root is absent. Reopening
        // the store cannot treat a provisioning/uncertain generation as retired.
        var write = s.Write with { SecurityRevision = await Revision() };
        Assert.True(await storage.TryReserveTenantSandboxLeaseAsync(write, s.Lease, 0, DateTimeOffset.UtcNow));
        var before = await Revision();
        async Task Remove() => await directory.SetMemberAsync(s.Human.PrincipalId, s.Tenant, shared.SpaceId, s.Session.AgentInstanceId,
            "", "", [], [], await Revision(), shared.Revision, member.Revision, remove: true);
        var refusal = await Assert.ThrowsAsync<SecurityConflictException>(Remove);
        Assert.Equal("space_membership_live_sandbox", refusal.ReasonCode); Assert.Equal(before, await Revision());
        Assert.Equal(member, Assert.Single(await spaces.ReadMembershipsAsync(s.Tenant, shared.SpaceId)));
        Assert.True(await storage.TrySaveTenantSandboxLeaseFactsAsync(s.Tenant, s.Lease with { Revision = 2, State = SandboxLeaseState.Terminated }, 1));
        await Remove(); Assert.Empty(await spaces.ReadMembershipsAsync(s.Tenant, shared.SpaceId));
        await directory.DeleteAsync(s.Human.PrincipalId, s.Tenant, shared.SpaceId, await Revision(), shared.Revision + 1);
        Assert.True(await spaces.CanPurgeDeletedSpaceWorkspaceAsync(new(s.Tenant, shared.SpaceId)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Shared_Space_cleanup_waits_for_uncertain_mount_retirement_and_recovers_from_tombstone(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var s = await PrepareRuntimeSandbox(f);
        var storage = (ITenantSandboxRuntimeStore)f.Iam; var spaces = (ITenantSpaceStore)f.Iam;
        Assert.True(await storage.TryReserveTenantSandboxLeaseAsync(s.Write, s.Lease, 0, DateTimeOffset.UtcNow));
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision;
        var directory = new TenantSpaceService(f.Iam, spaces, (ITenantAgentStore)f.Iam, f.Authorizer, s.Resolver,
            (ITenantCredentialBindingStore)f.Iam, (ITenantCredentialStore)f.Iam, f.Ids, TimeProvider.System);
        var shared = await directory.CreateAsync(s.Human.PrincipalId, s.Tenant, f.Ids.NewId(), "Retired root", "", "", await Revision());
        var root = Path.Combine(Path.GetTempPath(), "space-cleanup-" + f.Ids.NewId().ToString("N"));
        try
        {
            var files = new AgentCore.Infrastructure.Workspaces.FileSpaceWorkspaceStore(root, spaces, (ITenantAgentStore)f.Iam,
                (ITenantSessionStore)f.Iam, (ITenantAgentDecommissionStore)f.Iam, f.Sandbox);
            var owner = new TenantWorkspaceReference.SpaceHome(s.Tenant, shared.SpaceId);
            await files.WriteTenantAsync(owner, "/home/preserved.txt", "retained while uncertain"u8.ToArray(), _ => ValueTask.CompletedTask);
            var path = AgentCore.Infrastructure.Workspaces.AgentWorkspacePhysicalPaths.SpaceHomeDirectory(root, s.Tenant, shared.SpaceId);
            await directory.DeleteAsync(s.Human.PrincipalId, s.Tenant, shared.SpaceId, await Revision(), shared.Revision);
            Assert.False(await spaces.CanPurgeDeletedSpaceWorkspaceAsync(owner));
            await Assert.ThrowsAsync<SecurityConflictException>(() => files.PurgeDeletedSpaceAsync(owner).AsTask());
            var cleanup = new AgentCore.Application.Workspaces.TenantSpaceWorkspaceCleanupService(spaces, files);
            Assert.Equal(0, await cleanup.RecoverAsync()); Assert.True(File.Exists(Path.Combine(path, "preserved.txt")));
            // The existing generation owner supplies verified termination; a canceled Run alone did not suffice.
            Assert.True(await storage.TrySaveTenantSandboxLeaseFactsAsync(s.Tenant, s.Lease with { Revision = 2, State = SandboxLeaseState.Terminated }, 1));
            var reopened = sqlite ? new AgentCore.Infrastructure.Security.SqliteIamStore(f.Contexts!, f.Content) : f.Iam;
            Assert.True(await ((ITenantSpaceStore)reopened).CanPurgeDeletedSpaceWorkspaceAsync(owner));
            Assert.Equal(1, await new AgentCore.Application.Workspaces.TenantSpaceWorkspaceCleanupService((ITenantSpaceStore)reopened, files).RecoverAsync());
            Assert.False(Directory.Exists(path));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Pending_Space_switch_cannot_be_followed_by_an_old_Run_sandbox_reservation(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var s = await PrepareRuntimeSandbox(f);
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision;
        var spaces = (ITenantSpaceStore)f.Iam;
        var directory = new TenantSpaceService(f.Iam, spaces, (ITenantAgentStore)f.Iam, f.Authorizer, s.Resolver,
            (ITenantCredentialBindingStore)f.Iam, (ITenantCredentialStore)f.Iam, f.Ids, TimeProvider.System);
        var shared = await directory.CreateAsync(s.Human.PrincipalId, s.Tenant, f.Ids.NewId(), "Next turn", "", "", await Revision());
        await directory.SetMemberAsync(s.Human.PrincipalId, s.Tenant, shared.SpaceId, s.Session.AgentInstanceId, "", "", [], [], await Revision(), 1, 0);
        var role = await f.Access.SaveRoleAsync(s.Human.PrincipalId, s.Tenant, null, "Read destination", ["spaces.read"], await Revision());
        await f.Access.AssignRoleAsync(s.Human.PrincipalId, s.Tenant, s.Session.ExecutorPrincipalId, role.RoleId, TenantSpaceService.Scope(s.Tenant, shared.SpaceId), await Revision());
        var saved = (await ((ITenantSessionStore)f.Iam).FindTenantSessionAsync(s.Tenant, s.Session.SessionId))!;
        var agent = (await ((ITenantAgentStore)f.Iam).FindTenantAgentAsync(s.Tenant, s.Session.AgentInstanceId))!;
        var change = new TenantContextSwitch(s.Session, SandboxHuman(s), f.Ids.NewId(), shared.SpaceId, null, 0,
            await Revision(), saved.Snapshot.Revision, saved.Snapshot.ContextSelection!.Revision, agent.Instance.Revision);
        var frame = new TenantRuntimeFrame(s.Run.Owner, s.Run.SessionId, s.Run.AgentRunId, s.Run.Claim!.Generation); var at = DateTimeOffset.UtcNow;
        var selection = TenantSessionContextRules.Selection(change, saved.Snapshot.ContextSelection, frame.RunId, at);
        await ((ITenantSessionContextStore)f.Iam).SaveTenantRuntimeContextAsync(frame,
            saved.Snapshot with { Revision = saved.Snapshot.Revision + 1, ContextSelection = selection, UpdatedAt = at }, saved.Snapshot.Revision, change, f.Ids.NewId());
        var denied = await Assert.ThrowsAsync<SecurityConflictException>(() => ((ITenantSandboxRuntimeStore)f.Iam).TryReserveTenantSandboxLeaseAsync(
            s.Write with { SecurityRevision = change.ExpectedSecurityRevision + 1 }, s.Lease, 0, DateTimeOffset.UtcNow).AsTask());
        Assert.Equal("sandbox_context_changed", denied.ReasonCode);
        Assert.Null((await ((ITenantSandboxRuntimeStore)f.Iam).ReadTenantSandboxRuntimeAsync(s.Tenant, s.Session.SessionId))!.Lease);
        Assert.Equal(shared.SpaceId, (await ((ITenantSessionStore)f.Iam).FindTenantSessionAsync(s.Tenant, s.Session.SessionId))!.Snapshot.ContextSelection!.SpaceId);
        Assert.NotEqual(shared.SpaceId, TenantWorkspaceRules.Context(s.Run).SpaceId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Space_switch_does_not_interrupt_or_claim_retirement_of_an_in_flight_command(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var s = await PrepareRuntimeSandbox(f);
        var storage = (ITenantSandboxRuntimeStore)f.Iam;
        Assert.True(await storage.TryReserveTenantSandboxLeaseAsync(s.Write, s.Lease, 0, DateTimeOffset.UtcNow));
        Assert.True(await storage.TrySaveTenantSandboxLeaseFactsAsync(s.Tenant, s.Lease with { Revision = 2, State = SandboxLeaseState.Ready }, 1));
        var execution = RuntimeExecution(f, s);
        Assert.True(await storage.TryReserveTenantSandboxExecutionAsync(s.Write, execution, DateTimeOffset.UtcNow));
        var before = (await storage.ReadTenantSandboxRuntimeAsync(s.Tenant, s.Session.SessionId))!;
        var worker = new OpenSandboxService(new(), f.Sandbox, null!, f.SessionStore, TimeProvider.System, tenantRuntime: storage);
        Assert.Equal("sandbox_execution_busy", (await Assert.ThrowsAsync<SecurityConflictException>(() => worker.RetireIdleGenerationAsync(s.Session).AsTask())).ReasonCode);
        Assert.Equal(before, await storage.ReadTenantSandboxRuntimeAsync(s.Tenant, s.Session.SessionId));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public async Task Space_switch_retires_exact_idle_generation_and_waits_for_remote_confirmation(bool sqlite, bool confirmed)
    {
        await using var f = await Fixture.Create(sqlite); var s = await PrepareRuntimeSandbox(f);
        var storage = (ITenantSandboxRuntimeStore)f.Iam;
        var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders();
        await using var peer = builder.Build(); peer.Urls.Add("http://127.0.0.1:0"); var retires = 0;
        peer.Run(async http =>
        {
            Assert.Equal("context-key", http.Request.Headers["OPEN-SANDBOX-API-KEY"]);
            Assert.Equal("/agent-core/generations/retire", http.Request.Path);
            using var document = await JsonDocument.ParseAsync(http.Request.Body); var body = document.RootElement;
            Assert.Equal(s.Tenant.ToString("N"), body.GetProperty("tenant").GetString());
            Assert.Equal(s.Session.SessionId.ToString("N"), body.GetProperty("session").GetString());
            Assert.Equal(s.Session.ExecutorPrincipalId.ToString("N"), body.GetProperty("executor").GetString());
            Assert.Equal(s.Lease.Generation.ToString("N"), body.GetProperty("generation").GetString()); retires++;
            await http.Response.WriteAsJsonAsync(new { contractVersion = 1, retired = confirmed,
                tenant = s.Tenant.ToString("N"), session = s.Session.SessionId.ToString("N"),
                executor = s.Session.ExecutorPrincipalId.ToString("N"), generation = s.Lease.Generation.ToString("N") });
        });
        await peer.StartAsync();
        var options = new OpenSandboxOptions { Enabled = true, Endpoint = peer.Urls.Single(), ApiKey = "context-key" };
        var lease = s.Lease with { ProviderHostScope = new SandboxVerificationHost(options).Scope };
        Assert.True(await storage.TryReserveTenantSandboxLeaseAsync(s.Write, lease, 0, DateTimeOffset.UtcNow));
        Assert.True(await storage.TrySaveTenantSandboxLeaseFactsAsync(s.Tenant, lease with { Revision = 2, State = SandboxLeaseState.Ready }, 1));
        var sessionStore = (ITenantSessionStore)f.Iam;
        var saved = (await sessionStore.FindTenantSessionAsync(s.Tenant, s.Session.SessionId))!;
        var selection = saved.Snapshot.ContextSelection!;
        var change = new TenantContextSwitch(s.Session, SandboxHuman(s), f.Ids.NewId(), selection.SpaceId, null, 0,
            (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision, saved.Snapshot.Revision, selection.Revision,
            (await ((ITenantAgentStore)f.Iam).FindTenantAgentAsync(s.Tenant, s.Session.AgentInstanceId))!.Instance.Revision);
        var frame = new TenantRuntimeFrame(s.Run.Owner, s.Run.SessionId, s.Run.AgentRunId, s.Run.Claim!.Generation);
        var at = DateTimeOffset.UtcNow;
        var target = TenantSessionContextRules.Selection(change, selection, frame.RunId, at);
        var next = saved.Snapshot with { ContextSelection = target, Revision = saved.Snapshot.Revision + 1, UpdatedAt = at };
        async Task Switch() => await ((ITenantSessionContextStore)f.Iam).SaveTenantRuntimeContextAsync(frame, next, saved.Snapshot.Revision, change, f.Ids.NewId());
        Assert.Equal("sandbox_context_retirement_required", (await Assert.ThrowsAsync<SecurityConflictException>(Switch)).ReasonCode);
        var worker = new OpenSandboxService(options, f.Sandbox, null!, f.SessionStore, TimeProvider.System, tenantRuntime: storage);
        if (confirmed) await worker.RetireIdleGenerationAsync(s.Session);
        else Assert.Equal("sandbox_context_retirement_required", (await Assert.ThrowsAsync<SecurityConflictException>(() => worker.RetireIdleGenerationAsync(s.Session).AsTask())).ReasonCode);
        Assert.Equal(1, retires);
        var current = (await storage.ReadTenantSandboxRuntimeAsync(s.Tenant, s.Session.SessionId))!.Lease!;
        Assert.Equal(confirmed ? SandboxLeaseState.Terminated : SandboxLeaseState.NeedsReconciliation, current.State);
        Assert.Equal(lease.Generation, current.Generation); Assert.Equal("contextChanged", current.CleanupReason);
        Assert.Equal(lease.Profile, current.Profile); Assert.Equal(lease.SelectedProfileId, current.SelectedProfileId);
        if (confirmed) await Switch();
        else
        {
            await Assert.ThrowsAsync<SecurityConflictException>(Switch);
            Assert.Equal(selection, (await sessionStore.FindTenantSessionAsync(s.Tenant, s.Session.SessionId))!.Snapshot.ContextSelection);
        }
        await peer.StopAsync();
    }
}
