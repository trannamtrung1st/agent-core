using System.Text.Json;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Credentials;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Tests;

public sealed class TenantAccountOperationReadContractTests
{
    [Theory]
    [InlineData("{\"accountRef\":\"exact\",\"operationId\":null}")]
    [InlineData("{\"accountRef\":\"exact\",\"operationId\":17}")]
    [InlineData("{\"accountRef\":\"exact\",\"operationId\":\"00000000-0000-0000-0000-000000000000\"}")]
    [InlineData("{\"accountRef\":\"exact\",\"operationId\":\"guessed\"}")]
    [InlineData("{\"accountRef\":\"exact\",\"operationId\":\"b830f89d-1b34-4a6e-a53f-b9fdcf4f9253\",\"fileId\":\"file\"}")]
    [InlineData("{\"accountRef\":\"exact\",\"operationId\":\"b830f89d-1b34-4a6e-a53f-b9fdcf4f9253\",\"resumeAllowed\":true}")]
    public void Operation_inspection_requires_one_exact_read_selector(string arguments) =>
        Assert.Throws<ArgumentException>(() => TenantAccountReadContract.Parse(ToolCatalog.DriveGet, arguments));

    [Fact]
    public void Source_identity_is_hashed_but_never_supplies_raw_source_or_write_authority()
    {
        var id = Guid.NewGuid(); var session = Guid.NewGuid(); var sourceSession = Guid.NewGuid(); var sourceRun = Guid.NewGuid(); var instance = Guid.NewGuid();
        var args = TenantAccountReadContract.Parse(ToolCatalog.DriveGet, JsonSerializer.Serialize(new { accountRef = "exact", operationId = id }));
        var read = new PreparedAccountRead(session, ToolCatalog.DriveGet, args.Json, new(Guid.NewGuid(), Guid.NewGuid(), "exact", 2, 3, 4, 5),
            CredentialPurpose.GoogleDriveAccount, new(id, Guid.NewGuid(), sourceRun, sourceSession, new('a', 64), ToolCatalog.DriveRename, "Indeterminate",
                JsonSerializer.Serialize(new[] { new TenantAccountNativeReference(id, "exact-file", null, null) })));
        var op = TenantApprovalRules.PrepareAccountRead(Guid.NewGuid(), instance, read, 2, 1);
        Assert.Equal(op, TenantApprovalRules.Rebuild(op)); Assert.Equal("credentials.use", op.Permission);
        Assert.NotEqual(op.ActionHash, TenantApprovalRules.PrepareAccountRead(op.OperationId, instance, read with { Source = read.Source! with { RecordedOutcome = "Completed" } }, 2, 1).ActionHash);
        var targets = TenantAccountReadRules.Targets(Guid.NewGuid(), read).ToArray();
        Assert.Equal(2, targets.Count(t => t.Permission == "sessions.read")); Assert.Single(targets, t => t.Permission == "agent-runs.read");
        Assert.Contains(targets, t => t.Permission == "credentials.read"); Assert.Contains(targets, t => t.Permission == "drive.read");
        Assert.DoesNotContain(targets, t => t.Permission is "drive.write" or "drive.delete" or "workspaces.read");
        Assert.Throws<ArgumentException>(() => TenantApprovalRules.PrepareAccountRead(op.OperationId, instance, read with { Source = null }, 2, 1));
        Assert.Throws<ArgumentException>(() => TenantApprovalRules.PrepareAccountRead(op.OperationId, instance, read with { Source = read.Source! with { OperationId = Guid.NewGuid() } }, 2, 1));
    }
}
