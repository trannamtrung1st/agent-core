using System.Text;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantDefinitionIntegrationTests
{
    private static TenantSandboxRuntimeReadService ModelSandboxReads(Fixture f, RuntimeSandboxSetup s, ITenantSandboxRuntimeStore? store = null,
        ISandboxImageAuthority? images = null, ISandboxReadiness? readiness = null)
    {
        var p = SetupProfiles(f);
        var operations = new TenantOperationAuthorizer(f.Iam, (ITenantRunStore)f.Iam, (ITenantAgentStore)f.Iam, f.Authorizer,
            new((ITenantAuthorizationAuditStore)f.Iam, f.Ids, TimeProvider.System));
        return new(f.Iam, (ITenantSessionStore)f.Iam, store ?? (ITenantSandboxRuntimeStore)f.Iam, f.Identity, f.Authorizer,
            operations, s.Resolver, (ITenantSandboxProfileStore)f.Iam, images ?? p.Images, readiness ?? p.Ready, TimeProvider.System);
    }
    private static TenantToolAuthorization ModelSandboxTools(Fixture f, RuntimeSandboxSetup s, TenantSandboxRuntimeReadService? reads = null)
    {
        var operations = new TenantOperationAuthorizer(f.Iam, (ITenantRunStore)f.Iam, (ITenantAgentStore)f.Iam, f.Authorizer,
            new((ITenantAuthorizationAuditStore)f.Iam, f.Ids, TimeProvider.System));
        return new((ITenantRunStore)f.Iam, operations, configuration: s.Resolver, sandboxReads: reads ?? ModelSandboxReads(f, s));
    }
    private static ToolExecutionAdmission SandboxReadAdmission(RuntimeSandboxSetup s) => new(false, TriggerKind.UserTurn,
        AgentInstanceId: s.Session.AgentInstanceId, AgentRunId: s.Run.AgentRunId, OwnedSessionId: s.Session.SessionId,
        TenantId: s.Tenant, AgentRunGeneration: s.Run.Claim!.Generation);
    private static async Task<Guid> GrantSandboxRead(Fixture f, RuntimeSandboxSetup s, string permission, AccessScope scope)
    {
        async ValueTask<long> Revision() => (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision;
        var role = await f.Access.SaveRoleAsync(s.Human.PrincipalId, s.Tenant, null, "Exact model " + permission, [permission], await Revision());
        return (await f.Access.AssignRoleAsync(s.Human.PrincipalId, s.Tenant, s.Session.ExecutorPrincipalId, role.RoleId, scope, await Revision())).AssignmentId;
    }

    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task Tenant_Sandbox_model_inspection_uses_exact_read_scopes_hides_owner_details_and_never_mutates_facts(bool sqlite, bool large)
    {
        await using var f = await Fixture.Create(sqlite); var s = await PrepareRuntimeSandbox(f, inspection: true); await ReadySandbox(f, s);
        var store = (ITenantSandboxRuntimeStore)f.Iam;
        var execution = RuntimeExecution(f, s) with { CommandText = "OWNER_ONLY_COMMAND" };
        Assert.True(await store.TryReserveTenantSandboxExecutionAsync(s.Write, execution, DateTimeOffset.UtcNow));
        Assert.True(await store.TrySaveTenantSandboxExecutionFactsAsync(s.Tenant, execution with { Revision = 2, State = SandboxExecutionState.Completed,
            Stdout = large ? new string('界', 10000) : "factual output", Stderr = "factual error", ExitCode = 7, TerminationConfirmed = true, FinishedAt = DateTimeOffset.UtcNow }, 1));
        var snapshot = (await store.ReadTenantSandboxRuntimeAsync(s.Tenant, s.Session.SessionId))!;
        var lease = snapshot.Lease! with { Revision = snapshot.Lease.Revision + 1, State = SandboxLeaseState.Ready, ActiveExecutionId = null,
            Initialization = new("Succeeded", s.Lease.Profile.Id, 1, 1, Stdout: "OWNER_ONLY_SETUP") };
        Assert.True(await store.TrySaveTenantSandboxLeaseFactsAsync(s.Tenant, lease, snapshot.Lease.Revision));
        var tools = ModelSandboxTools(f, s); var admission = SandboxReadAdmission(s);
        async Task<JsonDocument> Call(string name, string args = "{}", int budget = ToolLimits.MaxOutputBytes) => JsonDocument.Parse((await tools.ExecuteAsync(s.Session.SessionId, new("read", name, args), admission, default, budget))!);
        using (var denied = await Call(ToolCatalog.SandboxProfiles)) Assert.Equal("forbidden", denied.RootElement.GetProperty("error").GetString());
        await GrantSandboxRead(f, s, "sessions.read", TenantSessionRules.Scope(s.Tenant, s.Session.SessionId));
        using (var denied = await Call(ToolCatalog.SandboxProfiles)) Assert.Equal("forbidden", denied.RootElement.GetProperty("error").GetString());
        await GrantSandboxRead(f, s, "sandbox.read", AccessScope.ForResource(s.Tenant, "agent", s.Session.AgentInstanceId.ToString("D")));
        using (var profiles = await Call(ToolCatalog.SandboxProfiles))
        {
            var p = Assert.Single(profiles.RootElement.GetProperty("profiles").EnumerateArray()); Assert.Equal(s.Lease.Profile.Id, p.GetProperty("id").GetString());
            Assert.True(p.GetProperty("available").GetBoolean()); Assert.False(profiles.RootElement.GetProperty("liveAuthorityConfirmed").GetBoolean());
            Assert.DoesNotContain("initialization", profiles.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);
        }
        using (var status = await Call(ToolCatalog.SandboxStatus))
        {
            Assert.Equal("Ready", status.RootElement.GetProperty("environment").GetProperty("storedState").GetString());
            Assert.Equal("Succeeded", status.RootElement.GetProperty("initializationState").GetString());
            Assert.DoesNotContain("OWNER_ONLY", status.RootElement.GetRawText()); Assert.DoesNotContain("factual output", status.RootElement.GetRawText());
        }
        using (var status = await Call(ToolCatalog.SandboxProcessStatus, JsonSerializer.Serialize(new { executionId = execution.Id }), large ? 6000 : ToolLimits.MaxOutputBytes))
        {
            var result = status.RootElement.GetProperty("execution");
            if (large) { Assert.True(result.GetProperty("truncated").GetBoolean()); Assert.NotEmpty(result.GetProperty("stdout").GetString()!); Assert.DoesNotContain("�", result.GetRawText()); Assert.True(Encoding.UTF8.GetByteCount(status.RootElement.GetRawText()) <= 6000); }
            else Assert.Equal("factual output", result.GetProperty("stdout").GetString());
            Assert.Equal(7, result.GetProperty("exitCode").GetInt32()); Assert.Equal("", result.GetProperty("commandText").GetString());
            Assert.DoesNotContain("OWNER_ONLY", status.RootElement.GetRawText()); Assert.True(status.RootElement.GetProperty("observationOnly").GetBoolean());
        }
        using (var denied = await Call(ToolCatalog.SandboxProcessStatus, JsonSerializer.Serialize(new { executionId = Guid.NewGuid() }))) Assert.Equal("forbidden", denied.RootElement.GetProperty("error").GetString());
        using (var denied = await Call(ToolCatalog.SandboxStatus, "{\"sessionId\":\"untrusted\"}")) Assert.Equal("invalid", denied.RootElement.GetProperty("error").GetString());
        var stale = await tools.ExecuteAsync(s.Session.SessionId, new("read", ToolCatalog.SandboxStatus, "{}"), admission with { AgentRunGeneration = Guid.NewGuid() }, default);
        Assert.Contains("forbidden", stale); Assert.DoesNotContain("OWNER_ONLY", stale);
        var final = (await store.ReadTenantSandboxRuntimeAsync(s.Tenant, s.Session.SessionId))!;
        Assert.Equal(lease, final.Lease); Assert.Equal(2, final.Execution!.Revision); Assert.Equal("OWNER_ONLY_COMMAND", final.Execution.CommandText);
        Assert.Null(await f.Sandbox.GetLeaseAsync(s.Session.SessionId));
    }
    private sealed class InspectionImageHook(ISandboxImageAuthority inner, Func<Task> during) : ISandboxImageAuthority
    {
        public ValueTask<AgentCore.Domain.Definitions.SandboxProfile> ResolveAsync(AgentCore.Domain.Definitions.SandboxProfile profile, CancellationToken ct = default) => throw new InvalidOperationException("Inspection cannot prepare an image.");
        public async ValueTask<SandboxImageAvailability> InspectAsync(AgentCore.Domain.Definitions.SandboxProfile profile, CancellationToken ct = default, bool requireVerification = true)
        { var result = await inner.InspectAsync(profile, ct, requireVerification); await during(); return result; }
    }
    private sealed class InspectionOffline : ISandboxReadiness
    { public bool IsAvailable => false; public string State => "Unavailable"; public string SafeMessage => "Controlled offline observation"; }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_Sandbox_model_profile_discovery_filters_revoked_defaults_and_pending_selection_independently(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var s = await PrepareRuntimeSandbox(f, alternate: true, inspection: true); await ReadySandbox(f, s);
        var store = (ITenantSandboxRuntimeStore)f.Iam; var p = SetupProfiles(f);
        await GrantSandboxRead(f, s, "sessions.read", TenantSessionRules.Scope(s.Tenant, s.Session.SessionId));
        await GrantSandboxRead(f, s, "sandbox.read", AccessScope.ForResource(s.Tenant, "agent", s.Session.AgentInstanceId.ToString("D")));
        async ValueTask<long> Revision() => (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision;
        async Task Revoke(string id)
        {
            var state = (await f.Iam.ReadTenantAsync(s.Tenant))!;
            var assignment = Assert.Single(state.Assignments, a => a.PrincipalId == s.Session.ExecutorPrincipalId && a.Scope == TenantSandboxProfileRules.Scope(s.Tenant, id));
            await f.Access.RevokeAssignmentAsync(s.Human.PrincipalId, s.Tenant, assignment.AssignmentId, await Revision());
        }
        var tools = ModelSandboxTools(f, s); var admission = SandboxReadAdmission(s);
        await Revoke(s.Lease.Profile.Id);
        var filtered = (await tools.ExecuteAsync(s.Session.SessionId, new("read", ToolCatalog.SandboxProfiles, "{}"), admission, default))!;
        using (var result = JsonDocument.Parse(filtered))
        {
            Assert.Equal(s.Alternate!.Id, Assert.Single(result.RootElement.GetProperty("profiles").EnumerateArray()).GetProperty("id").GetString());
            Assert.Equal(JsonValueKind.Null, result.RootElement.GetProperty("defaultProfileId").ValueKind); Assert.Equal(JsonValueKind.Null, result.RootElement.GetProperty("selectedProfileId").ValueKind);
            Assert.True(result.RootElement.GetProperty("selectionRestricted").GetBoolean()); Assert.DoesNotContain(s.Lease.Profile.Id, filtered);
        }
        await GrantSandboxRead(f, s, "sandbox.read", TenantSandboxProfileRules.Scope(s.Tenant, s.Lease.Profile.Id));
        var before = (await store.ReadTenantSandboxRuntimeAsync(s.Tenant, s.Session.SessionId))!;
        await new TenantSandboxControlService(f.Iam, (ITenantSessionStore)f.Iam, store, f.Identity, f.Authorizer, f.Ids, TimeProvider.System, SetupResolver(f), p.Images, p.Ready, p.Host)
            .RequestProfileSwitchAsync(s.Tenant, s.Human, new(s.Session.SessionId, before.SessionRevision, before.Lease!.Generation, before.Lease.Revision, null, s.Alternate!.Id, 1, 1), await Revision());
        await Revoke(s.Alternate.Id);
        var observed = (await tools.ExecuteAsync(s.Session.SessionId, new("read", ToolCatalog.SandboxStatus, "{}"), admission, default))!;
        using (var result = JsonDocument.Parse(observed))
        {
            var environment = result.RootElement.GetProperty("environment"); Assert.Equal("Terminating", environment.GetProperty("storedState").GetString());
            Assert.True(environment.GetProperty("selectionRestricted").GetBoolean()); Assert.Equal(JsonValueKind.Null, environment.GetProperty("selection").ValueKind);
            Assert.DoesNotContain(s.Alternate.Id, observed); Assert.DoesNotContain(s.Alternate.Name, observed);
        }
        Assert.Equal(s.Alternate.Id, (await store.ReadTenantSandboxRuntimeAsync(s.Tenant, s.Session.SessionId))!.Lease!.SelectedProfileId);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_Sandbox_model_discovery_does_not_expand_admission_and_rechecks_revocation_after_image_metadata(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var s = await PrepareRuntimeSandbox(f, verify: false, inspection: true); var p = SetupProfiles(f);
        await GrantSandboxRead(f, s, "sessions.read", TenantSessionRules.Scope(s.Tenant, s.Session.SessionId));
        await GrantSandboxRead(f, s, "sandbox.read", AccessScope.ForResource(s.Tenant, "agent", s.Session.AgentInstanceId.ToString("D")));
        async ValueTask<long> Revision() => (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision;
        await p.Verify.VerifyAsync(s.Tenant, s.Lease.Profile.Id, s.Human, await Revision(), 1);
        var tools = ModelSandboxTools(f, s); var admission = SandboxReadAdmission(s);
        using (var result = JsonDocument.Parse((await tools.ExecuteAsync(s.Session.SessionId, new("read", ToolCatalog.SandboxProfiles, "{}"), admission, default))!))
        {
            var profile = Assert.Single(result.RootElement.GetProperty("profiles").EnumerateArray()); Assert.Equal("NotAdmitted", profile.GetProperty("availability").GetString()); Assert.False(profile.GetProperty("available").GetBoolean());
        }
        var hook = new InspectionImageHook(p.Images, async () => {
            var state = (await f.Iam.ReadTenantAsync(s.Tenant))!; var assignment = Assert.Single(state.Assignments, a => a.PrincipalId == s.Session.ExecutorPrincipalId && a.Scope == TenantSandboxProfileRules.Scope(s.Tenant, s.Lease.Profile.Id));
            await f.Access.RevokeAssignmentAsync(s.Human.PrincipalId, s.Tenant, assignment.AssignmentId, await Revision());
        });
        tools = ModelSandboxTools(f, s, ModelSandboxReads(f, s, images: hook));
        var denied = (await tools.ExecuteAsync(s.Session.SessionId, new("read", ToolCatalog.SandboxProfiles, "{}"), admission, default))!;
        Assert.Contains("stale_revision", denied); Assert.DoesNotContain(s.Lease.Profile.Id, denied); Assert.DoesNotContain(s.Lease.Profile.Name, denied);
        using (var recovered = JsonDocument.Parse((await ModelSandboxTools(f, s).ExecuteAsync(s.Session.SessionId, new("read", ToolCatalog.SandboxProfiles, "{}"), admission, default))!))
            Assert.Empty(recovered.RootElement.GetProperty("profiles").EnumerateArray());
        Assert.Null((await ((ITenantSandboxRuntimeStore)f.Iam).ReadTenantSandboxRuntimeAsync(s.Tenant, s.Session.SessionId))!.Lease);
    }

    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task Tenant_Sandbox_model_inspection_remains_readable_offline_but_cannot_bypass_stronger_review_policy(bool sqlite, bool independent)
    {
        await using var f = await Fixture.Create(sqlite); var s = await PrepareRuntimeSandbox(f, inspection: true); await ReadySandbox(f, s);
        await GrantSandboxRead(f, s, "sessions.read", TenantSessionRules.Scope(s.Tenant, s.Session.SessionId));
        await GrantSandboxRead(f, s, "sandbox.read", AccessScope.ForResource(s.Tenant, "agent", s.Session.AgentInstanceId.ToString("D")));
        var tools = ModelSandboxTools(f, s, ModelSandboxReads(f, s, readiness: new InspectionOffline())); var admission = SandboxReadAdmission(s);
        using (var status = JsonDocument.Parse((await tools.ExecuteAsync(s.Session.SessionId, new("read", ToolCatalog.SandboxStatus, "{}"), admission, default))!))
            Assert.Equal("Ready", status.RootElement.GetProperty("environment").GetProperty("storedState").GetString());
        var policies = new TenantOperationPolicyService(f.Iam, (ITenantOperationPolicyStore)f.Iam, f.Authorizer, f.Identity, f.Ids, TimeProvider.System);
        await policies.SaveAsync(s.Tenant, SandboxHuman(s), "sandbox.read", new(OperationApprovalMode.Always, independent), (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision, 0);
        var denied = (await tools.ExecuteAsync(s.Session.SessionId, new("read", ToolCatalog.SandboxStatus, "{}"), admission, default))!;
        Assert.Contains(independent ? "no_eligible_approver" : "sandbox_read_review_required", denied); Assert.DoesNotContain(s.Lease.Profile.Name, denied);
    }

    [Theory] [InlineData(false, "keep")] [InlineData(true, "keep")] [InlineData(false, "remove")] [InlineData(true, "remove")]
    [InlineData(false, "add")] [InlineData(true, "add")]
    public async Task Tenant_Sandbox_model_stored_inspection_survives_execution_policy_removal_but_cannot_expand_saved_or_current_read_capability(bool sqlite, string mode)
    {
        await using var f = await Fixture.Create(sqlite); var s = await PrepareRuntimeSandbox(f, inspection: mode != "add"); await ReadySandbox(f, s);
        await GrantSandboxRead(f, s, "sessions.read", TenantSessionRules.Scope(s.Tenant, s.Session.SessionId));
        await GrantSandboxRead(f, s, "sandbox.read", AccessScope.ForResource(s.Tenant, "agent", s.Session.AgentInstanceId.ToString("D")));
        async ValueTask<long> Revision() => (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision;
        var draft = await f.Service.ForkAsync(s.Human.PrincipalId, s.Tenant, Guid.Parse(s.Run.DefinitionId), 1, await Revision());
        draft = await f.Service.UpdateAsync(s.Human.PrincipalId, s.Tenant, draft.DraftId, draft.Candidate with {
            SandboxPolicy = null, Environment = new(Capabilities: new("Selected", mode == "remove" ? [] : [ToolCatalog.SandboxProfiles, ToolCatalog.SandboxStatus, ToolCatalog.SandboxProcessStatus]))
        }, await Revision(), draft.Revision);
        await f.Service.PublishAsync(s.Human.PrincipalId, s.Tenant, draft.DraftId, await Revision(), draft.Revision);
        await ConfigurationServices(f).Configuration.AdoptVersionAsync(s.Human.PrincipalId, s.Tenant, s.Session.AgentInstanceId, 2, await Revision(), 1);
        var tools = ModelSandboxTools(f, s); var admission = SandboxReadAdmission(s);
        var store = (ITenantSandboxRuntimeStore)f.Iam; var before = (await store.ReadTenantSandboxRuntimeAsync(s.Tenant, s.Session.SessionId))!;
        var observed = (await tools.ExecuteAsync(s.Session.SessionId, new("read", ToolCatalog.SandboxStatus, "{}"), admission, default))!;
        using (var result = JsonDocument.Parse(observed))
        {
            if (mode == "keep") Assert.Equal("Ready", result.RootElement.GetProperty("environment").GetProperty("storedState").GetString());
            else { Assert.Equal("sandbox_policy_changed", result.RootElement.GetProperty("reasonCode").GetString()); Assert.DoesNotContain(s.Lease.Profile.Name, observed); }
        }
        if (mode == "keep")
        {
            using var profiles = JsonDocument.Parse((await tools.ExecuteAsync(s.Session.SessionId, new("read", ToolCatalog.SandboxProfiles, "{}"), admission, default))!);
            Assert.Empty(profiles.RootElement.GetProperty("profiles").EnumerateArray()); Assert.Equal(JsonValueKind.Null, profiles.RootElement.GetProperty("defaultProfileId").ValueKind);
        }
        Assert.Equal(before.Lease, (await store.ReadTenantSandboxRuntimeAsync(s.Tenant, s.Session.SessionId))!.Lease);
    }

}
