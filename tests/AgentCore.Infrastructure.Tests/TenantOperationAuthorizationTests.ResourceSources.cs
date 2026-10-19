using System.Text;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantOperationAuthorizationTests
{
    private sealed record ResourceCase(Environment Setup, string Key, string Path, byte[] Bytes);
    private static TenantResourceSourceTarget ResourceSources(Fixture f, ITenantResourceSourceContentStore? content = null, IBuiltInAgentDefinitionStore? currentBuiltIns = null) =>
        new(Operations(f), (ITenantSessionStore)f.Iam, f.RunConfiguration(currentBuiltIns), content ?? (ITenantResourceSourceContentStore)f.RunContent, f.Authorizer, f.Clock);
    private static async Task<ResourceCase> ResourceSourceCase(Fixture f, Environment? existing = null, byte[]? data = null)
    {
        var s = existing ?? await Setup(f, "general-assistant", 32); byte[] bytes = data ?? Encoding.UTF8.GetBytes("Private bound resource: thử nghiệm");
        var added = await f.AgentConfiguration().UpsertResourceAsync(s.Owner.PrincipalId, s.Tenant, s.Agent.Instance.InstanceId,
            (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision, 1, new(null, null, "source.txt", AgentDefinitionResourceKind.Reference, "text/plain", bytes), initiator: s.Owner);
        var resource = added.Resource!;
        var agent = (await ((ITenantAgentStore)f.Iam).FindTenantAgentAsync(s.Tenant, s.Agent.Instance.InstanceId))!;
        var session = await f.Sessions.CreateAsync(s.Tenant, agent.Instance.InstanceId, s.Owner, SessionMode.Text,
            (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision, agent.Instance.Revision);
        var admitted = await f.RunService().SubmitTextAsync(s.Tenant, session.Owner.SessionId, s.Owner, f.Ids.NewId(), "Read this bound resource",
            (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision, 1);
        var run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, admitted.RunId))!;
        return new(s with { Agent = agent, Run = run }, resource.Key, resource.VirtualPath, bytes);
    }
    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task Tenant_resource_source_captures_only_an_actual_pinned_current_binding_and_defensive_bytes(bool sqlite, bool native)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var v = await ResourceSourceCase(f); var s = v.Setup;
        var frame = await ClaimArtifactRun(f, s); await GrantWorkspaceRuntimeReads(f, s, ["sessions.read", "agents.read", "resources.read"]);
        ITenantResourceSourceContentStore store = (ITenantResourceSourceContentStore)f.RunContent;
        if (native)
        {
            var files = new FileDefinitionResourceContentStore(Path.Combine(f.Root, "resource-blobs"));
            var pinned = s.Run.Admission.Configuration!.Resources.Single(r => r.Key == v.Key);
            await files.StoreVerifiedAsync(pinned.ContentSha256, v.Bytes); store = files;
        }
        var result = await ResourceSources(f, store).CaptureAsync(frame, ToolCatalog.WorkspaceRead, v.Path);
        Assert.Equal(v.Bytes, result.CopyContent()); Assert.Equal(s.Tenant, result.Observation.TenantId);
        Assert.Equal(s.Agent.Instance.InstanceId, result.Observation.InstanceId); Assert.Equal(frame.SessionId, result.Observation.SessionId);
        Assert.Equal(v.Key, result.Observation.Resource.Key); Assert.Equal(v.Path, result.Observation.Resource.VirtualPath);
        var mutable = result.CopyContent(); mutable[0] = 0; Assert.Equal(v.Bytes, result.CopyContent());
        Assert.Empty((await ((ITenantApprovalStore)f.Iam).ListApprovalsAsync(s.Tenant, null, null, new(0, 20))).Items);
    }

    [Theory] [InlineData(false, "sessions.read")] [InlineData(true, "sessions.read")]
    [InlineData(false, "agents.read")] [InlineData(true, "agents.read")]
    [InlineData(false, "resources.read")] [InlineData(true, "resources.read")]
    public async Task Tenant_resource_source_requires_each_independent_executor_read_before_byte_IO(bool sqlite, string missing)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var v = await ResourceSourceCase(f); var s = v.Setup;
        var frame = await ClaimArtifactRun(f, s);
        await GrantWorkspaceRuntimeReads(f, s, new[] { "sessions.read", "agents.read", "resources.read" }.Where(p => p != missing).ToArray());
        var probe = new ResourceContentProbe((ITenantResourceSourceContentStore)f.RunContent);
        Assert.Equal("executor_read_required", (await Assert.ThrowsAsync<AccessDeniedException>(() => ResourceSources(f, probe).CaptureAsync(frame, ToolCatalog.WorkspaceRead, v.Path).AsTask())).ReasonCode);
        Assert.Equal(0, probe.Reads);
    }
    [Theory] [InlineData(false, "sessions.read")] [InlineData(true, "sessions.read")]
    [InlineData(false, "agents.read")] [InlineData(true, "agents.read")]
    [InlineData(false, "resources.read")] [InlineData(true, "resources.read")]
    public async Task Tenant_resource_source_requires_current_policy_on_each_compound_source_scope(bool sqlite, string permission)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var v = await ResourceSourceCase(f); var s = v.Setup;
        var frame = await ClaimArtifactRun(f, s); await GrantWorkspaceRuntimeReads(f, s, ["sessions.read", "agents.read", "resources.read"]);
        await Policies(f).SaveAsync(s.Tenant, s.Owner, permission, new(OperationApprovalMode.Always, false),
            (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision, 0);
        var probe = new ResourceContentProbe((ITenantResourceSourceContentStore)f.RunContent);
        Assert.Equal("resource_read_review_required", (await Assert.ThrowsAsync<AccessDeniedException>(() => ResourceSources(f, probe).CaptureAsync(frame, ToolCatalog.WorkspaceRead, v.Path).AsTask())).ReasonCode);
        Assert.Equal(0, probe.Reads);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_resource_source_uses_exact_child_grant_without_workspace_or_recipient_authority(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var v = await ResourceSourceCase(f); var s = v.Setup;
        var frame = await ClaimArtifactRun(f, s); await GrantWorkspaceRuntimeReads(f, s, ["sessions.read", "agents.read"]);
        var sourceGrant = await GrantWorkspaceRuntimeReads(f, s, ["resources.read"], TenantAgentChildScopes.Scope(s.Tenant, s.Agent.Instance.InstanceId, "resource", v.Key));
        Assert.False((await f.Authorizer.CheckAsync(s.Agent.PrincipalId, "workspaces.read", TenantWorkspaceRules.Home(s.Tenant, s.Agent.Instance.InstanceId))).Allowed);
        Assert.Equal(v.Bytes, (await ResourceSources(f).CaptureAsync(frame, ToolCatalog.WorkspaceRead, v.Path)).CopyContent());
        await f.Access.RevokeAssignmentAsync(s.Owner.PrincipalId, s.Tenant, sourceGrant.AssignmentId, (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
        Assert.Equal("executor_read_required", (await Assert.ThrowsAsync<AccessDeniedException>(() => ResourceSources(f).CaptureAsync(frame, ToolCatalog.WorkspaceRead, v.Path).AsTask())).ReasonCode);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_resource_source_inherited_binding_keeps_the_admitted_Definition_origin(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var s = await Setup(f, "general-assistant", 32);
        var resource = s.Run.Admission.Configuration!.Resources.First(r => r.Key.StartsWith("definition:", StringComparison.Ordinal));
        var frame = await ClaimArtifactRun(f, s); await GrantWorkspaceRuntimeReads(f, s, ["sessions.read", "agents.read"]);
        await GrantWorkspaceRuntimeReads(f, s, ["resources.read"], TenantAgentChildScopes.Scope(s.Tenant, s.Agent.Instance.InstanceId, "resource", resource.Key));
        var captured = await ResourceSources(f).CaptureAsync(frame, ToolCatalog.WorkspaceRead, resource.VirtualPath);
        Assert.Equal(resource, captured.Observation.Resource); Assert.Equal(s.Run.DefinitionId, captured.Observation.DefinitionId);
        Assert.Equal(s.Run.DefinitionVersion, captured.Observation.DefinitionVersion); Assert.Equal(resource.ByteLength, captured.CopyContent().LongLength);
    }

    [Theory] [InlineData(false, "rewrite")] [InlineData(true, "rewrite")]
    [InlineData(false, "disable")] [InlineData(true, "disable")]
    [InlineData(false, "revoke")] [InlineData(true, "revoke")]
    [InlineData(false, "security")] [InlineData(true, "security")]
    public async Task Tenant_resource_source_withholds_bytes_after_binding_or_authority_changes_during_IO(bool sqlite, string change)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var v = await ResourceSourceCase(f); var s = v.Setup;
        var frame = await ClaimArtifactRun(f, s); var grant = await GrantWorkspaceRuntimeReads(f, s, ["sessions.read", "agents.read", "resources.read"]);
        var probe = new ResourceContentProbe((ITenantResourceSourceContentStore)f.RunContent, async () =>
        {
            var revision = (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision;
            if (change == "revoke") await f.Access.RevokeAssignmentAsync(s.Owner.PrincipalId, s.Tenant, grant.AssignmentId, revision);
            else if (change == "security") await f.Access.SaveRoleAsync(s.Owner.PrincipalId, s.Tenant, null, "Unrelated changed security", ["sessions.read"], revision);
            else if (change == "disable") await f.AgentConfiguration().ChangeResourceAsync(s.Owner.PrincipalId, s.Tenant, s.Agent.Instance.InstanceId,
                v.Key, false, false, revision, s.Agent.Instance.Revision, 1, initiator: s.Owner);
            else await f.AgentConfiguration().UpsertResourceAsync(s.Owner.PrincipalId, s.Tenant, s.Agent.Instance.InstanceId, revision, s.Agent.Instance.Revision,
                new(Guid.Parse(v.Key[9..]), 1, "source.txt", AgentDefinitionResourceKind.Reference, "text/plain", Encoding.UTF8.GetBytes("New source bytes")), initiator: s.Owner);
        });
        if (change == "revoke") Assert.Equal("executor_read_required", (await Assert.ThrowsAsync<AccessDeniedException>(() => ResourceSources(f, probe).CaptureAsync(frame, ToolCatalog.WorkspaceRead, v.Path).AsTask())).ReasonCode);
        else Assert.Equal("resource_source_changed", (await Assert.ThrowsAsync<SecurityConflictException>(() => ResourceSources(f, probe).CaptureAsync(frame, ToolCatalog.WorkspaceRead, v.Path).AsTask())).ReasonCode);
        Assert.Equal(1, probe.Reads);
    }
    [Theory] [InlineData(false, "generation")] [InlineData(true, "generation")]
    [InlineData(false, "tenant")] [InlineData(true, "tenant")]
    [InlineData(false, "instance")] [InlineData(true, "instance")]
    [InlineData(false, "session")] [InlineData(true, "session")]
    public async Task Tenant_resource_source_rejects_untrusted_frame_before_byte_IO(bool sqlite, string field)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var v = await ResourceSourceCase(f); var s = v.Setup;
        var frame = await ClaimArtifactRun(f, s); await GrantWorkspaceRuntimeReads(f, s, ["sessions.read", "agents.read", "resources.read"]);
        var owner = field == "tenant" ? AgentRunOwner.ForTenant(f.Ids.NewId(), s.Agent.Instance.InstanceId, s.Agent.PrincipalId)
            : field == "instance" ? AgentRunOwner.ForTenant(s.Tenant, f.Ids.NewId(), s.Agent.PrincipalId) : frame.Owner;
        var fake = new TenantRuntimeFrame(owner, field == "session" ? f.Ids.NewId() : frame.SessionId, frame.RunId, field == "generation" ? f.Ids.NewId() : frame.Generation);
        var probe = new ResourceContentProbe((ITenantResourceSourceContentStore)f.RunContent);
        await Assert.ThrowsAsync<AccessDeniedException>(() => ResourceSources(f, probe).CaptureAsync(fake, ToolCatalog.WorkspaceRead, v.Path).AsTask()); Assert.Equal(0, probe.Reads);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_resource_source_rejects_an_unadmitted_path_and_current_disabled_binding_without_blob_IO(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var v = await ResourceSourceCase(f); var s = v.Setup;
        var frame = await ClaimArtifactRun(f, s); await GrantWorkspaceRuntimeReads(f, s, ["sessions.read", "agents.read", "resources.read"]);
        var probe = new ResourceContentProbe((ITenantResourceSourceContentStore)f.RunContent);
        await Assert.ThrowsAsync<AccessDeniedException>(() => ResourceSources(f, probe).CaptureAsync(frame, ToolCatalog.WorkspaceRead, v.Path + ".other").AsTask());
        foreach (var invalid in new[] { "/home/source.txt", "/agent/resources", "/agent/resources/../source.txt", "/agent/resources//source.txt", "/agent/resources/*", "/agent/resources/source.txt/", "/agent/resources/a\\b", "/agent/resources/a\nfile" })
            await Assert.ThrowsAsync<ArgumentException>(() => ResourceSources(f, probe).CaptureAsync(frame, ToolCatalog.WorkspaceRead, invalid).AsTask());
        await f.AgentConfiguration().ChangeResourceAsync(s.Owner.PrincipalId, s.Tenant, s.Agent.Instance.InstanceId, v.Key, false, false,
            (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision, s.Agent.Instance.Revision, 1, initiator: s.Owner);
        Assert.Equal("resource_source_changed", (await Assert.ThrowsAsync<SecurityConflictException>(() => ResourceSources(f, probe).CaptureAsync(frame, ToolCatalog.WorkspaceRead, v.Path).AsTask())).ReasonCode);
        Assert.Equal(0, probe.Reads);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_resource_source_rechecks_authority_after_the_final_metadata_read(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var v = await ResourceSourceCase(f); var s = v.Setup;
        var frame = await ClaimArtifactRun(f, s); var grant = await GrantWorkspaceRuntimeReads(f, s, ["sessions.read", "agents.read", "resources.read"]);
        var current = new ResourceBuiltInProbe(f.BuiltIns, async () =>
            await f.Access.RevokeAssignmentAsync(s.Owner.PrincipalId, s.Tenant, grant.AssignmentId, (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision));
        var error = await Assert.ThrowsAsync<SecurityConflictException>(() => ResourceSources(f, currentBuiltIns: current).CaptureAsync(frame, ToolCatalog.WorkspaceRead, v.Path).AsTask());
        Assert.Equal("resource_source_changed", error.ReasonCode); Assert.Equal(2, current.Reads);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_resource_source_data_does_not_enable_unconverted_model_namespace_access(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var v = await ResourceSourceCase(f); var s = v.Setup;
        var frame = await ClaimArtifactRun(f, s); await GrantWorkspaceRuntimeReads(f, s, ["sessions.read", "agents.read", "resources.read"]);
        Assert.Equal(v.Bytes, (await ResourceSources(f).CaptureAsync(frame, ToolCatalog.WorkspaceRead, v.Path)).CopyContent());
        var executor = new SessionToolExecutor(tenantAuthorization: new((ITenantRunStore)f.Iam, Operations(f), workspaceReads: WorkspaceRuntimeReads(f)));
        var admission = new ToolExecutionAdmission(false, TriggerKind.UserTurn, AgentInstanceId: s.Agent.Instance.InstanceId, OwnedSessionId: frame.SessionId,
            SupportsTools: true, AgentRunId: frame.RunId, TenantId: s.Tenant, AgentRunGeneration: frame.Generation);
        var call = new ModelToolCall("unconverted-resource", ToolCatalog.WorkspaceRead, System.Text.Json.JsonSerializer.Serialize(new { path = v.Path }));
        var result = (await executor.ExecuteAsync(s.Run.Admission.Configuration!.Definition, frame.SessionId, call, 16000, admission: admission)).Text;
        using var parsed = System.Text.Json.JsonDocument.Parse(result); Assert.True(parsed.RootElement.TryGetProperty("error", out _));
        Assert.DoesNotContain("Private bound resource", result);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_resource_source_keeps_same_Account_tenant_bindings_and_equal_virtual_paths_separate(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var alpha = await ResourceSourceCase(f); var a = alpha.Setup;
        var system = (await f.Iam.ReadSystemAsync()).Principals.Single(p => p.AccountId == a.Owner.AccountId);
        var beta = await f.Access.CreateTenantAsync(system.PrincipalId, a.Owner.AccountId, "Beta", "beta");
        var betaHuman = a.Owner with { PrincipalId = beta.Users.Single().PrincipalId };
        var betaAgent = await f.Agents.CreateAsync(betaHuman.PrincipalId, beta.Tenant.TenantId, "general-assistant", 32, beta.Tenant.Revision);
        var bound = await ResourceSourceCase(f, new(beta.Tenant.TenantId, betaHuman, betaAgent, a.Run), Encoding.UTF8.GetBytes("Private Beta resource")); var b = bound.Setup;
        Assert.NotEqual(a.Owner.PrincipalId, b.Owner.PrincipalId); Assert.Equal(alpha.Path, bound.Path);
        var frameA = await ClaimArtifactRun(f, a); var frameB = await ClaimArtifactRun(f, b);
        await GrantWorkspaceRuntimeReads(f, a, ["sessions.read", "agents.read", "resources.read"]);
        var probe = new ResourceContentProbe((ITenantResourceSourceContentStore)f.RunContent);
        await Assert.ThrowsAsync<AccessDeniedException>(() => ResourceSources(f, probe).CaptureAsync(frameB, ToolCatalog.WorkspaceRead, bound.Path).AsTask()); Assert.Equal(0, probe.Reads);
        await GrantWorkspaceRuntimeReads(f, b, ["sessions.read", "agents.read", "resources.read"]);
        Assert.Equal(alpha.Bytes, (await ResourceSources(f).CaptureAsync(frameA, ToolCatalog.WorkspaceRead, alpha.Path)).CopyContent());
        Assert.Equal(bound.Bytes, (await ResourceSources(f).CaptureAsync(frameB, ToolCatalog.WorkspaceRead, bound.Path)).CopyContent());
        await Assert.ThrowsAsync<AccessDeniedException>(() => ResourceSources(f, probe).CaptureAsync(new(frameA.Owner, frameA.SessionId, frameB.RunId, frameB.Generation), ToolCatalog.WorkspaceRead, alpha.Path).AsTask());
        Assert.Equal(0, probe.Reads);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_resource_source_rechecks_expired_claim_after_the_final_metadata_read(bool sqlite)
    {
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var f = await Fixture.Create(sqlite, clock, builtInAuthority: true); var v = await ResourceSourceCase(f); var s = v.Setup;
        var frame = await ClaimArtifactRun(f, s); await GrantWorkspaceRuntimeReads(f, s, ["sessions.read", "agents.read", "resources.read"]);
        var current = new ResourceBuiltInProbe(f.BuiltIns, () => { clock.Advance(TimeSpan.FromMinutes(10)); return Task.CompletedTask; });
        Assert.Equal("tenant_runtime_unavailable", (await Assert.ThrowsAsync<AccessDeniedException>(() => ResourceSources(f, currentBuiltIns: current).CaptureAsync(frame, ToolCatalog.WorkspaceRead, v.Path).AsTask())).ReasonCode);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_resource_source_cancellation_during_byte_IO_never_returns_a_captured_body(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var v = await ResourceSourceCase(f); var s = v.Setup;
        var frame = await ClaimArtifactRun(f, s); await GrantWorkspaceRuntimeReads(f, s, ["sessions.read", "agents.read", "resources.read"]);
        using var cancellation = new CancellationTokenSource();
        var probe = new ResourceContentProbe((ITenantResourceSourceContentStore)f.RunContent, () => { cancellation.Cancel(); return Task.CompletedTask; });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ResourceSources(f, probe).CaptureAsync(frame, ToolCatalog.WorkspaceRead, v.Path, cancellation.Token).AsTask());
        Assert.Equal(1, probe.Reads);
    }
    private sealed class ResourceBuiltInProbe(IBuiltInAgentDefinitionStore inner, Func<Task> afterFinalRead) : IBuiltInAgentDefinitionStore
    {
        public int Reads { get; private set; }
        public ValueTask<IReadOnlyList<AgentDefinition>> ListAsync(CancellationToken ct = default) => inner.ListAsync(ct);
        public async ValueTask<AgentDefinition?> GetAsync(string id, int? version = null, CancellationToken ct = default)
        { var value = await inner.GetAsync(id, version, ct); Reads++; if (Reads == 2) await afterFinalRead(); return value; }
    }
    private sealed class ResourceContentProbe(ITenantResourceSourceContentStore inner, Func<Task>? after = null) : ITenantResourceSourceContentStore
    {
        public int Reads { get; private set; }
        public async ValueTask<byte[]?> ReadVerifiedResourceSourceAsync(string hash, long size, CancellationToken ct = default)
        { Reads++; var bytes = await inner.ReadVerifiedResourceSourceAsync(hash, size, ct); if (after is not null) await after(); return bytes; }
    }
}
