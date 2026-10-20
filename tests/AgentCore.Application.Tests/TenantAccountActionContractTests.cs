using System.Text.Json;
using AgentCore.Application.Security;
using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Application.Tools;
using AgentCore.Domain.Credentials;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Tests;

public sealed class TenantAccountActionContractTests
{
    private static readonly Guid Session = Guid.Parse("00000000-0000-0000-0000-000000000001");
    public static TheoryData<string, string, string> ExactActions => new() {
        { ToolCatalog.EmailCreateDraft, """{"accountRef":"work","to":["friend@example.test"],"subject":"Exact subject","body":"Exact body"}""", "email.draft" },
        { ToolCatalog.EmailSend, """{"accountRef":"work","draftId":"exact-draft"}""", "email.send" },
        { ToolCatalog.DriveCreateFolder, """{"accountRef":"work","authEpoch":1,"name":"Folder"}""", "drive.write" },
        { ToolCatalog.DriveUpload, """{"accountRef":"work","authEpoch":1,"path":"/home/original.bin"}""", "drive.write" },
        { ToolCatalog.DriveDownload, """{"accountRef":"work","fileId":"file","expectedVersion":"1","path":"/working/original.bin"}""", "workspaces.write" },
        { ToolCatalog.DriveRename, """{"accountRef":"work","authEpoch":1,"fileId":"file","expectedVersion":"1","name":"New"}""", "drive.write" },
        { ToolCatalog.DriveMove, """{"accountRef":"work","authEpoch":1,"fileId":"file","expectedVersion":"1","sourceParentId":"before","destinationParentId":"after"}""", "drive.write" },
        { ToolCatalog.DriveTrash, """{"accountRef":"work","authEpoch":1,"fileId":"file","expectedVersion":"1"}""", "drive.delete" },
        { ToolCatalog.DriveTrashBatch, """{"accountRef":"work","authEpoch":1,"files":[{"fileId":"first","expectedVersion":"1"},{"fileId":"second","expectedVersion":"2"}]}""", "drive.delete" }
    };
    [Theory] [MemberData(nameof(ExactActions))]
    public void Closed_actions_round_trip_through_authorization_without_accepting_authority_fields(string tool, string json, string permission)
    {
        var parsed = TenantAccountActionContract.Parse(Session, tool, json);
        Assert.Equal(permission, parsed.Permission); Assert.Equal(parsed, TenantAccountActionContract.Parse(Session, tool, parsed.Json));
        using var args = JsonDocument.Parse(parsed.Json);
        var intent = TenantApprovalToolContract.Parse(new("call", ToolCatalog.RequestAuthorization, JsonSerializer.Serialize(new { operation = TenantAccountActionContract.Operation,
            action = new { tool, arguments = args.RootElement } })));
        Assert.Equal(new TenantAccountActionIntent(tool, parsed.Json), intent.AccountAction);
        foreach (var injected in new[] { "tenantId", "principalId", "grantId", "credentialId", "secretRevision", "operationId", "approved" })
            Assert.Throws<ArgumentException>(() => TenantAccountActionContract.Parse(Session, tool, parsed.Json[..^1] + ",\"" + injected + "\":true}"));
        Assert.Throws<ArgumentException>(() => TenantAccountActionContract.Parse(Session, tool, parsed.Json[..^1] + ",\"accountRef\":\"other\"}"));
    }
    [Theory]
    [InlineData("email.create_draft", "{\"accountRef\":\"work\",\"to\":[\"friend@example.test\\r\\nBcc:evil@example.test\"],\"subject\":\"Hello\",\"body\":\"Body\"}")]
    [InlineData("drive.upload", "{\"accountRef\":\"work\",\"authEpoch\":1,\"path\":\"/home/../escape\"}")]
    [InlineData("drive.download", "{\"accountRef\":\"work\",\"fileId\":\"file\",\"expectedVersion\":\"1\",\"path\":\"/working/*.bin\"}")]
    [InlineData("drive.move", "{\"accountRef\":\"work\",\"authEpoch\":1,\"fileId\":\"file\",\"expectedVersion\":\"1\",\"sourceParentId\":\"same\",\"destinationParentId\":\"same\"}")]
    [InlineData("drive.trash_batch", "{\"accountRef\":\"work\",\"authEpoch\":1,\"files\":[{\"fileId\":\"same\",\"expectedVersion\":\"1\"},{\"fileId\":\"same\",\"expectedVersion\":\"1\"}]}")]
    public void Unsafe_or_ambiguous_actions_are_refused(string tool, string json) => Assert.Throws<ArgumentException>(() => TenantAccountActionContract.Parse(Session, tool, json));

