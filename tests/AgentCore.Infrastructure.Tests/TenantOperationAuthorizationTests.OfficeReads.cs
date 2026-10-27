using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Office;
using AgentCore.Infrastructure.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantOperationAuthorizationTests
{
    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task Tenant_Office_reads_withhold_processed_data_after_authority_loss_during_source_IO(bool sqlite, bool workbook)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var s = await Setup(f, "general-assistant", 35);
        var frame = await ClaimArtifactRun(f, s); var processor = new OfficeProcessor();
        var bytes = workbook ? processor.CreateWorkbook(new([new("Private", [[new("text", Text: "Protected Office body")]])]), default).Bytes
            : processor.CreateDocument(new(null, [new(null, ["Protected Office body"], null)]), default).Bytes;
        var name = workbook ? "private.xlsx" : "private.docx"; await SeedRuntimeWorkspace(f, s, true, name, bytes, maxFileBytes: OfficeLimits.MaxPackageBytes); var grant = await GrantWorkspaceRuntimeReads(f, s);
        var probe = new WorkspaceRuntimeReadProbe(WorkspaceReader(f), async () => await f.Access.RevokeAssignmentAsync(s.Owner.PrincipalId, s.Tenant,
            grant.AssignmentId, (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision));
        var call = new ModelToolCall("raced-office", workbook ? ToolCatalog.OfficeSpreadsheetInspect : ToolCatalog.OfficeDocumentInspect, JsonSerializer.Serialize(new { path = "/working/" + name }));
        Assert.Equal("executor_read_required", (await Assert.ThrowsAsync<AccessDeniedException>(() => WorkspaceRuntimeReads(f, probe, processor).InspectAsync(frame, call, 16000).AsTask())).ReasonCode);
        Assert.Equal(1, probe.Fired);
    }

    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task Tenant_Office_reads_do_not_slice_cells_or_elements_to_fit_an_unusable_output_budget(bool sqlite, bool workbook)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var s = await Setup(f, "general-assistant", 35);
        var frame = await ClaimArtifactRun(f, s); var processor = new OfficeProcessor();
        var text = "Protected complete Office value " + new string('X', 2048);
        var bytes = workbook ? processor.CreateWorkbook(new([new("Private", [[new("text", Text: text)]])]), default).Bytes
            : processor.CreateDocument(new(null, [new(null, [text], null)]), default).Bytes;
        var name = workbook ? "private.xlsx" : "private.docx"; await SeedRuntimeWorkspace(f, s, true, name, bytes, maxFileBytes: OfficeLimits.MaxPackageBytes); await GrantWorkspaceRuntimeReads(f, s);
        var call = new ModelToolCall("bounded-office", workbook ? ToolCatalog.OfficeSpreadsheetInspect : ToolCatalog.OfficeDocumentInspect, JsonSerializer.Serialize(new { path = "/working/" + name }));
        var output = await WorkspaceRuntimeReads(f, office: processor).InspectAsync(frame, call, 256);
        using var json = JsonDocument.Parse(output); Assert.Equal("office_output_limit", json.RootElement.GetProperty("error").GetString());
        Assert.DoesNotContain("Protected complete", output); Assert.DoesNotContain("nextCursor", output);
    }

    [Fact]
    public async Task Tenant_Office_read_SQLite_upgrade_shape_reopen_and_rollback_guard_preserve_exact_and_prior_consent()
    {
        await using var f = await Fixture.Create(true, builtInAuthority: true); var prior = await WorkspaceReviewFixture(f);
        await using (var db = f.Contexts!.CreateDbContext())
        {
            var migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync("20261023180000_TenantPublicDownloads");
            await Assert.ThrowsAsync<SqliteException>(() => migrator.MigrateAsync("20261019023004_TenantWorkspaceMutationReview"));
            Assert.Contains("20261023180000_TenantPublicDownloads", await db.Database.GetAppliedMigrationsAsync());
            await db.Database.MigrateAsync();
            Assert.False(db.Database.HasPendingModelChanges());
        }
        Assert.Equal(prior.Request, await ((ITenantApprovalStore)f.Iam).FindApprovalAsync(prior.Setup.Tenant, prior.Request.RequestId));
        var s = prior.Setup;
        var session = await f.Sessions.CreateAsync(s.Tenant, s.Agent.Instance.InstanceId, s.Owner, SessionMode.Text, (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision, 1);
        var submitted = await f.RunService().SubmitTextAsync(s.Tenant, session.Owner.SessionId, s.Owner, f.Ids.NewId(), "Inspect exact Office source", (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision, 1);
        s = s with { Run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, submitted.RunId))! };
        var frame = await ClaimArtifactRun(f, s); var processor = new OfficeProcessor();
        await SeedRuntimeWorkspace(f, s, true, "private.docx", processor.CreateDocument(new(null, [new(null, ["Private Office body"], null)]), default).Bytes);
        await GrantWorkspaceRuntimeReads(f, s);
        await Policies(f).SaveAsync(s.Tenant, s.Owner, "workspaces.read", new(OperationApprovalMode.Always, false), (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision, (await f.Iam.ReadTenantAsync(s.Tenant))!.OperationPolicies.SingleOrDefault(p => p.PermissionKey == "workspaces.read")?.Revision ?? 0);
        var reads = WorkspaceRuntimeReads(f, office: processor); var approvals = ApprovalService(f, workspaceReads: reads);
        var run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, frame.RunId))!;
        var request = (await approvals.PrepareWorkspaceReadAsync(new(s.Tenant, frame.RunId), new("exact-office", ToolCatalog.OfficeDocumentInspect,
            """{"path":"/working/private.docx","offset":0,"limit":20}"""), run.Revision, frame.Generation)).Request!;
        Assert.NotNull(request);
        var reopened = new SqliteIamStore(f.Contexts!, builtIns: f.BuiltIns, clock: f.Clock);
        Assert.Equal(request, await ((ITenantApprovalStore)reopened).FindApprovalAsync(s.Tenant, request.RequestId));
        foreach (var change in new[] { "tool", "mixed", "grant", "hash" })
        {
            await using var db = f.Contexts!.CreateDbContext(); var row = await db.Set<TenantApprovalRecord>().SingleAsync(r => r.RequestId == request.RequestId); var payload = JsonNode.Parse(row.PayloadJson)!;
            if (change == "tool") payload["Operation"]!["WorkspaceRead"]!["ToolName"] = ToolCatalog.OfficeDocumentEdit;
            if (change == "mixed") payload["Operation"]!["ArtifactPublication"] = new JsonObject { ["SessionId"] = frame.SessionId.ToString("D"), ["ArgumentsJson"] = "{}" };
            if (change == "grant") row.Kind = (int)ApprovalKind.ExceptionalGrant;
            if (change == "hash") payload["Operation"]!["WorkspaceRead"]!["ObservationHash"] = new string('z', 64);
            row.PayloadJson = payload.ToJsonString();
            Assert.Equal(275, Assert.IsType<SqliteException>((await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync())).InnerException).SqliteExtendedErrorCode);
        }
        await using (var db = f.Contexts!.CreateDbContext()) await Assert.ThrowsAsync<SqliteException>(() => db.GetService<IMigrator>().MigrateAsync("20261019023004_TenantWorkspaceMutationReview"));
        await using (var restored = f.Contexts!.CreateDbContext()) await restored.Database.MigrateAsync();
        Assert.Equal(request, await ((ITenantApprovalStore)reopened).FindApprovalAsync(s.Tenant, request.RequestId));
    }
}
