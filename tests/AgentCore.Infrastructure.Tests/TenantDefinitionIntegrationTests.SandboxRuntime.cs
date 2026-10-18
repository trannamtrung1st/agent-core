using System.Text.Json;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Providers;
using AgentCore.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantDefinitionIntegrationTests
{
    private sealed record RuntimeSandboxSetup(Guid Tenant, HumanSessionReference Human, TenantSessionRoot Session, AgentRun Run,
        SandboxLease Lease, TenantSandboxRuntimeWrite Write, Guid ExecutionGrant, TenantRunConfigurationResolver Resolver);

    private static async Task<RuntimeSandboxSetup> PrepareRuntimeSandbox(Fixture f, bool verify = true, HomeAccess? home = null, Func<Guid, HumanSessionReference, ValueTask<Guid>>? registry = null,
        AgentCore.Infrastructure.Sandbox.OpenSandboxOptions? options = null, string? initialization = null, bool template = false, string? instanceInitialization = null, string? imageReference = null)
    {
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        var human = await SetupHuman(f, owner.AccountId, owner.OwnerPrincipalId); var p = SetupProfiles(f, options);
        async ValueTask<long> Revision() => (await f.Iam.ReadTenantAsync(owner.TenantId))!.Tenant.Revision;
        var sourceCredential = registry is null ? (Guid?)null : await registry(owner.TenantId, human);
        var profile = (await p.Profiles.SaveAsync(owner.TenantId, null, human, SetupInput("Runtime source") with { RegistryCredentialId = sourceCredential, InitializationScript = initialization,
                ImageReference = imageReference ?? (options?.AllowLocalImageIds == true ? "sha256:" + new string('b', 64) : SetupInput("Runtime source").ImageReference) }, await Revision(), 0)).Profile;
        if (verify) await p.Verify.VerifyAsync(owner.TenantId, profile.Id, human, await Revision(), 1);
        var draft = await f.Service.CreateAsync(human.PrincipalId, owner.TenantId, "Tenant runtime", await Revision());
        draft = await f.Service.UpdateAsync(human.PrincipalId, owner.TenantId, draft.DraftId, draft.Candidate with {
            SandboxPolicy = new(profile.Id, [profile.Id]), Environment = new(Capabilities: new("Selected", [ToolCatalog.SandboxExec]), Workspace: template ? new("notes") : null),
            StateIsolationPolicy = home is { } access ? new(HomeAccess: access) : draft.Candidate.StateIsolationPolicy
        }, await Revision(), draft.Revision);
        if (template)
        {
            await f.Service.BindResourcesAsync(human.PrincipalId, owner.TenantId, draft.DraftId, await Revision(), draft.Revision,
                [new(null, "notes/welcome.txt", AgentDefinitionResourceKind.Template, "text/plain", System.Text.Encoding.UTF8.GetBytes("immutable admitted template"))]);
            draft = (await f.Definitions.FindDefinitionDraftAsync(owner.TenantId, draft.DraftId))!;
        }
        await f.Service.PublishAsync(human.PrincipalId, owner.TenantId, draft.DraftId, await Revision(), draft.Revision);
        var builtIns = new SessionNoBuiltinFallback();
        var agents = new TenantAgentService(f.Iam, (ITenantAgentStore)f.Iam, builtIns, f.Authorizer, f.Ids, TimeProvider.System, ownedDefinitions: f.Definitions);
        var agent = await agents.CreateFromPublicationAsync(human.PrincipalId, owner.TenantId, Guid.Parse(draft.DefinitionId), 1, await Revision());
        if (instanceInitialization is not null)
        {
            var configuration = ConfigurationServices(f, sandboxImages: p.Images, sandboxReadiness: p.Ready).Configuration;
            await configuration.PatchSettingsAsync(human.PrincipalId, owner.TenantId, agent.Instance.InstanceId, "sandboxPolicy", await Revision(), agent.Instance.Revision,
                Fields(new { initializationScript = instanceInitialization }), [], initiator: human);
            agent = (await ((ITenantAgentStore)f.Iam).FindTenantAgentAsync(owner.TenantId, agent.Instance.InstanceId))!;
        }
        var role = await f.Access.SaveRoleAsync(human.PrincipalId, owner.TenantId, null, "Execute Sandbox", ["sandbox.execute"], await Revision());
        var grant = await f.Access.AssignRoleAsync(human.PrincipalId, owner.TenantId, agent.PrincipalId, role.RoleId,
            AccessScope.ForResource(owner.TenantId, "agent", agent.Instance.InstanceId.ToString("D")), await Revision());
        role = await f.Access.SaveRoleAsync(human.PrincipalId, owner.TenantId, null, "Read source", ["sandbox.read"], await Revision());
        await f.Access.AssignRoleAsync(human.PrincipalId, owner.TenantId, agent.PrincipalId, role.RoleId, TenantSandboxProfileRules.Scope(owner.TenantId, profile.Id), await Revision());
        if (sourceCredential is { } credential)
        {
            role = await f.Access.SaveRoleAsync(human.PrincipalId, owner.TenantId, null, "Use exact image source", ["credentials.use"], await Revision());
            await f.Access.AssignRoleAsync(human.PrincipalId, owner.TenantId, agent.PrincipalId, role.RoleId, TenantCredentialRules.Scope(owner.TenantId, credential), await Revision());
        }
        var sessions = new TenantSessionService(f.Iam, (ITenantAgentStore)f.Iam, (ITenantSessionStore)f.Iam, (ITenantRunStore)f.Iam,
            f.Definitions, builtIns, f.Identity, f.Authorizer, ModelCatalogFactory.Synthetic(), new(), f.Ids, TimeProvider.System);
        var session = await sessions.CreateAsync(owner.TenantId, agent.Instance.InstanceId, new(human.AccountId, human.AuthenticationSessionId, human.PrincipalId), SessionMode.Text, await Revision(), agent.Instance.Revision);
        var resolver = new TenantRunConfigurationResolver((ITenantAgentStore)f.Iam, f.Definitions, builtIns,
            new(f.Resources, builtIns, new NoBuiltinKnowledge(), f.Content), (ITenantCredentialBindingStore)f.Iam,
            (ITenantSandboxProfileStore)f.Iam, p.Images, p.Ready, f.Authorizer);
        var runs = new TenantRunService(f.Iam, (ITenantSessionStore)f.Iam, (ITenantRunStore)f.Iam, resolver, f.Identity, f.Authorizer, ModelCatalogFactory.Synthetic(), f.Ids, TimeProvider.System);
        var receipt = await runs.SubmitTextAsync(owner.TenantId, session.Owner.SessionId, new(human.AccountId, human.AuthenticationSessionId, human.PrincipalId),
            f.Ids.NewId(), "Run one bounded command", await Revision(), 1);
        var run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(owner.TenantId, receipt.RunId))!;
        var now = DateTimeOffset.UtcNow;
        run = await ((ITenantRuntimeStore)f.Iam).ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId,
            new AgentRunCommand.Claim(run.Revision, now, f.Ids.NewId(), now.AddMinutes(5)), f.Ids.NewId());
        var pinned = Assert.Single(run.Admission.Configuration!.Definition.ExecutionSandboxProfiles!);
        var lease = new SandboxLease(session.Owner.SessionId, agent.Instance.InstanceId, f.Ids.NewId(), 1, pinned,
            SandboxLeaseState.Provisioning, null, now, now.AddMinutes(10), AdmissionRunId: run.AgentRunId, AdmissionToolCallId: "sandbox-1",
            TenantId: owner.TenantId, ExecutorPrincipalId: agent.PrincipalId, ProviderHostScope: p.Host.Scope);
        return new(owner.TenantId, human, session.Owner, run, lease,
            new(new(run.Owner, run.SessionId, run.AgentRunId, run.Claim!.Generation), await Revision(), run.Revision), grant.AssignmentId, resolver);
    }
    private static SandboxExecution RuntimeExecution(Fixture f, RuntimeSandboxSetup s, string call = "sandbox-1") => new(f.Ids.NewId(), s.Session.SessionId,
        s.Session.AgentInstanceId, s.Lease.Generation, s.Run.AgentRunId, s.Run.Claim!.Generation, call, new string('a', 64), SandboxExecutionState.Dispatching,
        s.Lease.CreatedAt, CommandText: "printf tenant-output", DeadlineAt: s.Lease.CreatedAt.AddSeconds(30), ProfileId: s.Lease.Profile.Id,
        ProfileRevision: s.Lease.Profile.Revision, TenantId: s.Tenant, ExecutorPrincipalId: s.Session.ExecutorPrincipalId, Revision: 1);

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_sandbox_runtime_reserves_one_owned_receipt_settles_facts_and_reopens_without_legacy_exposure(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var s = await PrepareRuntimeSandbox(f); var store = (ITenantSandboxRuntimeStore)f.Iam;
        Assert.True(s.Lease.Profile.AdmittedForExecution); Assert.Null(s.Run.Owner.ProfileIdOrNull);
        Assert.True(await store.TryReserveTenantSandboxLeaseAsync(s.Write, s.Lease, 0, DateTimeOffset.UtcNow));
        Assert.False(await store.TryReserveTenantSandboxLeaseAsync(s.Write, s.Lease, 0, DateTimeOffset.UtcNow));
        var ready = s.Lease with { Revision = 2, State = SandboxLeaseState.Ready, ProviderId = "PRIVATE_PROVIDER" };
        Assert.True(await store.TrySaveTenantSandboxLeaseFactsAsync(s.Tenant, ready, 1));
        var execution = RuntimeExecution(f, s);
        Assert.True(await store.TryReserveTenantSandboxExecutionAsync(s.Write, execution, DateTimeOffset.UtcNow));
        var saved = (await store.ReadTenantSandboxRuntimeAsync(s.Tenant, s.Session.SessionId))!;
        Assert.Equal(execution, saved.Execution); Assert.Equal(3, saved.Lease!.Revision);
        Assert.Equal(SandboxLeaseState.Executing, saved.Lease.State); Assert.Equal(execution.Id, saved.Lease.ActiveExecutionId);
        Assert.False(await store.TryReserveTenantSandboxExecutionAsync(s.Write, execution with { Id = f.Ids.NewId() }, DateTimeOffset.UtcNow));
        await Assert.ThrowsAsync<SecurityConflictException>(() => store.TryReserveTenantSandboxExecutionAsync(s.Write, execution with { ActionHash = new string('b', 64) }, DateTimeOffset.UtcNow).AsTask());
        await Assert.ThrowsAsync<SecurityConflictException>(() => store.TrySaveTenantSandboxLeaseFactsAsync(s.Tenant, saved.Lease with { Revision = 4, State = SandboxLeaseState.Ready, ActiveExecutionId = null }, 3).AsTask());
        var done = execution with { Revision = 2, State = SandboxExecutionState.Completed, Stdout = "tenant-output", FinishedAt = execution.StartedAt.AddSeconds(1), ExitCode = 0, TerminationConfirmed = true, ProviderCommandId = "PRIVATE_COMMAND" };
        Assert.True(await store.TrySaveTenantSandboxExecutionFactsAsync(s.Tenant, done, 1));
        Assert.False(await store.TrySaveTenantSandboxExecutionFactsAsync(s.Tenant, done, 1));
        await Assert.ThrowsAsync<ArgumentException>(() => store.TrySaveTenantSandboxExecutionFactsAsync(s.Tenant, done with { Revision = 3, State = SandboxExecutionState.Running }, 2).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => store.TrySaveTenantSandboxExecutionFactsAsync(s.Tenant, done with { Revision = 3, CommandText = "replacement" }, 2).AsTask());
        Assert.True(await store.TrySaveTenantSandboxLeaseFactsAsync(s.Tenant, saved.Lease with { Revision = 4, State = SandboxLeaseState.Ready, ActiveExecutionId = null }, 3));
        if (sqlite) store = new SqliteIamStore(f.Contexts!, f.Content);
        Assert.Equal(done, await store.FindTenantSandboxOperationAsync(s.Tenant, s.Session.SessionId, s.Run.AgentRunId, execution.ToolCallId));
        var reads = new TenantSandboxRuntimeReadService(f.Iam, (ITenantSessionStore)f.Iam, store, f.Identity, f.Authorizer);
        var view = await reads.ReadAsync(s.Tenant, s.Session.SessionId, s.Human, execution.Id);
        Assert.Equal("tenant-output", view.Execution!.Stdout); Assert.Equal("Ready", view.Environment!.StoredState); Assert.False(view.LiveAuthorityConfirmed);
        Assert.DoesNotContain("PRIVATE_", JsonSerializer.Serialize(view));
        Assert.Null(await store.ReadTenantSandboxRuntimeAsync(f.Ids.NewId(), s.Session.SessionId));
        Assert.Null(await f.Sandbox.GetLeaseAsync(s.Session.SessionId)); Assert.Null(await f.Sandbox.GetExecutionAsync(s.Session.SessionId, execution.Id));
        Assert.Null(await f.Sandbox.GetOperationAsync(s.Session.SessionId, s.Run.AgentRunId, execution.ToolCallId)); Assert.Empty(await f.Sandbox.ListLeasesAsync());
        await Assert.ThrowsAsync<AccessDeniedException>(() => f.Sandbox.SaveLeaseAsync(ready with { Revision = 3, TenantId = null, ExecutorPrincipalId = null, Profile = ready.Profile with { TenantId = null } }, 2).AsTask());
        await Assert.ThrowsAsync<AccessDeniedException>(() => f.Sandbox.SaveExecutionAsync(execution with { TenantId = null, ExecutorPrincipalId = null, Revision = 0 }, true).AsTask());
        await Assert.ThrowsAsync<AccessDeniedException>(() => f.Sandbox.SaveInstanceVerificationAsync(new(s.Session.AgentInstanceId, 1,
            new(s.Lease.Profile.Id, 1, DateTimeOffset.UtcNow, true, "forged"))).AsTask());
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_sandbox_runtime_revocation_blocks_reservation_but_retired_Run_and_Session_can_settle_exact_facts(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var s = await PrepareRuntimeSandbox(f); var store = (ITenantSandboxRuntimeStore)f.Iam;
        async ValueTask<long> Revision() => (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision;
        Assert.True(await store.TryReserveTenantSandboxLeaseAsync(s.Write, s.Lease, 0, DateTimeOffset.UtcNow));
        var ready = s.Lease with { Revision = 2, State = SandboxLeaseState.Ready };
        Assert.True(await store.TrySaveTenantSandboxLeaseFactsAsync(s.Tenant, ready, 1));
        var execution = RuntimeExecution(f, s);
        Assert.True(await store.TryReserveTenantSandboxExecutionAsync(s.Write, execution, DateTimeOffset.UtcNow));
        await f.Access.RevokeAssignmentAsync(s.Human.PrincipalId, s.Tenant, s.ExecutionGrant, await Revision());
        await Assert.ThrowsAsync<SecurityConflictException>(() => store.TryReserveTenantSandboxExecutionAsync(s.Write, RuntimeExecution(f, s, "new-call"), DateTimeOffset.UtcNow).AsTask());
        var currentRevision = await Revision();
        await Assert.ThrowsAsync<AccessDeniedException>(() => store.TryReserveTenantSandboxExecutionAsync(s.Write with { SecurityRevision = currentRevision }, RuntimeExecution(f, s, "new-call"), DateTimeOffset.UtcNow).AsTask());
        var runtime = (ITenantRuntimeStore)f.Iam;
        var run = await runtime.ApplyTenantRuntimeCommandAsync(s.Run.Owner, s.Run.AgentRunId,
            new AgentRunCommand.RequestCancellation(s.Run.Revision, DateTimeOffset.UtcNow, "An owned command may have started"), f.Ids.NewId());
        await runtime.ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId,
            new AgentRunCommand.CommitCancellation(run.Revision, DateTimeOffset.UtcNow, run.Claim!.Generation, "Exact cleanup required"), f.Ids.NewId());
        var sessions = new TenantSessionService(f.Iam, (ITenantAgentStore)f.Iam, (ITenantSessionStore)f.Iam, (ITenantRunStore)f.Iam, f.Definitions,
            new SessionNoBuiltinFallback(), f.Identity, f.Authorizer, ModelCatalogFactory.Synthetic(), new(), f.Ids, TimeProvider.System);
        var savedSession = (await ((ITenantSessionStore)f.Iam).FindTenantSessionAsync(s.Tenant, s.Session.SessionId))!;
        await sessions.ChangeAsync(s.Tenant, new(s.Human.AccountId, s.Human.AuthenticationSessionId, s.Human.PrincipalId),
            new(s.Session.SessionId, savedSession.Snapshot.Revision, TenantSessionAction.Delete), await Revision());
        Assert.Null(await store.ReadTenantSandboxRuntimeAsync(s.Tenant, s.Session.SessionId));
        Assert.True(await store.TrySaveTenantSandboxExecutionFactsAsync(s.Tenant, execution with { Revision = 2, State = SandboxExecutionState.Canceled,
            FinishedAt = DateTimeOffset.UtcNow, FilesystemEffectsMayHaveOccurred = true, TerminationConfirmed = true }, 1));
        var retired = s.Lease with { Revision = 4, State = SandboxLeaseState.Terminated, LastExecutionId = execution.Id,
            AdmissionRunId = execution.AgentRunId, AdmissionToolCallId = execution.ToolCallId };
        Assert.True(await store.TrySaveTenantSandboxLeaseFactsAsync(s.Tenant, retired, 3));
        await Assert.ThrowsAsync<ArgumentException>(() => store.TrySaveTenantSandboxLeaseFactsAsync(s.Tenant, retired with { Revision = 5, State = SandboxLeaseState.Ready }, 4).AsTask());
        currentRevision = await Revision();
        await Assert.ThrowsAsync<AccessDeniedException>(() => store.TryReserveTenantSandboxLeaseAsync(s.Write with { SecurityRevision = currentRevision },
            s.Lease with { Generation = f.Ids.NewId(), Revision = 5 }, 4, DateTimeOffset.UtcNow).AsTask());
        Assert.Null(await f.Sandbox.GetLeaseAsync(s.Session.SessionId)); Assert.Empty(await f.Sandbox.ListLeasesAsync());
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_sandbox_runtime_requires_live_exact_claim_admitted_setup_and_bounded_deadline(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var s = await PrepareRuntimeSandbox(f); var store = (ITenantSandboxRuntimeStore)f.Iam;
        await Assert.ThrowsAsync<AccessDeniedException>(() => store.TryReserveTenantSandboxLeaseAsync(s.Write with { Runtime = s.Write.Runtime with { Generation = f.Ids.NewId() } }, s.Lease, 0, DateTimeOffset.UtcNow).AsTask());
        await Assert.ThrowsAsync<SecurityConflictException>(() => store.TryReserveTenantSandboxLeaseAsync(s.Write with { RunRevision = 0 }, s.Lease, 0, DateTimeOffset.UtcNow).AsTask());
        await Assert.ThrowsAsync<AccessDeniedException>(() => store.TryReserveTenantSandboxLeaseAsync(s.Write, s.Lease, 0, s.Run.Claim!.LeaseExpiresAtUtc).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => store.TryReserveTenantSandboxLeaseAsync(s.Write, s.Lease with { Profile = s.Lease.Profile with { Cpu = .5 } }, 0, DateTimeOffset.UtcNow).AsTask());
        Assert.Null((await store.ReadTenantSandboxRuntimeAsync(s.Tenant, s.Session.SessionId))!.Lease);
        Assert.True(await store.TryReserveTenantSandboxLeaseAsync(s.Write, s.Lease, 0, DateTimeOffset.UtcNow));
        var ready = s.Lease with { Revision = 2, State = SandboxLeaseState.Ready };
        Assert.True(await store.TrySaveTenantSandboxLeaseFactsAsync(s.Tenant, ready, 1));
        await Assert.ThrowsAsync<ArgumentException>(() => store.TrySaveTenantSandboxLeaseFactsAsync(s.Tenant, ready with { Revision = 3, CleanupReason = "reset" }, 2).AsTask());
        var execution = RuntimeExecution(f, s);
        await Assert.ThrowsAsync<ArgumentException>(() => store.TryReserveTenantSandboxExecutionAsync(s.Write, execution with { DeadlineAt = execution.StartedAt.AddSeconds(181) }, DateTimeOffset.UtcNow).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => store.TryReserveTenantSandboxExecutionAsync(s.Write, execution with { Generation = f.Ids.NewId() }, DateTimeOffset.UtcNow).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => store.TryReserveTenantSandboxExecutionAsync(s.Write, execution with { ActionHash = null! }, DateTimeOffset.UtcNow).AsTask());
        Assert.Null(await store.FindTenantSandboxOperationAsync(s.Tenant, s.Session.SessionId, s.Run.AgentRunId, execution.ToolCallId));
        Assert.Equal(ready, (await store.ReadTenantSandboxRuntimeAsync(s.Tenant, s.Session.SessionId))!.Lease);
        Assert.True(await store.TryReserveTenantSandboxExecutionAsync(s.Write, execution, DateTimeOffset.UtcNow));
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_sandbox_runtime_later_verification_cannot_expand_an_existing_Run_pin(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var s = await PrepareRuntimeSandbox(f, verify: false); var store = (ITenantSandboxRuntimeStore)f.Iam;
        Assert.False(s.Lease.Profile.AdmittedForExecution);
        var p = SetupProfiles(f); var revision = (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision;
        await p.Verify.VerifyAsync(s.Tenant, s.Lease.Profile.Id, s.Human, revision, 1);
        var current = await s.Resolver.ResolveAsync(s.Tenant, s.Session.AgentInstanceId, default);
        Assert.True(Assert.Single(current.Configuration.Configuration.Definition.ExecutionSandboxProfiles!).AdmittedForExecution);
        var write = s.Write with { SecurityRevision = (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision };
        await Assert.ThrowsAsync<ArgumentException>(() => store.TryReserveTenantSandboxLeaseAsync(write, s.Lease, 0, DateTimeOffset.UtcNow).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => store.TryReserveTenantSandboxLeaseAsync(write, s.Lease with { Profile = s.Lease.Profile with { AdmittedForExecution = true } }, 0, DateTimeOffset.UtcNow).AsTask());
        Assert.Null((await store.ReadTenantSandboxRuntimeAsync(s.Tenant, s.Session.SessionId))!.Lease);
    }

    private sealed class RuntimeSandboxReadHook(ITenantSandboxRuntimeStore inner, Func<Task> during) : ITenantSandboxRuntimeStore
    {
        public async ValueTask<TenantSandboxRuntimeSnapshot?> ReadTenantSandboxRuntimeAsync(Guid tenant, Guid session, Guid? id = null, CancellationToken ct = default)
        { var value = await inner.ReadTenantSandboxRuntimeAsync(tenant, session, id, ct); await during(); return value; }
        public ValueTask<SandboxExecution?> FindTenantSandboxOperationAsync(Guid t, Guid s, Guid r, string call, CancellationToken ct = default) => inner.FindTenantSandboxOperationAsync(t, s, r, call, ct);
        public ValueTask<bool> TryReserveTenantSandboxLeaseAsync(TenantSandboxRuntimeWrite w, SandboxLease l, long rev, DateTimeOffset at, CancellationToken ct = default) => inner.TryReserveTenantSandboxLeaseAsync(w, l, rev, at, ct);
        public ValueTask<bool> TryReserveTenantSandboxExecutionAsync(TenantSandboxRuntimeWrite w, SandboxExecution e, DateTimeOffset at, CancellationToken ct = default) => inner.TryReserveTenantSandboxExecutionAsync(w, e, at, ct);
        public ValueTask<bool> TrySaveTenantSandboxLeaseFactsAsync(Guid t, SandboxLease l, long rev, CancellationToken ct = default) => inner.TrySaveTenantSandboxLeaseFactsAsync(t, l, rev, ct);
        public ValueTask<bool> TrySaveTenantSandboxExecutionFactsAsync(Guid t, SandboxExecution e, long rev, CancellationToken ct = default) => inner.TrySaveTenantSandboxExecutionFactsAsync(t, e, rev, ct);
    }
    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task Tenant_sandbox_runtime_read_fences_original_login_and_security_changes_during_storage(bool sqlite, bool logout)
    {
        await using var f = await Fixture.Create(sqlite); var s = await PrepareRuntimeSandbox(f); var store = (ITenantSandboxRuntimeStore)f.Iam;
        Assert.True(await store.TryReserveTenantSandboxLeaseAsync(s.Write, s.Lease, 0, DateTimeOffset.UtcNow));
        var hook = new RuntimeSandboxReadHook(store, async () => {
            if (logout) { await f.Identity.SignOutAsync(s.Human.AuthenticationSessionId); _ = await SetupHuman(f, s.Human.AccountId, s.Human.PrincipalId); }
            else await f.Access.SaveRoleAsync(s.Human.PrincipalId, s.Tenant, null, "Unrelated security change", ["agents.read"], (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
        });
        var reads = new TenantSandboxRuntimeReadService(f.Iam, (ITenantSessionStore)f.Iam, hook, f.Identity, f.Authorizer);
        if (logout) await Assert.ThrowsAsync<AccessDeniedException>(() => reads.ReadAsync(s.Tenant, s.Session.SessionId, s.Human).AsTask());
        else await Assert.ThrowsAsync<SecurityConflictException>(() => reads.ReadAsync(s.Tenant, s.Session.SessionId, s.Human).AsTask());
        var fresh = logout ? await SetupHuman(f, s.Human.AccountId, s.Human.PrincipalId) : s.Human;
        reads = new(f.Iam, (ITenantSessionStore)f.Iam, store, f.Identity, f.Authorizer);
        Assert.Equal(s.Lease.Generation, (await reads.ReadAsync(s.Tenant, s.Session.SessionId, fresh)).Environment!.Generation);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_sandbox_runtime_read_requires_independent_exact_Session_agent_and_profile_authority(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var s = await PrepareRuntimeSandbox(f); var store = (ITenantSandboxRuntimeStore)f.Iam;
        Assert.True(await store.TryReserveTenantSandboxLeaseAsync(s.Write, s.Lease, 0, DateTimeOffset.UtcNow));
        async ValueTask<long> Revision() => (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision;
        var minimal = await f.Access.SaveRoleAsync(s.Human.PrincipalId, s.Tenant, null, "Minimal", ["agents.chat"], await Revision());
        var account = await f.Identity.RegisterAsync("sandbox-reader@example.test", "Reader", "strong-reader-password");
        var invite = await f.Access.InviteAsync(s.Human.PrincipalId, s.Tenant, account.LoginKey, minimal.RoleId, await Revision());
        var member = await f.Access.AcceptInvitationAsync(account.AccountId, s.Tenant, invite.Invitation.InvitationId, invite.Token);
        var reader = await SetupHuman(f, account.AccountId, member.PrincipalId, account.LoginKey, "strong-reader-password");
        async Task<Guid> Grant(string permission, AccessScope scope) {
            var role = await f.Access.SaveRoleAsync(s.Human.PrincipalId, s.Tenant, null, permission + scope.ResourceType, [permission], await Revision());
            return (await f.Access.AssignRoleAsync(s.Human.PrincipalId, s.Tenant, reader.PrincipalId, role.RoleId, scope, await Revision())).AssignmentId;
        }
        await Grant("sessions.read", TenantSessionRules.Scope(s.Tenant, s.Session.SessionId));
        var reads = new TenantSandboxRuntimeReadService(f.Iam, (ITenantSessionStore)f.Iam, store, f.Identity, f.Authorizer);
        await Assert.ThrowsAsync<AccessDeniedException>(() => reads.ReadAsync(s.Tenant, s.Session.SessionId, reader).AsTask());
        await Grant("sandbox.read", AccessScope.ForResource(s.Tenant, "agent", s.Session.AgentInstanceId.ToString("D")));
        await Assert.ThrowsAsync<AccessDeniedException>(() => reads.ReadAsync(s.Tenant, s.Session.SessionId, reader).AsTask());
        var profileGrant = await Grant("sandbox.read", TenantSandboxProfileRules.Scope(s.Tenant, s.Lease.Profile.Id));
        Assert.NotNull((await reads.ReadAsync(s.Tenant, s.Session.SessionId, reader)).Environment);
        await f.Access.RevokeAssignmentAsync(s.Human.PrincipalId, s.Tenant, profileGrant, await Revision());
        await Assert.ThrowsAsync<AccessDeniedException>(() => reads.ReadAsync(s.Tenant, s.Session.SessionId, reader).AsTask());
    }

    [Fact]
    public async Task Tenant_sandbox_runtime_SQLite_enforces_actual_owners_exact_Run_profile_and_nonnullable_JSON_scope()
    {
        await using var f = await Fixture.Create(true); var s = await PrepareRuntimeSandbox(f); var store = (ITenantSandboxRuntimeStore)f.Iam;
        Assert.True(await store.TryReserveTenantSandboxLeaseAsync(s.Write, s.Lease, 0, DateTimeOffset.UtcNow));
        Assert.True(await store.TrySaveTenantSandboxLeaseFactsAsync(s.Tenant, s.Lease with { Revision = 2, State = SandboxLeaseState.Ready }, 1));
        var execution = RuntimeExecution(f, s); Assert.True(await store.TryReserveTenantSandboxExecutionAsync(s.Write, execution, DateTimeOffset.UtcNow));
        var foreign = f.Ids.NewId(); var sessionId = s.Session.SessionId.ToString("D"); var executionId = execution.Id.ToString("D");
        await using var db = f.Contexts!.CreateDbContext();
        var row = await db.Set<SandboxLeaseRow>().AsNoTracking().SingleAsync();
        var json = System.Text.Json.Nodes.JsonNode.Parse(row.Json)!; json["tenantId"] = foreign; json["profile"]!["tenantId"] = foreign;
        var changed = json.ToJsonString();
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE SandboxSessionLeases SET TenantId={foreign}, Json={changed} WHERE SessionId={sessionId}"));
        json = System.Text.Json.Nodes.JsonNode.Parse(row.Json)!; json.AsObject().Remove("tenantId"); changed = json.ToJsonString();
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE SandboxSessionLeases SET Json={changed} WHERE SessionId={sessionId}"));
        json = System.Text.Json.Nodes.JsonNode.Parse(row.Json)!; json["profile"]!["id"] = "missing-profile"; changed = json.ToJsonString();
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE SandboxSessionLeases SET ProfileId={"missing-profile"}, Json={changed} WHERE SessionId={sessionId}"));
        var runId = foreign.ToString("D");
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE SandboxExecutions SET AgentRunId={runId}, OwnedRunId={runId} WHERE Id={executionId}"));
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE SandboxExecutions SET LeaseSessionId=NULL WHERE Id={executionId}"));
        Assert.Equal(execution, (await store.ReadTenantSandboxRuntimeAsync(s.Tenant, s.Session.SessionId))!.Execution);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_sandbox_runtime_never_reprovisions_same_operation_and_isolates_retained_process_collections(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var s = await PrepareRuntimeSandbox(f); var store = (ITenantSandboxRuntimeStore)f.Iam;
        var processes = new[] { "original-process" }; var lease = s.Lease with { BaselineProcesses = processes };
        Assert.True(await store.TryReserveTenantSandboxLeaseAsync(s.Write, lease, 0, DateTimeOffset.UtcNow));
        processes[0] = "mutated-input";
        var saved = (await store.ReadTenantSandboxRuntimeAsync(s.Tenant, s.Session.SessionId))!.Lease!;
        Assert.Equal("original-process", Assert.Single(saved.BaselineProcesses!));
        ((IList<string>)saved.BaselineProcesses!)[0] = "mutated-output";
        saved = (await store.ReadTenantSandboxRuntimeAsync(s.Tenant, s.Session.SessionId))!.Lease!;
        Assert.Equal("original-process", Assert.Single(saved.BaselineProcesses!));
        Assert.True(await store.TrySaveTenantSandboxLeaseFactsAsync(s.Tenant, saved with { Revision = 2, State = SandboxLeaseState.Terminated }, 1));
        await Assert.ThrowsAsync<SecurityConflictException>(() => store.TryReserveTenantSandboxLeaseAsync(s.Write,
            s.Lease with { Revision = 3, Generation = f.Ids.NewId() }, 2, DateTimeOffset.UtcNow).AsTask());
        var differentCall = s.Lease with { Revision = 3, Generation = f.Ids.NewId(), AdmissionToolCallId = "sandbox-2" };
        Assert.True(await store.TryReserveTenantSandboxLeaseAsync(s.Write, differentCall, 2, DateTimeOffset.UtcNow));
    }

}