    [Theory] [InlineData(0)] [InlineData(1)]
    public void Immutable_draft_hash_binds_content_sender_account_and_session_with_password_epoch_support(int epoch)
    {
        var args = TenantAccountActionContract.Parse(Session, ToolCatalog.EmailCreateDraft, """{"accountRef":"work","to":["friend@example.test"],"subject":"Hello","body":"Exact body"}""");
        var action = new PreparedAccountAction(Session, args.ToolName, args.Json, new(Guid.NewGuid(), Guid.NewGuid(), "work", 1, 1, 1, epoch), CredentialPurpose.EmailAccount,
            args.Permission, "[]", new("exact-draft", """{"to":["friend@example.test"],"cc":[],"bcc":[]}""", "Hello", "Exact body", "sender@example.test", 1));
        var instance = Guid.NewGuid(); var operation = TenantApprovalRules.PrepareAccountAction(Guid.NewGuid(), instance, action, 1, 1);
        Assert.Equal(operation, TenantApprovalRules.Rebuild(operation));
        Assert.NotEqual(operation.ActionHash, TenantApprovalRules.PrepareAccountAction(Guid.NewGuid(), instance, action with { Draft = action.Draft! with { Sender = "other@example.test" } }, 1, 1).ActionHash);
        Assert.Throws<ArgumentException>(() => TenantApprovalRules.PrepareAccountAction(Guid.NewGuid(), instance, action with { Draft = action.Draft! with { Body = "Changed" } }, 1, 1));
        Assert.NotEqual(operation.ActionHash, TenantApprovalRules.PrepareAccountAction(Guid.NewGuid(), instance, action with { Credential = action.Credential with { CredentialId = Guid.NewGuid() } }, 1, 1).ActionHash);
    }
    [Fact]
    public async Task Model_action_schemas_limit_selected_tools_and_never_offer_autonomous_or_report_execution()
    {
        var definition = await CapabilityProjectionTests.Definition(ToolCatalog.EmailCreateDraft, ToolCatalog.DriveTrash, ToolCatalog.BackgroundStart);
        var instance = Guid.NewGuid(); var session = Guid.NewGuid(); var activation = Guid.NewGuid(); var at = DateTimeOffset.UtcNow;
        var context = CapabilityProjectionTests.Context(definition) with {
            AgentInstanceId = instance, OwnedSessionId = session,
            Configuration = new(definition, 1, 1, [], [new(Guid.NewGuid(), instance, Guid.NewGuid(), "work", 1, at, at)]),
            ExecutionContext = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), session, Guid.NewGuid(), activation, activation, null, null, ExecutionOrigin.UserMessage) };
        var descriptor = Assert.Single(ToolCatalog.For(definition, context, ToolConfigurationGates.AllowAll), t => t.Name == ToolCatalog.RequestAuthorization);
        Assert.Contains(TenantAccountActionContract.Operation, descriptor.ParametersJson);
        Assert.Contains(ToolCatalog.EmailCreateDraft, descriptor.ParametersJson); Assert.Contains(ToolCatalog.DriveTrash, descriptor.ParametersJson);
        Assert.DoesNotContain(ToolCatalog.EmailSend, descriptor.ParametersJson); Assert.DoesNotContain(ToolCatalog.DriveUpload, descriptor.ParametersJson);
        Assert.DoesNotContain(TenantAccountActionContract.Operation, Assert.Single(ToolCatalog.For(definition, context with { Configuration = new(definition, 1, 1, []) }, ToolConfigurationGates.AllowAll), t => t.Name == ToolCatalog.RequestAuthorization).ParametersJson);
        var background = TenantTaskExecutionContract.Authorization(definition);
        Assert.Contains(TenantAccountActionContract.Operation, background.ModelDefinition.ParametersJson); Assert.Contains(ToolCatalog.EmailCreateDraft, background.ModelDefinition.ParametersJson);
        Assert.DoesNotContain(ToolCatalog.EmailSend, background.ModelDefinition.ParametersJson);
        var execution = context.ExecutionContext!;
        var autonomous = context with { ExecutionContext = new(execution.TenantId, execution.AgentPrincipalId, null, session, execution.RunId, activation, activation, null, null, ExecutionOrigin.ScheduledOccurrence) };
        Assert.DoesNotContain(ToolCatalog.For(definition, autonomous, ToolConfigurationGates.AllowAll), t => t.Name == ToolCatalog.RequestAuthorization);
    }

}
