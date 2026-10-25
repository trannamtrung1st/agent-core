using AgentCore.Domain.Conversation;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Security;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantOperationAuthorizationTests
{
    private static TenantWorkspaceMutationTarget MutationTargets(Fixture f, ITenantWorkspaceMutationObservationStore? files = null) => new(Operations(f),
        (ITenantSessionStore)f.Iam, f.RunConfiguration(), f.Authorizer, files ?? WorkspaceReader(f), f.Clock);

    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task Tenant_workspace_mutation_target_observes_private_current_state_without_borrowing_reads_or_granting_a_write(bool sqlite, bool working)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var s = await Setup(f, "general-assistant", 32); var frame = await ClaimArtifactRun(f, s);
        await GrantWorkspaceRuntimeReads(f, s, ["agents.read", "sessions.read"]); await SeedRuntimeWorkspace(f, s, working, "private.txt");
        var root = working ? "/working" : "/home"; var target = MutationTargets(f);
        var call = new ModelToolCall("exact-private-target", ToolCatalog.WorkspaceWrite, JsonSerializer.Serialize(new { path = root + "/private.txt", content = "user supplied replacement" }));
        var first = await target.PrepareAsync(frame, call); var next = await target.PrepareAsync(frame, call);
        Assert.Equal(first.ObservationHash, next.ObservationHash); Assert.Equal("workspaces.write", first.Permission);
        Assert.Equal(TenantWorkspaceRules.Scope(WorkspaceOwner(s, working)), first.Scope);
        Assert.False((await f.Authorizer.CheckAsync(s.Agent.PrincipalId, "workspaces.write", first.Scope)).Allowed);
        Assert.False((await f.Authorizer.CheckAsync(s.Agent.PrincipalId, "workspaces.read", first.Scope)).Allowed);
        Assert.Equal("Private model bytes: thử nghiệm", System.Text.Encoding.UTF8.GetString((await WorkspaceReader(f).InspectTenantAsync(WorkspaceOwner(s, working), root + "/private.txt", 1024)).Bytes));
        await SeedRuntimeWorkspace(f, s, working, "private.txt", "changed same private target"u8.ToArray());
        Assert.NotEqual(first.ObservationHash, (await target.PrepareAsync(frame, call)).ObservationHash);
        Assert.Equal("executor_read_required", (await Assert.ThrowsAsync<AccessDeniedException>(() => target.PrepareAsync(frame,
            new("patch-needs-own-read", ToolCatalog.WorkspacePatch, JsonSerializer.Serialize(new { path = root + "/private.txt", edits = new[] { new { oldText = "changed", newText = "new" } } }))).AsTask())).ReasonCode);
        Assert.Empty((await ((ITenantApprovalStore)f.Iam).ListApprovalsAsync(s.Tenant, null, null, new(0, 100))).Items);
        Assert.DoesNotContain(await f.Iam.ReadAuditAsync(s.Tenant, 200), a => a.Action == TenantWorkspaceEffectRules.Dispatch);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_workspace_mutation_target_rechecks_independent_current_parent_authority_after_observation(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var s = await Setup(f, "general-assistant", 32); var frame = await ClaimArtifactRun(f, s);
        var grant = await GrantWorkspaceRuntimeReads(f, s, ["agents.read", "sessions.read"]);
        var probe = new MutationObservationProbe(WorkspaceReader(f), async () => await f.Access.RevokeAssignmentAsync(s.Owner.PrincipalId, s.Tenant, grant.AssignmentId, (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision));
        var call = new ModelToolCall("private-race", ToolCatalog.WorkspaceWrite, """{"path":"/working/missing.txt","content":"literal"}""");
        Assert.Equal("executor_read_required", (await Assert.ThrowsAsync<AccessDeniedException>(() => MutationTargets(f, probe).PrepareAsync(frame, call).AsTask())).ReasonCode);
        Assert.Equal(1, probe.Calls);
        Assert.Empty((await WorkspaceReader(f).ListTenantAsync(WorkspaceOwner(s, true), "/working", null, 100)).Items);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_workspace_mutation_target_cross_root_copy_keeps_source_read_independent_and_rejects_foreign_owner_before_observation(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var s = await Setup(f, "general-assistant", 32); var frame = await ClaimArtifactRun(f, s);
        await GrantWorkspaceRuntimeReads(f, s, ["agents.read", "sessions.read"]);
        await GrantWorkspaceRuntimeReads(f, s, ["workspaces.read"], TenantWorkspaceRules.Home(s.Tenant, SpaceId(s)));
        await SeedRuntimeWorkspace(f, s, false, "source.txt");
        var probe = new MutationObservationProbe(WorkspaceReader(f), () => Task.CompletedTask); var service = MutationTargets(f, probe);
        var copy = new ModelToolCall("cross-root-target", ToolCatalog.WorkspaceCopy, """{"source":"/home/source.txt","destination":"/working/destination.txt"}""");
        var target = await service.PrepareAsync(frame, copy); Assert.Equal(2, probe.Calls);
        Assert.Equal(TenantWorkspaceRules.Working(s.Tenant, frame.SessionId), target.Scope);
        Assert.Equal("executor_read_required", (await Assert.ThrowsAsync<AccessDeniedException>(() => service.PrepareAsync(frame,
            copy with { ArgumentsJson = """{"source":"/working/destination.txt","destination":"/home/new.txt"}""" }).AsTask())).ReasonCode);
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.PrepareAsync(frame with { SessionId = Guid.NewGuid() }, copy).AsTask());
        Assert.Equal(2, probe.Calls);
        await Assert.ThrowsAsync<AccessDeniedException>(() => ((ITenantWorkspaceMutationObservationStore)WorkspaceReader(f)).ObserveMutationTenantAsync(
            new TenantWorkspaceReference.SpaceHome(Guid.NewGuid(), SpaceId(s)), [new("/home/source.txt", true, false)]).AsTask());
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_workspace_mutation_target_mixed_batch_never_uses_a_possible_delete_grant_for_secondary_write(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var s = await Setup(f, "general-assistant", 32); var frame = await ClaimArtifactRun(f, s);
        await GrantWorkspaceRuntimeReads(f, s, ["agents.read", "sessions.read"]);
        var probe = new MutationObservationProbe(WorkspaceReader(f), () => Task.CompletedTask); var service = MutationTargets(f, probe);
        var batch = new ModelToolCall("mixed-target", ToolCatalog.WorkspaceBatch, """{"operations":[{"op":"delete","path":"/working/old.txt"},{"op":"mkdir","path":"/working/new"}]}""");
        Assert.Equal("permission_denied", (await Assert.ThrowsAsync<AccessDeniedException>(() => service.PrepareAsync(frame, batch).AsTask())).ReasonCode); Assert.Equal(0, probe.Calls);
        await GrantWorkspaceRuntimeReads(f, s, ["workspaces.write"], TenantWorkspaceRules.Working(s.Tenant, frame.SessionId));
        var observed = await service.PrepareAsync(frame, batch); Assert.Equal("workspaces.delete", observed.Permission); Assert.Equal(1, probe.Calls);
        Assert.False((await f.Authorizer.CheckAsync(s.Agent.PrincipalId, "workspaces.delete", observed.Scope)).Allowed);
        Assert.Empty((await WorkspaceReader(f).ListTenantAsync(WorkspaceOwner(s, true), "/working", null, 100)).Items);
    }

    [Theory] [InlineData(false, false, "Disabled")] [InlineData(true, false, "Disabled")]
    [InlineData(false, true, "Disabled")] [InlineData(true, true, "Disabled")]
    [InlineData(false, false, "ReadOnly")] [InlineData(true, false, "ReadOnly")]
    [InlineData(false, true, "ReadOnly")] [InlineData(true, true, "ReadOnly")]
    public async Task Tenant_workspace_mutation_target_current_HomeAccess_narrowing_blocks_before_or_after_private_observation(bool sqlite, bool afterRead, string policy)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var s = await Setup(f, "general-assistant", 32); var frame = await ClaimArtifactRun(f, s);
        await GrantWorkspaceRuntimeReads(f, s, ["agents.read", "sessions.read", "workspaces.read", "workspaces.write"]);
        async Task Restrict()
        {
            var state = (await f.Iam.ReadTenantAsync(s.Tenant))!;
            Assert.NotNull(await ((ITenantAgentStore)f.Iam).TryChangeTenantAgentConfigurationAsync(state with { Tenant = state.Tenant with { Revision = state.Tenant.Revision + 1 } }, state.Tenant.Revision,
                new(s.Agent.Instance.InstanceId, s.Agent.Instance.Revision, AutomationPolicyChanges: [], SetSettingsOverrides: true, SettingsOverrides: new(HomeAccess: new(policy)),
                    Initiator: new(s.Owner.AccountId, s.Owner.AuthenticationSessionId, s.Owner.PrincipalId)), f.Clock.GetUtcNow(),
                new(f.Ids.NewId(), s.Tenant, s.Owner.PrincipalId, "agents.settings.update", s.Agent.Instance.InstanceId.ToString("D"), "allowed", "settings_changed", f.Clock.GetUtcNow())));
        }
        var probe = new MutationObservationProbe(WorkspaceReader(f), afterRead ? Restrict : () => Task.CompletedTask);
        if (!afterRead) await Restrict();
        var call = new ModelToolCall("current-home-policy", ToolCatalog.WorkspaceWrite, """{"path":"/home/new.txt","content":"literal"}""");
        Assert.Equal(policy == "Disabled" ? "home_access_disabled" : "home_read_only", (await Assert.ThrowsAsync<AccessDeniedException>(() => MutationTargets(f, probe).PrepareAsync(frame, call).AsTask())).ReasonCode);
        Assert.Equal(afterRead ? 1 : 0, probe.Calls);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_workspace_mutation_target_does_not_disclose_private_native_error_messages(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var s = await Setup(f, "general-assistant", 32); var frame = await ClaimArtifactRun(f, s);
        await GrantWorkspaceRuntimeReads(f, s, ["agents.read", "sessions.read"]);
        var probe = new MutationObservationProbe(WorkspaceReader(f), () => throw new IOException("Private native target and content details"));
        var error = await Assert.ThrowsAsync<AccessDeniedException>(() => MutationTargets(f, probe).PrepareAsync(frame,
            new("private-target-error", ToolCatalog.WorkspaceWrite, """{"path":"/working/new.txt","content":"literal"}""")).AsTask());
        Assert.Equal("workspace_observation_unavailable", error.ReasonCode); Assert.DoesNotContain("Private native", error.Message);
    }

    private sealed class MutationObservationProbe(ITenantWorkspaceMutationObservationStore inner, Func<Task> after) : ITenantWorkspaceMutationObservationStore
    {
        public int Calls { get; private set; }
        public async ValueTask<WorkspaceMutationObservation> ObserveMutationTenantAsync(TenantWorkspaceReference owner, IReadOnlyList<WorkspaceMutationObservationProbe> probes, CancellationToken ct = default)
        { Calls++; var result = await inner.ObserveMutationTenantAsync(owner, probes, ct); await after(); return result; }
    }
}
