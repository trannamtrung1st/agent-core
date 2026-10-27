using System.Text;
using System.Text.Json;
using AgentCore.Application.Admin;
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
    private const string ResourceRoot = "/agent/instance/resources";
    private sealed record NamespaceCase(Environment Setup, IReadOnlyDictionary<string, InstanceResourceView> Files);
    private static async Task<NamespaceCase> ResourceNamespaceCase(Fixture f)
    {
        var initial = await ResourceSourceCase(f, data: "needle A"u8.ToArray(), logicalPath: "a.txt"); var s = initial.Setup;
        var items = new Dictionary<string, InstanceResourceView>();
        foreach (var (path, body) in new[] { ("notes/b.txt", "needle B"), ("ordinary.txt", "No matching content"), ("secret/c.txt", "Hidden private body") })
        {
            var current = (await ((ITenantAgentStore)f.Iam).FindTenantAgentAsync(s.Tenant, s.Agent.Instance.InstanceId))!;
            var added = await f.AgentConfiguration().UpsertResourceAsync(s.Owner.PrincipalId, s.Tenant, current.Instance.InstanceId,
                (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision, current.Instance.Revision,
                new(null, null, path, AgentDefinitionResourceKind.Reference, "text/plain", Encoding.UTF8.GetBytes(body)), initiator: s.Owner);
            items.Add(path, added.Resource!);
        }
        var catalog = await f.AgentConfiguration().ReadResourcesAsync(s.Owner.PrincipalId, s.Tenant, s.Agent.Instance.InstanceId);
        items.Add("a.txt", catalog.Catalog.Resources.Single(r => r.Key == initial.Key));
        var agent = (await ((ITenantAgentStore)f.Iam).FindTenantAgentAsync(s.Tenant, s.Agent.Instance.InstanceId))!;
        var session = await f.Sessions.CreateAsync(s.Tenant, agent.Instance.InstanceId, s.Owner, SessionMode.Text,
            (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision, agent.Instance.Revision);
        var submitted = await f.RunService().SubmitTextAsync(s.Tenant, session.Owner.SessionId, s.Owner, f.Ids.NewId(), "Inspect the admitted resource namespace",
            (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision, 1);
        return new(s with { Agent = agent, Run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, submitted.RunId))! }, items);
    }
    private static ModelToolCall NamespaceCall(bool search, string path = ResourceRoot) => new("namespace-observation",
        search ? ToolCatalog.WorkspaceSearch : ToolCatalog.WorkspaceList, search ? JsonSerializer.Serialize(new { path, query = "needle", glob = "**/*.txt", maxResults = 10 }) : JsonSerializer.Serialize(new { path }));
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_resource_namespace_browse_and_search_filter_raw_child_reads_before_results_and_paging(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var v = await ResourceNamespaceCase(f); var s = v.Setup;
        var frame = await ClaimArtifactRun(f, s); var reads = WorkspaceRuntimeReads(f); await GrantWorkspaceRuntimeReads(f, s, ["sessions.read", "agents.read"]);
        using (var empty = JsonDocument.Parse(await reads.InspectAsync(frame, NamespaceCall(false), 16000)))
        { Assert.Empty(empty.RootElement.GetProperty("entries").EnumerateArray()); Assert.False(empty.RootElement.GetProperty("truncated").GetBoolean()); }
        await GrantWorkspaceRuntimeReads(f, s, ["resources.read"], TenantAgentChildScopes.Scope(s.Tenant, s.Agent.Instance.InstanceId, "resource", v.Files["a.txt"].Key));
        using (var only = JsonDocument.Parse(await reads.InspectAsync(frame, NamespaceCall(false), 16000)))
        { Assert.Equal(ResourceRoot + "/a.txt", Assert.Single(only.RootElement.GetProperty("entries").EnumerateArray()).GetProperty("path").GetString()); Assert.DoesNotContain("secret", only.RootElement.GetRawText()); }
        using (var only = JsonDocument.Parse(await reads.InspectAsync(frame, NamespaceCall(true), 16000)))
            Assert.Equal(ResourceRoot + "/a.txt", Assert.Single(only.RootElement.GetProperty("matches").EnumerateArray()).GetProperty("path").GetString());
        await GrantWorkspaceRuntimeReads(f, s, ["resources.read"]);
        var call = new ModelToolCall("page", ToolCatalog.WorkspaceList, JsonSerializer.Serialize(new { path = ResourceRoot, limit = 1 }));
        using var page = JsonDocument.Parse(await reads.InspectAsync(frame, call, 16000));
        Assert.Single(page.RootElement.GetProperty("entries").EnumerateArray()); Assert.True(page.RootElement.GetProperty("truncated").GetBoolean());
        var cursor = page.RootElement.GetProperty("nextPath").GetString();
        using var next = JsonDocument.Parse(await reads.InspectAsync(frame, call with { ArgumentsJson = JsonSerializer.Serialize(new { path = ResourceRoot, limit = 1, cursor }) }, 16000));
        Assert.NotEqual(cursor, Assert.Single(next.RootElement.GetProperty("entries").EnumerateArray()).GetProperty("path").GetString());
        using var searchResult = JsonDocument.Parse(await reads.InspectAsync(frame, NamespaceCall(true), 16000));
        Assert.Equal(2, searchResult.RootElement.GetProperty("matches").GetArrayLength()); Assert.DoesNotContain("Hidden private body", searchResult.RootElement.GetRawText());
    }
    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task Tenant_resource_namespace_Review_returns_exact_observation_through_existing_successor(bool sqlite, bool search)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var v = await ResourceNamespaceCase(f); var s = v.Setup;
        var frame = await ClaimArtifactRun(f, s); await GrantWorkspaceRuntimeReads(f, s, ["sessions.read", "agents.read", "resources.read"]);
        await Policies(f).SaveAsync(s.Tenant, s.Owner, "resources.read", new(OperationApprovalMode.Always, false), (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision, 0);
        var reads = WorkspaceRuntimeReads(f); var actual = NamespaceCall(search);
        Assert.Equal("resource_read_review_required", (await Assert.ThrowsAsync<AccessDeniedException>(() => reads.InspectAsync(frame, actual, 16000).AsTask())).ReasonCode);
        var call = new ModelToolCall("namespace-review", ToolCatalog.RequestAuthorization, JsonSerializer.Serialize(new { operation = "inspect_workspace", read = new { tool = actual.Name, arguments = JsonSerializer.Deserialize<JsonElement>(actual.ArgumentsJson) } }));
        var runs = (ITenantRunStore)f.Iam; var runtime = (ITenantRuntimeStore)f.Iam; var run = (await runs.FindTenantRunAsync(s.Tenant, frame.RunId))!;
        run = await runtime.ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId, new AgentRunCommand.Checkpoint(run.Revision, f.Clock.GetUtcNow(), frame.Generation,
            new(AgentRunToolCallCheckpoint.Write([new(ModelRole.Assistant, "", ToolCalls: [call])]), 1, 0, 10000, 100), null), f.Ids.NewId());
        var approvals = ApprovalService(f, workspaceReads: reads);
        var prepared = await new TenantToolAuthorization(runs, Operations(f), f.Clock, approvals, f.RunConfiguration(), workspaceReads: reads).PrepareAsync(run.SessionId, call,
            new(false, TriggerKind.UserTurn, AgentInstanceId: s.Agent.Instance.InstanceId, OwnedSessionId: run.SessionId, SupportsTools: true, AgentRunId: run.AgentRunId, TenantId: s.Tenant, AgentRunGeneration: frame.Generation), default);
        var request = Assert.IsType<TenantApprovalRequest>(prepared.Request); Assert.Equal("sessions.read", request.Permission); Assert.Equal("session", request.ResourceType);
        Assert.Equal(4, TenantApprovalRules.ResourceReadMembers(request.Operation.WorkspaceRead!).Count);
        Assert.DoesNotContain("needle A", JsonSerializer.Serialize(request));
        Assert.Equal(1, (await approvals.ListAsync(s.Tenant, s.Owner, new(0, 1))).Total);
        var view = await approvals.ReadViewAsync(s.Tenant, request.RequestId, s.Owner); Assert.True(view.Review!.CanDecide);
        await approvals.DecideAsync(s.Tenant, request.RequestId, s.Owner, true, request.Operation.ActionHash, view.SecurityRevision, request.Revision);
        Assert.Equal(1, await approvals.DispatchPendingAsync()); var source = (await runs.FindTenantRunAsync(s.Tenant, frame.RunId))!;
        var next = (await runs.FindTenantRunAsync(s.Tenant, source.Result!.ContinuationRunId!.Value))!;
        next = await runtime.ApplyTenantRuntimeCommandAsync(next.Owner, next.AgentRunId, new AgentRunCommand.Claim(next.Revision, f.Clock.GetUtcNow(), f.Ids.NewId(), f.Clock.GetUtcNow().AddMinutes(5)), f.Ids.NewId());
        var continuations = new TenantRunContinuationService(runs, (ITenantApprovalStore)f.Iam, Effects(f, workspaceReads: reads), f.Clock, runtime, f.Ids);
        var result = await continuations.ResumeAsync(next); Assert.Equal(TenantOperationOutcome.Completed, result.Outcome!.Outcome);
        Assert.True(AgentRunToolCallCheckpoint.TryRead(result.Run.Checkpoint, out var messages));
        using var data = JsonDocument.Parse(Assert.Single(messages!, m => m.Role == ModelRole.Tool).Text);
        var observation = data.RootElement.GetProperty("workspaceObservation"); Assert.Equal(search ? 2 : 4, observation.GetProperty(search ? "matches" : "entries").GetArrayLength());
        Assert.Null(await ((ITenantApprovalStore)f.Iam).FindOperationGrantAsync(s.Tenant, request.RequestId)); await reads.RequireStoredAsync(result.Run);
        var again = await continuations.ResumeAsync(result.Run); Assert.Equal(result.Run.Revision, again.Run.Revision);
    }
    private static async Task<(NamespaceCase Case, TenantRuntimeFrame Frame, TenantWorkspaceRuntimeReadService Reads, TenantApprovalService Approvals, TenantApprovalRequest Request)> PendingNamespace(Fixture f, bool search)
    {
        var v = await ResourceNamespaceCase(f); var s = v.Setup; var frame = await ClaimArtifactRun(f, s);
        await GrantWorkspaceRuntimeReads(f, s, ["sessions.read", "agents.read", "resources.read"]);
        await Policies(f).SaveAsync(s.Tenant, s.Owner, "resources.read", new(OperationApprovalMode.Always, false), (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision, 0);
        var reads = WorkspaceRuntimeReads(f); var approvals = ApprovalService(f, workspaceReads: reads); var actual = NamespaceCall(search);
        var call = new ModelToolCall("namespace-review", ToolCatalog.RequestAuthorization, JsonSerializer.Serialize(new { operation = "inspect_workspace", read = new { tool = actual.Name, arguments = JsonSerializer.Deserialize<JsonElement>(actual.ArgumentsJson) } }));
        var run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, frame.RunId))!;
        run = await ((ITenantRuntimeStore)f.Iam).ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId, new AgentRunCommand.Checkpoint(run.Revision, f.Clock.GetUtcNow(), frame.Generation,
            new(AgentRunToolCallCheckpoint.Write([new(ModelRole.Assistant, "", ToolCalls: [call])]), 1, 0, 10000, 100), null), f.Ids.NewId());
        var request = (await approvals.PrepareWorkspaceReadAsync(new(s.Tenant, frame.RunId), actual, run.Revision, frame.Generation)).Request!;
        Assert.NotNull(request); return (v, frame, reads, approvals, request);
    }
    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task Tenant_resource_namespace_Review_counts_require_every_member_and_current_child_ownership(bool sqlite, bool search)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var v = await PendingNamespace(f, search); var s = v.Case.Setup;
        var peer = await User(f, s, "namespace-review-reader@example.test", ["sessions.read", "agents.read", "approvals.read"]);
        var role = await f.Access.SaveRoleAsync(s.Owner.PrincipalId, s.Tenant, null, "Read one namespace member", ["resources.read"], (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
        await f.Access.AssignRoleAsync(s.Owner.PrincipalId, s.Tenant, peer.PrincipalId, role.RoleId, TenantAgentChildScopes.Scope(s.Tenant, s.Agent.Instance.InstanceId, "resource", v.Case.Files["a.txt"].Key), (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
        Assert.Equal(0, (await v.Approvals.ListAsync(s.Tenant, peer, new(0, 1))).Total);
        await Assert.ThrowsAsync<AccessDeniedException>(() => v.Approvals.ReadViewAsync(s.Tenant, v.Request.RequestId, peer).AsTask());
        await f.Access.AssignRoleAsync(s.Owner.PrincipalId, s.Tenant, peer.PrincipalId, role.RoleId, AccessScope.ForTenant(s.Tenant), (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
        Assert.Equal(1, (await v.Approvals.ListAsync(s.Tenant, peer, new(0, 1))).Total);
        await f.AgentConfiguration().ChangeResourceAsync(s.Owner.PrincipalId, s.Tenant, s.Agent.Instance.InstanceId, v.Case.Files["ordinary.txt"].Key, true, null,
            (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision, s.Agent.Instance.Revision, 1, initiator: s.Owner);
        Assert.Equal(0, (await v.Approvals.ListAsync(s.Tenant, s.Owner, new(0, 1))).Total);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_resource_namespace_search_Review_binds_negative_matches(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var v = await PendingNamespace(f, true); var s = v.Case.Setup;
        await f.AgentConfiguration().UpsertResourceAsync(s.Owner.PrincipalId, s.Tenant, s.Agent.Instance.InstanceId,
            (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision, s.Agent.Instance.Revision,
            new(Guid.Parse(v.Case.Files["ordinary.txt"].Key[9..]), 1, "ordinary.txt", AgentDefinitionResourceKind.Reference, "text/plain", "Now contains needle"u8.ToArray()), initiator: s.Owner);
        var run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, v.Frame.RunId))!;
        await Assert.ThrowsAsync<SecurityConflictException>(() => v.Reads.RequireInspectionAsync(v.Request, run).AsTask());
        Assert.False((await v.Approvals.ReadViewAsync(s.Tenant, v.Request.RequestId, s.Owner)).Review!.CanDecide);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_resource_namespace_retained_page_refuses_changed_visible_scope(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var v = await ResourceNamespaceCase(f); var s = v.Setup; var frame = await ClaimArtifactRun(f, s);
        await GrantWorkspaceRuntimeReads(f, s, ["sessions.read", "agents.read"]);
        await GrantWorkspaceRuntimeReads(f, s, ["resources.read"], TenantAgentChildScopes.Scope(s.Tenant, s.Agent.Instance.InstanceId, "resource", v.Files["a.txt"].Key));
        var reads = WorkspaceRuntimeReads(f); var call = NamespaceCall(false); var text = await reads.InspectAsync(frame, call, 16000);
        var run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, frame.RunId))!;
        run = await ((ITenantRuntimeStore)f.Iam).ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId, new AgentRunCommand.Checkpoint(run.Revision, f.Clock.GetUtcNow(), frame.Generation,
            new(AgentRunToolCallCheckpoint.Write([new(ModelRole.Assistant, "", ToolCalls: [call]), new(ModelRole.Tool, text, ToolCallId: call.Id, Name: call.Name)]), 1, text.Length, 10000, 100), null), f.Ids.NewId());
        await reads.RequireStoredAsync(run); await GrantWorkspaceRuntimeReads(f, s, ["resources.read"]);
        Assert.Equal("resource_source_changed", (await Assert.ThrowsAsync<AccessDeniedException>(() => reads.RequireStoredAsync(run).AsTask())).ReasonCode);
    }
    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task Tenant_resource_namespace_refusal_survives_policy_relaxation(bool sqlite, bool search)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var v = await PendingNamespace(f, search); var s = v.Case.Setup;
        var view = await v.Approvals.ReadViewAsync(s.Tenant, v.Request.RequestId, s.Owner);
        await v.Approvals.DecideAsync(s.Tenant, v.Request.RequestId, s.Owner, false, v.Request.Operation.ActionHash, view.SecurityRevision, v.Request.Revision);
        Assert.Equal(1, await v.Approvals.DispatchPendingAsync()); var runs = (ITenantRunStore)f.Iam;
        var source = (await runs.FindTenantRunAsync(s.Tenant, v.Frame.RunId))!; var next = (await runs.FindTenantRunAsync(s.Tenant, source.Result!.ContinuationRunId!.Value))!;
        next = await ((ITenantRuntimeStore)f.Iam).ApplyTenantRuntimeCommandAsync(next.Owner, next.AgentRunId, new AgentRunCommand.Claim(next.Revision, f.Clock.GetUtcNow(), f.Ids.NewId(), f.Clock.GetUtcNow().AddMinutes(5)), f.Ids.NewId());
        await Policies(f).SaveAsync(s.Tenant, s.Owner, "resources.read", new(OperationApprovalMode.None, false), (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision, 1);
        var frame = new TenantRuntimeFrame(next.Owner, next.SessionId, next.AgentRunId, next.Claim!.Generation);
        Assert.Equal("previously_rejected", (await Assert.ThrowsAsync<AccessDeniedException>(() => v.Reads.InspectAsync(frame, NamespaceCall(search), 16000).AsTask())).ReasonCode);
        Assert.Equal("previously_rejected", (await v.Approvals.PrepareWorkspaceReadAsync(new(s.Tenant, next.AgentRunId), NamespaceCall(search), next.Revision, frame.Generation)).ReasonCode);
    }

    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task Tenant_resource_namespace_inherited_and_empty_observations_have_exact_parent_and_member_consent(bool sqlite, bool readable)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var s = await Setup(f, "general-assistant", 35); var frame = await ClaimArtifactRun(f, s);
        await GrantWorkspaceRuntimeReads(f, s, ["sessions.read", "agents.read"]);
        if (readable)
        {
            var file = s.Run.Admission.Configuration!.Resources.First(r => r.Key.StartsWith("definition:", StringComparison.Ordinal));
            await GrantWorkspaceRuntimeReads(f, s, ["resources.read"], TenantAgentChildScopes.Scope(s.Tenant, s.Agent.Instance.InstanceId, "resource", file.Key));
        }
        var reads = WorkspaceRuntimeReads(f); var call = NamespaceCall(false, "/agent/resources");
        using var data = JsonDocument.Parse(await reads.InspectAsync(frame, call, 16000)); Assert.Equal(readable ? 1 : 0, data.RootElement.GetProperty("entries").GetArrayLength());
        await Policies(f).SaveAsync(s.Tenant, s.Owner, "sessions.read", new(OperationApprovalMode.Always, false), (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision, 0);
        var run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, frame.RunId))!; var approvals = ApprovalService(f, workspaceReads: reads);
        var request = (await approvals.PrepareWorkspaceReadAsync(new(s.Tenant, frame.RunId), call, run.Revision, frame.Generation)).Request!;
        Assert.NotNull(request); Assert.Equal(readable ? 1 : 0, TenantApprovalRules.ResourceReadMembers(request.Operation.WorkspaceRead!).Count);
        Assert.Equal("sessions.read", request.Permission); Assert.Equal(1, (await approvals.ListAsync(s.Tenant, s.Owner, new(0, 1))).Total);
        Assert.True((await approvals.ReadViewAsync(s.Tenant, request.RequestId, s.Owner)).Review!.CanDecide);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_resource_namespace_search_withholds_data_after_revocation_during_byte_IO(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var v = await ResourceNamespaceCase(f); var s = v.Setup; var frame = await ClaimArtifactRun(f, s);
        var grant = await GrantWorkspaceRuntimeReads(f, s, ["sessions.read", "agents.read", "resources.read"]);
        var probe = new ResourceContentProbe((ITenantResourceSourceContentStore)f.RunContent, async () => await f.Access.RevokeAssignmentAsync(s.Owner.PrincipalId, s.Tenant, grant.AssignmentId, (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision));
        var reads = WorkspaceRuntimeReads(f, resourceSources: ResourceSources(f, probe));
        Assert.Equal("executor_read_required", (await Assert.ThrowsAsync<AccessDeniedException>(() => reads.InspectAsync(frame, NamespaceCall(true), 16000).AsTask())).ReasonCode); Assert.Equal(1, probe.Reads);
    }
    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task Tenant_resource_namespace_search_skips_binary_and_over_bound_content_without_widening_reader(bool sqlite, bool large)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true);
        var v = await ResourceSourceCase(f, data: large ? Encoding.UTF8.GetBytes(new string('x', WorkspaceSearchLimits.MaxFileBytesToScan + 1)) : new byte[] { 0, 1, 2 }, logicalPath: "needle.txt");
        var s = v.Setup; var frame = await ClaimArtifactRun(f, s); await GrantWorkspaceRuntimeReads(f, s, ["sessions.read", "agents.read", "resources.read"]);
        var probe = new ResourceContentProbe((ITenantResourceSourceContentStore)f.RunContent); var reads = WorkspaceRuntimeReads(f, resourceSources: ResourceSources(f, probe));
        using var data = JsonDocument.Parse(await reads.InspectAsync(frame, NamespaceCall(true), 16000));
        var match = Assert.Single(data.RootElement.GetProperty("matches").EnumerateArray()); Assert.Equal("name", match.GetProperty("kind").GetString()); Assert.Equal("needle.txt", match.GetProperty("snippet").GetString());
        Assert.Equal(large ? 0 : 1, probe.Reads);
    }

}
