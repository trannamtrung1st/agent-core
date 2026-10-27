using System.Security.Cryptography;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Infrastructure.Office;
using AgentCore.Infrastructure.Workspaces;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantOperationAuthorizationTests
{
    private static TenantArtifactOperationService SourcePublicationService(Fixture f, ITenantArtifactOperationStore? effects = null,
        ITenantWorkspaceArtifactSourceStore? files = null, TenantResourceSourceTarget? resourceSources = null) => new(effects ?? (ITenantArtifactOperationStore)f.Iam,
        Operations(f), f.RunConfiguration(), f.Ids, f.Clock, f.Authorizer, (ITenantApprovalStore)f.Iam,
        new(Operations(f), (ITenantSessionStore)f.Iam, f.RunConfiguration(), files ?? WorkspaceReader(f), f.Authorizer, f.Clock, resourceSources ?? ResourceSources(f), (ITenantAttachmentStore)f.Attachments));

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_workspace_Artifact_publication_commits_exact_Office_bytes_and_one_durable_receipt(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true, artifactAuthority: true); var s = await Setup(f, "general-assistant", 35);
        var frame = await ClaimArtifactRun(f, s); await GrantPublication(f, s); await GrantWorkspaceRuntimeReads(f, s);
        var bytes = new OfficeProcessor().CreateDocument(new("Exact report", [new(null, ["Private source text: thử nghiệm"], null)]), default).Bytes;
        await SeedRuntimeWorkspace(f, s, true, "source.docx", bytes, OfficeLimits.MaxPackageBytes);
        const string arguments = """{"path":"/working/source.docx","displayName":"published.docx"}""";
        var service = SourcePublicationService(f); var result = await service.ExecuteWorkspaceAsync(new(s.Tenant, frame.RunId), frame.Generation, "publish-source", arguments);
        Assert.NotNull(result.Receipt); Assert.Null(result.Receipt.RequestId);
        var artifacts = (ITenantArtifactStore)f.Artifacts; var artifact = (await artifacts.FindTenantAsync(s.Tenant, frame.SessionId, result.Receipt.OperationId))!;
        Assert.Equal(frame.RunId, artifact.AgentRunId); Assert.Equal("published.docx", artifact.DisplayName); Assert.Equal(OfficeLimits.DocumentMime, artifact.ContentType);
        Assert.Null(artifact.WorkspaceLogicalPath); Assert.Null(artifact.SourceAttachmentId);
        Assert.Equal(bytes.Length, artifact.ByteSize); Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), artifact.Sha256Hex);
        Assert.Equal(bytes, await artifacts.ReadTenantContentAsync(s.Tenant, frame.SessionId, artifact.ArtifactId));
        var run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, frame.RunId))!;
        Assert.Equal("operation:" + artifact.ArtifactId.ToString("D"), run.SideEffect.ToolCallId);
        await SeedRuntimeWorkspace(f, s, true, "source.docx", "Changed original file"u8.ToArray());
        Assert.Equal(result, await service.ExecuteWorkspaceAsync(new(s.Tenant, frame.RunId), frame.Generation, "publish-source", arguments));
        Assert.Single((await artifacts.ListTenantAsync(s.Tenant, frame.SessionId, null, null, 20)).Items);
        Assert.Equal(bytes, (await ArtifactService(f, artifacts).ReadContentAsync(s.Tenant, frame.SessionId, artifact.ArtifactId, s.Owner)).Bytes);
        var sourceReader = await User(f, s, "source-only@example.test", ["sessions.read", "agents.read", "workspaces.read"]);
        await Assert.ThrowsAsync<AccessDeniedException>(() => ArtifactService(f, artifacts).ReadContentAsync(s.Tenant, frame.SessionId, artifact.ArtifactId, sourceReader).AsTask());
        var audit = await f.Iam.ReadAuditAsync(s.Tenant, 200); Assert.Single(audit, a => a.Action == TenantArtifactOperationRules.AuditAction);
        Assert.DoesNotContain("Private source text", JsonSerializer.Serialize(audit)); Assert.DoesNotContain("source.docx", JsonSerializer.Serialize(audit));
        if (sqlite)
        {
            var reopened = new SqliteIamStore(f.Contexts!, builtIns: f.BuiltIns, clock: f.Clock, artifactBacking: (SqliteArtifactStore)f.Artifacts);
            // Existing factual replay needs no source materializer or repeated source read after store recreation.
            Assert.Equal(result, await PublicationService(f, reopened).ExecuteWorkspaceAsync(new(s.Tenant, frame.RunId), frame.Generation, "publish-source", arguments));
            Assert.Single((await artifacts.ListTenantAsync(s.Tenant, frame.SessionId, null, null, 20)).Items);
        }
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_workspace_Artifact_publication_rechecks_native_source_at_the_atomic_commit_owner(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true, artifactAuthority: true); var s = await Setup(f, "general-assistant", 35);
        var frame = await ClaimArtifactRun(f, s); await GrantPublication(f, s); await GrantWorkspaceRuntimeReads(f, s);
        await SeedRuntimeWorkspace(f, s, true, "source.bin", [0, 255, 128, 3]);
        var gate = new ArtifactCommitGate((ITenantArtifactOperationStore)f.Iam);
        var pending = SourcePublicationService(f, gate).ExecuteWorkspaceAsync(new(s.Tenant, frame.RunId), frame.Generation, "changed-source",
            """{"path":"/working/source.bin","displayName":"published.bin"}""").AsTask();
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try { await SeedRuntimeWorkspace(f, s, true, "source.bin", [0, 255, 128, 4]); }
        finally { gate.Proceed.TrySetResult(); }
        Assert.Equal("workspace_source_changed", (await Assert.ThrowsAsync<SecurityConflictException>(async () => await pending)).ReasonCode);
        Assert.Empty((await ((ITenantArtifactStore)f.Artifacts).ListTenantAsync(s.Tenant, frame.SessionId, null, null, 20)).Items);
        Assert.Null(await ((ITenantArtifactOperationStore)f.Iam).FindDirectOperationReceiptAsync(s.Tenant, frame.RunId, "changed-source"));
        Assert.DoesNotContain(await f.Iam.ReadAuditAsync(s.Tenant, 200), a => a.Action == TenantArtifactOperationRules.AuditAction);
        var actual = await WorkspaceReader(f).InspectTenantAsync(WorkspaceOwner(s, true), "/working/source.bin", 4096);
        Assert.Equal(new byte[] { 0, 255, 128, 4 }, actual.Bytes);
    }

    [Fact]
    public async Task Tenant_workspace_Artifact_SQLite_receipt_save_failure_rolls_back_metadata_Run_security_and_audit()
    {
        await using var f = await Fixture.Create(true, builtInAuthority: true, artifactAuthority: true); var s = await Setup(f, "general-assistant", 35);
        var frame = await ClaimArtifactRun(f, s); await GrantPublication(f, s); await GrantWorkspaceRuntimeReads(f, s);
        byte[] bytes = [0, 255, 128, 3]; await SeedRuntimeWorkspace(f, s, true, "source.bin", bytes);
        string connection; await using (var db = f.Contexts!.CreateDbContext()) connection = db.Database.GetDbConnection().ConnectionString;
        var faulty = new ArtifactFaultContexts(new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite(connection).AddInterceptors(new ArtifactReceiptFault()).Options);
        var store = new SqliteIamStore(faulty, builtIns: f.BuiltIns, clock: f.Clock, artifactBacking: (SqliteArtifactStore)f.Artifacts);
        var revision = (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision; var run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, frame.RunId))!;
        const string args = """{"path":"/working/source.bin","displayName":"published.bin"}""";
        await Assert.ThrowsAsync<InvalidOperationException>(() => SourcePublicationService(f, store).ExecuteWorkspaceAsync(new(s.Tenant, frame.RunId), frame.Generation, "source-save-fault", args).AsTask());
        Assert.Equal(revision, (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
        Assert.Equal(run.Revision, (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, frame.RunId))!.Revision);
        Assert.Empty((await ((ITenantArtifactStore)f.Artifacts).ListTenantAsync(s.Tenant, frame.SessionId, null, null, 20)).Items);
        Assert.Null(await ((ITenantArtifactOperationStore)f.Iam).FindDirectOperationReceiptAsync(s.Tenant, frame.RunId, "source-save-fault"));
        Assert.DoesNotContain(await f.Iam.ReadAuditAsync(s.Tenant, 200), a => a.Action == TenantArtifactOperationRules.AuditAction);
        var retry = await SourcePublicationService(f, store).ExecuteWorkspaceAsync(new(s.Tenant, frame.RunId), frame.Generation, "source-save-fault", args);
        Assert.NotNull(retry.Receipt); Assert.Single((await ((ITenantArtifactStore)f.Artifacts).ListTenantAsync(s.Tenant, frame.SessionId, null, null, 20)).Items);
        Assert.Equal(bytes, await ((ITenantArtifactStore)f.Artifacts).ReadTenantContentAsync(s.Tenant, frame.SessionId, retry.Receipt.OperationId));
    }
}
