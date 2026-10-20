using System.Text.Json;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Credentials;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Tests;

public sealed class TenantAccountReadContractTests
{
    [Theory] [InlineData("email.search", "{\"accountRef\":\"exact\",\"query\":\"from:work@example.test\",\"limit\":1}")]
    [InlineData("email.read", "{\"accountRef\":\"exact\",\"messageId\":\"gmail-account:message\"}")]
    [InlineData("drive.list", "{\"accountRef\":\"exact\",\"folderId\":\"root\",\"limit\":1}")]
    [InlineData("drive.search", "{\"accountRef\":\"exact\",\"query\":\"literal <script>\",\"limit\":1}")]
    [InlineData("drive.get", "{\"accountRef\":\"exact\",\"fileId\":\"exact-file\"}")]
    public void Exact_account_intent_round_trips_canonical_scope_and_rebuilds_its_closed_operation(string tool, string arguments)
    {
        var parsed = TenantAccountReadContract.Parse(tool, arguments);
        var call = new AgentCore.Application.Ports.ModelToolCall("one-call", ToolCatalog.RequestAuthorization,
            JsonSerializer.Serialize(new { operation = TenantAccountReadContract.Operation, read = new { tool, arguments = JsonSerializer.Deserialize<JsonElement>(arguments) } }));
        var intent = TenantApprovalToolContract.Parse(call); Assert.NotNull(intent.AccountRead);
        var operation = TenantApprovalRules.PrepareAccountRead(Guid.NewGuid(), Guid.NewGuid(), new(Guid.NewGuid(), tool, parsed.Json,
            new(Guid.NewGuid(), Guid.NewGuid(), "exact", 1, 2, 3, 4), parsed.Purpose), 2, 1);
        Assert.True(intent.Matches(operation)); Assert.Equal(operation, TenantApprovalRules.Rebuild(operation));
        Assert.Equal("credentials.use", operation.Permission); Assert.Equal("credential", operation.ResourceType);
        Assert.Equal(operation.AccountRead!.Credential.CredentialId.ToString("D"), operation.ResourceId);
        Assert.NotEqual(operation.ActionHash, TenantApprovalRules.PrepareAccountRead(operation.OperationId, operation.InstanceId,
            operation.AccountRead with { Credential = operation.AccountRead.Credential with { SecretRevision = 4 } }, 2, 1).ActionHash);
        Assert.Equal(operation.OperationFingerprint, TenantApprovalRules.PrepareAccountRead(operation.OperationId, operation.InstanceId,
            operation.AccountRead with { Credential = operation.AccountRead.Credential with { SecretRevision = 4 } }, 2, 1).OperationFingerprint);
        Assert.Throws<ArgumentException>(() => (operation with { CredentialRead = new(Guid.NewGuid(), null, "{}", "[]") }).Kind);
    }
    [Theory]
    [InlineData("email.search", "{\"accountRef\":\"exact\",\"limit\":26}")]
    [InlineData("email.search", "{\"accountRef\":\"exact\",\"limit\":\"1\"}")]
    [InlineData("email.search", "{\"accountRef\":\"exact\",\"limit\":1.5}")]
    [InlineData("email.search", "{\"accountRef\":\"exact\",\"limit\":0}")]
    [InlineData("email.search", "{\"accountRef\":\"exact\",\"credentialId\":\"chosen\"}")]
    [InlineData("email.search", "{\"accountRef\":\"exact\",\"accountRef\":\"other\"}")]
    [InlineData("email.read", "{\"accountRef\":\"exact\"}")]
    [InlineData("email.read", "{\"accountRef\":\"exact\",\"messageId\":\"message\",\"query\":\"broader\"}")]
    [InlineData("drive.search", "{\"accountRef\":\"exact\"}")]
    [InlineData("drive.search", "{\"accountRef\":\"exact\",\"query\":\"\"}")]
    [InlineData("drive.list", "{\"accountRef\":\"exact\",\"limit\":101}")]
    [InlineData("drive.get", "{\"accountRef\":\"exact\",\"fileId\":\"../other\"}")]
    [InlineData("drive.download", "{\"accountRef\":\"exact\",\"fileId\":\"file\"}")]
    public void Account_read_rejects_widening_authority_and_invalid_bounds(string tool, string arguments)
    {
        var failure = Record.Exception(() => TenantAccountReadContract.Parse(tool, arguments));
        Assert.True(failure is ArgumentException or AgentCore.Application.Sessions.AgentCoreException, failure?.ToString());
    }
    [Theory] [InlineData("{\"operation\":\"read_connected_account\",\"read\":{\"arguments\":{}}}")]
    [InlineData("{\"operation\":\"read_connected_account\",\"read\":{\"tool\":17,\"arguments\":{}}}")]
    [InlineData("{\"operation\":\"read_connected_account\",\"read\":{\"tool\":\"email.search\"}}")]
    [InlineData("{\"operation\":\"read_connected_account\",\"principal\":\"chosen\",\"read\":{\"tool\":\"email.search\",\"arguments\":{\"accountRef\":\"exact\"}}}")]
    public void Account_read_model_intent_refuses_incomplete_or_caller_selected_authority(string arguments) =>
        Assert.Throws<ArgumentException>(() => TenantApprovalToolContract.Parse(new("call", ToolCatalog.RequestAuthorization, arguments)));
}
