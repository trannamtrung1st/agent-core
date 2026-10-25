using System.Text;
using System.Text.Json;
using AgentCore.Application.Agents;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantOperationAuthorizationTests
{
    private sealed class KnowledgeFallbackTrap : IApprovedKnowledgeCatalog
    {
        public int Reads { get; private set; }
        public ValueTask<string?> ReadContentAsync(string identity, CancellationToken cancellationToken = default)
        { Reads++; throw new InvalidOperationException("Tenant retrieval must not use a legacy knowledge reader."); }
    }
    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task Tenant_knowledge_model_read_keeps_origin_citation_and_never_uses_legacy_fallback(bool sqlite, bool inherited)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true);
        Environment s; string identity; string key; string expected;
        if (inherited)
        {
            s = await Setup(f, "general-assistant", 32); var source = DefinitionExecutionPolicies.Of(s.Run.Admission.Configuration!.Definition).KnowledgeList.First(); identity = source.Identity;
            var resource = s.Run.Admission.Configuration.Resources.Single(r => r.Key.StartsWith("definition:", StringComparison.Ordinal) && r.LogicalPath == KnowledgeSourcePaths.ResolveBackingPath(source)); key = resource.Key;
            expected = Encoding.UTF8.GetString((await f.RunContent.ReadAsync(resource.ContentSha256))!);
        }
        else
        {
            // Same path as inherited knowledge: origin must remain the exact Instance key.
            var v = await ResourceSourceCase(f, logicalPath: "knowledge/support-order-policy", kind: AgentDefinitionResourceKind.Knowledge); s = v.Setup; identity = key = v.Key; expected = Encoding.UTF8.GetString(v.Bytes);
        }
        var frame = await ClaimArtifactRun(f, s); var trap = new KnowledgeFallbackTrap();
        var executor = new SessionToolExecutor( tenantAuthorization:new((ITenantRunStore)f.Iam, Operations(f), f.Clock, workspaceReads: WorkspaceRuntimeReads(f)));
        var admission = new ToolExecutionAdmission(false, TriggerKind.UserTurn, AgentInstanceId: s.Agent.Instance.InstanceId, OwnedSessionId: frame.SessionId, SupportsTools: true,
            AgentRunId: frame.RunId, TenantId: s.Tenant, AgentRunGeneration: frame.Generation);
        var call = new ModelToolCall("approved-knowledge", ToolCatalog.KnowledgeRetrieve, JsonSerializer.Serialize(new { identity }));
        async Task<JsonDocument> Execute(ModelToolCall? c = null) => JsonDocument.Parse((await executor.ExecuteAsync(s.Run.Admission.Configuration!.Definition, frame.SessionId, c ?? call, 16000, admission: admission)).Text);
        using (var denied = await Execute()) Assert.Equal("executor_read_required", denied.RootElement.GetProperty("reasonCode").GetString());
        await GrantWorkspaceRuntimeReads(f, s, ["sessions.read", "agents.read"]);
        await GrantWorkspaceRuntimeReads(f, s, ["resources.read"], TenantAgentChildScopes.Scope(s.Tenant, s.Agent.Instance.InstanceId, "resource", key));
        using (var data = await Execute())
        {
            var source = DefinitionExecutionPolicies.Of(s.Run.Admission.Configuration!.Definition).KnowledgeList.Single(r => r.Identity == identity);
            Assert.Equal(expected, data.RootElement.GetProperty("content").GetString()); Assert.Equal(source.Title, data.RootElement.GetProperty("title").GetString());
            Assert.Equal(source.Citation, data.RootElement.GetProperty("citation").GetString()); Assert.True(data.RootElement.GetProperty("untrusted").GetBoolean());
        }
        using (var unknown = await Execute(call with { ArgumentsJson = """{"identity":"unapproved-other-identity"}""" })) Assert.True(unknown.RootElement.TryGetProperty("error", out _));
        using (var forged = await Execute(call with { ArgumentsJson = JsonSerializer.Serialize(new { identity, path = "/home/escape.txt" }) })) Assert.Equal("invalid", forged.RootElement.GetProperty("error").GetString());
        Assert.Equal(0, trap.Reads);
    }
    [Theory] [InlineData(false, "sessions.read")] [InlineData(true, "sessions.read")]
    [InlineData(false, "agents.read")] [InlineData(true, "agents.read")]
    [InlineData(false, "resources.read")] [InlineData(true, "resources.read")]
    public async Task Tenant_knowledge_requires_each_raw_executor_read_before_content_IO(bool sqlite, string missing)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var v = await ResourceSourceCase(f, kind: AgentDefinitionResourceKind.Knowledge); var s = v.Setup; var frame = await ClaimArtifactRun(f, s);
        await GrantWorkspaceRuntimeReads(f, s, new[] { "sessions.read", "agents.read", "resources.read" }.Where(p => p != missing).ToArray());
        var probe = new ResourceContentProbe((ITenantResourceSourceContentStore)f.RunContent); var reads = WorkspaceRuntimeReads(f, resourceSources: ResourceSources(f, probe));
        Assert.Equal("executor_read_required", (await Assert.ThrowsAsync<AccessDeniedException>(() => reads.InspectAsync(frame, new("knowledge", ToolCatalog.KnowledgeRetrieve, JsonSerializer.Serialize(new { identity = v.Key })), 16000).AsTask())).ReasonCode);
        Assert.Equal(0, probe.Reads);
    }
    [Theory] [InlineData(false, "sessions.read")] [InlineData(true, "sessions.read")] [InlineData(false, "agents.read")] [InlineData(true, "agents.read")]
    public Task Tenant_knowledge_parent_policy_Review_uses_the_same_exact_source(bool sqlite, string permission) => ResourceReadReviewJourney(sqlite, permission, knowledge: true);
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_knowledge_Review_rejects_changed_source_and_filters_raw_resource_reads(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var v = await PendingResourceRead(f, knowledge: true); var s = v.Resource.Setup;
        var peer = await User(f, s, "knowledge-inbox-reader@example.test", ["approvals.read", "agents.read", "sessions.read"]);
        Assert.Equal(0, (await v.Approvals.ListAsync(s.Tenant, peer, new(0, 1))).Total); await Assert.ThrowsAsync<AccessDeniedException>(() => v.Approvals.ReadViewAsync(s.Tenant, v.Request.RequestId, peer).AsTask());
        await f.AgentConfiguration().UpsertResourceAsync(s.Owner.PrincipalId, s.Tenant, s.Agent.Instance.InstanceId, (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision, s.Agent.Instance.Revision,
            new(Guid.Parse(v.Resource.Key[9..]), 1, "source.txt", AgentDefinitionResourceKind.Knowledge, "text/plain", "Changed knowledge source"u8.ToArray()), initiator: s.Owner);
        var run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, v.Frame.RunId))!;
        await Assert.ThrowsAnyAsync<SecurityConflictException>(() => v.Reads.RequireInspectionAsync(v.Request, run).AsTask());
        Assert.False((await v.Approvals.ReadViewAsync(s.Tenant, v.Request.RequestId, s.Owner)).Review!.CanDecide);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_knowledge_refusal_survives_policy_relaxation_in_same_root(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var v = await PendingResourceRead(f, knowledge: true); var s = v.Resource.Setup;
        var view = await v.Approvals.ReadViewAsync(s.Tenant, v.Request.RequestId, s.Owner);
        await v.Approvals.DecideAsync(s.Tenant, v.Request.RequestId, s.Owner, false, v.Request.Operation.ActionHash, view.SecurityRevision, v.Request.Revision);
        Assert.Equal(1, await v.Approvals.DispatchPendingAsync()); var runs = (ITenantRunStore)f.Iam; var source = (await runs.FindTenantRunAsync(s.Tenant, v.Frame.RunId))!;
        var next = (await runs.FindTenantRunAsync(s.Tenant, source.Result!.ContinuationRunId!.Value))!;
        next = await ((ITenantRuntimeStore)f.Iam).ApplyTenantRuntimeCommandAsync(next.Owner, next.AgentRunId, new AgentRunCommand.Claim(next.Revision, f.Clock.GetUtcNow(), f.Ids.NewId(), f.Clock.GetUtcNow().AddMinutes(5)), f.Ids.NewId());
        await Policies(f).SaveAsync(s.Tenant, s.Owner, "resources.read", new(OperationApprovalMode.None, false), (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision, 1);
        var call = new ModelToolCall("refused-knowledge", ToolCatalog.KnowledgeRetrieve, JsonSerializer.Serialize(new { identity = v.Resource.Key }));
        Assert.Equal("previously_rejected", (await Assert.ThrowsAsync<AccessDeniedException>(() => v.Reads.InspectAsync(new(next.Owner, next.SessionId, next.AgentRunId, next.Claim!.Generation), call, 16000).AsTask())).ReasonCode);
        Assert.Equal("previously_rejected", (await v.Approvals.PrepareWorkspaceReadAsync(new(s.Tenant, next.AgentRunId), call, next.Revision, next.Claim!.Generation)).ReasonCode);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_knowledge_revocation_during_source_IO_withholds_all_content(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var v = await ResourceSourceCase(f, kind: AgentDefinitionResourceKind.Knowledge); var s = v.Setup; var frame = await ClaimArtifactRun(f, s);
        var grant = await GrantWorkspaceRuntimeReads(f, s, ["sessions.read", "agents.read", "resources.read"]);
        var probe = new ResourceContentProbe((ITenantResourceSourceContentStore)f.RunContent, async () => await f.Access.RevokeAssignmentAsync(s.Owner.PrincipalId, s.Tenant, grant.AssignmentId, (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision));
        var reads = WorkspaceRuntimeReads(f, resourceSources: ResourceSources(f, probe));
        Assert.Equal("executor_read_required", (await Assert.ThrowsAsync<AccessDeniedException>(() => reads.InspectAsync(frame, new("knowledge", ToolCatalog.KnowledgeRetrieve, JsonSerializer.Serialize(new { identity = v.Key })), 16000).AsTask())).ReasonCode);
        Assert.Equal(1, probe.Reads);
    }

}
