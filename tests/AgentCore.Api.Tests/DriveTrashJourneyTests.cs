using AgentCore.Application.Drive;
using AgentCore.Application.Ports;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed partial class DriveJourneyTests
{
    [Theory]
    [InlineData("drive.trash", "sample.txt")]
    [InlineData("drive.trash", "Budget / 2026.txt")]
    [InlineData("drive.rename", "sample.txt")]
    public async Task Readable_target_with_unavailable_parent_does_not_require_parent_access_for_trash_or_rename(string tool, string name)
    {
        using var host = new Factory(); var (service, access, fault) = await Recovery(host, "none");
        fault.DeniedParent = "parent-hidden";
        var files = host.Services.GetRequiredService<ISyntheticDriveStore>();
        var file = new DriveFile("target-file", name, "text/plain", 2, "1", "49f68a5c8493ec2c0bf489821c21fc3b", ["parent-hidden"], false);
        await files.SaveAsync(access.Credential.CredentialId, file, "hi"u8.ToArray(), null, default);
        Assert.Equal(file.Id, (await service.GetAsync(access, file.Id, default)).Id);
        var effect = new DriveEffect(tool == "drive.rename" ? "renamed.txt" : file.Name, "parent-hidden", ExpectedVersion: file.Version);
        var receipt = await service.EffectAsync(access, Guid.NewGuid(), tool, 1, file.Id, effect, ReadOnlyMemory<byte>.Empty, default);
        Assert.Equal("Succeeded", receipt.Status); Assert.Equal(0, fault.ParentReads);
        Assert.Equal(tool == "drive.trash" ? 1 : 0, fault.Trashes);
        Assert.Equal(tool == "drive.rename" ? 1 : 0, fault.Renames);
        Assert.Equal(tool == "drive.trash", receipt.File.Trashed);
        if (tool == "drive.rename") Assert.Equal("renamed.txt", receipt.File.Name);
    }
    [Theory]
    [InlineData("drive.create_folder")]
    [InlineData("drive.upload")]
    [InlineData("drive.move")]
    public async Task Unavailable_destination_still_blocks_creation_upload_and_move(string tool)
    {
        using var host = new Factory(); var (service, access, fault) = await Recovery(host, "none"); fault.DeniedParent = "unavailable-destination";
        var targets = await TrashFiles(host, access, 1);
        var effect = tool == "drive.upload" ? RecoveryEffect with { Parent = "unavailable-destination" } : new DriveEffect("sample.txt", "unavailable-destination", ExpectedVersion: "1", SourceParent: "parent-hidden");
        var failure = await Assert.ThrowsAsync<AgentCore.Application.Sessions.AgentCoreException>(() => service.EffectAsync(access, Guid.NewGuid(), tool, 1, tool == "drive.move" ? targets[0].FileId : null, effect, tool == "drive.upload" ? RecoveryBytes : ReadOnlyMemory<byte>.Empty, default).AsTask());
        Assert.Equal("drive_not_found", failure.Code); Assert.Equal(1, fault.ParentReads); Assert.Equal(0, fault.Uploads + fault.Trashes + fault.Renames);
        Assert.Single(await host.Services.GetRequiredService<ISyntheticDriveStore>().ListAsync(access.Credential.CredentialId, default));
    }
    [Fact]
    public async Task Move_validates_source_membership_without_reading_an_unavailable_source_folder()
    {
        using var host = new Factory(); var (service, access, fault) = await Recovery(host, "none"); fault.DeniedParent = "parent-hidden";
        var targets = await TrashFiles(host, access, 1); var files = host.Services.GetRequiredService<ISyntheticDriveStore>();
        await files.SaveAsync(access.Credential.CredentialId, new("destination", "Destination", "application/vnd.google-apps.folder", 0, "1", null, ["root"], false), [], null, default);
        var receipt = await service.EffectAsync(access, Guid.NewGuid(), "drive.move", 1, targets[0].FileId, new("sample-0.txt", "destination", ExpectedVersion: "1", SourceParent: "parent-hidden"), ReadOnlyMemory<byte>.Empty, default);
        Assert.Equal("Succeeded", receipt.Status); Assert.Equal(["destination"], receipt.File.Parents); Assert.Equal(0, fault.ParentReads);
    }
    [Fact]
    public async Task Successful_trash_patch_with_failed_reconciliation_stays_uncertain_until_exact_inspection()
    {
        using var host = new Factory(); var (service, access, fault) = await Recovery(host, "none"); var targets = await TrashFiles(host, access, 1);
        fault.FailTrashId = targets[0].FileId; fault.FailTrashRead = true; var operation = Guid.NewGuid();
        var effect = new DriveEffect("sample-0.txt", "parent-hidden", ExpectedVersion: "1");
        await Assert.ThrowsAsync<DriveIndeterminateException>(() => service.EffectAsync(access, operation, "drive.trash", 1, targets[0].FileId, effect, ReadOnlyMemory<byte>.Empty, default).AsTask());
        Assert.Equal("Indeterminate", (await host.Services.GetRequiredService<IDriveOperationStore>().GetAsync(access.InstanceId, operation, default))!.State);
        Assert.True((await host.Services.GetRequiredService<ISyntheticDriveStore>().GetAsync(access.Credential.CredentialId, targets[0].FileId, default))!.Trashed);
        await Assert.ThrowsAsync<DriveIndeterminateException>(() => service.EffectAsync(access, operation, "drive.trash", 1, targets[0].FileId, effect, ReadOnlyMemory<byte>.Empty, default).AsTask()); Assert.Equal(1, fault.Trashes);
        fault.FailTrashRead = false; var receipt = await service.InspectOperationAsync(access, operation, default); Assert.Equal("Succeeded", receipt.Status); Assert.Equal(1, fault.Trashes);
    }
}
