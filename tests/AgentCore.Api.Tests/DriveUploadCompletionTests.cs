using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Net.Http.Json;
using AgentCore.Application.Drive;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Tools;
using AgentCore.Contracts.Http;
using AgentCore.Domain.Definitions;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed partial class DriveJourneyTests
{
    [Fact]
    public async Task Seven_uploads_verify_exact_bytes_when_Drive_normalizes_MIME_parameters()
    {
        using var host = new Factory();
        var (service, access, fault) = await Recovery(host, "none");
        fault.NormalizeUploadMime = true;
        var names = new[] { "sample.docx", "sample.xlsx", "sample.csv", "sample.js", "sample.doc", "sample.xls", "sample.cs" };
        foreach (var name in names)
        {
            var bytes = Encoding.UTF8.GetBytes("Exact fixture bytes for " + name);
            var mime = name.EndsWith(".docx") ? "application/vnd.openxmlformats-officedocument.wordprocessingml.document"
                : name.EndsWith(".xlsx") ? "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" : "text/plain; charset=utf-8";
            var effect = new DriveEffect(name, "root", Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.Length, mime);
            var receipt = await service.EffectAsync(access, Guid.NewGuid(), "drive.upload", 1, null, effect, bytes, default);
            Assert.Equal("Succeeded", receipt.Status); Assert.False(receipt.File.Trashed);
            Assert.Equal(bytes, (await service.ReadAsync(access, receipt.File.Id, receipt.File.Version, default)).Bytes);
        }
        Assert.Equal(7, fault.Uploads);
        Assert.Equal(7, (await service.ListAsync(access, null, null, null, 20, default)).Items.Count);
    }

    [Fact]
    public async Task Previously_uncertain_upload_in_Trash_is_terminal_and_a_fresh_explicit_upload_can_succeed()
    {
        using var host = new Factory(); var (service, access, fault) = await Recovery(host, "after");
        var id = Guid.NewGuid();
        var pending = await Assert.ThrowsAsync<DriveIndeterminateException>(() => service.EffectAsync(access, id, "drive.upload", 1, null, RecoveryEffect, RecoveryBytes, default).AsTask());
        await access.Provider.TrashAsync(pending.FileId, default);
        var failure = await Assert.ThrowsAsync<AgentCoreException>(() => service.InspectOperationAsync(access, id, default).AsTask());
        Assert.Equal("drive_operation_failed", failure.Code); Assert.Contains("Trash", failure.Message);
        var saved = (await host.Services.GetRequiredService<IDriveOperationStore>().GetAsync(access.InstanceId, id, default))!;
        Assert.Equal("Failed", saved.State); Assert.Null(saved.ProtectedUploadSession);
        Assert.Contains("verified_target_trashed", saved.ResultJson);
        Assert.Null(await host.Services.GetRequiredService<IDriveOperationStore>().FindAsync(access.InstanceId, access.Credential.CredentialId, saved.EffectHash, default));
        fault.Mode = "none";
        await Assert.ThrowsAsync<AgentCoreException>(() => service.EffectAsync(access, id, "drive.upload", 1, null, RecoveryEffect, RecoveryBytes, default).AsTask());
        Assert.Equal(1, fault.Uploads);
        var fresh = await service.EffectAsync(access, Guid.NewGuid(), "drive.upload", 1, null, RecoveryEffect, RecoveryBytes, default);
        Assert.Equal("Succeeded", fresh.Status); Assert.NotEqual(pending.FileId, fresh.File.Id);
        Assert.Single((await service.ListAsync(access, null, null, null, 20, default)).Items);
    }

    [Fact]
    public async Task Completed_checksum_mismatch_reports_exact_cleanup_guidance_without_resuming_or_deleting()
    {
        using var host = new Factory(); var (service, access, fault) = await Recovery(host, "after", 28);
        var id = Guid.NewGuid();
        var pending = await Assert.ThrowsAsync<DriveIndeterminateException>(() => service.EffectAsync(access, id, "drive.upload", 1, null, RecoveryEffect, RecoveryBytes, default).AsTask());
        var operations = host.Services.GetRequiredService<IDriveOperationStore>();
        var op = (await operations.GetAsync(access.InstanceId, id, default))!;
        await operations.UpdateAsync(op with { ProtectedUploadSession = "protected-original-session", Revision = op.Revision + 1 }, op.Revision, default);
        var files = host.Services.GetRequiredService<ISyntheticDriveStore>();
        var file = (await files.GetAsync(access.Credential.CredentialId, pending.FileId, default))!;
        await files.SaveAsync(access.Credential.CredentialId, file with { Md5Checksum = new string('0', 32) }, null, file.Version, default);
        var failure = await Assert.ThrowsAsync<DriveIndeterminateException>(() => service.EffectAsync(access, id, "drive.upload", 1, null, RecoveryEffect, RecoveryBytes, default).AsTask());
        Assert.False(failure.ResumeAllowed); Assert.Contains("checksum", failure.VerificationFailures);
        Assert.Equal("inspect_target_and_request_cleanup_approval", failure.RecoveryAction);
        Assert.Equal(1, fault.Uploads); Assert.Equal(0, fault.Trashes);
        Assert.Single((await service.ListAsync(access, null, null, null, 20, default)).Items);

        var client = host.CreateClient(); client.DefaultRequestHeaders.TryAddWithoutValidation(OwnerCapabilityHeaders.Name, TestOwnerCapability.Token(host.Services));
        var http = await client.GetAsync($"/api/v2/agent-instances/{access.InstanceId}/drive/accounts/drive/operations/{id}");
        Assert.Equal(System.Net.HttpStatusCode.Conflict, http.StatusCode);
        var error = await http.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("drive_upload_verification_failed", error.GetProperty("causeCode").GetString());
        Assert.Contains("checksum", error.GetProperty("verificationFailures").ToString());

        var made = await client.PostAsJsonAsync("/api/v2/sessions", new { agentInstanceId = access.InstanceId, mode = "text" }); made.EnsureSuccessStatusCode();
        var session = (await made.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("sessionId").GetGuid();
        var definition = (await host.Services.GetRequiredService<IMemoryStore>().LoadAsync(session))!.Definition;
        var executor = host.Services.GetRequiredService<SessionToolExecutor>();
        var admission = new ToolExecutionAdmission(false, TriggerKind.UserTurn, AgentInstanceId: access.InstanceId, OwnedSessionId: session);
        var inspected = await executor.ExecuteAsync(definition, session, new("inspect", "drive.get", JsonSerializer.Serialize(new { accountRef = "drive", operationId = id })), 16000, admission: admission);
        using var tool = JsonDocument.Parse(inspected.Text);
        Assert.Equal(pending.FileId, tool.RootElement.GetProperty("fileId").GetString());
        Assert.False(tool.RootElement.GetProperty("resumeAllowed").GetBoolean());
        Assert.Equal("inspect_target_and_request_cleanup_approval", tool.RootElement.GetProperty("recoveryAction").GetString());
        var cleanup = await executor.ExecuteAsync(definition, session, new("cleanup", "drive.trash", JsonSerializer.Serialize(new { accountRef = "drive", authEpoch = 1, fileId = pending.FileId, expectedVersion = file.Version })), 16000, admission: admission);
        Assert.Contains("approval_required", cleanup.Text);
        Assert.False((await files.GetAsync(access.Credential.CredentialId, pending.FileId, default))!.Trashed);

        // After an explicitly authorized exact Trash, terminal evidence releases
        // only this upload's pending claim. The old operation never resumes.
        var cleanupArgs = JsonSerializer.SerializeToElement(new { accountRef = "drive", authEpoch = 1, fileId = pending.FileId, expectedVersion = file.Version });
        var cleanupCall = new ModelToolCall("cleanup-approved", "drive.trash", cleanupArgs.GetRawText());
        var prepared = await ToolActionPreparation.PrepareApprovalAsync(executor, cleanupCall, cleanupArgs, definition: definition, sessionId: session, admission: admission);
        Assert.Null(prepared.ErrorJson);
        var grant = new ToolApprovalGrant(Guid.NewGuid(), cleanupCall.Name, prepared.Preparation!.ActionHash, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var cleaned = await executor.ExecuteAsync(definition, session, cleanupCall, 16000, admission: admission, approvalGrant: grant);
        using var cleanedJson = JsonDocument.Parse(cleaned.Text);
        Assert.True(cleanedJson.RootElement.GetProperty("receipt").GetProperty("file").GetProperty("trashed").GetBoolean());
        await Assert.ThrowsAsync<AgentCoreException>(() => service.InspectOperationAsync(access, id, default).AsTask());
        fault.Mode = "none";
        var fresh = await service.EffectAsync(access, Guid.NewGuid(), "drive.upload", 1, null, RecoveryEffect, RecoveryBytes, default);
        Assert.Equal("Succeeded", fresh.Status); Assert.NotEqual(pending.FileId, fresh.File.Id);
        Assert.Single((await service.ListAsync(access, null, null, null, 20, default)).Items);
    }
}
