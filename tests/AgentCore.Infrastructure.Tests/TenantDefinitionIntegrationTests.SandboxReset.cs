using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Sandbox;
using AgentCore.Infrastructure.Security;
using AgentCore.Infrastructure.Workspaces;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantDefinitionIntegrationTests
{
    [Theory] [InlineData(false, "known")] [InlineData(true, "known")]
    [InlineData(false, "unknown")] [InlineData(true, "unknown")]
    [InlineData(false, "foreignHandle")] [InlineData(true, "foreignHandle")]
    [InlineData(false, "pendingProof")] [InlineData(true, "pendingProof")]
    [InlineData(false, "foreignProof")] [InlineData(true, "foreignProof")]
    [InlineData(false, "hostChanged")] [InlineData(true, "hostChanged")]
    public async Task Tenant_Sandbox_human_reset_retires_exact_generation_after_logout_without_replay_or_file_loss(bool sqlite, string mode)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-reset-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var f = await Fixture.Create(sqlite); RuntimeSandboxSetup s = null!;
            var storage = (ITenantSandboxRuntimeStore)f.Iam;
            var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders(); await using var server = builder.Build(); server.Urls.Add("http://127.0.0.1:0");
            SandboxLease remote = null!; var contacts = 0; var kills = 0; var fences = 0; var repaired = false;
            server.Run(async http =>
            {
                contacts++; Assert.Equal("synthetic-reset-key", http.Request.Headers["OPEN-SANDBOX-API-KEY"]);
                if (http.Request.Path == "/agent-core/generations/retire")
                {
                    fences++; Assert.Equal("POST", http.Request.Method); using var body = await JsonDocument.ParseAsync(http.Request.Body); var scope = body.RootElement;
                    Assert.Equal(s.Tenant.ToString("N"), scope.GetProperty("tenant").GetString()); Assert.Equal(s.Session.SessionId.ToString("N"), scope.GetProperty("session").GetString());
                    Assert.Equal(s.Session.ExecutorPrincipalId.ToString("N"), scope.GetProperty("executor").GetString()); Assert.Equal(remote.Generation.ToString("N"), scope.GetProperty("generation").GetString());
                    await http.Response.WriteAsJsonAsync(new { contractVersion = 1, tenant = mode == "foreignProof" && !repaired ? Guid.Empty.ToString("N") : s.Tenant.ToString("N"),
                        session = s.Session.SessionId.ToString("N"), executor = s.Session.ExecutorPrincipalId.ToString("N"), generation = remote.Generation.ToString("N"), retired = mode != "pendingProof" || repaired });
                }
                else
                {
                    Assert.Equal("/v1/sandboxes/reset-provider", http.Request.Path);
                    if (http.Request.Method == "DELETE") { kills++; http.Response.StatusCode = 204; }
                    else
                    {
                        Assert.Equal("GET", http.Request.Method);
                        await http.Response.WriteAsJsonAsync(new { id = "reset-provider", entrypoint = new[] { "/bin/sh" }, status = new { state = "Running" }, createdAt = DateTimeOffset.UtcNow,
                            metadata = new Dictionary<string,string> { ["agent-core-generation"] = remote.Generation.ToString("N"), ["agent-core-tenant"] = mode == "foreignHandle" && !repaired ? Guid.Empty.ToString("N") : s.Tenant.ToString("N"),
                                ["agent-core-session"] = s.Session.SessionId.ToString("N"), ["agent-core-executor"] = s.Session.ExecutorPrincipalId.ToString("N") } });
                    }
                }
            });
            await server.StartAsync();
            var options = new OpenSandboxOptions { Enabled = true, Endpoint = server.Urls.Single(), ApiKey = "synthetic-reset-key", CanonicalWorkspaceRoot = root, WorkspaceDockerHostRoot = root };
            s = await PrepareRuntimeSandbox(f, options: options);
            var mount = new CanonicalSandboxMountResolver(options).Resolve(s.Session.AgentInstanceId, s.Session.SessionId,
                s.Run.Admission.Configuration!.Definition.StateIsolationPolicy?.HomeAccess ?? AgentCore.Domain.Definitions.HomeAccess.ReadWrite, create: true);
            await mount.Working.WriteAsync("/working/keep.txt", "retained working bytes"u8.ToArray(), 1024, long.MaxValue, default);
            if (mount.Home is not null) await mount.Home.WriteAsync("/home/keep.txt", "retained home bytes"u8.ToArray(), 1024, long.MaxValue, default);
            var known = mode is "known" or "foreignHandle" or "hostChanged";
            if (known)
            {
                s = s with { Lease = s.Lease with { MountAuthority = mount.Authority, ProviderHostScope = new SandboxVerificationHost(options).Scope } };
                Assert.True(await storage.TryReserveTenantSandboxLeaseAsync(s.Write, s.Lease, 0, DateTimeOffset.UtcNow));
                Assert.True(await storage.TrySaveTenantSandboxLeaseFactsAsync(s.Tenant, s.Lease with { Revision = 2, State = SandboxLeaseState.Ready, ProviderId = "reset-provider", BaselineProcesses = [] }, 1));
            }
            var services = SandboxApprovalServices(f, s, options: options);
            var request = (await services.Approvals.PrepareSandboxAsync(new(s.Tenant, s.Run.AgentRunId), s.Lease.Profile.Id,
                new("printf never-replay", TimeoutSeconds: 120, Background: known), s.Run.Revision, s.Run.Claim!.Generation)).Request!;
            var review = await services.Approvals.ReadViewAsync(s.Tenant, request.RequestId, SandboxHuman(s));
            await services.Approvals.DecideAsync(s.Tenant, request.RequestId, SandboxHuman(s), true, request.Operation.ActionHash, review.SecurityRevision, request.Revision);
            var frame = await ResumeSandboxApproval(f, s, services.Approvals); var reserved = await services.Execution.ReserveAsync(frame, request.RequestId);
            var execution = reserved.Execution with { Revision = 2, FilesystemEffectsMayHaveOccurred = true,
                State = known ? SandboxExecutionState.Running : SandboxExecutionState.Dispatching, ProviderCommandId = known ? "reset-command" : null, Phase = known ? "execution" : "provisioning" };
            Assert.True(await storage.TrySaveTenantSandboxExecutionFactsAsync(s.Tenant, execution, 1));
            if (known) await services.Execution.SettleAsync(s.Tenant, frame.SessionId, execution.Id);
            var originalEffect = await ((ITenantApprovedOperationStore)f.Iam).FindOperationReceiptAsync(s.Tenant, request.Operation.OperationId);
            var saved = (await storage.ReadTenantSandboxRuntimeAsync(s.Tenant, frame.SessionId))!; remote = saved.Lease!;
            await Assert.ThrowsAsync<ArgumentException>(() => storage.TrySaveTenantSandboxLeaseFactsAsync(s.Tenant, remote with { Revision = remote.Revision + 1, CleanupReason = "reset" }, remote.Revision).AsTask());
            var reset = new TenantSandboxReset(frame.SessionId, saved.SessionRevision, remote.Generation, remote.Revision, execution.Id);
            var humanReset = new TenantSandboxControlService(f.Iam, (ITenantSessionStore)f.Iam, storage, f.Identity, f.Authorizer, f.Ids, TimeProvider.System);
            var securityRevision = (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision;
            await Assert.ThrowsAsync<SecurityConflictException>(() => humanReset.RequestResetAsync(s.Tenant, s.Human, reset with { ExpectedGeneration = Guid.NewGuid() }, securityRevision).AsTask());
            await Assert.ThrowsAsync<SecurityConflictException>(() => humanReset.RequestResetAsync(s.Tenant, s.Human, reset with { ExpectedExecutionId = null }, securityRevision).AsTask());
            var accepted = await humanReset.RequestResetAsync(s.Tenant, s.Human, reset, securityRevision);
            Assert.False(accepted.RetirementConfirmed); Assert.Equal(remote.Revision + 1, accepted.LeaseRevision); Assert.Equal(securityRevision + 1, accepted.SecurityRevision); Assert.Equal(0, contacts);
            Assert.Single(await f.Iam.ReadAuditAsync(s.Tenant, 200), a => a.Action == "sandbox.reset.request");
            var pending = (await storage.ReadTenantSandboxRuntimeAsync(s.Tenant, frame.SessionId))!.Lease!;
            Assert.Equal(SandboxLeaseState.Terminating, pending.State); Assert.Equal("reset", pending.CleanupReason); Assert.Null(pending.SelectedProfileId);
            await f.Identity.SignOutAsync(s.Human.AuthenticationSessionId);
            var reopened = sqlite ? new SqliteIamStore(f.Contexts!, f.Content) : f.Iam; storage = (ITenantSandboxRuntimeStore)reopened;
            services = SandboxApprovalServices(f, s, reopened, options);
            var sdk = new OpenSandboxService(options,f.Sandbox,null!,f.SessionStore,TimeProvider.System,tenantRuntime: storage,approvedSandbox: services.Execution);
            var originalMapping = options.WorkspaceDockerHostRoot;
            if (mode == "hostChanged") options.WorkspaceDockerHostRoot += "-changed";
            await sdk.ReconcileAsync(default);
            var first = (await storage.ReadTenantSandboxRuntimeAsync(s.Tenant, frame.SessionId))!;
            var blocked = mode is "foreignHandle" or "pendingProof" or "foreignProof" or "hostChanged";
            Assert.Equal(blocked ? SandboxLeaseState.NeedsReconciliation : SandboxLeaseState.Terminated, first.Lease!.State);
            Assert.Equal(!blocked, first.Execution!.RemoteTerminationConfirmed);
            if (blocked)
            {
                Assert.Equal(0, kills); if (mode == "hostChanged") Assert.Equal(0, contacts);
                repaired = true; options.WorkspaceDockerHostRoot = originalMapping; await sdk.ReconcileAsync(default);
            }
            var final = (await storage.ReadTenantSandboxRuntimeAsync(s.Tenant, frame.SessionId))!;
            Assert.Equal(SandboxLeaseState.Terminated, final.Lease!.State); Assert.Equal("reset", final.Lease.CleanupReason); Assert.Null(final.Lease.ActiveExecutionId);
            Assert.Equal(SandboxExecutionState.Canceled, final.Execution!.State); Assert.True(final.Execution.TerminationConfirmed); Assert.True(final.Execution.RemoteTerminationConfirmed);
            Assert.Equal("humanReset", final.Execution.CancellationReason);
            Assert.Equal(known ? 1 : 0, kills); Assert.Equal(known ? 0 : blocked ? 2 : 1, fences);
            Assert.Equal("retained working bytes", System.Text.Encoding.UTF8.GetString(await mount.Working.ReadAsync("/working/keep.txt", 1024, default)));
            if (mount.Home is not null) Assert.Equal("retained home bytes", System.Text.Encoding.UTF8.GetString(await mount.Home.ReadAsync("/home/keep.txt", 1024, default)));
            var effect = await ((ITenantApprovedOperationStore)reopened).FindOperationReceiptAsync(s.Tenant, request.Operation.OperationId);
            Assert.Equal(known ? TenantOperationOutcome.Completed : TenantOperationOutcome.Indeterminate, effect!.Outcome);
            if (known) Assert.Equal(originalEffect, effect);
            var total = contacts; await sdk.ReconcileAsync(default); Assert.Equal(total, contacts);
            Assert.Null(await f.Sandbox.GetLeaseAsync(frame.SessionId)); await server.StopAsync();
        }
        finally { if (Directory.Exists(root)) CanonicalWorkspaceFilesystem.PurgeOwner(root, default); }
    }

    [Theory] [InlineData(false, "logout")] [InlineData(true, "logout")]
    [InlineData(false, "relogin")] [InlineData(true, "relogin")]
    [InlineData(false, "password")] [InlineData(true, "password")]
    [InlineData(false, "wrong-principal")] [InlineData(true, "wrong-principal")]
    public async Task Tenant_Sandbox_reset_transaction_requires_original_human_and_retains_failed_initialization_until_explicit_reset(bool sqlite, string retirement)
    {
        await using var f = await Fixture.Create(sqlite); var s = await PrepareRuntimeSandbox(f);
        var storage = (ITenantSandboxRuntimeStore)f.Iam;
        var initial = s.Lease with { SelectedProfileId = s.Lease.Profile.Id };
        Assert.True(await storage.TryReserveTenantSandboxLeaseAsync(s.Write, initial, 0, DateTimeOffset.UtcNow));
        var failed = initial with { Revision = 2, State = SandboxLeaseState.Terminated, CleanupReason = "initializationFailed",
            Initialization = new("Failed", initial.Profile.Id, initial.Profile.Revision, initial.Profile.InstanceRevision) };
        Assert.True(await storage.TrySaveTenantSandboxLeaseFactsAsync(s.Tenant, failed, 1));
        var session = (await storage.ReadTenantSandboxRuntimeAsync(s.Tenant, s.Session.SessionId))!;
        var reset = new TenantSandboxReset(session.Owner.SessionId, session.SessionRevision, failed.Generation, failed.Revision, null);
        var before = (await f.Iam.ReadTenantAsync(s.Tenant))!; var next = before with { Tenant = before.Tenant with { Revision = before.Tenant.Revision + 1 } };
        var audits = (await f.Iam.ReadAuditAsync(s.Tenant, 200)).Count; var human = s.Human;
        if (retirement == "password") await f.Identity.ChangePasswordAsync(human.AccountId, "strong-owner-password", "replacement-owner-password");
        else if (retirement == "wrong-principal") human = human with { PrincipalId = f.Ids.NewId() };
        else
        {
            await f.Identity.SignOutAsync(human.AuthenticationSessionId);
            if (retirement == "relogin") Assert.NotNull(await f.Identity.SignInAsync("owner@example.test", "strong-owner-password"));
        }
        AuthorizationAudit Audit(HumanSessionReference actor) => new(f.Ids.NewId(), s.Tenant, actor.PrincipalId, "sandbox.reset.request", reset.SessionId.ToString("D"), "allowed", "human_reset", DateTimeOffset.UtcNow);
        Assert.Equal("initiator_unavailable", (await Assert.ThrowsAsync<AccessDeniedException>(() => storage.TryRequestTenantSandboxResetAsync(next, before.Tenant.Revision, reset, human, Audit(human)).AsTask())).ReasonCode);
        Assert.Equal(failed, (await storage.ReadTenantSandboxRuntimeAsync(s.Tenant, s.Session.SessionId))!.Lease);
        Assert.Equal(before.Tenant.Revision, (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision); Assert.Equal(audits, (await f.Iam.ReadAuditAsync(s.Tenant, 200)).Count);
        var login = (await f.Identity.SignInAsync("owner@example.test", retirement == "password" ? "replacement-owner-password" : "strong-owner-password"))!.Session;
        human = new(s.Human.AccountId, login.SessionId, s.Human.PrincipalId);
        var requested = await storage.TryRequestTenantSandboxResetAsync(next, before.Tenant.Revision, reset, human, Audit(human));
        Assert.NotNull(requested); Assert.Equal(SandboxLeaseState.Terminated, requested.State); Assert.Equal("reset", requested.CleanupReason);
        Assert.Null(requested.SelectedProfileId); Assert.Null(requested.TransitionProfileId); Assert.Equal(failed.Initialization, requested.Initialization);
        Assert.Equal(failed.Generation, requested.Generation); Assert.Equal(failed.ProviderHostScope, requested.ProviderHostScope);
        Assert.Equal(before.Tenant.Revision + 1, (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision); Assert.Equal(audits + 1, (await f.Iam.ReadAuditAsync(s.Tenant, 200)).Count);
        await Assert.ThrowsAsync<ArgumentException>(() => storage.TrySaveTenantSandboxLeaseFactsAsync(s.Tenant, requested with { Revision = requested.Revision + 1, CleanupReason = null }, requested.Revision).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => storage.TrySaveTenantSandboxLeaseFactsAsync(s.Tenant, requested with { Revision = requested.Revision + 1, SelectedProfileId = initial.Profile.Id }, requested.Revision).AsTask());
        if (sqlite)
        {
            var reopened = new SqliteIamStore(f.Contexts!, f.Content);
            Assert.Equal(requested, (await reopened.ReadTenantSandboxRuntimeAsync(s.Tenant, s.Session.SessionId))!.Lease);
            Assert.Equal(audits + 1, (await reopened.ReadAuditAsync(s.Tenant, 200)).Count);
        }
        // Explicit reset permits a fresh reviewed generation, never replay of the old initialization.
        s = s with { Human = human }; var services = SandboxApprovalServices(f, s);
        var reviewRequest = (await services.Approvals.PrepareSandboxAsync(new(s.Tenant, s.Run.AgentRunId), initial.Profile.Id,
            new("printf fresh-reviewed-command", TimeoutSeconds: 30), s.Run.Revision, s.Run.Claim!.Generation)).Request!;
        Assert.True(reviewRequest.Snapshot!.Sandbox!.WillInitialize);
        var review = await services.Approvals.ReadViewAsync(s.Tenant, reviewRequest.RequestId, SandboxHuman(s));
        await services.Approvals.DecideAsync(s.Tenant, reviewRequest.RequestId, SandboxHuman(s), true, reviewRequest.Operation.ActionHash, review.SecurityRevision, reviewRequest.Revision);
        var frame = await ResumeSandboxApproval(f, s, services.Approvals);
        var fresh = await services.Execution.ReserveAsync(frame, reviewRequest.RequestId); Assert.True(fresh.Reserved); Assert.True(fresh.Execution.InitializesGeneration);
        Assert.NotEqual(failed.Generation, fresh.Execution.Generation);
        Assert.False((await services.Execution.ReserveAsync(frame, reviewRequest.RequestId)).Reserved);
    }


    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task Tenant_Sandbox_human_control_requires_independent_exact_scopes_and_revocation_precedes_stale_witnesses(bool sqlite, bool stopProcess)
    {
        await using var f = await Fixture.Create(sqlite); var s = await PrepareRuntimeSandbox(f); await ReadySandbox(f, s);
        var storage = (ITenantSandboxRuntimeStore)f.Iam;
        SandboxExecution? execution = null;
        if (stopProcess) { execution = RuntimeExecution(f, s); Assert.True(await storage.TryReserveTenantSandboxExecutionAsync(s.Write, execution, DateTimeOffset.UtcNow)); }
        async ValueTask<long> Revision() => (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision;
        var role = await f.Access.SaveRoleAsync(s.Human.PrincipalId, s.Tenant, null, "Membership", ["members.read"], await Revision());
        var account = await f.Identity.RegisterAsync("reset-manager@example.test", "Reset manager", "strong-manager-password");
        var invitation = await f.Access.InviteAsync(s.Human.PrincipalId, s.Tenant, account.LoginKey, role.RoleId, await Revision());
        var member = await f.Access.AcceptInvitationAsync(account.AccountId, s.Tenant, invitation.Invitation.InvitationId, invitation.Token);
        var human = await SetupHuman(f, account.AccountId, member.PrincipalId, account.LoginKey, "strong-manager-password");
        async Task<Guid> Grant(string name, string[] permissions, AccessScope scope)
        {
            var r = await f.Access.SaveRoleAsync(s.Human.PrincipalId, s.Tenant, null, name, permissions, await Revision());
            return (await f.Access.AssignRoleAsync(s.Human.PrincipalId, s.Tenant, human.PrincipalId, r.RoleId, scope, await Revision())).AssignmentId;
        }
        var agentScope = AccessScope.ForResource(s.Tenant, "agent", s.Session.AgentInstanceId.ToString("D"));
        await Grant("Read one environment", ["sandbox.read"], agentScope);
        await Grant("Read one Session", ["sessions.read"], TenantSessionRules.Scope(s.Tenant, s.Session.SessionId));
        await Grant("Read exact source", ["sandbox.read"], TenantSandboxProfileRules.Scope(s.Tenant, s.Lease.Profile.Id));
        var service = new TenantSandboxControlService(f.Iam, (ITenantSessionStore)f.Iam, storage, f.Identity, f.Authorizer, f.Ids, TimeProvider.System);
        var saved = (await storage.ReadTenantSandboxRuntimeAsync(s.Tenant, s.Session.SessionId))!;
        var reset = new TenantSandboxReset(s.Session.SessionId, saved.SessionRevision, saved.Lease!.Generation, saved.Lease.Revision, execution?.Id);
        var action = stopProcess ? "sandbox.process.stop.request" : "sandbox.reset.request";
        async Task Act(TenantSandboxReset change, long revision)
        {
            if (stopProcess) await service.RequestProcessStopAsync(s.Tenant, human, new(change.SessionId, change.ExpectedSessionRevision, change.ExpectedGeneration,
                change.ExpectedLeaseRevision, execution!.Id, execution.Revision), revision);
            else await service.RequestResetAsync(s.Tenant, human, change, revision);
        }
        async Task Raw(TenantSecurityState state, long revision, AuthorizationAudit audit)
        {
            if (stopProcess) await storage.TryRequestTenantSandboxProcessStopAsync(state, revision, new(reset.SessionId, reset.ExpectedSessionRevision, reset.ExpectedGeneration,
                reset.ExpectedLeaseRevision, execution!.Id, execution.Revision), human, audit);
            else await storage.TryRequestTenantSandboxResetAsync(state, revision, reset, human, audit);
        }
        await Assert.ThrowsAsync<AccessDeniedException>(() => Act(reset, 0));
        await Grant("Manage exact Session", ["sessions.manage"], TenantSessionRules.Scope(s.Tenant, s.Session.SessionId));
        await Assert.ThrowsAsync<AccessDeniedException>(() => Act(reset, 0));
        var manage = await Grant("Manage exact environment", ["sandbox.manage"], agentScope);
        var currentRevision = await Revision();
        await Assert.ThrowsAsync<SecurityConflictException>(() => Act(reset with { ExpectedGeneration = f.Ids.NewId() }, currentRevision));
        await f.Access.RevokeAssignmentAsync(s.Human.PrincipalId, s.Tenant, manage, await Revision());
        var before = (await f.Iam.ReadTenantAsync(s.Tenant))!; var audits = (await f.Iam.ReadAuditAsync(s.Tenant, 200)).Count;
        await Assert.ThrowsAsync<AccessDeniedException>(() => Act(reset with { ExpectedGeneration = Guid.Empty }, 0));
        var next = before with { Tenant = before.Tenant with { Revision = before.Tenant.Revision + 1 } };
        await Assert.ThrowsAsync<AccessDeniedException>(() => Raw(next, before.Tenant.Revision,
            new(f.Ids.NewId(), s.Tenant, human.PrincipalId, action, (execution?.Id ?? reset.SessionId).ToString("D"), "allowed", stopProcess ? "human_stop" : "human_reset", DateTimeOffset.UtcNow)));
        Assert.Equal(saved.Lease, (await storage.ReadTenantSandboxRuntimeAsync(s.Tenant, s.Session.SessionId))!.Lease);
        Assert.Equal(before.Tenant.Revision, await Revision()); Assert.Equal(audits, (await f.Iam.ReadAuditAsync(s.Tenant, 200)).Count);
        await Grant("Restore exact environment management", ["sandbox.manage"], agentScope);
        // Cleanup remains available when the old source no longer permits execution.
        await SetupProfiles(f).Profiles.SaveAsync(s.Tenant, s.Lease.Profile.Id, s.Human, SetupInput("Disabled source") with { Enabled = false }, await Revision(), 1);
        var witness = await Revision(); await Act(reset, witness);
        Assert.False((await storage.ReadTenantSandboxRuntimeAsync(s.Tenant, s.Session.SessionId))!.Execution?.TerminationConfirmed == true);
        await Assert.ThrowsAsync<SecurityConflictException>(() => Act(reset, witness));
        Assert.Single(await f.Iam.ReadAuditAsync(s.Tenant, 200), a => a.Action == action);
    }

}
