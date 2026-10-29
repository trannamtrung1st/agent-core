using AgentCore.Domain.Conversation;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Office;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantOperationAuthorizationTests
{
    public static IEnumerable<object[]> OfficeMutationBoundaries()
    {
        foreach (var sqlite in new[] { false, true }) foreach (var workbook in new[] { false, true })
        foreach (var change in new[] { "occupied", "source", "revoke", "corrupt" }) yield return [sqlite, workbook, change];
    }
    [Theory] [MemberData(nameof(OfficeMutationBoundaries))]
    public async Task Tenant_Office_mutation_commit_boundaries_preserve_sources_and_never_replay_uncertain_processing(bool sqlite, bool workbook, string change)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var s = await Setup(f, "general-assistant", 35); var frame = await ClaimArtifactRun(f, s);
        var processor = new OfficeProcessor(); var create = change == "occupied";
        var tool = "office." + (workbook ? "spreadsheet" : "document") + (create ? ".create" : ".edit"); var name = workbook ? "exact.xlsx" : "exact.docx"; var path = "/working/" + name;
        byte[] Package(string text) => workbook ? processor.CreateWorkbook(new([new("Sheet", [[new("text", Text: text)]])]), default).Bytes : processor.CreateDocument(new(null, [new(null, [text], null)]), default).Bytes;
        var expected = change == "corrupt" ? "Not an Office package"u8.ToArray() : Package("Original private body");
        await SeedRuntimeWorkspace(f, s, true, name, expected, OfficeLimits.MaxPackageBytes);
        var assignment = await GrantWorkspaceRuntimeReads(f, s, ["agents.read", "sessions.read", "workspaces.read", "workspaces.write"]);
        object args = create ? workbook ? new { path, sheets = new[] { new { name = "Sheet", rows = new[] { new[] { new { type = "text", text = "Replacement" } } } } } } : new { path, sections = new[] { new { paragraphs = new[] { "Replacement" } } } }
            : workbook ? new { path, operations = new[] { new { operation = "set_cells", sheet = "Sheet", range = "A1", cells = new[] { new[] { new { type = "text", text = "Replacement" } } } } } }
            : new { path, operations = new[] { new { operation = "replace_paragraph", selector = change == "corrupt" ? "invalid-selector" : processor.InspectDocument(expected, 0, 20, default).Elements[0].Selector, text = "Replacement" } } };
        var call = new ModelToolCall("exact-office-boundary", tool, JsonSerializer.Serialize(args)); await WorkspaceEffectCheckpoint(f, frame, call);
        var hooked = new OfficeCommitHook(WorkspaceReader(f), async () => {
            if (change == "source") { expected = Package("Concurrent private body"); await SeedRuntimeWorkspace(f, s, true, name, expected, OfficeLimits.MaxPackageBytes); }
            if (change == "revoke") await f.Access.RevokeAssignmentAsync(s.Owner.PrincipalId, s.Tenant, assignment.AssignmentId, (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
        });
        var service = WorkspaceEffects(f, hooked, office: processor);
        var result = await service.ExecuteAsync(frame, call); Assert.NotNull(result.Receipt); Assert.Equal(TenantOperationOutcome.Indeterminate, result.Receipt.Outcome);
        Assert.Equal(result, await service.ExecuteAsync(frame, call)); Assert.Equal(1, hooked.Processed);
        Assert.Equal(expected, (await WorkspaceReader(f).InspectTenantAsync(WorkspaceOwner(s, true), path, OfficeLimits.MaxPackageBytes)).Bytes);
        var audit = await f.Iam.ReadAuditAsync(s.Tenant, 200); Assert.Single(audit, a => a.Action == TenantWorkspaceEffectRules.Dispatch); Assert.Single(audit, a => a.Action == TenantWorkspaceEffectRules.Result);
        Assert.Equal(0, await service.RecoverAsync());
    }
    [Fact]
    public async Task Tenant_Office_mutation_SQLite_upgrade_preserves_prior_consent_and_rejects_forgery_or_unsafe_rollback()
    {
        await using var f = await Fixture.Create(true, builtInAuthority: true); var prior = await WorkspaceReviewFixture(f, working: true);
        await HistoricalTenantDatabase.AssertApprovalUpgradeAsync(f.Contexts!,
            "20261019035251_TenantWorkspaceOfficeReadReview", "20261019041906_TenantWorkspaceOfficeMutation");
        Assert.Equal(prior.Request, await ((ITenantApprovalStore)f.Iam).FindApprovalAsync(prior.Setup.Tenant, prior.Request.RequestId));
        var s = prior.Setup;
        var session = await f.Sessions.CreateAsync(s.Tenant, s.Agent.Instance.InstanceId, s.Owner, AgentCore.Domain.Conversation.SessionMode.Text, (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision, 1);
        var submitted = await f.RunService().SubmitTextAsync(s.Tenant, session.Owner.SessionId, s.Owner, f.Ids.NewId(), "Create exact Office file", (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision, 1);
        s = s with { Run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, submitted.RunId))! };
        var frame = await ClaimArtifactRun(f, s); await GrantWorkspaceRuntimeReads(f, s, ["agents.read", "sessions.read", "workspaces.read", "workspaces.write"]);
        await Policies(f).SaveAsync(s.Tenant, s.Owner, "workspaces.write", new(OperationApprovalMode.Always, false), (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision, 0);
        var call = new ModelToolCall("exact-office-consent", ToolCatalog.OfficeDocumentCreate, """{"path":"/working/exact.docx","sections":[{"paragraphs":["Exact saved text"]}]}""");
        var run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, frame.RunId))!;
        var request = (await ApprovalService(f, workspaceMutations: MutationTargets(f)).PrepareWorkspaceMutationAsync(new(s.Tenant, frame.RunId), call, run.Revision, frame.Generation)).Request!;
        Assert.NotNull(request);
        var v = (Setup: s, Frame: frame, Request: request);
        var reopened = new SqliteIamStore(f.Contexts!, builtIns: f.BuiltIns, clock: f.Clock);
        Assert.Equal(v.Request, await ((ITenantApprovalStore)reopened).FindApprovalAsync(v.Setup.Tenant, v.Request.RequestId));
        foreach (var change in new[] { "tool", "permission", "mixed", "hash" })
        {
            await using var db = f.Contexts!.CreateDbContext(); var row = await db.Set<TenantApprovalRecord>().SingleAsync(r => r.RequestId == v.Request.RequestId); var payload = JsonNode.Parse(row.PayloadJson)!;
            if (change == "tool") payload["Operation"]!["WorkspaceMutation"]!["ToolName"] = ToolCatalog.OfficeDocumentInspect;
            if (change == "permission") row.Permission = "workspaces.delete";
            if (change == "mixed") payload["Operation"]!["ArtifactPublication"] = new JsonObject { ["SessionId"] = v.Frame.SessionId.ToString("D"), ["ArgumentsJson"] = "{}" };
            if (change == "hash") payload["Operation"]!["WorkspaceMutation"]!["ObservationHash"] = new string('z', 64);
            row.PayloadJson = payload.ToJsonString(); Assert.Equal(275, Assert.IsType<SqliteException>((await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync())).InnerException).SqliteExtendedErrorCode);
        }
        await HistoricalTenantDatabase.AssertApprovalDowngradeRefusedAsync(f.Contexts!,
            "20261019041906_TenantWorkspaceOfficeMutation", "20261019035251_TenantWorkspaceOfficeReadReview", "RetainedRows = 0");
        Assert.Equal(v.Request, await ((ITenantApprovalStore)reopened).FindApprovalAsync(v.Setup.Tenant, v.Request.RequestId));
    }
    [Theory] [InlineData(false, "office.document.create")] [InlineData(true, "office.document.create")]
    [InlineData(false, "office.document.edit")] [InlineData(true, "office.document.edit")]
    [InlineData(false, "office.spreadsheet.create")] [InlineData(true, "office.spreadsheet.create")]
    [InlineData(false, "office.spreadsheet.edit")] [InlineData(true, "office.spreadsheet.edit")]
    public async Task Tenant_Office_abandoned_dispatch_discovery_recovers_all_four_tools_without_processing_or_native_replay(bool sqlite, string tool)
    {
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.Parse("2026-10-19T04:00:00Z"));
        await using var f = await Fixture.Create(sqlite, clock, builtInAuthority: true); var s = await Setup(f, "general-assistant", 35); var frame = await ClaimArtifactRun(f, s);
        var assignment = await GrantWorkspaceRuntimeReads(f, s, ["agents.read", "sessions.read", "workspaces.read", "workspaces.write"]);
        var workbook = tool.StartsWith("office.spreadsheet."); var create = tool.EndsWith(".create"); var path = "/working/retained." + (workbook ? "xlsx" : "docx");
        object arguments = create ? workbook ? new { path, sheets = new[] { new { name = "Sheet", rows = Array.Empty<object[]>() } } } : new { path, sections = new[] { new { paragraphs = new[] { "Exact text" } } } }
            : workbook ? new { path, operations = new[] { new { operation = "set_formula", sheet = "Sheet", range = "A1", formula = "1+1" } } } : new { path, operations = new[] { new { operation = "replace_paragraph", selector = "exact-selector", text = "Exact text" } } };
        var call = new ModelToolCall("abandoned-office", tool, JsonSerializer.Serialize(arguments)); await WorkspaceEffectCheckpoint(f, frame, call);
        var fence = await StageWorkspaceFence(f, frame, call);
        await f.Access.RevokeAssignmentAsync(s.Owner.PrincipalId, s.Tenant, assignment.AssignmentId, (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
        clock.Advance(TimeSpan.FromMinutes(6));
        var hook = new OfficeCommitHook(WorkspaceReader(f), () => Task.CompletedTask); var service = WorkspaceEffects(f, hook, office: new OfficeProcessor());
        Assert.Equal(1, await service.RecoverAsync()); Assert.Equal(0, await service.RecoverAsync()); Assert.Equal(0, hook.Processed);
        var receipt = await ((ITenantDirectOperationStore)f.Iam).FindDirectOperationReceiptAsync(s.Tenant, frame.RunId, call.Id);
        Assert.Equal(TenantOperationOutcome.Indeterminate, receipt!.Outcome); Assert.Equal(fence.Origin.DirectOperation!.OperationId, receipt.OperationId);
        Assert.Empty((await WorkspaceReader(f).ListTenantAsync(WorkspaceOwner(s, true), "/working", null, 100)).Items);
    }
    private sealed class OfficeCommitHook(ITenantWorkspaceMutationStore inner, Func<Task> afterProcessing) : ITenantWorkspaceMutationStore
    {
        public ValueTask<bool> TryWriteNewTenantAsync(TenantWorkspaceReference owner, string path, ReadOnlyMemory<byte> bytes, Func<CancellationToken, ValueTask> authorizeCommit, CancellationToken ct = default) => inner.TryWriteNewTenantAsync(owner, path, bytes, authorizeCommit, ct);
        public int Processed { get; private set; }
        public ValueTask TransformOfficeTenantAsync(TenantWorkspaceReference owner, string path, bool create, Func<ReadOnlyMemory<byte>, CancellationToken, byte[]> process, Func<CancellationToken, ValueTask> authorizeCommit, CancellationToken ct = default)
        {
            var fired = false;
            return inner.TransformOfficeTenantAsync(owner, path, create, (bytes, token) => { Processed++; return process(bytes, token); }, async token => {
                if (Processed > 0 && !fired) { fired = true; await afterProcessing(); } await authorizeCommit(token);
            }, ct);
        }
        public ValueTask WriteTenantAsync(TenantWorkspaceReference owner, string path, ReadOnlyMemory<byte> bytes, Func<CancellationToken, ValueTask> authorizeCommit, CancellationToken ct = default) => inner.WriteTenantAsync(owner, path, bytes, authorizeCommit, ct);
        public ValueTask DeleteTenantAsync(TenantWorkspaceReference owner, string path, Func<CancellationToken, ValueTask> authorizeCommit, CancellationToken ct = default) => inner.DeleteTenantAsync(owner, path, authorizeCommit, ct);
        public ValueTask<WorkspacePatchResult> PatchTenantAsync(TenantWorkspaceReference owner, string path, IReadOnlyList<WorkspaceTextEdit> edits, Func<CancellationToken, ValueTask> authorizeCommit, CancellationToken ct = default) => inner.PatchTenantAsync(owner, path, edits, authorizeCommit, ct);
        public ValueTask<WorkspaceStructureResult> StructureTenantAsync(TenantWorkspaceReference owner, IReadOnlyList<WorkspaceStructuralOperation> operations, Func<CancellationToken, ValueTask> authorizeCommit, CancellationToken ct = default) => inner.StructureTenantAsync(owner, operations, authorizeCommit, ct);
        public ValueTask CopyTenantAsync(TenantWorkspaceReference sourceOwner, string source, TenantWorkspaceReference destinationOwner, string destination, Func<CancellationToken, ValueTask> authorizeCommit, CancellationToken ct = default) => inner.CopyTenantAsync(sourceOwner, source, destinationOwner, destination, authorizeCommit, ct);
    }
}
