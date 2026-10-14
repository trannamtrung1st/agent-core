using System.Net.Http.Json;
using System.Text.Json;
using AgentCore.Application.Credentials;
using AgentCore.Application.Drive;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Contracts.Http;
using Microsoft.Extensions.DependencyInjection;
namespace AgentCore.Api.Tests;
public sealed partial class DriveJourneyTests
{
    [Fact]
    public async Task Trusted_batch_preparation_and_exact_grant_reject_stale_changed_and_revoked_actions()
    {
        using var host = new Factory(); var (_, access, _) = await Recovery(host, "none", 28);
        var targets = await TrashFiles(host, access, 20);
        var store = host.Services.GetRequiredService<ISyntheticDriveStore>();
        foreach (var t in targets)
        {
            var file = (await store.GetAsync(access.Credential.CredentialId, t.FileId, default))!;
            await store.SaveAsync(access.Credential.CredentialId, file with { Name = new string('長', 200) + new string('"', 55) }, null, file.Version, default);
        }
        var client = host.CreateClient(); client.DefaultRequestHeaders.TryAddWithoutValidation(OwnerCapabilityHeaders.Name, TestOwnerCapability.Token(host.Services));
        var made = await client.PostAsJsonAsync("/api/v2/sessions", new { agentInstanceId = access.InstanceId, mode = "text" }); made.EnsureSuccessStatusCode();
        var session = (await made.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("sessionId").GetGuid();
        var definition = (await host.Services.GetRequiredService<IMemoryStore>().LoadAsync(session))!.Definition;
        var executor = host.Services.GetRequiredService<SessionToolExecutor>();
        var admission = new ToolExecutionAdmission(false, TriggerKind.UserTurn, AgentInstanceId: access.InstanceId, OwnedSessionId: session);
        var args = JsonSerializer.SerializeToElement(new { accountRef = "drive", authEpoch = 1, files = targets.Select(t => new { fileId = t.FileId, expectedVersion = t.ExpectedVersion }) });
        var call = new ModelToolCall("batch-approved", "drive.trash_batch", args.GetRawText());
        var prep = await ToolActionPreparation.PrepareApprovalAsync(executor, call, args, definition: definition, sessionId: session, admission: admission);
        Assert.Null(prep.ErrorJson); Assert.Contains("20 listed", prep.Preparation!.Preview); Assert.Contains("Recovery", prep.Preparation.Details!["Account"]);
        Assert.Equal(20, JsonSerializer.Deserialize<JsonElement>(prep.Preparation.Details["Files"]).GetArrayLength());
        var durablePreview = JsonSerializer.Serialize(new { summary = prep.Preparation.Preview, details = prep.Preparation.Details }, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        Assert.True(durablePreview.Length <= AgentCore.Domain.Conversation.AgentRunLimits.MaxDriveBatchPreviewCharacters);
        var missing = await executor.ExecuteAsync(definition, session, call, 256 * 1024, admission: admission);
        Assert.Contains("approval_required", missing.Text);
        var grant = new ToolApprovalGrant(Guid.NewGuid(), call.Name, prep.Preparation.ActionHash, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var changed = JsonSerializer.SerializeToElement(new { accountRef = "drive", authEpoch = 1, files = targets.Reverse().Select(t => new { fileId = t.FileId, expectedVersion = t.ExpectedVersion }) });
        var denied = await executor.ExecuteAsync(definition, session, call with { ArgumentsJson = changed.GetRawText() }, 256 * 1024, admission: admission, approvalGrant: grant);
        Assert.Contains("stale_approval", denied.Text);
        var otherAccount = await ToolActionPreparation.PrepareApprovalAsync(executor, call with { ArgumentsJson = args.GetRawText().Replace("\"drive\"", "\"other-account\"") }, JsonSerializer.Deserialize<JsonElement>(args.GetRawText().Replace("\"drive\"", "\"other-account\"")), sessionId: session, admission: admission);
        Assert.NotNull(otherAccount.ErrorJson);
        var first = (await store.GetAsync(access.Credential.CredentialId, targets[0].FileId, default))!;
        await store.SaveAsync(access.Credential.CredentialId, first with { Version = "2" }, null, "1", default);
        var stale = await ToolActionPreparation.PrepareApprovalAsync(executor, call, args, sessionId: session, admission: admission);
        Assert.Contains("drive_remote_conflict", stale.ErrorJson);
        var staleRun = await executor.ExecuteAsync(definition, session, call, 256 * 1024, admission: admission, approvalGrant: grant);
        Assert.Contains("drive_remote_conflict", staleRun.Text);
        var credential = host.Services.GetRequiredService<CredentialService>(); var instance = (await host.Services.GetRequiredService<IAgentInstanceStore>().FindAsync(access.InstanceId))!;
        await credential.UnbindAsync(access.InstanceId, access.Binding.BindingId, access.Binding.Revision, instance.Revision);
        var revoked = await executor.ExecuteAsync(definition, session, call, 256 * 1024, admission: admission, approvalGrant: grant);
        Assert.Contains("drive_not_connected", revoked.Text);
        foreach (var t in targets) Assert.False((await store.GetAsync(access.Credential.CredentialId, t.FileId, default))!.Trashed);
    }
    [Fact]
    public async Task Old_immutable_definition_never_gains_batch_trash()
    {
        using var host = new Factory(); var (_, access, _) = await Recovery(host, "none", 27);
        var client = host.CreateClient(); client.DefaultRequestHeaders.TryAddWithoutValidation(OwnerCapabilityHeaders.Name, TestOwnerCapability.Token(host.Services));
        var made = await client.PostAsJsonAsync("/api/v2/sessions", new { agentInstanceId = access.InstanceId, mode = "text" }); made.EnsureSuccessStatusCode();
        var session = (await made.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("sessionId").GetGuid();
        var definition = (await host.Services.GetRequiredService<IMemoryStore>().LoadAsync(session))!.Definition;
        var result = await host.Services.GetRequiredService<SessionToolExecutor>().ExecuteAsync(definition, session, new("call", "drive.trash_batch", "{\"accountRef\":\"drive\",\"authEpoch\":1,\"files\":[{\"fileId\":\"one\",\"expectedVersion\":\"1\"}]}"), 16000,
            admission: new(false, TriggerKind.UserTurn, AgentInstanceId: access.InstanceId));
        Assert.Contains("forbidden", result.Text);
    }
}
