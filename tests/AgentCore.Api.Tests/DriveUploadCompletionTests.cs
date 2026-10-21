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
}
