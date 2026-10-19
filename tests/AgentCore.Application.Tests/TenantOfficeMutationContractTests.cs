using System.Text.Json;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Tests;

public sealed class TenantOfficeMutationContractTests
{
    [Theory]
    [InlineData("office.document.create", """{"path":"/home/a.docx","sections":[],"tenantId":"forged"}""")]
    [InlineData("office.document.create", """{"path":"/home/a.docx","sections":[{"paragraphs":["x"],"principalId":"forged"}]}""")]
    [InlineData("office.document.create", """{"path":"/home/a.docx","sections":[{"heading":"a","heading":"b"}]}""")]
    [InlineData("office.document.create", """{"path":"/home/a.xlsx","sections":[{}]}""")]
    [InlineData("office.document.create", """{"path":"/home/../a.docx","sections":[{}]}""")]
    [InlineData("office.document.edit", """{"path":"/home/a.docx","operations":[{"operation":"delete","selector":"p:1"}]}""")]
    [InlineData("office.document.edit", """{"path":"/home/a.docx","operations":[{"operation":"replace_paragraph","selector":"p:1","text":null}]}""")]
    [InlineData("office.spreadsheet.create", """{"path":"/working/a.xlsx","sheets":[{"name":"S","rows":[[{"type":"number","number":"0"}]]}]}""")]
    [InlineData("office.spreadsheet.create", """{"path":"/working/a.xlsx","sheets":[{"name":"S","rows":[[{"type":"boolean","boolean":false,"grantId":"forged"}]]}]}""")]
    [InlineData("office.spreadsheet.edit", """{"path":"/working/a.xlsx","operations":[{"operation":"set_cells","sheet":"S","cells":[[{"type":"text","text":"x","text":"y"}]]}]}""")]
    [InlineData("office.spreadsheet.edit", """{"path":"/working/a*.xlsx","operations":[{"operation":"add_sheet","sheet":"S"}]}""")]
    public void Office_mutation_refuses_nested_authority_duplicate_malformed_or_nonowned_arguments(string tool, string json) =>
        Assert.Throws<ArgumentException>(() => TenantWorkspaceMutationContract.Parse(Guid.NewGuid(), tool, json));

    [Theory]
    [InlineData("office.document.create", """{"sections":[{"paragraphs":["","試験"],"pageBreakBefore":false}],"path":"/home/a.docx"}""", false)]
    [InlineData("office.document.edit", """{"operations":[{"operation":"replace_paragraph","selector":"exact","text":""}],"path":"/home/a.docx"}""", true)]
    [InlineData("office.spreadsheet.create", """{"sheets":[{"name":"S","rows":[[{"type":"number","number":0},{"type":"boolean","boolean":false}]]}],"path":"/working/a.xlsx"}""", false)]
    [InlineData("office.spreadsheet.edit", """{"operations":[{"operation":"set_formula","sheet":"S","range":"A1","formula":"1+1"}],"path":"/working/a.xlsx"}""", true)]
    public void Office_mutation_canonical_consent_preserves_empty_zero_false_and_exact_source_read_targets(string tool, string json, bool edit)
    {
        var session = Guid.NewGuid(); var args = TenantWorkspaceMutationContract.Parse(session, tool, json);
        Assert.Equal(args.ParametersHash, TenantWorkspaceMutationContract.Parse(session, tool, args.CanonicalArguments).ParametersHash);
        Assert.Equal(edit, args.Targets.Any(t => t.Permission == "workspaces.read")); Assert.Contains(args.Targets, t => t.Permission == "workspaces.write");
        var value = TenantApprovalRules.MutationParameters(session, args, new('a', 64));
        var operation = TenantApprovalRules.PrepareWorkspaceMutation(Guid.NewGuid(), Guid.NewGuid(), value, 1, 1);
        var intent = TenantApprovalToolContract.Parse(new("exact", ToolCatalog.RequestAuthorization, JsonSerializer.Serialize(new { operation = "mutate_workspace", mutation = new { tool, arguments = JsonSerializer.Deserialize<JsonElement>(json) } })));
        Assert.True(intent.Matches(operation));
        Assert.NotEqual(operation.ActionHash, TenantApprovalRules.PrepareWorkspaceMutation(operation.OperationId, operation.InstanceId, value with { ObservationHash = new('b', 64) }, 1, 1).ActionHash);
    }
    [Fact]
    public void Office_mutation_bounds_UTF8_before_processing()
    {
        var json = JsonSerializer.Serialize(new { path = "/home/a.docx", sections = new[] { new { paragraphs = Enumerable.Repeat(new string('é', 16000), 3) } } }, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        Assert.Throws<ArgumentException>(() => TenantWorkspaceMutationContract.Parse(Guid.NewGuid(), ToolCatalog.OfficeDocumentCreate, json));
    }
}
