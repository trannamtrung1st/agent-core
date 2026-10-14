using System.Text.Json;
using AgentCore.Application.Drive;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed partial class DriveJourneyTests
{
    private static async Task<IReadOnlyList<DriveTrashTarget>> TrashFiles(Factory host, DriveAccess access, int count)
    {
        var files = host.Services.GetRequiredService<ISyntheticDriveStore>(); var targets = new List<DriveTrashTarget>();
        for (var i = 0; i < count; i++)
        {
            var id = "batch-file-" + i;
            await files.SaveAsync(access.Credential.CredentialId, new(id, "sample-" + i + ".txt", "text/plain", 2, "1", "49f68a5c8493ec2c0bf489821c21fc3b", ["parent-hidden"], false), "hi"u8.ToArray(), null, default);
            targets.Add(new(id, "1"));
        }
        return targets;
    }
    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(20)]
    public async Task Exact_batch_trashes_only_its_manifest_and_replays_receipts_after_service_restart(int count)
    {
        using var host = new Factory(); var (service, access, fault) = await Recovery(host, "none");
        fault.DeniedParent = "parent-hidden"; var targets = await TrashFiles(host, access, count + 1); var id = Guid.NewGuid();
        var result = await service.TrashBatchAsync(access, id, 1, targets.Take(count).ToArray(), default);
        Assert.Equal(count, result.RequestedCount); Assert.Equal(count, result.TrashedCount);
        Assert.Equal(0, result.FailedCount + result.UncertainCount + result.NotAttemptedCount); Assert.Equal(0, fault.ParentReads);
        var replay = await RecoveryService(host, fault).TrashBatchAsync(access, id, 1, targets.Take(count).ToArray(), default);
        Assert.Equal(count, replay.TrashedCount); Assert.Equal(count, fault.Trashes);
        Assert.Equal(result.Items.Select(i => i.OperationId), replay.Items.Select(i => i.OperationId));
        Assert.False((await host.Services.GetRequiredService<ISyntheticDriveStore>().GetAsync(access.Credential.CredentialId, targets[count].FileId, default))!.Trashed);
    }
    [Fact]
    public async Task Stale_second_target_rejects_the_entire_new_manifest_before_any_patch()
    {
        using var host = new Factory(); var (service, access, fault) = await Recovery(host, "none"); var targets = await TrashFiles(host, access, 7);
        var result = await service.TrashBatchAsync(access, Guid.NewGuid(), 1, targets.Select((t, i) => i == 1 ? t with { ExpectedVersion = "stale" } : t).ToArray(), default);
        Assert.Equal(0, result.TrashedCount); Assert.Equal(1, result.FailedCount); Assert.Equal(6, result.NotAttemptedCount);
        Assert.Equal("drive_remote_conflict", result.Items[1].ErrorCode); Assert.Equal(0, fault.Trashes);
    }
    [Theory]
    [InlineData("known", "Failed", 1)]
    [InlineData("unknown-before", "Indeterminate", 1)]
    [InlineData("after", "Indeterminate", 2)]
    public async Task Partial_batch_stops_and_reports_each_outcome_without_repeating_unknown_effects(string failure, string status, int firstTrashed)
    {
        using var host = new Factory(); var (service, access, fault) = await Recovery(host, "none"); var targets = await TrashFiles(host, access, 7);
        fault.FailTrashId = targets[1].FileId; fault.TrashFailure = failure; var id = Guid.NewGuid();
        var result = await service.TrashBatchAsync(access, id, 1, targets, default);
        Assert.Equal(1, result.TrashedCount); Assert.Equal(5, result.NotAttemptedCount); Assert.Equal(status, result.Items[1].Status);
        Assert.Equal(2, fault.Trashes);
        var files = host.Services.GetRequiredService<ISyntheticDriveStore>(); var actual = 0;
        foreach (var target in targets) if ((await files.GetAsync(access.Credential.CredentialId, target.FileId, default))!.Trashed) actual++;
        Assert.Equal(firstTrashed, actual);
        fault.TrashFailure = "none";
        var replay = await RecoveryService(host, fault).TrashBatchAsync(access, id, 1, targets, default);
        if (failure == "after") { Assert.Equal(7, replay.TrashedCount); Assert.Equal(7, fault.Trashes); }
        else { Assert.Equal(status, replay.Items[1].Status); Assert.Equal(2, fault.Trashes); Assert.Equal(5, replay.NotAttemptedCount); }
    }
    [Fact]
    public async Task Batch_rejects_oversize_duplicate_and_unknown_manifest_arguments()
    {
        using var host = new Factory(); var (service, access, fault) = await Recovery(host, "none"); var targets = await TrashFiles(host, access, 21);
        await Assert.ThrowsAsync<AgentCoreException>(() => service.TrashBatchAsync(access, Guid.NewGuid(), 1, targets, default).AsTask());
        await Assert.ThrowsAsync<AgentCoreException>(() => service.TrashBatchAsync(access, Guid.NewGuid(), 1, [targets[0], targets[0]], default).AsTask());
        using var args = JsonDocument.Parse("""{"accountRef":"drive","authEpoch":1,"files":[{"fileId":"x","expectedVersion":"1","all":true}]}""");
        Assert.Throws<AgentCoreException>(() => DriveMutationArguments.Manifest(args.RootElement)); Assert.Equal(0, fault.Trashes);
    }
    [Theory]
    [InlineData("cancel")]
    [InlineData("revoke")]
    public async Task Interrupted_batch_stops_without_unreviewed_effects(string mode)
    {
        using var host = new Factory(); var (service, access, fault) = await Recovery(host, "none"); var targets = await TrashFiles(host, access, 7);
        using var cancellation = new CancellationTokenSource();
        fault.AfterTrash = async (id, ct) =>
        {
            if (id != targets[0].FileId) return;
            if (mode == "cancel") { cancellation.Cancel(); ct.ThrowIfCancellationRequested(); }
            else await host.Services.GetRequiredService<IGoogleDriveOAuthService>().DisconnectAsync(access.Credential.CredentialId, access.Credential.Revision, default);
        };
        var result = await service.TrashBatchAsync(access, Guid.NewGuid(), 1, targets, cancellation.Token);
        Assert.Equal(1, result.UncertainCount); Assert.Equal(6, result.NotAttemptedCount); Assert.Equal(1, fault.Trashes);
        Assert.True((await host.Services.GetRequiredService<ISyntheticDriveStore>().GetAsync(access.Credential.CredentialId, targets[0].FileId, default))!.Trashed);
        foreach (var target in targets.Skip(1)) Assert.False((await host.Services.GetRequiredService<ISyntheticDriveStore>().GetAsync(access.Credential.CredentialId, target.FileId, default))!.Trashed);
    }
    [Fact]
    public async Task Batch_freezes_the_approved_order_before_a_caller_can_change_its_list()
    {
        using var host = new Factory(); var (service, access, fault) = await Recovery(host, "none"); var targets = await TrashFiles(host, access, 8);
        var approved = targets.Take(7).ToList(); fault.AfterTrash = (_, _) => { approved[1] = targets[7]; return Task.CompletedTask; };
        var result = await service.TrashBatchAsync(access, Guid.NewGuid(), 1, approved, default); Assert.Equal(7, result.TrashedCount);
        var files = host.Services.GetRequiredService<ISyntheticDriveStore>();
        foreach (var target in targets.Take(7)) Assert.True((await files.GetAsync(access.Credential.CredentialId, target.FileId, default))!.Trashed);
        Assert.False((await files.GetAsync(access.Credential.CredentialId, targets[7].FileId, default))!.Trashed);
    }
}
