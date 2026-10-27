using System.Security.Cryptography;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using AgentCore.Infrastructure.Office;
using AgentCore.Infrastructure.Workspaces;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantOperationAuthorizationTests
{
    [Theory]
    [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task Tenant_Artifact_source_captures_owned_binary_bytes_and_refuses_changed_source(bool sqlite, bool working)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true, artifactAuthority: true); var s = await Setup(f, "general-assistant", 35);
        byte[] original = [0, 255, 128, 1, 2, 3]; await SeedRuntimeWorkspace(f, s, working, "source.bin", original);
        var owner = WorkspaceOwner(s, working);
        var path = owner.LogicalRoot + "/source.bin";
        var source = await WorkspaceReader(f).CaptureArtifactSourceTenantAsync(owner, path);
        Assert.Equal(owner, source.Observation.Owner); Assert.Equal(path, source.Observation.Path);
        Assert.Equal("application/octet-stream", source.Observation.ContentType); Assert.Equal(original.Length, source.Observation.ByteSize);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(original)), source.Observation.Sha256Hex);
        Assert.Equal(64, source.Observation.ObservationHash.Length); Assert.Equal(original, source.CopyContent());
        var exposed = source.CopyContent(); exposed[0] = 42; Assert.Equal(original, source.CopyContent());
        await source.RequireCurrentAsync();
        var physical = working ? AgentWorkspacePhysicalPaths.WorkingDirectory(Path.Combine(f.Root, "workspaces"), s.Agent.Instance.InstanceId, s.Run.SessionId)
            : AgentWorkspacePhysicalPaths.SpaceHomeDirectory(Path.Combine(f.Root, "workspaces"), owner.TenantId, owner.OwnerId);
        var file = Path.Combine(physical, "source.bin"); var stamp = File.GetLastWriteTimeUtc(file);
        await File.WriteAllBytesAsync(file, new byte[] { 0, 255, 128, 1, 2, 4 }); File.SetLastWriteTimeUtc(file, stamp);
        var error = await Assert.ThrowsAsync<SecurityConflictException>(() => source.RequireCurrentAsync().AsTask());
        Assert.Equal("workspace_source_changed", error.ReasonCode); Assert.Equal(original, source.CopyContent());
        Assert.Equal(4, (await File.ReadAllBytesAsync(file))[^1]);
        Assert.Empty((await ((ITenantArtifactStore)f.Artifacts).ListTenantAsync(s.Tenant, s.Run.SessionId, null, null, 10)).Items);
        var currentRun = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, s.Run.AgentRunId))!;
        Assert.Equal(s.Run.Revision, currentRun.Revision); Assert.Equal(s.Run.Status, currentRun.Status); Assert.Equal(s.Run.Claim, currentRun.Claim);
    }

    [Theory]
    [InlineData(false, false, false)] [InlineData(true, false, false)] [InlineData(false, true, false)] [InlineData(true, true, false)]
    [InlineData(false, false, true)] [InlineData(true, false, true)] [InlineData(false, true, true)] [InlineData(true, true, true)]
    public async Task Tenant_Artifact_sources_preserve_complete_Office_packages_and_actual_owned_location(bool sqlite, bool workbook, bool working)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var s = await Setup(f, "general-assistant", 35);
        var processor = new OfficeProcessor();
        var bytes = workbook ? processor.CreateWorkbook(new([new("Dữ liệu", [[new("number", Number: 0), new("boolean", Boolean: false), new("text", Text: "Private Office body")]])]), default).Bytes
            : processor.CreateDocument(new("Exact report", [new(null, ["Private Office body: thử nghiệm"], null)]), default).Bytes;
        var name = workbook ? "source.xlsx" : "source.docx"; await SeedRuntimeWorkspace(f, s, working, name, bytes, OfficeLimits.MaxPackageBytes);
        var owner = WorkspaceOwner(s, working);
        var source = await WorkspaceReader(f).CaptureArtifactSourceTenantAsync(owner, owner.LogicalRoot + "/" + name);
        Assert.Equal(bytes, source.CopyContent()); Assert.Equal(bytes.Length, source.Observation.ByteSize);
        Assert.Equal(workbook ? OfficeLimits.SpreadsheetMime : OfficeLimits.DocumentMime, source.Observation.ContentType);
        await source.RequireCurrentAsync();
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_Artifact_source_refuses_foreign_same_Account_owners_mismatched_Sessions_and_nonconcrete_paths_without_creating_roots(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var s = await Setup(f, "general-assistant", 35); var store = WorkspaceReader(f);
        var system = (await f.Iam.ReadSystemAsync()).Principals.Single(p => p.AccountId == s.Owner.AccountId);
        var beta = await f.Access.CreateTenantAsync(system.PrincipalId, s.Owner.AccountId, "Beta", "beta");
        var betaActor = s.Owner with { PrincipalId = beta.Users.Single().PrincipalId };
        var other = await f.Agents.CreateAsync(betaActor.PrincipalId, beta.Tenant.TenantId, "examiner", 1, beta.Tenant.Revision);
        var sibling = await f.Agents.CreateAsync(s.Owner.PrincipalId, s.Tenant, "examiner", 1, (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
        Assert.Throws<ArgumentException>(() => new TenantWorkspaceReference.SpaceHome(Guid.Empty, Guid.NewGuid()));
        Assert.Throws<ArgumentException>(() => new TenantWorkspaceReference.SpaceHome(s.Tenant, Guid.Empty));
        Assert.Throws<ArgumentException>(() => new TenantWorkspaceReference.SessionWorking(s.Tenant, Guid.Empty));
        var foreignHome = (await ((ITenantSpaceStore)f.Iam).FindPrivateSpaceAsync(beta.Tenant.TenantId, other.Instance.InstanceId))!;
        foreach (var owner in new TenantWorkspaceReference.SpaceHome[] { new(s.Tenant, foreignHome.SpaceId), new(beta.Tenant.TenantId, SpaceId(s)) })
            await Assert.ThrowsAsync<AccessDeniedException>(() => store.CaptureArtifactSourceTenantAsync(owner, "/home/source").AsTask());
        var actual = WorkspaceOwner(s);
        foreach (var path in new[] { "source", "/home/*", "/home/a?", "/home/[x]", "/home/../source", "/working/source", "/home" })
            await Assert.ThrowsAsync<AgentCoreException>(() => store.CaptureArtifactSourceTenantAsync(actual, path).AsTask());
        await Assert.ThrowsAsync<AgentCoreException>(() => store.CaptureArtifactSourceTenantAsync(actual, "/home/missing").AsTask());
        Assert.False(Directory.Exists(AgentWorkspacePhysicalPaths.AgentRoot(Path.Combine(f.Root, "workspaces"), s.Agent.Instance.InstanceId)));
        Assert.False(Directory.Exists(AgentWorkspacePhysicalPaths.AgentRoot(Path.Combine(f.Root, "workspaces"), other.Instance.InstanceId)));
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_Artifact_source_applies_configured_and_global_bounds_and_revalidation_reports_missing_source_safely(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var s = await Setup(f, "general-assistant", 35);
        await SeedRuntimeWorkspace(f, s, false, "source.bin", [0, 1, 2, 3, 4]); var owner = WorkspaceOwner(s);
        var restricted = new FileSpaceWorkspaceStore(Path.Combine(f.Root, "workspaces"), (ITenantSpaceStore)f.Iam, (ITenantAgentStore)f.Iam, (ITenantSessionStore)f.Iam, (ITenantAgentDecommissionStore)f.Iam, f.Sandboxes, maxFileBytes: 4);
        Assert.Equal("WorkspaceQuotaExceeded", (await Assert.ThrowsAsync<AgentCoreException>(() => restricted.CaptureArtifactSourceTenantAsync(owner, "/home/source.bin").AsTask())).Code);
        var source = await WorkspaceReader(f).CaptureArtifactSourceTenantAsync(owner, "/home/source.bin");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.RequireCurrentAsync(cancellation.Token).AsTask());
        var physical = Path.Combine(AgentWorkspacePhysicalPaths.SpaceHomeDirectory(Path.Combine(f.Root, "workspaces"), owner.TenantId, owner.OwnerId), "source.bin");
        File.Delete(physical);
        Assert.Equal("workspace_source_changed", (await Assert.ThrowsAsync<SecurityConflictException>(() => source.RequireCurrentAsync().AsTask())).ReasonCode);
        using (var file = new FileStream(physical, FileMode.CreateNew)) file.SetLength(ArtifactLimits.MaxBytesEach + 1);
        var permissive = new FileSpaceWorkspaceStore(Path.Combine(f.Root, "workspaces"), (ITenantSpaceStore)f.Iam, (ITenantAgentStore)f.Iam, (ITenantSessionStore)f.Iam, (ITenantAgentDecommissionStore)f.Iam, f.Sandboxes, maxFileBytes: ArtifactLimits.MaxBytesEach * 2);
        Assert.Equal("WorkspaceQuotaExceeded", (await Assert.ThrowsAsync<AgentCoreException>(() => permissive.CaptureArtifactSourceTenantAsync(owner, "/home/source.bin").AsTask())).Code);
    }
}
