using System.Text.Json;
using AgentCore.Application.Tools;
using AgentCore.Application.Security;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Tests;

public sealed class TenantAttachmentNamespaceContractTests
{
    [Theory]
    [InlineData("/attachments/")] [InlineData("/attachments/name.txt")] [InlineData("/attachments/../home")]
    [InlineData("/attachments/00112233-4455-6677-8899-aabbccddeeff")] [InlineData("/attachments/subdir")]
    public void Collection_does_not_accept_filename_aliases_traversal_or_exact_file_paths(string path)
    {
        Assert.Throws<ArgumentException>(() => TenantWorkspaceReadContract.Parse(ToolCatalog.WorkspaceList, JsonSerializer.Serialize(new { path })));
        Assert.Throws<ArgumentException>(() => TenantWorkspaceReadContract.Parse(ToolCatalog.WorkspaceSearch, JsonSerializer.Serialize(new { path, query = "needle" })));
    }
    [Theory]
    [InlineData("/home/a")] [InlineData("/attachments/name.txt")] [InlineData("/attachments/00112233445566778899aabbccddeeff")]
    [InlineData("/attachments/00112233-4455-6677-8899-AABBCCDDEEFF")]
    public void Collection_cursor_is_an_exact_canonical_child_path(string cursor) =>
        Assert.Throws<ArgumentException>(() => TenantWorkspaceReadContract.Parse(ToolCatalog.WorkspaceList, JsonSerializer.Serialize(new { path = "/attachments", cursor })));
    [Fact]
    public void Exact_consent_validates_members_and_excludes_other_resource_payloads()
    {
        var member = new PreparedAttachmentReadMember(Guid.NewGuid(), Guid.NewGuid(), new string('a', 64));
        var value = new PreparedWorkspaceRead(Guid.NewGuid(), false, ToolCatalog.WorkspaceList, """{"path":"/attachments","limit":1}""", new string('b', 64), AttachmentMembersJson: JsonSerializer.Serialize(new[] { member }));
        var operation = TenantApprovalRules.PrepareWorkspaceRead(Guid.NewGuid(), Guid.NewGuid(), value, 1, 1);
        Assert.Equal("sessions.read", operation.Permission); Assert.Equal(value.SessionId.ToString("D"), operation.ResourceId);
        Assert.Throws<ArgumentException>(() => TenantApprovalRules.PrepareWorkspaceRead(operation.OperationId, operation.InstanceId, value with { ResourceMembersJson = "[]" }, 1, 1));
        Assert.Throws<ArgumentException>(() => TenantApprovalRules.AttachmentReadMembers(value with { AttachmentMembersJson = JsonSerializer.Serialize(new[] { member, member }) }));
        Assert.Throws<ArgumentException>(() => TenantApprovalRules.AttachmentReadMembers(value with { AttachmentMembersJson = JsonSerializer.Serialize(new[] { member with { EntryId = Guid.Empty } }) }));
        Assert.Throws<ArgumentException>(() => TenantApprovalRules.AttachmentReadMembers(value with { Working = true }));
    }
}
