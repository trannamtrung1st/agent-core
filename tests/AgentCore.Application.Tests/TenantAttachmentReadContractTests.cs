using AgentCore.Application.Tools;
using System.Text.Json;
using AgentCore.Application.Security;
using AgentCore.Application.Ports;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Tests;

public sealed class TenantAttachmentReadContractTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"attachmentId\":\"/private/source.txt\"}")]
    [InlineData("{\"attachmentId\":\"00000000-0000-0000-0000-000000000000\"}")]
    [InlineData("{\"attachmentId\":\"00112233-4455-6677-8899-aabbccddeeff\",\"sessionId\":\"00112233-4455-6677-8899-aabbccddeeff\"}")]
    [InlineData("{\"attachmentId\":\"00112233-4455-6677-8899-aabbccddeeff\",\"attachmentId\":\"00112233-4455-6677-8899-aabbccddeeff\"}")]
    [InlineData("{\"attachmentId\":\"00112233-4455-6677-8899-aabbccddeeff\",\"sheet\":null}")]
    [InlineData("{\"attachmentId\":\"00112233-4455-6677-8899-aabbccddeeff\",\"offset\":-1}")]
    [InlineData("{\"attachmentId\":\"00112233-4455-6677-8899-aabbccddeeff\",\"limit\":101}")]
    [InlineData("{\"attachmentId\":\"00112233-4455-6677-8899-aabbccddeeff\",\"range\":\"A1:B2\",\"cursor\":\"another-window\"}")]
    public void Read_rejects_scope_injection_host_paths_duplicates_and_invalid_windows(string json) =>
        Assert.ThrowsAny<ArgumentException>(() => TenantAttachmentReadContract.Parse(json));

    [Theory]
    [InlineData("/attachments")] [InlineData("/attachments/")]
    [InlineData("/attachments/00112233445566778899aabbccddeeff")]
    [InlineData("/attachments/00112233-4455-6677-8899-AABBCCDDEEFF")]
    [InlineData("/attachments/00112233-4455-6677-8899-aabbccddeeff/read.docx")]
    [InlineData("/attachments/00112233-4455-6677-8899-aabbccddeeff/../private")]
    [InlineData("/attachments/00000000-0000-0000-0000-000000000000")]
    public void Virtual_attachment_read_rejects_namespace_aliases_and_path_injection(string path)
    {
        foreach (var tool in new[] { ToolCatalog.WorkspaceRead, ToolCatalog.OfficeDocumentInspect, ToolCatalog.OfficeSpreadsheetInspect })
            Assert.Throws<ArgumentException>(() => TenantAttachmentVirtualReadContract.Resolve(tool, JsonSerializer.Serialize(new { path })));
    }

    [Theory] [InlineData(ToolCatalog.WorkspaceList)] [InlineData(ToolCatalog.WorkspaceSearch)] [InlineData(ToolCatalog.WorkspaceWrite)]
    public void Virtual_attachment_file_reader_cannot_authorize_discovery_or_mutation(string tool) =>
        Assert.Throws<ArgumentException>(() => TenantAttachmentVirtualReadContract.Resolve(tool, """{"path":"/attachments/00112233-4455-6677-8899-aabbccddeeff"}"""));

    [Fact]
    public void Exact_virtual_consent_cannot_substitute_a_workspace_source_reader_media_or_read_window()
    {
        var id = Guid.NewGuid(); var path = "/attachments/" + id.ToString("D");
        var arguments = TenantAttachmentReadContract.Canonical(new(id));
        var saved = TenantApprovalRules.PrepareAttachmentRead(Guid.NewGuid(), Guid.NewGuid(),
            new(Guid.NewGuid(), id, Guid.NewGuid(), "source.txt", "text/plain", 10, new string('a', 64), "test-v1", arguments, false), 1, 1);
        Assert.True(new TenantWorkspaceReadIntent(ToolCatalog.WorkspaceRead, JsonSerializer.Serialize(new { path })).Matches(saved));
        Assert.False(new TenantWorkspaceReadIntent(ToolCatalog.WorkspaceRead, """{"path":"/home/source.txt"}""").Matches(saved));
        Assert.False(new TenantWorkspaceReadIntent(ToolCatalog.WorkspaceRead, JsonSerializer.Serialize(new { path = "/attachments/" + Guid.NewGuid().ToString("D") })).Matches(saved));
        Assert.False(new TenantWorkspaceReadIntent(ToolCatalog.OfficeSpreadsheetInspect, JsonSerializer.Serialize(new { path })).Matches(saved));
        var docArguments = TenantAttachmentReadContract.Canonical(new(id, Offset: 1, Limit: 1));
        var document = TenantApprovalRules.PrepareAttachmentRead(Guid.NewGuid(), saved.InstanceId, saved.AttachmentRead! with {
            ContentType = OfficeLimits.DocumentMime, DisplayName = "source.docx", ArgumentsJson = docArguments }, 1, 1);
        Assert.True(new TenantWorkspaceReadIntent(ToolCatalog.OfficeDocumentInspect, JsonSerializer.Serialize(new { path, offset = 1, limit = 1 })).Matches(document));
        Assert.False(new TenantWorkspaceReadIntent(ToolCatalog.OfficeDocumentInspect, JsonSerializer.Serialize(new { path, offset = 2, limit = 1 })).Matches(document));
    }
}
