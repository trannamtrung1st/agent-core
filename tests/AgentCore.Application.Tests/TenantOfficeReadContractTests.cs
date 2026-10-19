using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Tests;

public sealed class TenantOfficeReadContractTests
{
    [Theory]
    [InlineData("office.document.inspect", "{\"path\":\"/home/a.docx\",\"offset\":-1}")]
    [InlineData("office.document.inspect", "{\"path\":\"/home/a.docx\",\"limit\":101}")]
    [InlineData("office.document.inspect", "{\"path\":\"/home/a.docx\",\"sheet\":\"Hidden\"}")]
    [InlineData("office.document.inspect", "{\"path\":\"/home/a.xlsx\"}")]
    [InlineData("office.document.inspect", "{\"path\":\"/home/a.docx\",\"path\":\"/working/b.docx\"}")]
    [InlineData("office.spreadsheet.inspect", "{\"path\":\"/working/a.xlsx\",\"range\":\"A1\",\"cursor\":\"opaque\"}")]
    [InlineData("office.spreadsheet.inspect", "{\"path\":\"/working/a.xlsx\",\"offset\":0}")]
    [InlineData("office.spreadsheet.inspect", "{\"path\":\"/agent/a.xlsx\"}")]
    [InlineData("office.spreadsheet.inspect", "{\"path\":\"/working/a.xlsx\",\"tenantId\":\"other\"}")]
    public void Office_reads_reject_wrong_format_mixed_fields_duplicate_keys_and_caller_authority(string tool, string json) =>
        Assert.Throws<ArgumentException>(() => TenantWorkspaceReadContract.Parse(tool, json));

    [Theory] [InlineData("office.document.inspect", "{\"path\":\"/home/a.docx\",\"offset\":2,\"limit\":3}")]
    [InlineData("office.spreadsheet.inspect", "{\"path\":\"/working/a.xlsx\",\"sheet\":\"試験\",\"cursor\":\"opaque/cursor\"}")]
    public void Exact_Office_read_consent_roundtrips_without_treating_sparse_cursor_as_a_workspace_path(string tool, string json)
    {
        var args = TenantWorkspaceReadContract.Parse(tool, json); var canonical = TenantWorkspaceReadContract.Canonical(tool, args);
        Assert.Equal(args, TenantWorkspaceReadContract.Parse(tool, canonical));
        var read = new PreparedWorkspaceRead(Guid.NewGuid(), args.Path.StartsWith("/working/"), tool, canonical, new string('a', 64));
        var operation = TenantApprovalRules.PrepareWorkspaceRead(Guid.NewGuid(), Guid.NewGuid(), read, 1, 1);
        var intent = TenantApprovalToolContract.Parse(new("exact", ToolCatalog.RequestAuthorization,
            System.Text.Json.JsonSerializer.Serialize(new { operation = "inspect_workspace", read = new { tool, arguments = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(canonical) } })));
        Assert.True(intent.Matches(operation));
        Assert.NotEqual(operation.OperationFingerprint, TenantApprovalRules.PrepareWorkspaceRead(operation.OperationId, operation.InstanceId,
            read with { ArgumentsJson = TenantWorkspaceReadContract.Canonical(tool, args with { Path = args.Path.Replace("a.", "b.") }) }, 1, 1).OperationFingerprint);
    }
}
