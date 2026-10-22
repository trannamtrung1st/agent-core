using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantBrowserApprovalTests
{
    [Theory] [InlineData("foreign")] [InlineData("00000000-0000-0000-0000-000000000000")] [InlineData("ABCDEF00-0000-0000-0000-000000000001")]
    public void Tenant_Browser_Artifact_drop_rejects_noncanonical_ids(string id) => Assert.Throws<ArgumentException>(() =>
        TenantApprovalRules.BrowserIntent(Guid.NewGuid(), ToolCatalog.BrowserDrop, JsonSerializer.Serialize(new { target = new { by = "testId", value = "drop" }, artifactId = id })));

    [Fact]
    public void Tenant_Browser_Artifact_drop_has_one_exact_source_and_disallows_literal_or_MIME_overrides()
    {
        var id = Guid.NewGuid(); var command = new BrowserDrop(new("testId", "drop"), ArtifactId: id.ToString("D"));
        Assert.Equal(id, Assert.Single(TenantBrowserUploadRules.Ids(command)));
        Assert.Throws<ArgumentException>(() => TenantBrowserUploadRules.Ids(command with { Text = "ambiguous text" }));
        Assert.Throws<ArgumentException>(() => TenantBrowserUploadRules.Ids(command with { MimeType = "text/plain" }));
        foreach (var property in new[] { "text", "mimeType" })
        {
            var json = System.Text.Json.Nodes.JsonNode.Parse(FileTransferArguments(true, id))!; json[property] = "override";
            Assert.Throws<ArgumentException>(() => TenantApprovalRules.BrowserIntent(Guid.NewGuid(), ToolCatalog.BrowserDrop, json.ToJsonString()));
        }
        var schema = TenantBrowserAuthorizationContract.Schema([ToolCatalog.BrowserDrop]).ToJsonString();
        Assert.Contains("Independent current file read", schema); Assert.DoesNotContain("uploads", schema, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("oneOf", schema); Assert.DoesNotContain("base64", schema, StringComparison.OrdinalIgnoreCase);
        Assert.IsType<BrowserDrop>(TenantApprovalRules.BrowserIntent(Guid.NewGuid(), ToolCatalog.BrowserDrop,
            """{"target":{"by":"testId","value":"drop"},"text":"literal only","mimeType":"text/plain"}""").Command);
    }

    [Fact]
    public async Task Tenant_Browser_Artifact_drop_SQLite_refuses_argument_swaps_and_lossy_downgrade()
    {
        await using var f = await Fixture.Create(true, builtInAuthority: true, artifactAuthority: true);
        var s = await Prepare(f); var (file, _) = await UploadSource(f, s);
        var request = (await Request(Service(f, Probe(s)), s, ToolCatalog.BrowserDrop, FileTransferArguments(true, file.ArtifactId))).Request!;
        await using var db = await f.Contexts!.CreateDbContextAsync();
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE TenantApprovalRequests SET PayloadJson = json_set(PayloadJson, '$.Operation.BrowserCommand.ArgumentsJson', '{{}}') WHERE RequestId = {request.RequestId}"));
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => db.GetService<IMigrator>().MigrateAsync("20261020000100_TenantBrowserArtifactPublication"));
        await db.Database.MigrateAsync();
        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Equal(request, await ((ITenantApprovalStore)f.Iam).FindApprovalAsync(s.Tenant, request.RequestId));
    }

    [Fact]
    public async Task Tenant_Browser_Artifact_drop_SQLite_empty_upgrade_downgrade_restores_previous_upload_guards()
    {
        await using var f = await Fixture.Create(true, builtInAuthority: true, artifactAuthority: true);
        await using var db = await f.Contexts!.CreateDbContextAsync();
        await db.GetService<IMigrator>().MigrateAsync("20261020000100_TenantBrowserArtifactPublication");
        await db.Database.MigrateAsync();
        var s = await Prepare(f); var (file, _) = await UploadSource(f, s);
        Assert.NotNull((await Request(Service(f, Probe(s)), s, ToolCatalog.BrowserDrop, FileTransferArguments(true, file.ArtifactId))).Request);
    }
}
