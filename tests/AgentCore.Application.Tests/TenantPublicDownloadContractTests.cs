using System.Text.Json;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Tests;

public sealed class TenantPublicDownloadContractTests
{
    [Theory]
    [InlineData("{}")] [InlineData("[]")] [InlineData("null")]
    [InlineData("{\"url\":\"file:///etc/passwd\"}")]
    [InlineData("{\"url\":\"https://user:password@example.test/file\"}")]
    [InlineData("{\"url\":\"https://example.test/a\",\"url\":\"https://example.test/b\"}")]
    [InlineData("{\"url\":\"https://example.test/a\",\"tenantId\":\"other\"}")]
    [InlineData("{\"url\":\"https://example.test/a\",\"path\":\"/home/../escape\"}")]
    [InlineData("{\"url\":\"https://example.test/a\",\"path\":\"/working\"}")]
    [InlineData("{\"url\":\"https://example.test/a\",\"path\":\"/agent/instance/resources/a\"}")]
    [InlineData("{\"url\":\"https://example.test/a\",\"path\":null}")]
    public void Download_rejects_ambiguous_input_or_caller_authority(string json) => Assert.Throws<ArgumentException>(() => TenantWorkspaceMutationContract.Parse(Guid.NewGuid(), ToolCatalog.WebDownload, json));

    [Fact]
    public void Canonical_expansion_cannot_bypass_the_saved_URL_bound() => Assert.Throws<ArgumentException>(() => TenantPublicDownloadContract.Url("https://example.test/" + new string('試', 1200)));

    [Theory]
    [InlineData(null)] [InlineData("/home/form.docx")] [InlineData("/working/form.docx")]
    public void Download_is_a_stable_workspace_write_with_body_free_source_binding(string? path)
    {
        var session = Guid.NewGuid(); var json = path is null ? JsonSerializer.Serialize(new { url = "https://example.test/form.docx" }) : JsonSerializer.Serialize(new { url = "https://example.test/form.docx", path });
        var args = TenantWorkspaceMutationContract.Parse(session, ToolCatalog.WebDownload, json);
        Assert.Equal(new[] { (path ?? "/home/downloads", "workspaces.write") }, args.Targets);
        Assert.Equal(args.ParametersHash, TenantWorkspaceMutationContract.Parse(session, args.Tool, args.CanonicalArguments).ParametersHash);
        Assert.NotEqual(args.ParametersHash, TenantWorkspaceMutationContract.Parse(session, args.Tool, json.Replace("form.docx", "other.docx")).ParametersHash);
    }
    [Theory]
    [InlineData("/home/../escape")] [InlineData("/working/a/../escape")] [InlineData("/agent/instance/resources/a")]
    public void Download_receipt_cannot_reintroduce_noncanonical_paths(string path) => Assert.Throws<ArgumentException>(() => TenantPublicDownloadRules.Validate(new("https://example.test/a", "https://example.test/a", path, "application/octet-stream", 1, new('a', 64))));
}
