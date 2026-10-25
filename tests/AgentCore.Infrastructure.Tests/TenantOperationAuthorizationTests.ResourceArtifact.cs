using System.Security.Cryptography;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantOperationAuthorizationTests
{
    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task Tenant_resource_Artifact_direct_copies_exact_origin_and_replays_only_receipt(bool sqlite, bool inherited)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true, artifactAuthority: true);
        Environment s; string path; byte[] bytes;
        if (inherited)
        {
            s = await Setup(f, "general-assistant", 32); var resource = s.Run.Admission.Configuration!.Resources.First(r => r.Key.StartsWith("definition:", StringComparison.Ordinal));
            path = resource.VirtualPath; bytes = (await f.RunContent.ReadAsync(resource.ContentSha256))!;
        }
        else { var v = await ResourceSourceCase(f, data: new AgentCore.Infrastructure.Office.OfficeProcessor().CreateDocument(new(null, [new(null, ["Exact resource package"], null)]), default).Bytes, logicalPath: "literal[resource]?.docx", mediaType: OfficeLimits.DocumentMime); s = v.Setup; path = v.Path; bytes = v.Bytes; }
        var frame = await ClaimArtifactRun(f, s); await GrantPublication(f, s); await GrantWorkspaceRuntimeReads(f, s, ["sessions.read", "agents.read", "resources.read"]);
        var args = JsonSerializer.Serialize(new { path, displayName = "resource-copy.bin" }); var service = SourcePublicationService(f);
        var result = await service.ExecuteWorkspaceAsync(new(s.Tenant, frame.RunId), frame.Generation, "resource-export", args);
        Assert.NotNull(result.Receipt); Assert.Null(result.Receipt.RequestId);
        var artifacts = (ITenantArtifactStore)f.Artifacts; var artifact = Assert.Single((await artifacts.ListTenantAsync(s.Tenant, frame.SessionId, null, null, 20)).Items);
        Assert.Equal(result.Receipt.OperationId, artifact.ArtifactId); Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), artifact.Sha256Hex);
        Assert.Equal(bytes, await artifacts.ReadTenantContentAsync(s.Tenant, frame.SessionId, artifact.ArtifactId)); Assert.Null(artifact.WorkspaceLogicalPath);
        Assert.Equal(result, await service.ExecuteWorkspaceAsync(new(s.Tenant, frame.RunId), frame.Generation, "resource-export", args));
        Assert.Single((await artifacts.ListTenantAsync(s.Tenant, frame.SessionId, null, null, 20)).Items);
        Assert.False((await f.Authorizer.CheckAsync(s.Agent.PrincipalId, "artifacts.read", TenantArtifactRules.Scope(s.Tenant, artifact.ArtifactId))).Allowed);
    }
    private sealed class ResourceExportOnlyBuiltIns(IBuiltInAgentDefinitionStore inner) : IBuiltInAgentDefinitionStore
    {
        public ValueTask<IReadOnlyList<AgentCore.Domain.Definitions.AgentDefinition>> ListAsync(CancellationToken ct = default) => inner.ListAsync(ct);
        public async ValueTask<AgentCore.Domain.Definitions.AgentDefinition?> GetAsync(string id, int? version = null, CancellationToken ct = default)
        {
            var value = await inner.GetAsync(id, version, ct);
            return value is null ? null : value with { ExecutionPolicy = value.ExecutionPolicy! with { Capabilities = new("Selected", [ToolCatalog.ArtifactsCreateFromWorkspace]) } };
        }
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_resource_Artifact_export_remains_available_when_current_read_tool_capability_is_removed(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true, artifactAuthority: true); var v = await ResourceSourceCase(f); var s = v.Setup;
        var frame = await ClaimArtifactRun(f, s); await GrantPublication(f, s); await GrantWorkspaceRuntimeReads(f, s, ["sessions.read", "agents.read", "resources.read"]);
        var current = new ResourceExportOnlyBuiltIns(f.BuiltIns);
        Assert.False(TenantWorkspaceReadContract.Allows((await current.GetAsync("general-assistant", 32))!, ToolCatalog.WorkspaceRead));
        var result = await SourcePublicationService(f, resourceSources: ResourceSources(f, currentBuiltIns: current))
            .ExecuteWorkspaceAsync(new(s.Tenant, frame.RunId), frame.Generation, "export-only-capability", JsonSerializer.Serialize(new { path = v.Path, displayName = "copy.txt" }));
        Assert.NotNull(result.Receipt);
        Assert.Equal(v.Bytes, await ((ITenantArtifactStore)f.Artifacts).ReadTenantContentAsync(s.Tenant, frame.SessionId, result.Receipt.OperationId));
    }

}
