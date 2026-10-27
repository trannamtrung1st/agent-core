using System.Text;
using System.Text.Json;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantOperationAuthorizationTests
{
    [Theory] [InlineData(false, "resources.read")] [InlineData(true, "resources.read")]
    [InlineData(false, "sessions.read")] [InlineData(true, "sessions.read")]
    [InlineData(false, "agents.read")] [InlineData(true, "agents.read")]
    public Task Tenant_resource_read_Review_uses_exact_resource_scope_and_existing_successor_receipt(bool sqlite, string policy) => ResourceReadReviewJourney(sqlite, policy);
    [Theory] [InlineData(false, "docx")] [InlineData(true, "docx")] [InlineData(false, "xlsx")] [InlineData(true, "xlsx")]
    public Task Tenant_resource_Office_read_Review_uses_the_existing_processor_and_exact_successor(bool sqlite, string format) => ResourceReadReviewJourney(sqlite, "resources.read", format);
    [Theory] [InlineData(false)] [InlineData(true)]
    public Task Tenant_resource_inherited_read_Review_keeps_admitted_Definition_source(bool sqlite) => ResourceReadReviewJourney(sqlite, "resources.read", inherited: true);
    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public Task Tenant_knowledge_read_Review_uses_exact_source_and_existing_successor(bool sqlite, bool inherited) => ResourceReadReviewJourney(sqlite, "resources.read", inherited: inherited, knowledge: true);
    private async Task ResourceReadReviewJourney(bool sqlite, string policy, string? format = null, bool inherited = false, bool knowledge = false)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var processor = new AgentCore.Infrastructure.Office.OfficeProcessor();
        const string body = "Private bound resource: thử nghiệm";
        ResourceCase v;
        if (inherited)
        {
            var setup = await Setup(f, "general-assistant", 35);
            var inheritedResource = setup.Run.Admission.Configuration!.Resources.First(r => r.Key.StartsWith("definition:", StringComparison.Ordinal) && r.MediaType.StartsWith("text/", StringComparison.Ordinal) && r.ByteLength < 4000);
            v = new(setup, inheritedResource.Key, inheritedResource.VirtualPath, (await f.RunContent.ReadAsync(inheritedResource.ContentSha256))!);
        }
        else v = format is null ? await ResourceSourceCase(f, kind: knowledge ? AgentCore.Domain.Definitions.AgentDefinitionResourceKind.Knowledge : AgentCore.Domain.Definitions.AgentDefinitionResourceKind.Reference) : await ResourceSourceCase(f, data: format == "docx"
            ? processor.CreateDocument(new(null, [new(null, [body], null)]), default).Bytes
            : processor.CreateWorkbook(new([new("Sheet", [[new("text", Text: body)]])]), default).Bytes,
            logicalPath: "source." + format, mediaType: format == "docx" ? OfficeLimits.DocumentMime : OfficeLimits.SpreadsheetMime);
        var s = v.Setup;
        var frame = await ClaimArtifactRun(f, s); var grant = await GrantWorkspaceRuntimeReads(f, s, ["sessions.read", "agents.read", "resources.read"]);
        await Policies(f).SaveAsync(s.Tenant, s.Owner, policy, new(OperationApprovalMode.Always, false), (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision, 0);
        var runtime = (ITenantRuntimeStore)f.Iam; var runs = (ITenantRunStore)f.Iam; var reads = WorkspaceRuntimeReads(f, office: processor);
        var path = v.Path;
        var tool = knowledge ? ToolCatalog.KnowledgeRetrieve : format == "docx" ? ToolCatalog.OfficeDocumentInspect : format == "xlsx" ? ToolCatalog.OfficeSpreadsheetInspect : ToolCatalog.WorkspaceRead;
        var identity = inherited ? AgentCore.Domain.Definitions.DefinitionExecutionPolicies.Of(s.Run.Admission.Configuration!.Definition).KnowledgeList.Single(k => AgentCore.Domain.Definitions.KnowledgeSourcePaths.ResolveBackingPath(k) == s.Run.Admission.Configuration.Resources.Single(r => r.Key == v.Key).LogicalPath).Identity : v.Key;
        var json = knowledge ? JsonSerializer.Serialize(new { identity }) : JsonSerializer.Serialize(new { path });
        var call = new ModelToolCall("inspect-exact-workspace", ToolCatalog.RequestAuthorization, JsonSerializer.Serialize(new { operation = "inspect_workspace", read = new { tool, arguments = JsonSerializer.Deserialize<JsonElement>(json) } }));
        var run = (await runs.FindTenantRunAsync(s.Tenant, frame.RunId))!;
        run = await runtime.ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId, new AgentRunCommand.Checkpoint(run.Revision, f.Clock.GetUtcNow(), frame.Generation,
            new(AgentRunToolCallCheckpoint.Write([new(ModelRole.Assistant, "", ToolCalls: [call])]), 1, 0, 10000, 100), null), f.Ids.NewId());
        var approvals = ApprovalService(f, workspaceReads: reads);
        var prepared = await new TenantToolAuthorization(runs, Operations(f), f.Clock, approvals, f.RunConfiguration(), workspaceReads: reads).PrepareAsync(run.SessionId, call,
            new(false, TriggerKind.UserTurn, AgentInstanceId: s.Agent.Instance.InstanceId, OwnedSessionId: run.SessionId, SupportsTools: true, AgentRunId: run.AgentRunId, TenantId: s.Tenant, AgentRunGeneration: frame.Generation), default);
        var request = Assert.IsType<TenantApprovalRequest>(prepared.Request); Assert.Equal(ApprovalKind.Review, request.Kind); Assert.Equal(TenantOperationKind.WorkspaceRead, request.Operation.Kind);
        Assert.Equal("resources.read", request.Permission);
        Assert.Equal(TenantAgentChildScopes.Scope(s.Tenant, s.Agent.Instance.InstanceId, "resource", v.Key).ResourceId, request.ResourceId);
        Assert.Equal(v.Key, request.Operation.WorkspaceRead!.ResourceKey); Assert.Equal(inherited ? 0 : 1, request.Operation.WorkspaceRead.ResourceRevision);
        Assert.DoesNotContain("Private bound resource", JsonSerializer.Serialize(request));
        var waiting = (await runs.FindTenantRunAsync(s.Tenant, frame.RunId))!; Assert.Equal(AgentRunStatus.WaitingForApproval, waiting.Status); Assert.Null(waiting.Claim);
        var view = await approvals.ReadViewAsync(s.Tenant, request.RequestId, s.Owner); Assert.True(view.Review!.CanDecide);
        Assert.Contains(view.Review.Changes, c => c.Field == "path" && Equals(c.After, path));
        await approvals.DecideAsync(s.Tenant, request.RequestId, s.Owner, true, request.Operation.ActionHash, view.SecurityRevision, request.Revision);
        Assert.Equal(1, await approvals.DispatchPendingAsync()); var source = (await runs.FindTenantRunAsync(s.Tenant, frame.RunId))!;
        var next = (await runs.FindTenantRunAsync(s.Tenant, source.Result!.ContinuationRunId!.Value))!;
        next = await runtime.ApplyTenantRuntimeCommandAsync(next.Owner, next.AgentRunId, new AgentRunCommand.Claim(next.Revision, f.Clock.GetUtcNow(), f.Ids.NewId(), f.Clock.GetUtcNow().AddMinutes(5)), f.Ids.NewId());
        var continuations = new TenantRunContinuationService(runs, (ITenantApprovalStore)f.Iam, Effects(f, workspaceReads: reads), f.Clock, runtime, f.Ids);
        var result = await continuations.ResumeAsync(next); Assert.Equal(TenantOperationOutcome.Completed, result.Outcome!.Outcome);
        Assert.True(AgentRunToolCallCheckpoint.TryRead(result.Run.Checkpoint, out var messages)); var text = Assert.Single(messages!, m => m.Role == ModelRole.Tool).Text;
        using var doc = JsonDocument.Parse(text); Assert.True(doc.RootElement.GetProperty("readAuthorized").GetBoolean()); Assert.False(doc.RootElement.GetProperty("applied").GetBoolean());
        var data = doc.RootElement.GetProperty("workspaceObservation"); Assert.True(data.GetProperty("tenantWorkspaceRead").GetBoolean());
        if (knowledge) { Assert.Equal(identity, data.GetProperty("identity").GetString()); Assert.Equal(request.Operation.WorkspaceRead.Knowledge!.Citation, data.GetProperty("citation").GetString()); }
        if (format is null) Assert.Equal(Encoding.UTF8.GetString(v.Bytes), data.GetProperty("content").GetString());
        else Assert.Contains("Private bound resource", data.GetRawText());
        Assert.Null(await ((ITenantApprovalStore)f.Iam).FindOperationGrantAsync(s.Tenant, request.RequestId));
        Assert.Single(await f.Iam.ReadAuditAsync(s.Tenant, 200), a => a.Action == TenantWorkspaceReadOperationRules.AuditAction);
        await reads.RequireStoredAsync(result.Run);
        var again = await continuations.ResumeAsync(result.Run); Assert.Equal(result.Run.Checkpoint!.PayloadJson, again.Run.Checkpoint!.PayloadJson); Assert.Equal(result.Run.Revision, again.Run.Revision);
        await f.Access.RevokeAssignmentAsync(s.Owner.PrincipalId, s.Tenant, grant.AssignmentId, (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
        await Assert.ThrowsAsync<AccessDeniedException>(() => reads.RequireStoredAsync(result.Run).AsTask());
    }
    private static async Task<(ResourceCase Resource, TenantRuntimeFrame Frame, TenantWorkspaceRuntimeReadService Reads, TenantApprovalService Approvals, TenantApprovalRequest Request)> PendingResourceRead(Fixture f, bool knowledge = false)
    {
        var v = await ResourceSourceCase(f, kind: knowledge ? AgentCore.Domain.Definitions.AgentDefinitionResourceKind.Knowledge : AgentCore.Domain.Definitions.AgentDefinitionResourceKind.Reference); var s = v.Setup; var frame = await ClaimArtifactRun(f, s);
        await GrantWorkspaceRuntimeReads(f, s, ["sessions.read", "agents.read", "resources.read"]);
        await Policies(f).SaveAsync(s.Tenant, s.Owner, "resources.read", new(OperationApprovalMode.Always, false), (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision, 0);
        var reads = WorkspaceRuntimeReads(f); var approvals = ApprovalService(f, workspaceReads: reads);
        var run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, frame.RunId))!;
        var read = new ModelToolCall("resource-review", knowledge ? ToolCatalog.KnowledgeRetrieve : ToolCatalog.WorkspaceRead, knowledge ? JsonSerializer.Serialize(new { identity = v.Key }) : JsonSerializer.Serialize(new { path = v.Path }));
        var call = new ModelToolCall("resource-authorization", ToolCatalog.RequestAuthorization, JsonSerializer.Serialize(new {
            operation = "inspect_workspace", read = new { tool = read.Name, arguments = JsonSerializer.Deserialize<JsonElement>(read.ArgumentsJson) } }));
        run = await ((ITenantRuntimeStore)f.Iam).ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId,
            new AgentRunCommand.Checkpoint(run.Revision, f.Clock.GetUtcNow(), frame.Generation,
                new(AgentRunToolCallCheckpoint.Write([new(ModelRole.Assistant, "", ToolCalls: [call])]), 1, 0, 10000, 100), null), f.Ids.NewId());
        var request = (await approvals.PrepareWorkspaceReadAsync(new(s.Tenant, frame.RunId), read, run.Revision, frame.Generation)).Request!;
        Assert.NotNull(request); return (v, frame, reads, approvals, request);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_resource_read_Review_filters_exact_child_and_parent_reads_before_counts(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var v = await PendingResourceRead(f); var s = v.Resource.Setup;
        var reader = await User(f, s, "resource-inbox-reader@example.test", ["approvals.read", "sessions.read", "agents.read"]);
        var page = await v.Approvals.ListAsync(s.Tenant, reader, new(0, 1)); Assert.Empty(page.Items); Assert.Equal(0, page.Total);
        var role = await f.Access.SaveRoleAsync(s.Owner.PrincipalId, s.Tenant, null, "Read exact resource", ["resources.read"], (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
        var grant = await f.Access.AssignRoleAsync(s.Owner.PrincipalId, s.Tenant, reader.PrincipalId, role.RoleId,
            TenantAgentChildScopes.Scope(s.Tenant, s.Agent.Instance.InstanceId, "resource", v.Resource.Key), (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
        page = await v.Approvals.ListAsync(s.Tenant, reader, new(0, 1)); Assert.Single(page.Items); Assert.Equal(1, page.Total); Assert.False(page.Items[0].Review!.CanDecide);
        await f.Access.RevokeAssignmentAsync(s.Owner.PrincipalId, s.Tenant, grant.AssignmentId, (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
        page = await v.Approvals.ListAsync(s.Tenant, reader, new(0, 1)); Assert.Empty(page.Items); Assert.Equal(0, page.Total);
        await Assert.ThrowsAsync<AccessDeniedException>(() => v.Approvals.ReadViewAsync(s.Tenant, v.Request.RequestId, reader).AsTask());
    }
    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task Tenant_resource_read_Review_rechecks_changed_or_disabled_resource_before_decision(bool sqlite, bool disabled)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var v = await PendingResourceRead(f); var s = v.Resource.Setup;
        var agent = (await ((ITenantAgentStore)f.Iam).FindTenantAgentAsync(s.Tenant, s.Agent.Instance.InstanceId))!;
        await f.AgentConfiguration().UpsertResourceAsync(s.Owner.PrincipalId, s.Tenant, agent.Instance.InstanceId,
            (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision, agent.Instance.Revision,
            new(Guid.Parse(v.Resource.Key[9..]), 1, "source.txt", AgentCore.Domain.Definitions.AgentDefinitionResourceKind.Reference, "text/plain", "Changed private resource"u8.ToArray(), !disabled), initiator: s.Owner);
        var view = await v.Approvals.ReadViewAsync(s.Tenant, v.Request.RequestId, s.Owner); Assert.False(view.Review!.CanDecide);
        await Assert.ThrowsAnyAsync<SecurityConflictException>(() => v.Approvals.DecideAsync(s.Tenant, v.Request.RequestId, s.Owner, true,
            v.Request.Operation.ActionHash, view.SecurityRevision, v.Request.Revision).AsTask());
        Assert.Null(await ((ITenantApprovedOperationStore)f.Iam).FindOperationReceiptAsync(s.Tenant, v.Request.Operation.OperationId));
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_resource_read_Review_rejection_remains_suppressed_after_policy_relaxation(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var v = await PendingResourceRead(f); var s = v.Resource.Setup;
        var view = await v.Approvals.ReadViewAsync(s.Tenant, v.Request.RequestId, s.Owner);
        await v.Approvals.DecideAsync(s.Tenant, v.Request.RequestId, s.Owner, false, v.Request.Operation.ActionHash, view.SecurityRevision, v.Request.Revision);
        Assert.Equal(1, await v.Approvals.DispatchPendingAsync()); var runs = (ITenantRunStore)f.Iam;
        var source = (await runs.FindTenantRunAsync(s.Tenant, v.Frame.RunId))!; var next = (await runs.FindTenantRunAsync(s.Tenant, source.Result!.ContinuationRunId!.Value))!;
        next = await ((ITenantRuntimeStore)f.Iam).ApplyTenantRuntimeCommandAsync(next.Owner, next.AgentRunId,
            new AgentRunCommand.Claim(next.Revision, f.Clock.GetUtcNow(), f.Ids.NewId(), f.Clock.GetUtcNow().AddMinutes(5)), f.Ids.NewId());
        await Policies(f).SaveAsync(s.Tenant, s.Owner, "resources.read", new(OperationApprovalMode.None, false), (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision, 1);
        var call = new ModelToolCall("refused-resource", ToolCatalog.WorkspaceRead, JsonSerializer.Serialize(new { path = v.Resource.Path }));
        Assert.Equal("previously_rejected", (await Assert.ThrowsAsync<AccessDeniedException>(() => v.Reads.InspectAsync(new(next.Owner, next.SessionId, next.AgentRunId, next.Claim!.Generation), call, 16000).AsTask())).ReasonCode);
        var again = await v.Approvals.PrepareWorkspaceReadAsync(new(s.Tenant, next.AgentRunId), call, next.Revision, next.Claim!.Generation);
        Assert.Equal("previously_rejected", again.ReasonCode); Assert.Equal(v.Request.RequestId, again.Request!.RequestId);
    }
    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task Tenant_resource_direct_retained_observation_rechecks_binding_and_policy(bool sqlite, bool changed)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var v = await ResourceSourceCase(f); var s = v.Setup;
        var frame = await ClaimArtifactRun(f, s); await GrantWorkspaceRuntimeReads(f, s, ["sessions.read", "agents.read", "resources.read"]);
        var reads = WorkspaceRuntimeReads(f); var call = new ModelToolCall("retained-resource", ToolCatalog.WorkspaceRead, JsonSerializer.Serialize(new { path = v.Path }));
        var text = await reads.InspectAsync(frame, call, 16000); var run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, frame.RunId))!;
        run = await ((ITenantRuntimeStore)f.Iam).ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId,
            new AgentRunCommand.Checkpoint(run.Revision, f.Clock.GetUtcNow(), frame.Generation, new(AgentRunToolCallCheckpoint.Write([
                new(ModelRole.Assistant, "", ToolCalls: [call]), new(ModelRole.Tool, text, ToolCallId: call.Id, Name: call.Name)]), 1, text.Length, 10000, 100), null), f.Ids.NewId());
        await reads.RequireStoredAsync(run);
        if (changed)
        {
            await f.AgentConfiguration().UpsertResourceAsync(s.Owner.PrincipalId, s.Tenant, s.Agent.Instance.InstanceId,
                (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision, s.Agent.Instance.Revision,
                new(Guid.Parse(v.Key[9..]), 1, "source.txt", AgentCore.Domain.Definitions.AgentDefinitionResourceKind.Reference, "text/plain", "Changed private resource"u8.ToArray()), initiator: s.Owner);
            Assert.Equal("resource_source_changed", (await Assert.ThrowsAsync<SecurityConflictException>(() => reads.RequireStoredAsync(run).AsTask())).ReasonCode);
        }
        else
        {
            await Policies(f).SaveAsync(s.Tenant, s.Owner, "resources.read", new(OperationApprovalMode.Always, false), (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision, 0);
            Assert.Equal("resource_read_review_required", (await Assert.ThrowsAsync<AccessDeniedException>(() => reads.RequireStoredAsync(run).AsTask())).ReasonCode);
        }
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_resource_read_Review_removed_binding_is_hidden_before_owner_inbox_counts(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var v = await PendingResourceRead(f); var s = v.Resource.Setup;
        Assert.Equal(1, (await v.Approvals.ListAsync(s.Tenant, s.Owner, new(0, 1))).Total);
        await f.AgentConfiguration().ChangeResourceAsync(s.Owner.PrincipalId, s.Tenant, s.Agent.Instance.InstanceId, v.Resource.Key, true, null,
            (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision, s.Agent.Instance.Revision, 1, initiator: s.Owner);
        var page = await v.Approvals.ListAsync(s.Tenant, s.Owner, new(0, 1)); Assert.Empty(page.Items); Assert.Equal(0, page.Total);
        await Assert.ThrowsAsync<AccessDeniedException>(() => v.Approvals.ReadViewAsync(s.Tenant, v.Request.RequestId, s.Owner).AsTask());
    }

    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task Tenant_resource_literal_file_names_never_expand_namespace(bool sqlite, bool unicode)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true);
        var v = await ResourceSourceCase(f, logicalPath: unicode ? new string('界', 100) + ".txt" : "literal[2026]?*.txt"); var s = v.Setup;
        var frame = await ClaimArtifactRun(f, s); await GrantWorkspaceRuntimeReads(f, s, ["sessions.read", "agents.read", "resources.read"]);
        var result = await WorkspaceRuntimeReads(f).InspectAsync(frame, new("literal-resource", ToolCatalog.WorkspaceRead, JsonSerializer.Serialize(new { path = v.Path })), 16000);
        using var doc = JsonDocument.Parse(result); Assert.Equal(Encoding.UTF8.GetString(v.Bytes), doc.RootElement.GetProperty("content").GetString());
        var probe = new ResourceContentProbe((ITenantResourceSourceContentStore)f.RunContent);
        await Assert.ThrowsAsync<AccessDeniedException>(() => ResourceSources(f, probe).CaptureAsync(frame, ToolCatalog.WorkspaceRead, "/agent/instance/resources/literal*").AsTask());
        Assert.Equal(0, probe.Reads);
    }

}
